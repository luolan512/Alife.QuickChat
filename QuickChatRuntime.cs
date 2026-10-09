using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace Marisa.QuickChat;

public sealed class QuickChatRuntime : IDisposable
{
    static QuickChatRuntime? current;
    static readonly object CurrentGate = new();

    readonly ChatActivitySystem chatActivitySystem;
    readonly PluginSystem pluginSystem;
    readonly ILogger<QuickChatModule> logger;
    readonly Dictionary<string, List<QuickChatMessage>> histories = new();
    readonly HashSet<string> busyPetIds = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> busyRequestIds = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> registeredHotkeys = new();
    readonly object stateGate = new();
    readonly object configGate = new();
    readonly List<object> configSources = new();
    readonly Dictionary<object, QuickChatConfig> sourceConfigs = new();
    readonly Dictionary<object, string> sourcePetIds = new();
    readonly Dictionary<string, QuickChatConfig> petConfigs = new(StringComparer.OrdinalIgnoreCase);
    object? configSource;
    readonly Dictionary<string, ChatBot> subscribedBots = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Action<ChatContext>> chatFinishedHandlers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Action<string>> chatSentHandlers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Action<string>> chatReceivedHandlers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Action> chatOverHandlers = new(StringComparer.OrdinalIgnoreCase);

    // 流式回显：ChatBot.ChatReceived 每来一个分片就攒一次，按节流推给窗口。
    //
    // 为什么需要它：助手气泡原本只在 ChatFinished（整轮生成结束、且所有 ChatFinishedAsync
    // 处理器都跑完）时才由 AddMessage 推出去，所以「模型输出已经在别处流式显示了、
    // 快聊却半天不出话」的延迟正好等于整段生成耗时 —— 输出越长越明显。
    //
    // 这些临时气泡<b>只推窗口、不进 histories</b>：histories 里始终只有定稿后的那一条，
    // 落盘编号、翻页游标、去重全部维持原样，流式失败也不会污染历史。
    readonly Dictionary<string, StreamingTurn> streamingTurns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>流式分片两次推送之间的最小间隔。太小会把渲染层刷爆（每次都是全量重建气泡）。</summary>
    const int StreamingPushIntervalMs = 90;

    /// <summary>
    /// 角色一次能读走的历史条数上限。
    /// <para>
    /// 写死 100 而不是跟着配置走：再往上就是拿上下文换「一次看全」，而这件事用
    /// <c>before</c> 游标翻页本来就能做到 —— 翻页还能让角色自己决定要不要继续，
    /// 一次灌 500 条则是无条件把上下文挤爆。
    /// </para>
    /// </summary>
    const int AgentReadMax = 100;

    /// <summary>
    /// 「这个角色刚翻过哪个会话」—— 留给紧接着的<b>工具返回轮</b>当落点。
    /// <para>
    /// 为什么需要：模型调 <c>QuickChatRead</c> 之后，真正的回答是在<b>工具返回那一轮</b>
    /// 才说出来的（读结果要先投回去，模型才接着说话）。而 <see cref="OnChatFinished"/>
    /// 对 <c>kind=tool</c> 是整轮丢弃的 —— 于是回答永远不出现，
    /// 用户看到的就是「问了一句，快聊里什么都没有，后面也没动静」。
    /// </para>
    /// <para>
    /// 只记私聊：群聊的文字输出不是发言（要发言得用 <c>QuickChatSend</c>），
    /// 落进去会污染群记录。另外任何一次发送类工具调用（<c>QuickChatSend</c>/<c>QuickChatDirect</c>，
    /// 后者最终也走 <see cref="SendFromAgent"/>）都会把它清掉 ——
    /// 否则会重演当年那个「她跟别人私聊，话却发到了主人这里」的老问题。
    /// </para>
    /// <para>
    /// 生命周期由 <see cref="OnChatSent"/> 按 kind 维护（私聊轮重新指向、群聊与无信封的注入清掉），
    /// 读取方（<see cref="OnChatFinished"/> / <see cref="BeginStreamingTurn"/>）都<b>只读不取</b>：
    /// 模型可能连着翻好几次记录，取走一次的话第二次之后又会开始丢回答。
    /// </para>
    /// </summary>
    readonly Dictionary<string, string> toolReplyTargets = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Interactor<QuickChatModule>> promptInteractors = new(StringComparer.OrdinalIgnoreCase);
    // 发送登记：快聊自己发出去的消息在这里留一张「提货单」。
    // <para>
    // 归属判定必须靠<b>身份</b>（我们登记过），不能靠<b>正文</b>。
    // 原因：<c>ChatSent</c> 拿到的正文已经被整条 <c>ChatSend</c> 过滤器链改写过
    // （MessageFilter 会前置时间戳、后置尾注；SystemEventBoost 可能整条替换），
    // 而且 <c>ChatBot.Poke</c> 会把多个插件的内容拼成同一条消息。
    // 正文是所有插件共写的共享可变区，靠它猜「这条是不是我发的」必然出错：
    // 猜错一次就是把用户自己的话整条吞掉。
    // </para>
    // <para>
    // 按 requestId 存而不是按 petId 存，是为了让「被后来消息顶掉」的那条也能被正确收尾：
    // 同一个角色可能连着发两条，先发的那条提货单必须还找得到，否则它会永远停在「发送中」。
    // </para>
    readonly Dictionary<string, PendingOutgoing> outgoingByRequest = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> outgoingRequestByPet = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan OutgoingClaimWindow = TimeSpan.FromSeconds(60);

    // ────────────────────────── 群聊引擎 ──────────────────────────
    // 六个引擎件（Identity / Envelope / Gate / Store / Format / Channel）都是从
    // Marisa.LocalChat 平移过来的同族实现，改名后放进快聊，避免跨插件强依赖。
    // 运行时只做「接线」：用户发言 → store.Send → 各成员 channel.Enqueue；
    // 角色发言（QuickChatSend）→ store.Send → 推前端 + 投递给其他成员。

    /// <summary>消息落盘存储。两个「保存记录」开关都关掉时仍然存在，只是不写磁盘。</summary>
    QuickChatStore? store;

    /// <summary>每角色一个投递通道。</summary>
    readonly Dictionary<string, QuickChatChannel> channels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每角色当前投递深度。人类发言 0，角色转发一次 +1，用于阻断角色互相刷屏。</summary>
    readonly Dictionary<string, int> deliveryDepths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 每角色「本轮应当有回复」的会话列表（会话 ID + 本批投递到的最大消息编号），ChatFinished 时消费。
    /// </summary>
    readonly Dictionary<string, List<(string ConversationId, long UpToId)>> pendingReplies =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>会话结构同步签名。群成员跟着激活状态变，靠它避免每帧重复写盘。</summary>
    string storeSignature = string.Empty;

    /// <summary>用户还没看过、但已经落盘的更早历史：用于「向上翻页」。</summary>
    readonly Dictionary<string, long> historyCursors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已经「定稿」的会话：内存里的那份就是权威，不要再拿磁盘覆盖它。</summary>
    readonly HashSet<string> historyLoaded = new(StringComparer.OrdinalIgnoreCase);

    readonly string dataRoot;

    /// <summary>群聊会话 Id 的前缀。私聊会话 Id 不带前缀（就是角色名）。</summary>
    const string GroupPrefix = "group:";

    /// <summary>公共大厅的会话 Id。它不在配置里，是隐式存在的。</summary>
    const string LobbyGroupId = "group:__lobby__";

    /// <summary>
    /// 多开私聊会话 Id 的前缀。
    /// <para>
    /// 默认私聊的 Id 就是角色名（<c>小梦</c>），一个角色只能有一条；用户点「新建会话」
    /// 时开的是 <c>dm:小梦#2</c> 这种带序号的新线，与默认私聊并存、各自独立记录。
    /// 加前缀让「它不是角色名」一眼可辨，不会被误当成一个叫 <c>dm:小梦#2</c> 的角色。
    /// </para>
    /// </summary>
    const string DirectPrefix = "dm:";

    /// <summary>用户在会话里的发言者标识（用户不是角色，没有 petId）。</summary>
    const string UserSpeakerId = "__user__";

    static bool IsGroupConversationId(string? conversationId) =>
        conversationId != null && conversationId.StartsWith(GroupPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 会话里的角色名（按成员顺序）。
    /// <para>
    /// 私聊要靠它定位「对话对象是谁」：默认私聊的 ID 就是角色名，但用户「新建」出来的
    /// 多开私聊 ID 是 <c>dm:小梦#2</c>、角色自建的 AI↔AI 私聊是 <c>dm:甲|乙</c>，
    /// 这两种都必须从成员里取，直接拿 ID 去 <c>chatActivitySystem</c> 找会找不到人。
    /// </para>
    /// </summary>
    List<string> AgentsOfConversation(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return new List<string>();

        try
        {
            QuickChatStoredConversation? conversation = store?.Find(conversationId);
            if (conversation != null)
            {
                List<string> agents = conversation.Members
                    .Select(QuickChatPrincipal.Parse)
                    .Where(principal => principal.IsAgent)
                    .Select(principal => principal.AgentName)
                    .Where(name => string.IsNullOrWhiteSpace(name) == false)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (agents.Count > 0)
                    return agents;
            }
        }
        catch (Exception)
        {
            // 存储不可用时退化成「ID 就是角色名」，保持旧行为。
        }

        // 存储里还没有这个会话（例如刚激活、同步还没跑）：ID 本身就是角色名。
        return IsGroupConversationId(conversationId)
            ? new List<string>()
            : new List<string> { conversationId.Trim() };
    }

    static readonly HttpClient ImageHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly string QuickChatAttachmentPrompt = """
        在 QuickChat 快聊界面中发送图片或文件时，输出以下专用协议标记（不要用代码块包裹）：
        [[QuickChatAttachment]]完整本机路径或可访问的 http(s) 直链[[/QuickChatAttachment]]
        可输出多个标记。路径必须真实存在或可访问；不要编造路径，不要改用 Markdown 链接/图片语法。
        这是普通双中括号标记，不是 XML/HTML；不要输出 <QuickChatAttachment>，也不要写成 [QuickChatAttachment:路径]。
        图片可用完整本机路径或 http(s) 直链；文件优先使用完整本机路径。不要发送目录。
        用户发送的截图/图片/文件消息里包含完整路径，可以在需要引用时直接使用。
        """ + "\n";

    QuickChatConfig config = new();
    readonly IQuickChatWindowFactory windowFactory;
    readonly IQuickChatShortcutService shortcutService;
    IQuickChatWindow? window;
    // 当前选中的会话。
    // 私聊会话的 Id 就是角色名（petId），所以历史键与旧行为完全兼容；
    // 群聊会话的 Id 形如 "group:群名"。
    string? selectedConversationId;
    string configSignature = string.Empty;
    bool hasAppliedConfig;
    int referenceCount;
    bool disposed;

    QuickChatRuntime(
        ChatActivitySystem chatActivitySystem,
        PluginSystem pluginSystem,
        ILogger<QuickChatModule> logger,
        IQuickChatWindowFactory? windowFactory = null,
        IQuickChatShortcutService? shortcutService = null)
    {
        this.chatActivitySystem = chatActivitySystem;
        this.pluginSystem = pluginSystem;
        this.logger = logger;
        this.windowFactory = windowFactory ?? new ElectronQuickChatWindowFactory();
        this.shortcutService = shortcutService ?? new ElectronQuickChatShortcutService(logger);

        // 落盘根目录放在插件自己的数据区，和 Alife 的 Storage 并列但不混进角色目录。
        dataRoot = Path.Combine(Alife.Foundation.AlifePath.StorageFolderPath, "QuickChat");
        QuickChatLog.SetRoot(dataRoot);
        try
        {
            store = new QuickChatStore(dataRoot);
        }
        catch (Exception exception)
        {
            // 存储起不来不能让整个快聊挂掉：退化成纯内存模式，私聊照常用。
            QuickChatLog.Write("初始化消息存储失败，已退化为不落盘：" + exception);
            store = null;
        }

        this.chatActivitySystem.Activated += OnChatActivityActivated;
        this.chatActivitySystem.Deactivated += OnChatActivityDeactivated;

        foreach (ChatActivity activity in chatActivitySystem.GetAllChatActivities().ToList())
            AttachChatBot(activity);
    }

    public static QuickChatRuntime GetOrCreate(
        ChatActivitySystem chatActivitySystem,
        PluginSystem pluginSystem,
        ILogger<QuickChatModule> logger,
        IQuickChatWindowFactory? windowFactory = null,
        IQuickChatShortcutService? shortcutService = null)
    {
        lock (CurrentGate)
        {
            if (current == null)
                current = new QuickChatRuntime(
                    chatActivitySystem,
                    pluginSystem,
                    logger,
                    windowFactory,
                    shortcutService);

            return current;
        }
    }

    public void AddReference(object? source)
    {
        if (source == null)
            return;

        lock (configGate)
        {
            referenceCount++;
            if (configSources.Contains(source) == false)
            {
                configSources.Add(source);
                sourceConfigs[source] = config;
            }

            configSource ??= source;
        }
    }

    public bool Release(object? source)
    {
        if (source == null)
            return false;

        bool shouldDispose;
        lock (configGate)
        {
            referenceCount--;
            if (referenceCount < 0)
                referenceCount = 0;

            sourceConfigs.Remove(source);
            if (sourcePetIds.Remove(source, out string? removedPetId))
                petConfigs.Remove(removedPetId);
            configSources.Remove(source);

            if (ReferenceEquals(configSource, source))
                configSource = configSources.FirstOrDefault();

            shouldDispose = referenceCount <= 0;
        }

        if (shouldDispose)
        {
            Dispose();
            return true;
        }

        return false;
    }

    public void ApplyConfig(QuickChatConfig value, object? source)
    {
        QuickChatConfig normalized = NormalizeConfig(value);

        lock (configGate)
        {
            if (source != null)
            {
                if (configSources.Contains(source) == false)
                {
                    configSources.Add(source);
                    configSource ??= source;
                }

                sourceConfigs[source] = normalized;
                if (source is ChatBehaviour behaviour)
                {
                    string petId = behaviour.Character.Name;
                    sourcePetIds[source] = petId;
                    petConfigs[petId] = normalized;
                }

                // 多个桌宠都会实例化 QuickChat 模块。全局悬浮窗只允许一个配置源，
                // 否则不同角色的配置会在每帧 Update 中互相抢占并反复 resize。
                if (ReferenceEquals(source, configSource) == false)
                    return;
            }

            string signature = CreateConfigSignature(normalized);
            if (hasAppliedConfig && string.Equals(signature, configSignature, StringComparison.Ordinal))
                return;

            config = normalized;
            configSignature = signature;
            hasAppliedConfig = true;
        }

        RegisterHotkeys();
        SyncStoreConversations();
        RefreshAllPrompts();
        ApplyConversationWindowSize();

        SendState();
    }

    public Task ApplyConfigAsync(QuickChatConfig value, object? source)
    {
        ApplyConfig(value, source);
        return Task.CompletedTask;
    }

    void OnChatActivityActivated(ChatActivity activity)
    {
        AttachChatBot(activity);
        // 会话索引变了（成员增减），角色手里的「我能去哪发言」也得跟着变。
        // 刷新提示词只在「不在对话中途」的时机做，避免改到正在使用的聊天历史。
        if (SyncStoreConversations())
            RefreshAllPrompts();
        EnsureSelectedConversation();
        SendState();
        SendSelectedHistory();
    }

    void OnChatActivityDeactivated(ChatActivity activity)
    {
        DetachChatBot(activity.Character.Name);
        if (SyncStoreConversations())
            RefreshAllPrompts();
        EnsureSelectedConversation();
        SendState();
        SendSelectedHistory();
    }

    void AttachChatBot(ChatActivity activity)
    {
        if (disposed || activity.ChatBot == null)
            return;

        string petId = activity.Character.Name;
        if (subscribedBots.TryGetValue(petId, out ChatBot? existing))
        {
            if (ReferenceEquals(existing, activity.ChatBot))
                return;
            DetachChatBot(petId);
        }

        Action<ChatContext> handler = context => OnChatFinished(petId, context);
        Action<string> sentHandler = text => OnChatSent(petId, text);
        Action<string> receivedHandler = text => OnChatReceived(petId, text);
        Action overHandler = () => OnChatOver(petId);
        activity.ChatBot.ChatFinished += handler;
        activity.ChatBot.ChatSent += sentHandler;
        activity.ChatBot.ChatReceived += receivedHandler;
        activity.ChatBot.ChatOver += overHandler;
        subscribedBots[petId] = activity.ChatBot;
        chatFinishedHandlers[petId] = handler;
        chatSentHandlers[petId] = sentHandler;
        chatReceivedHandlers[petId] = receivedHandler;
        chatOverHandlers[petId] = overHandler;

        if (promptInteractors.ContainsKey(petId) == false)
            promptInteractors[petId] = new Interactor<QuickChatModule>(activity.ChatBot);

        RefreshPrompt(petId);

        // 每个激活角色一条投递通道。通道是「角色能被群聊叫醒」的前提，
        // 没有它就只能人类单向发言。
        if (store != null && channels.ContainsKey(petId) == false)
        {
            QuickChatChannel channel = new(
                petId,
                activity.ChatBot,
                store,
                () => GetConfigForPet(petId),
                depth => SetDeliveryDepth(petId, depth),
                ids => ArmReplyCheck(petId, ids),
                BroadcastTyping,
                SendState);
            channels[petId] = channel;
            channel.Start();
        }
    }

    void DetachChatBot(string? petId)
    {
        if (string.IsNullOrWhiteSpace(petId))
            return;

        if (subscribedBots.Remove(petId, out ChatBot? bot))
        {
            if (chatFinishedHandlers.Remove(petId, out Action<ChatContext>? handler))
                bot.ChatFinished -= handler;

            if (chatSentHandlers.Remove(petId, out Action<string>? sentHandler))
                bot.ChatSent -= sentHandler;

            if (chatReceivedHandlers.Remove(petId, out Action<string>? receivedHandler))
                bot.ChatReceived -= receivedHandler;

            if (chatOverHandlers.Remove(petId, out Action? overHandler))
                bot.ChatOver -= overHandler;
        }

        if (promptInteractors.Remove(petId, out Interactor<QuickChatModule>? promptInteractor))
            promptInteractor.Dispose();

        if (channels.Remove(petId, out QuickChatChannel? channel))
        {
            channel.ClearPending();
            channel.Dispose();
        }

        lock (stateGate)
        {
            deliveryDepths.Remove(petId);
            pendingReplies.Remove(petId);
            streamingTurns.Remove(petId);
            toolReplyTargets.Remove(petId);
        }
    }

    void OnChatSent(string petId, string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
            return;

        // 记下「这一轮是在回答哪条会话」，给紧接着的<b>工具返回轮</b>当落点
        // （见 toolReplyTargets）。规则按 kind 分三路，刻意保守：
        //
        // - kind=tool：不动。它正是「调完工具接着回答」的那一轮，落点必须留着。
        // - kind=dm：记成 conv= 指的会话。这样即使模型调的是 QuickChatList 这种
        //   不指定会话的工具，回答也知道该回到哪儿 —— 否则用户问了话一样什么都收不到。
        // - 其余（kind=group，以及不带信封的 poke / 周期报点）：清掉。
        //   群聊的文字输出不是发言（要发言得用 QuickChatSend），落进去会把
        //   「没发出去的话」写进群记录；无信封的注入则没有明确对象，维持旧的丢弃行为。
        string? sentKind = QuickChatEnvelope.KindOf(rawMessage);
        if (sentKind != null && sentKind.Equals("tool", StringComparison.OrdinalIgnoreCase))
        {
            // 保留落点，什么都不做。
        }
        else if (sentKind != null && sentKind.Equals("dm", StringComparison.OrdinalIgnoreCase))
        {
            string target = QuickChatEnvelope.ConversationOf(rawMessage) ?? petId;
            lock (stateGate)
                toolReplyTargets[petId] = target;
        }
        else
        {
            ClearToolReplyTarget(petId);
        }

        // 只认领「发送登记」里那一条。认领不到，就说明这条不是快聊发出去的
        // ——poke、周期报点、群聊投递、别的插件的注入——一律不进私聊流。
        //
        // 这一步取代了以前的黑名单 Contains 匹配（IsInjectedSystemMessage）。
        // 好处是快聊不再需要知道「系统消息长什么样」：那些标记（时间戳、尾注、
        // [消息来源(...)]）全都是用户可编辑的提示词，改一个字黑名单就失效，
        // 而失效的代价是用户自己的话被整条吞掉。
        bool claimed = ClaimPendingOutgoing(petId, rawMessage, out PendingOutgoing? pending);

        // 本轮流式回显落到哪个会话，必须在这里就定下来：
        // ClaimPendingOutgoing 会把登记项取走，晚一步就拿不到 ConversationId 了。
        BeginStreamingTurn(petId, rawMessage, claimed ? pending?.ConversationId : null);

        if (claimed == false)
            return;

        // 能走到这里说明本条已经进入对话上下文（ChatSent 在装载历史之后触发），
        // 把本地乐观气泡从「发送中」翻成「已送达」。
        // 用 pending 里记的会话而不是 petId：多开私聊时两者不是一回事。
        UpdateOutgoingState(
            string.IsNullOrWhiteSpace(pending!.ConversationId) ? petId : pending.ConversationId,
            pending.MessageId,
            "sent");
    }

    /// <summary>这条进来的消息是不是「工具返回值」那一轮（kind=tool）。</summary>
    static bool IsToolRound(string? rawMessage)
    {
        string? kind = QuickChatEnvelope.KindOf(rawMessage);
        return kind != null && kind.Equals("tool", StringComparison.OrdinalIgnoreCase);
    }

    void ClearToolReplyTarget(string petId)
    {
        lock (stateGate)
            toolReplyTargets.Remove(petId);
    }

    /// <summary>
    /// 为一轮对话准备流式回显。
    /// <para>
    /// 落点判定必须和 <see cref="OnChatFinished"/> 完全一致，否则会出现
    /// 「流式时飘到一个会话、定稿后又跳到另一个会话」。规则照抄那边：
    /// 群聊轮次和工具返回轮次的文字输出不算发言，不显示；其余落到 <c>conv=</c>
    /// 指定的会话，没有 <c>conv=</c>（poke、周期报点等注入）就用默认私聊。
    /// </para>
    /// <para>
    /// <b>一个例外</b>：工具返回轮如果带着 <see cref="toolReplyTargets"/> 里的落点
    /// （刚翻过某个会话），它的文字输出就是回答本身，要照常流式 —— 不这么做的话，
    /// 前面几轮都是实时出字，唯独这一轮卡到最后才蹦出来，看起来就像「卡住了」。
    /// </para>
    /// <para>
    /// 配置项「流式输出」关掉时这里直接不建状态，行为完全回到旧版 ——
    /// 气泡仍然只在 <see cref="OnChatFinished"/> 出现。
    /// </para>
    /// </summary>
    void BeginStreamingTurn(string petId, string? rawMessage, string? pendingConversationId)
    {
        // 按角色取（和其它配置一样走 petConfigs），这样每个桌宠可以各开各的。
        bool enabled = GetConfigForPet(petId).StreamingOutput;

        string? kind = QuickChatEnvelope.KindOf(rawMessage);
        bool toolRound = IsToolRound(rawMessage);

        // 只读不取：落点的生命周期由 OnChatSent 管，这里取走的话
        // 「连着翻两次记录」的第二次就没落点了（见 toolReplyTargets）。
        string? toolTarget = null;
        if (toolRound)
        {
            lock (stateGate)
                toolReplyTargets.TryGetValue(petId, out toolTarget);
        }

        bool streamable = enabled
            && (kind == null
                || (kind.Equals("group", StringComparison.OrdinalIgnoreCase) == false
                    && (toolRound == false || string.IsNullOrWhiteSpace(toolTarget) == false)));

        lock (stateGate)
        {
            // 上一轮如果没走完（被新消息打断），旧状态在这里丢掉；
            // 它留在前端的那半个气泡由前端收到新分片时自己清。
            streamingTurns.Remove(petId);

            if (streamable == false)
                return;

            string conversationId = QuickChatEnvelope.ConversationOf(rawMessage)
                // 工具返回信封里没有 conv=（见 QuickChatEnvelope.EncodeToolResult），
                // 落点只能靠刚才那次翻记录记下来的目标。
                ?? (string.IsNullOrWhiteSpace(toolTarget) ? null : toolTarget)
                ?? (string.IsNullOrWhiteSpace(pendingConversationId) ? petId : pendingConversationId);

            streamingTurns[petId] = new StreamingTurn(conversationId);
        }
    }

    /// <summary>
    /// 模型输出的一个流式分片。攒到节流窗口就推一次，让气泡边生成边长，
    /// 而不是等整轮结束（<see cref="OnChatFinished"/>）才一次性出现。
    /// </summary>
    void OnChatReceived(string petId, string? chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return;

        string conversationId;
        string messageId;
        string text;

        lock (stateGate)
        {
            if (streamingTurns.TryGetValue(petId, out StreamingTurn? turn) == false)
                return;

            turn.Buffer.Append(chunk);

            // 第一个分片立刻推（用户马上就能看到反应），之后按 StreamingPushIntervalMs 节流。
            // 每次推送前端都会整段重建气泡，推太密只会换来卡顿。
            long now = Environment.TickCount64;
            if (turn.Pushed && now - turn.LastPushTicks < StreamingPushIntervalMs)
                return;

            text = QuickChatContentFilter.CleanStreamingText(turn.Buffer.ToString());
            if (text.Length == 0)
                return;

            turn.Pushed = true;
            turn.LastPushTicks = now;
            conversationId = turn.ConversationId;
            messageId = turn.MessageId;
        }

        PushStreamingMessage(petId, conversationId, messageId, text, streaming: true);
    }

    /// <summary>
    /// 模型的文字输出结束（<c>ChatOver</c>）：把攒下来的全文立刻补推一次，并摘掉「还在生成」标记。
    /// <para>
    /// 这一步不能省。分片是<b>节流</b>推送的 —— 末尾那几个分片如果落在节流窗口里就再也不会被推出去；
    /// 而 <c>ChatFinished</c> 之后还要等 TTS（<c>&lt;Speak&gt;</c> 那段得念完）和函数执行，
    /// 可能好几秒。这段时间气泡就停在半句话上，看起来正是「后台明明生成完了，快聊却卡住不动」。
    /// </para>
    /// <para>
    /// 这里<b>不</b>摘掉 <c>streamingTurns</c> 状态：万一还有迟到的分片，让它照常追加，
    /// 总比提前封口、把后半句永久截断好。状态由下一轮 <see cref="BeginStreamingTurn"/>
    /// 或 <see cref="OnChatFinished"/> 收走。
    /// </para>
    /// </summary>
    void OnChatOver(string petId)
    {
        string conversationId;
        string messageId;
        string text;

        lock (stateGate)
        {
            if (streamingTurns.TryGetValue(petId, out StreamingTurn? turn) == false)
                return;

            text = QuickChatContentFilter.CleanStreamingText(turn.Buffer.ToString());
            if (text.Length == 0)
                return;

            conversationId = turn.ConversationId;
            messageId = turn.MessageId;
        }

        PushStreamingMessage(petId, conversationId, messageId, text, streaming: false);
    }

    /// <summary>把一条临时气泡推给窗口。可见性规则和 <see cref="AddMessage"/> 保持一致。</summary>
    void PushStreamingMessage(string petId, string conversationId, string messageId, string text, bool streaming)
    {
        // 只推当前选中的会话。别的会话等用户切过去时，从 histories 拿到的是定稿那条。
        if (string.Equals(selectedConversationId, conversationId, StringComparison.OrdinalIgnoreCase) == false)
            return;

        window?.Send("message", BuildStreamingPayload(petId, conversationId, messageId, text, streaming));
    }

    /// <summary>
    /// 临时气泡的载荷。字段和 <see cref="AddMessage"/> 推的形状保持一致，额外两个标记：
    /// <list type="bullet">
    /// <item><c>streaming</c>：模型还在吐字，前端据此在末尾显示闪动光标。</item>
    /// <item><c>provisional</c>：这条是占位气泡，定稿到达时前端要把它撤掉 ——
    /// 定稿用的是落盘编号，和这里的临时 id 不同，不撤就会同一句话显示两遍。</item>
    /// </list>
    /// </summary>
    object BuildStreamingPayload(string petId, string conversationId, string messageId, string text, bool streaming)
    {
        ChatActivity? activity = chatActivitySystem.GetAllChatActivities()
            .FirstOrDefault(item => item.Character.Name == petId);
        string petName = activity?.Character.Name ?? petId;

        return new
        {
            id = messageId,
            role = "assistant",
            petId,
            petName,
            senderName = petName,
            conversationId,
            kind = IsGroupConversationId(conversationId) ? "group" : "dm",
            text,
            deliveryState = string.Empty,
            attachments = Array.Empty<QuickChatAttachment>(),
            streaming,
            provisional = true,
            createdAt = DateTimeOffset.Now,
        };
    }

    /// <summary>
    /// 一轮流式回显的临时状态。只活在前端：不写 histories、不落盘，
    /// 所以这个临时 id 和落盘编号不会撞，定稿时也不需要做任何替换。
    /// </summary>
    sealed class StreamingTurn
    {
        public StreamingTurn(string conversationId)
        {
            ConversationId = conversationId;
            MessageId = "stream:" + Guid.NewGuid().ToString("N");
        }

        public string ConversationId { get; }

        public string MessageId { get; }

        public StringBuilder Buffer { get; } = new();

        /// <summary>这一轮是否已经推过至少一个分片。没推过就说明气泡还没出现过，定稿时无需收尾。</summary>
        public bool Pushed { get; set; }

        public long LastPushTicks { get; set; }
    }

    void OnChatFinished(string petId, ChatContext context)
    {
        // 这一轮结束了，流式回显到此为止。先摘掉状态再分流：之后万一还有迟到的分片，
        // 也不会在定稿气泡后面又冒出一个临时气泡。
        lock (stateGate)
            streamingTurns.Remove(petId);

        // 按信封里的 kind 分流，而不是「有没有信封」——私聊现在也带信封（kind=dm），
        // 继续用「有信封 = 群聊」判断，会把私聊的回复误当成群聊轮次丢掉，私聊就再也不显示回复了。
        string? kind = QuickChatEnvelope.KindOf(context.UserMessage);

        // 群聊投递引发的轮次：模型的文字输出不是群消息，只有 QuickChatSend 才发言。
        // 所以这里既不把它塞进私聊流，也不做「已送达」收尾，只检查漏回复。
        if (kind != null && kind.Equals("group", StringComparison.OrdinalIgnoreCase))
        {
            CheckMissingGroupReply(petId, context);
            return;
        }

        // 工具返回值引发的轮次：模型那段文字是在「处理工具返回」，不是在跟谁说话，
        // 所以默认哪儿都不该落。
        //
        // 不拦的话它会落到下面的 `?? petId` —— 也就是**这个角色和主人的默认私聊**。
        // 实测就是这么漏的：小睦用 QuickChatDirect 给酒狐发了私聊，紧接着工具返回值
        // 唤起了下一轮，她顺手说了一句「……嗯，收到了。测试成功。谢谢你，酒狐。」，
        // 那句话就出现在了主人的窗口里 —— 主人看到的就是「她跟别人私聊，话却发给了我」。
        //
        // 工具返回值信封里没有 conv=（见 QuickChatEnvelope.EncodeToolResult），
        // 没法反推该落哪儿；而且真要发言就该调 QuickChatSend，那一轮才算发言。
        //
        // 但有一个必须放行的例外：**刚翻过某个会话**的那一轮。模型调 QuickChatRead
        // 之后，读结果要先投回去它才能接着说话，所以真正的回答是在这一轮说出来的 ——
        // 照上面一刀切掉的话，用户问了它一句，快聊里永远什么都不出现，
        // 而且它每轮都调 QuickChatRead 的话就永远不出现（看起来就是「卡死了」）。
        //
        // 落点由 ReadForAgent / OnChatSent 维护在 toolReplyTargets 里，这里只读不取：
        // 模型可能连着翻好几次（「还有更早的」），取走一次的话第二次之后又会开始丢。
        // 落点的生命周期由 OnChatSent 按 kind 管：私聊轮重新指向、群聊和无信封的注入清掉。
        if (kind != null && kind.Equals("tool", StringComparison.OrdinalIgnoreCase))
        {
            string? target;
            lock (stateGate)
                toolReplyTargets.TryGetValue(petId, out target);

            // 没有落点 → 维持原样整轮丢弃。绝不能回落到 `?? petId`：
            // 那正是上面那段注释里记录的跨频道事故。
            if (string.IsNullOrWhiteSpace(target) == false)
                _ = AddAssistantMessageAsync(petId, target, context);

            return;
        }

        // 私聊（kind=dm）以及不带信封的其它注入：文字输出就是发言，收进私聊流。
        //
        // 落到哪个会话必须看信封里的 conv=，不能默认用角色名：一个角色可以有多条私聊线
        // （默认私聊 ID = 角色名，用户「新建」出来的是 dm:小梦#2）。不认 conv= 的话，
        // 在 #2 里问的话，回答会跑到默认私聊里去。
        string conversationId = QuickChatEnvelope.ConversationOf(context.UserMessage) ?? petId;
        _ = AddAssistantMessageAsync(petId, conversationId, context);
    }

    /// <summary>
    /// 角色收到了群聊消息，却没有用 <c>QuickChatSend</c> 发言——提醒它补上。
    /// <para>
    /// 只在「用了别的标签但没回复」时补一刀：如果整段输出里连一个 <c>&lt;</c> 都没有，
    /// MessageFilter 的全局「调用丢失验证」会先提醒它，这里再补就是重复打扰。
    /// </para>
    /// </summary>
    void CheckMissingGroupReply(string petId, ChatContext context)
    {
        List<(string ConversationId, long UpToId)>? pending;
        lock (stateGate)
        {
            pending = pendingReplies.TryGetValue(petId, out List<(string ConversationId, long UpToId)>? value)
                ? value
                : null;
            pendingReplies.Remove(petId);
        }

        if (pending == null || pending.Count == 0)
            return;
        if (config.AutoCorrectMissingReply == false)
            return;
        if (context.CancellationToken.IsCancellationRequested)
            return;
        if (context.AIMessage?.Contains("QuickChatSend", StringComparison.OrdinalIgnoreCase) == true)
            return;
        if (context.AIMessage?.Contains('<') != true)
            return;

        if (channels.TryGetValue(petId, out QuickChatChannel? channel) == false)
            return;

        foreach ((string conversationId, long upToId) in pending)
        {
            // 先查存储：这一轮真的发过言就别再提醒。
            // 比在模型输出里找 "QuickChatSend" 字样可靠——输出被裁剪、工具名写法不同，
            // 都会让字符串匹配误判成「没发言」，于是多提醒一次，角色就把同一句话又说一遍。
            if (HasRepliedSince(conversationId, petId, upToId))
                continue;

            // 提醒里必须点名是哪个会话。以前只说「请用 QuickChatSend 发送」，
            // 模型不知道往哪发，就自己挑一个——于是跨频道乱发。
            string title = ResolveConversationTitle(conversationId, petId);
            channel.EnqueueNudge(conversationId,
                $"你在快聊的群聊「{title}」（conversation={conversationId}）收到了新消息，"
                + "但本次输出里没有 QuickChatSend，所以你的话没有发到群里。"
                + $"如果要发言，请用 QuickChatSend，conversation 填 {conversationId}；"
                + "如果确实不想回复，忽略本条即可。");
        }
    }

    /// <summary>该角色在 <paramref name="conversationId"/> 里，是否已经在 <paramref name="upToId"/> 之后发过言。</summary>
    bool HasRepliedSince(string conversationId, string petId, long upToId)
    {
        if (store == null)
            return false;

        try
        {
            ChatRecord? last = store.LastMessageUnchecked(conversationId);
            return last != null
                && last.Id > upToId
                && last.SenderId.Equals(QuickChatPrincipal.Agent(petId).Id, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    string ResolveConversationTitle(string conversationId, string petId)
    {
        try
        {
            QuickChatStoredConversation? conversation = store?.Find(conversationId);
            if (conversation != null)
                return QuickChatFormat.Title(conversation, QuickChatPrincipal.Agent(petId));
        }
        catch (Exception)
        {
        }

        return conversationId;
    }

    /// <param name="conversationId">
    /// 回复落在哪个会话。默认私聊就是角色名；多开私聊是 <c>dm:小梦#2</c> 这种。
    /// </param>
    async Task AddAssistantMessageAsync(string petId, string conversationId, ChatContext context)
    {
        try
        {
            List<string> attachmentSources = QuickChatContentFilter.ExtractQuickChatAttachmentSources(
                context.AIMessage, out string textWithoutAttachmentTags);
            string reply = QuickChatContentFilter.CleanAssistantText(textWithoutAttachmentTags);
            List<QuickChatAttachment> attachments = await ResolveQuickChatAttachmentsAsync(attachmentSources);

            if (string.IsNullOrWhiteSpace(reply) == false || attachments.Count > 0)
            {
                QuickChatPrincipal speaker = QuickChatPrincipal.Agent(petId);

                // 角色的回复同样落盘，这样重启后翻历史能看到完整的来回，而不只是用户那半边。
                // 深度取本角色当前的投递深度：角色互相私聊时它每轮 +1，到 MaxRelayDepth 自动断路。
                ChatRecord? record = TryPersistDirectMessage(
                    conversationId,
                    speaker,
                    reply,
                    GetConfigForPet(petId).SaveDirectMessages,
                    GetDeliveryDepth(petId));

                if (record != null)
                    AddMessage(petId, "assistant", reply, attachments, MessageIdOf(record), conversationId: conversationId);
                else
                    AddMessage(petId, "assistant", reply, attachments, conversationId: conversationId);

                // 会话里除自己外还有别的角色（角色互相私聊）：把这条话投给对方。
                // 只有「人类 ↔ 该角色」的单人私聊才不需要 —— 那种情况下另一端是人，推窗口就够了。
                if (record != null && store != null)
                {
                    QuickChatStoredConversation? conversation = store.Find(conversationId);
                    bool hasOtherAgents = conversation != null && conversation.Members
                        .Select(QuickChatPrincipal.Parse)
                        .Any(principal => principal.IsAgent && principal.SameAs(speaker) == false);

                    if (conversation != null && hasOtherAgents)
                        DeliverToChannels(conversation, record);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "QuickChat 处理 AI 回复失败：{PetId}", petId);
        }
        finally
        {
            // ChatFinished 表示文字回复已经结束。此时可能仍在等待 XmlFunctionCaller/GSV 语音等
            // ChatFinishedAsync 处理器，所以这里提前解除 QuickChat 的输入锁定。
            if (MarkRequestCompleted(petId))
                SendState();
        }
    }

    async Task<List<QuickChatAttachment>> ResolveQuickChatAttachmentsAsync(IEnumerable<string> sources)
    {
        List<QuickChatAttachment> attachments = new();
        if (sources.Any() == false)
            return attachments;

        string imageDirectory = Path.Combine(
            pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.QuickChat"),
            "Temp",
            "Images");
        Directory.CreateDirectory(imageDirectory);

        foreach (string source in sources.Where(item => string.IsNullOrWhiteSpace(item) == false).Distinct(StringComparer.Ordinal))
        {
            try
            {
                QuickChatAttachment? attachment = await ResolveQuickChatAttachmentAsync(source, imageDirectory);
                if (attachment != null)
                    attachments.Add(attachment);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "QuickChat 加载 AI 附件失败：{Source}", source);
            }
        }

        return attachments;
    }

    static async Task<QuickChatAttachment?> ResolveQuickChatAttachmentAsync(string source, string imageDirectory)
    {
        string value = source.Trim().Trim('"', '\'', '<', '>');
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string localPath;
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            string extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
                extension = ".bin";

            byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(value);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(pathBytes));
            localPath = Path.Combine(imageDirectory, "ai-" + hash + extension);

            if (File.Exists(localPath) == false)
            {
                byte[] data = await ImageHttpClient.GetByteArrayAsync(uri);
                if (data.Length == 0)
                    return null;
                await File.WriteAllBytesAsync(localPath, data);
            }
        }
        else
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? fileUri) && fileUri.IsFile)
                value = fileUri.LocalPath;

            localPath = Path.GetFullPath(value);
            if (File.Exists(localPath) == false)
                return null;
        }

        if (File.GetAttributes(localPath).HasFlag(FileAttributes.Directory))
            return null;

        bool isImage = IsImagePath(localPath);
        return new QuickChatAttachment {
            Kind = isImage ? "image" : "file",
            Path = localPath,
            Name = Path.GetFileName(localPath),
            ThumbnailPath = isImage ? CreateScreenshotThumbnail(localPath, imageDirectory) : string.Empty
        };
    }

    static bool IsImagePath(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" => true,
            _ => false
        };
    }

    bool MarkRequestCompleted(string petId, string? requestId = null)
    {
        lock (stateGate)
        {
            if (string.IsNullOrWhiteSpace(requestId) == false &&
                busyRequestIds.TryGetValue(petId, out string? currentId) &&
                string.Equals(currentId, requestId, StringComparison.Ordinal) == false)
            {
                return false;
            }

            busyRequestIds.Remove(petId);
            return busyPetIds.Remove(petId);
        }
    }

    // ────────────────────────── 发送登记（归属判定） ──────────────────────────

    /// <summary>
    /// 登记一条即将发出的用户消息，并<b>立刻在本地回显</b>。
    /// <para>
    /// 先回显再等回执，是「我看得见自己说了什么」的唯一可靠保证：
    /// 消息后面被谁打断、被谁挤掉，都不影响它已经出现在界面上，
    /// 顶多把状态从「发送中」翻成「未送达」。
    /// </para>
    /// </summary>
    /// <param name="conversationId">
    /// 这条消息落在哪个会话。留空 = 私聊且 ID 就是角色名（旧行为）。
    /// </param>
    void RegisterOutgoingMessage(
        string petId,
        string requestId,
        string displayText,
        List<QuickChatAttachment> attachments,
        ChatRecord? record = null,
        string? conversationId = null)
    {
        // 落盘成功就用存储给的编号当消息 ID：这样「历史里读回来的」和「刚发出去的」
        // 是同一条，前端按 ID 去重时才不会出现两个气泡。
        string messageId = record != null ? MessageIdOf(record) : Guid.NewGuid().ToString("N");
        string key = string.IsNullOrWhiteSpace(conversationId) ? petId : conversationId;

        lock (stateGate)
        {
            outgoingByRequest[requestId] = new PendingOutgoing
            {
                RequestId = requestId,
                PetId = petId,
                MessageId = messageId,
                ConversationId = key,
                DisplayText = displayText ?? string.Empty,
                RegisteredAt = DateTimeOffset.Now
            };

            // 同一角色只有一张「当前提货单」。被顶掉的那张仍留在 outgoingByRequest 里，
            // 由它自己的 finally 收尾，标成「未送达」。
            outgoingRequestByPet[petId] = requestId;
        }

        AddMessage(petId, "user", displayText, attachments, messageId, "sending", key);
    }

    /// <summary>认领提货单：只有登记过、且内容对得上的那一条才算快聊自己发的。</summary>
    bool ClaimPendingOutgoing(string petId, string rawMessage, out PendingOutgoing? claimed)
    {
        claimed = null;

        string requestId;
        PendingOutgoing pending;
        lock (stateGate)
        {
            if (outgoingRequestByPet.TryGetValue(petId, out string? current) == false ||
                outgoingByRequest.TryGetValue(current, out PendingOutgoing? value) == false)
            {
                return false;
            }

            requestId = current;
            pending = value;
        }

        // 登记过期：防止一条迟迟不回执的消息，把很久之后的系统消息认成自己的。
        if (DateTimeOffset.Now - pending.RegisteredAt > OutgoingClaimWindow)
        {
            lock (stateGate)
            {
                outgoingByRequest.Remove(requestId);
                if (outgoingRequestByPet.TryGetValue(petId, out string? still) &&
                    string.Equals(still, requestId, StringComparison.Ordinal))
                {
                    outgoingRequestByPet.Remove(petId);
                }
            }

            return false;
        }

        string cleaned = QuickChatContentFilter.CleanOutgoingDisplayText(rawMessage, Array.Empty<string>());
        if (LooksLikeRegisteredOutgoing(pending, cleaned) == false)
            return false;

        lock (stateGate)
        {
            outgoingByRequest.Remove(requestId);
            if (outgoingRequestByPet.TryGetValue(petId, out string? still) &&
                string.Equals(still, requestId, StringComparison.Ordinal))
            {
                outgoingRequestByPet.Remove(petId);
            }
        }

        pending.Claimed = true;
        claimed = pending;
        return true;
    }

    /// <summary>
    /// 内容核对只是兜底，登记本身才是主依据。
    /// 之所以还要看一眼正文：<c>Poke</c> 的冲刷和用户的发送可能抢同一个信号量，
    /// 万一别人的注入先到，不能让它的回执把用户的提货单用掉。
    /// </summary>
    static bool LooksLikeRegisteredOutgoing(PendingOutgoing pending, string cleaned)
    {
        // 纯附件消息的展示文本可能被清洗成空（或占位符），此时只能信登记。
        if (string.IsNullOrEmpty(cleaned))
            return true;
        if (pending.DisplayText.Length == 0)
            return true;

        // 正文被 MessageFilter 截断过时，前缀仍然对得上。
        string probe = pending.DisplayText.Length <= 64
            ? pending.DisplayText
            : pending.DisplayText[..64];

        return cleaned.Contains(probe, StringComparison.Ordinal)
            || pending.DisplayText.Contains(cleaned, StringComparison.Ordinal);
    }

    /// <summary>请求收尾：仍未认领就说明这条没进对话上下文（被打断、被系统消息挤掉、或模型报错）。</summary>
    void FinalizeOutgoing(string petId, string requestId)
    {
        string? messageId = null;
        string? conversationId = null;

        lock (stateGate)
        {
            if (outgoingByRequest.TryGetValue(requestId, out PendingOutgoing? pending))
            {
                if (pending.Claimed == false)
                {
                    messageId = pending.MessageId;
                    conversationId = pending.ConversationId;
                }

                outgoingByRequest.Remove(requestId);
            }

            if (outgoingRequestByPet.TryGetValue(petId, out string? still) &&
                string.Equals(still, requestId, StringComparison.Ordinal))
            {
                outgoingRequestByPet.Remove(petId);
            }
        }

        if (messageId != null)
            UpdateOutgoingState(string.IsNullOrWhiteSpace(conversationId) ? petId : conversationId, messageId, "failed");
    }

    /// <summary>按消息 Id 翻状态，并推给前端（前端按 Id 替换而不是追加）。</summary>
    /// <param name="conversationId">消息所在的会话。多开私聊时它不等于角色名。</param>
    void UpdateOutgoingState(string conversationId, string messageId, string state)
    {
        QuickChatMessage? updated = null;

        lock (stateGate)
        {
            if (histories.TryGetValue(conversationId, out List<QuickChatMessage>? list))
            {
                int index = list.FindIndex(item => string.Equals(item.Id, messageId, StringComparison.Ordinal));
                if (index >= 0 && string.Equals(list[index].DeliveryState, state, StringComparison.Ordinal) == false)
                {
                    list[index].DeliveryState = state;
                    updated = list[index];
                }
            }
        }

        if (updated != null && string.Equals(selectedConversationId, conversationId, StringComparison.OrdinalIgnoreCase))
            window?.Send("message", updated);
    }

    /// <summary>把待发附件变成可渲染的附件卡片（缩略图失败不影响原图可见）。</summary>
    List<QuickChatAttachment> BuildOutgoingAttachments(IEnumerable<string> paths)
    {
        List<QuickChatAttachment> attachments = new();
        List<string> items = paths
            .Where(item => string.IsNullOrWhiteSpace(item) == false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (items.Count == 0)
            return attachments;

        string imageDirectory = Path.Combine(
            pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.QuickChat"),
            "Temp",
            "Images");

        foreach (string path in items)
        {
            QuickChatAttachment attachment = new()
            {
                Kind = IsImagePath(path) ? "image" : "file",
                Path = path,
                Name = Path.GetFileName(path),
                ThumbnailPath = string.Empty
            };

            try
            {
                if (IsImagePath(path))
                    attachment.ThumbnailPath = CreateScreenshotThumbnail(path, imageDirectory);
            }
            catch
            {
                // 缩略图失败不影响原图可见。
            }

            attachments.Add(attachment);
        }

        return attachments;
    }

    void OnRendererMessage(string type, System.Text.Json.JsonElement payload)
    {
        switch (type)
        {
            case "ready":
                EnsureSelectedConversation();
                SendState();
                SendSelectedHistory();
                break;

            case "select":
                // 前端可能给 conversationId（新）或 id（旧），两个键都收。
                if (payload.TryGetProperty("conversationId", out System.Text.Json.JsonElement conversationElement))
                    SelectConversation(conversationElement.GetString());
                else if (payload.TryGetProperty("id", out System.Text.Json.JsonElement legacyIdElement))
                    SelectConversation(legacyIdElement.GetString());
                break;

            case "select-conversation":
                if (payload.TryGetProperty("conversationId", out System.Text.Json.JsonElement selectConversationElement))
                    SelectConversation(selectConversationElement.GetString());
                break;

            case "send":
                // 会话维度发送：conversationId 是私聊角色名或 "group:群名"。
                // 旧前端发 petId，这里兼容一下。
                string? sendConversationId =
                    payload.TryGetProperty("conversationId", out System.Text.Json.JsonElement sendConversationElement)
                        ? sendConversationElement.GetString()
                        : payload.TryGetProperty("petId", out System.Text.Json.JsonElement sendPetElement)
                            ? sendPetElement.GetString()
                            : null;

                if (string.IsNullOrWhiteSpace(sendConversationId) == false &&
                    payload.TryGetProperty("text", out System.Text.Json.JsonElement textElement))
                {
                    List<string> attachmentPaths = new();
                    if (payload.TryGetProperty("attachmentPaths", out System.Text.Json.JsonElement attachmentPathsElement) &&
                        attachmentPathsElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (System.Text.Json.JsonElement item in attachmentPathsElement.EnumerateArray())
                        {
                            string? attachmentPath = item.GetString();
                            if (string.IsNullOrWhiteSpace(attachmentPath) == false)
                                attachmentPaths.Add(attachmentPath);
                        }
                    }

                    _ = SendMessageAsync(sendConversationId, textElement.GetString(), attachmentPaths);
                }
                break;

            case "resize":
                if (window != null && window.IsWindowDragging == false && payload.TryGetProperty("height", out System.Text.Json.JsonElement heightElement))
                {
                    bool compact = payload.TryGetProperty("compact", out System.Text.Json.JsonElement compactElement)
                        && compactElement.GetBoolean();
                    int minHeight = compact
                        ? Math.Min(config.MinWindowHeight, 56)
                        : config.MinWindowHeight;

                    int desiredHeight = heightElement.GetInt32();

                    // 群聊允许长得更高：自适应高度不能卡在私聊的 WindowHeight 上。
                    int maxHeight = SelectedIsGroup
                        ? Math.Max(config.GroupWindowHeight, config.WindowHeight)
                        : config.WindowHeight;

                    // 用户手动拖过就以他拖的为准：渲染层的自适应只允许往上长，不许缩回去，
                    // 上限也要抬到他拖到的高度 —— 否则「拉得比配置上限还高」会被当场压回去。
                    // 少了这一道，用户拉开一次、切个会话就被按内容重新算小，表现为「不记录窗口大小」。
                    if (compact == false && window.SavedSize(SelectedIsGroup) is { } savedSize)
                    {
                        if (desiredHeight < savedSize.Height)
                            desiredHeight = savedSize.Height;
                        if (maxHeight < savedSize.Height)
                            maxHeight = savedSize.Height;
                    }

                    if (compact)
                        _ = window.SetCompactHeightAsync(desiredHeight, minHeight);
                    else
                        _ = window.SetAdaptiveHeightAsync(
                            config.EffectiveWindowWidth,
                            desiredHeight,
                            minHeight,
                            maxHeight,
                            config.AutoFitHeight);
                }
                break;

            case "resize-start":
                if (window != null && payload.TryGetProperty("edge", out System.Text.Json.JsonElement edgeElement))
                {
                    _ = window.BeginManualResizeAsync(edgeElement.GetString() ?? "se");
                }
                break;

            case "resize-move":
                _ = window?.UpdateManualResizeAsync();
                break;

            case "resize-end":
                window?.EndManualResize(SelectedIsGroup);
                break;

            case "window-drag-start":
                _ = window?.BeginWindowDragAsync();
                break;

            case "window-drag-move":
                _ = window?.UpdateWindowDragAsync();
                break;

            case "window-drag-end":
                window?.EndWindowDrag();
                break;

            case "sidebar":
            {
                // 侧栏展开/收起：窗口向左加宽并左移，让面板「往外弹」而不是挤窄聊天区。
                if (window == null ||
                    payload.TryGetProperty("expanded", out System.Text.Json.JsonElement sidebarElement) == false)
                {
                    break;
                }

                bool sidebarExpanded = sidebarElement.GetBoolean();
                _ = window.SetSidebarExpandedAsync(sidebarExpanded);

                // 只有「用户自己点了轨道」才把这次状态记成偏好（前端会带 remember:true）。
                // 选中会话后自动收回、最小化成输入条、关掉群聊整列隐藏这些都会让侧栏收起，
                // 那是临时的界面行为，不该覆盖用户「我要它一直开着」的选择。
                if (payload.TryGetProperty("remember", out System.Text.Json.JsonElement rememberElement) &&
                    rememberElement.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    window.RememberSidebarExpanded(sidebarExpanded);
                }
                break;
            }

            case "clear":
            {
                string? clearConversationId =
                    payload.TryGetProperty("conversationId", out System.Text.Json.JsonElement clearIdElement)
                        ? clearIdElement.GetString()
                        : null;

                // 缺省 true：弹窗里的勾选框默认是勾上的；老前端不发这个字段时也按「含原始记录」处理。
                bool purgeOriginal = true;
                if (payload.TryGetProperty("purge", out System.Text.Json.JsonElement purgeElement) &&
                    (purgeElement.ValueKind == System.Text.Json.JsonValueKind.True ||
                     purgeElement.ValueKind == System.Text.Json.JsonValueKind.False))
                {
                    purgeOriginal = purgeElement.GetBoolean();
                }

                ClearDisplayedHistory(clearConversationId, purgeOriginal);
                break;
            }

            case "conversation-create":
            {
                // 以某个会话为模板，新开一条独立的对话线（私聊 dm:小梦#2 / 群聊 group:小群A#2）。
                CreateConversation(
                    payload.TryGetProperty("conversationId", out System.Text.Json.JsonElement createElement)
                        ? createElement.GetString()
                        : null);
                break;
            }

            case "conversation-delete":
                if (payload.TryGetProperty("conversationId", out System.Text.Json.JsonElement deleteElement))
                    DeleteConversation(deleteElement.GetString());
                break;

            case "load-older":
                LoadOlderHistory();
                break;

            case "screenshot-request":
                _ = CaptureScreenshotAsync(
                    payload.TryGetProperty("petId", out System.Text.Json.JsonElement screenshotPetElement)
                        ? screenshotPetElement.GetString()
                        : SelectedPetId,
                    false);
                break;

            case "screenshot-region-request":
                _ = CaptureScreenshotAsync(
                    payload.TryGetProperty("petId", out System.Text.Json.JsonElement screenshotRegionPetElement)
                        ? screenshotRegionPetElement.GetString()
                        : SelectedPetId,
                    true);
                break;

            case "screenshot-taken":
                if (payload.TryGetProperty("petId", out System.Text.Json.JsonElement screenshotTakenPetElement) &&
                    payload.TryGetProperty("path", out System.Text.Json.JsonElement screenshotPathElement))
                {
                    string screenshotPetId = screenshotTakenPetElement.GetString() ?? string.Empty;
                    string screenshotPath = screenshotPathElement.GetString() ?? string.Empty;
                    string screenshotInfo = payload.TryGetProperty("text", out System.Text.Json.JsonElement screenshotTextElement)
                        ? screenshotTextElement.GetString() ?? "QuickChat全屏截图"
                        : "QuickChat全屏截图";

                    if (string.IsNullOrWhiteSpace(screenshotPetId) == false && string.IsNullOrWhiteSpace(screenshotPath) == false)
                    {
                        window?.ShowAndFocus();
                        string outgoingScreenshot = "用户发送了一张图片：\n" + screenshotPath +
                            "\n\n用户文字：\n" + screenshotInfo;
                        _ = SendMessageAsync(screenshotPetId, outgoingScreenshot, new[] { screenshotPath });
                    }
                }
                break;

            case "screenshot-failed":
                if (payload.TryGetProperty("petId", out System.Text.Json.JsonElement failedScreenshotPetElement))
                {
                    string failedScreenshotPetId = failedScreenshotPetElement.GetString() ?? string.Empty;
                    string failedScreenshotMessage = payload.TryGetProperty("message", out System.Text.Json.JsonElement failedMessageElement)
                        ? failedMessageElement.GetString() ?? "未知错误"
                        : "未知错误";
                    window?.ShowAndFocus();
                    if (string.IsNullOrWhiteSpace(failedScreenshotPetId) == false)
                        AddSystemMessage(failedScreenshotPetId, "截图失败：" + failedScreenshotMessage);
                }
                break;

            case "hide":
                window?.Hide();
                break;
        }
    }

    void LogRegionDebug(string message, Exception? exception = null)
    {
        try
        {
            string debugDirectory = Path.Combine(
                pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.QuickChat"),
                "Temp");
            Directory.CreateDirectory(debugDirectory);
            string debugPath = Path.Combine(debugDirectory, "region-debug.log");
            string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
            if (exception != null)
                text += Environment.NewLine + exception;
            File.AppendAllText(debugPath, text + Environment.NewLine);
        }
        catch
        {
            // 诊断日志失败不能影响截图。
        }
    }

    async Task CaptureScreenshotAsync(string? petId, bool region)
    {
        LogRegionDebug($"Capture requested: region={region}, petId={petId ?? "<null>"}, windowNull={window == null}");
        if (window == null || string.IsNullOrWhiteSpace(petId))
        {
            LogRegionDebug($"Capture rejected: windowNull={window == null}, petId={petId ?? "<null>"}");
            window?.Send("screenshot-failed", new { petId, message = "当前没有可发送的桌宠。" });
            return;
        }

        IQuickChatWindow? screenshotWindow = window;
        try
        {
            string imageDirectory = Path.Combine(
                pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.QuickChat"),
                "Temp",
                "Images");
            Directory.CreateDirectory(imageDirectory);

            LogRegionDebug("Capture window hiding");
            screenshotWindow.Hide();
            await Task.Delay(260);

            IntPtr previousDpiContext = IntPtr.Zero;
            try
            {
                try
                {
                    previousDpiContext = SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
                }
                catch (EntryPointNotFoundException)
                {
                    // Older Windows fallback uses logical screen bounds below.
                }

                if (previousDpiContext == IntPtr.Zero ||
                    TryGetPhysicalMonitorBounds(out System.Drawing.Rectangle monitorBounds) == false)
                {
                    System.Windows.Forms.Screen screen =
                        System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
                    monitorBounds = screen.Bounds;
                }

                LogRegionDebug($"Monitor bounds: {monitorBounds}");
                System.Drawing.Rectangle captureBounds = monitorBounds;
                if (region)
                {
                    LogRegionDebug("Region selection starting");
                    using System.Drawing.Bitmap screenPreview =
                        new(monitorBounds.Width, monitorBounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (System.Drawing.Graphics previewGraphics = System.Drawing.Graphics.FromImage(screenPreview))
                    {
                        previewGraphics.CopyFromScreen(
                            monitorBounds.Left,
                            monitorBounds.Top,
                            0,
                            0,
                            monitorBounds.Size);
                    }

                    System.Drawing.Rectangle? selectedBounds = await SelectScreenRegionAsync(
                        monitorBounds,
                        message => LogRegionDebug(message),
                        screenPreview);
                    LogRegionDebug($"Region selection returned: hasValue={selectedBounds.HasValue}, value={selectedBounds?.ToString() ?? "<null>"}");
                    if (selectedBounds == null)
                    {
                        screenshotWindow.ShowAndFocus();
                        screenshotWindow.Send("screenshot-complete", new { mode = "region", canceled = true });
                        return;
                    }

                    captureBounds = selectedBounds.Value;
                    await Task.Delay(90);
                }

                string modeName = region ? "区域截图" : "全屏截图";
                DateTime capturedAt = DateTime.Now;
                string screenshotInfo = $"QuickChat{modeName} {capturedAt:yyyy-MM-dd HH:mm:ss}";
                string filePath = Path.Combine(
                    imageDirectory,
                    $"QuickChat{modeName}_{capturedAt:yyyy-MM-dd_HH-mm-ss}.png");

                using (System.Drawing.Bitmap bitmap =
                    new System.Drawing.Bitmap(captureBounds.Width, captureBounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(captureBounds.Left, captureBounds.Top, 0, 0, captureBounds.Size);
                    bitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
                }

                LogRegionDebug($"Screenshot saved: {filePath}");
                CreateScreenshotThumbnail(filePath, imageDirectory);
                AddSystemMessage(petId, $"{modeName}已保存，正在上传给 AI…");
                screenshotWindow.ShowAndFocus();
                screenshotWindow.Send("screenshot-uploading", new { mode = region ? "region" : "fullscreen" });

                string outgoingScreenshot = "用户发送了一张图片：\n" + filePath +
                    "\n\n用户文字：\n" + screenshotInfo;
                await SendMessageAsync(petId, outgoingScreenshot, new[] { filePath });
                screenshotWindow.Send("screenshot-complete");
            }
            finally
            {
                if (previousDpiContext != IntPtr.Zero)
                    _ = SetThreadDpiAwarenessContext(previousDpiContext);
            }
        }
        catch (Exception exception)
        {
            LogRegionDebug("Capture failed", exception);
            logger.LogError(exception, "QuickChat 截图失败");
            screenshotWindow.ShowAndFocus();
            screenshotWindow.Send("screenshot-failed", new
            {
                petId,
                message = exception.Message
            });
        }
    }

    static bool TryGetPhysicalMonitorBounds(out System.Drawing.Rectangle bounds)
    {
        bounds = System.Drawing.Rectangle.Empty;

        if (GetCursorPos(out NativePoint cursor) == false)
            return false;

        IntPtr monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return false;

        MonitorInfo info = new()
        {
            CbSize = (uint)Marshal.SizeOf<MonitorInfo>()
        };

        if (GetMonitorInfoW(monitor, ref info) == false)
            return false;

        bounds = System.Drawing.Rectangle.FromLTRB(
            info.RcMonitor.Left,
            info.RcMonitor.Top,
            info.RcMonitor.Right,
            info.RcMonitor.Bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    static string CreateScreenshotThumbnail(string sourcePath, string imageDirectory)
    {
        byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath));
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(pathBytes))
            .ToLowerInvariant()[..16];
        string thumbnailPath = Path.Combine(imageDirectory, "thumb-" + hash + ".jpg");

        using System.Drawing.Bitmap source = new(sourcePath);
        double scale = Math.Min(1.0, 320.0 / Math.Max(source.Width, Math.Max(source.Height, 1)));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using System.Drawing.Bitmap thumbnail = new(width, height);
        using System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(thumbnail);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);
        thumbnail.Save(thumbnailPath, System.Drawing.Imaging.ImageFormat.Jpeg);
        return thumbnailPath;
    }

    static async Task<System.Drawing.Rectangle?> SelectScreenRegionAsync(
        System.Drawing.Rectangle monitorBounds,
        Action<string>? log,
        System.Drawing.Bitmap screenPreview)
    {
        TaskCompletionSource<System.Drawing.Rectangle?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread staThread = new(() => {
            try
            {
                log?.Invoke("Region STA thread started");
                System.Windows.Forms.Application.EnableVisualStyles();
                IntPtr previousDpiContext = SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
                try
                {
                    log?.Invoke($"Region form creating: {monitorBounds}");
                    using RegionSelectionForm form = new(monitorBounds, screenPreview);
                    log?.Invoke("Region form showing dialog");
                    System.Windows.Forms.DialogResult result = form.ShowDialog();
                    log?.Invoke($"Region form dialog returned: {result}, selected={form.SelectedRectangle}");
                    completion.SetResult(result == System.Windows.Forms.DialogResult.OK ? form.SelectedRectangle : null);
                }
                finally
                {
                    if (previousDpiContext != IntPtr.Zero)
                        _ = SetThreadDpiAwarenessContext(previousDpiContext);
                }
            }
            catch (Exception exception)
            {
                log?.Invoke("Region form failed: " + exception);
                completion.SetException(exception);
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        return await completion.Task;
    }

    static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);
    const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        public uint CbSize;
        public NativeRect RcMonitor;
        public NativeRect RcWork;
        public uint Flags;
    }

    /// <summary>
    /// 当前会话对应的「收件角色」。
    /// 私聊就是角色本身；群聊截图/兜底需要一个具体角色来收图，取群里第一个仍在激活的成员。
    /// </summary>
    string? SelectedPetId
    {
        get
        {
            string? conversationId = selectedConversationId;
            if (string.IsNullOrWhiteSpace(conversationId))
                return null;

            if (IsGroupConversationId(conversationId) == false)
            {
                // 私聊：会话 ID 不一定等于角色名（多开私聊是 dm:小梦#2、AI↔AI 私聊是 dm:甲|乙），
                // 拿会话 ID 去找角色会找不到，截图就发不出去了。从成员里取第一个角色。
                List<string> agents = AgentsOfConversation(conversationId);
                return agents.Count > 0 ? agents[0] : conversationId;
            }

            List<string> activeNames = chatActivitySystem.GetAllChatActivities()
                .Select(item => item.Character.Name)
                .ToList();

            QuickChatConversation? group = BuildConversations()
                .FirstOrDefault(item => string.Equals(item.Id, conversationId, StringComparison.OrdinalIgnoreCase));

            string? member = group?.Members
                .FirstOrDefault(name => activeNames.Contains(name, StringComparer.OrdinalIgnoreCase));

            return member ?? activeNames.FirstOrDefault();
        }
    }

    async Task SendMessageAsync(string? conversationId, string? rawText, IEnumerable<string>? attachmentPaths = null)
    {
        string text = rawText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(conversationId) || text.Length == 0)
            return;

        // 群聊，以及「两个以上角色」的私聊（角色互相私聊）：人类发言落到共享会话里，
        // 再投递给每个成员。这类会话没有单一的「对话对象」，不能只跟其中一个 ChatAsync
        // ——那样另一个就永远收不到。
        List<string> directAgents = AgentsOfConversation(conversationId);
        if (IsGroupConversationId(conversationId) || directAgents.Count > 1)
        {
            await SendSharedMessageAsync(conversationId, text, attachmentPaths);
            return;
        }

        // 到这里一定是「人类 ↔ 单个角色」的私聊。角色名从会话成员取，不直接用会话 ID：
        // 用户「新建」出来的多开私聊 ID 形如 dm:小梦#2，拿它去找角色会找不到。
        string petId = directAgents.Count == 1 ? directAgents[0] : conversationId;

        ChatActivity? activity = chatActivitySystem.GetAllChatActivities()
            .FirstOrDefault(item => item.Character.Name == petId);

        if (activity == null)
        {
            AddSystemMessage(petId, "该桌宠已停止活动。", conversationId);
            return;
        }

        QuickChatConfig messageConfig = GetConfigForPet(petId);

        SyncStoreConversations();

        string requestId = Guid.NewGuid().ToString("N");
        lock (stateGate)
        {
            busyPetIds.Add(petId);
            busyRequestIds[petId] = requestId;
        }

        SendState();

        try
        {
            List<string> pendingPaths = (attachmentPaths ?? Enumerable.Empty<string>())
                .Where(item => string.IsNullOrWhiteSpace(item) == false)
                .Select(item => Path.GetFullPath(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string outgoing = messageConfig.MarkMessageSource
                ? "[消息来源(QuickChat)]" + text
                : text;


            QuickChatScreenshotImageMode imageMode = NormalizeScreenshotImageMode(messageConfig.ScreenshotImageMode);
            List<string> validImagePaths = pendingPaths
                .Where(path => File.Exists(path) && IsImagePath(path))
                .ToList();

            List<(byte[] Data, string MimeType)> modelImages = new();
            bool anyImageCompressed = false;
            if (validImagePaths.Count > 0 && imageMode != QuickChatScreenshotImageMode.仅路径)
            {
                foreach (string imagePath in validImagePaths)
                {
                    (byte[] data, string mimeType, bool compressed) =
                        await LoadModelImageForModelAsync(imagePath);
                    modelImages.Add((data, mimeType));
                    anyImageCompressed |= compressed;
                }

                if (anyImageCompressed)
                    AddSystemMessage(petId, "图片 base64 超过 1MB，已压缩后发送给 AI。", conversationId);
            }

            // 展示层永远不带路径元数据；AI 请求里的 modelText 才带路径。
            string outgoingDisplay = QuickChatContentFilter.CleanOutgoingDisplayText(outgoing, pendingPaths);

            // 登记 + 本地乐观回显。
            // 顺序很重要：必须在发起对话之前登记，否则 ChatSent 可能先到、认领不到；
            // 回显也放在这里，因为此时展示文本和附件都已经确定。
            // 之后这条消息能不能送达都不影响「我看得见自己说了什么」。
            List<QuickChatAttachment> outgoingAttachments = BuildOutgoingAttachments(pendingPaths);

            // 私聊同样走存储：消息编号、未读、分页、落盘全都复用同一套，
            // 前端也就不需要区分「这条 ID 是 GUID 还是编号」。
            ChatRecord? outgoingRecord = TryPersistDirectMessage(
                conversationId, QuickChatPrincipal.Human, outgoingDisplay, messageConfig.SaveDirectMessages);
            RegisterOutgoingMessage(
                petId, requestId, outgoingDisplay, outgoingAttachments, outgoingRecord, conversationId);

            // 私聊也要带信封。没有它，模型收到的就是一段不带任何频道标记的裸文本，
            // 而系统提示词里写着「要在快聊里发言必须调用 QuickChatSend」——
            // 模型于是以为自己在群聊，把本该只出现在私聊的回复也发进群聊。
            // 带 kind=dm 之后，模型一眼就能看出「这是私聊，直接回文字就行」。
            //
            // conv= 必须是会话 ID 而不是角色名：一个角色可以有多条私聊线
            // （默认私聊 ID = 角色名，用户「新建」的是 dm:小梦#2）。角色的回复靠 conv=
            // 落回原会话，写错角色名的话，在 #2 里问的话，回答会跑到默认私聊去。
            string outgoingEnveloped = QuickChatEnvelope.Compose(
                QuickChatEnvelope.EncodeHuman(conversationId, "dm", outgoingRecord?.Id ?? 0, "主人"),
                outgoing);

            string modelText = outgoingEnveloped;
            if (validImagePaths.Count > 0 && imageMode != QuickChatScreenshotImageMode.仅路径)
            {
                modelText = BuildImageModelText(outgoingEnveloped, validImagePaths);
            }

            if (validImagePaths.Count > 0 && imageMode == QuickChatScreenshotImageMode.临时分析)
            {
                // 临时模式不调用 ChatAsync，避免图片消息永久进入主历史。
                OnChatSent(petId, outgoing);
                string analysis = await SendTemporaryScreenshotAsync(activity, modelText, modelImages);
                if (string.IsNullOrWhiteSpace(analysis))
                {
                    AddSystemMessage(petId, "模型没有返回图片分析结果。", conversationId);
                }
                else
                {
                    await AddAssistantMessageAsync(petId, conversationId, new ChatContext {
                        UserMessage = modelText,
                        AIMessage = analysis,
                        CancellationToken = CancellationToken.None
                    });
                }
            }
            else
            {
                ChatMessageContent outgoingMessage;
                if (modelImages.Count > 0 && imageMode == QuickChatScreenshotImageMode.多模态)
                {
                    ChatMessageContentItemCollection items = new()
                    {
                        new TextContent(modelText)
                    };
                    foreach ((byte[] data, string mimeType) in modelImages)
                        items.Add(new ImageContent(data, mimeType));

                    // ChatBot.ChatSent 会读取 Content，所以这里保持干净展示文本。
                    // 带路径的 modelText 已作为 TextContent item 发给模型。
                    outgoingMessage = new ChatMessageContent(AuthorRole.User, items) {
                        Content = outgoingDisplay
                    };
                }
                else
                {
                    outgoingMessage = new ChatMessageContent(AuthorRole.User, modelText);
                }

                var result = await activity.ChatBot.ChatAsync(outgoingMessage, true);

                if (result.Exception != null)
                {
                    logger.LogError(result.Exception, "QuickChat 对话失败：{PetId}", petId);
                    AddSystemMessage(petId, "回复失败，请检查模型或网络配置。", conversationId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 用户发送新消息时会通过 ChatAsync(breakLast:true) 打断旧请求，
            // 旧请求被取消属于正常流程，不应显示为回复失败。
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "QuickChat 对话异常：{PetId}", petId);
            AddSystemMessage(petId, "回复失败，请查看 Alife 日志。", conversationId);
        }
        finally
        {
            // 请求收尾：仍未认领的消息标成「未送达」。
            // 被后来的消息打断、被 poke 冲刷挤掉、或还没进上下文就出错，都会走到这里。
            // 注意这不影响气泡本身已经显示过 —— 只是状态从「发送中」翻成「未送达」。
            FinalizeOutgoing(petId, requestId);

            bool changed = MarkRequestCompleted(petId, requestId);
            if (changed)
                SendState();
        }
    }

    // ────────────────────────── 群聊 ──────────────────────────

    /// <summary>
    /// 人类在「多人会话」里发言：落盘 → 本地回显 → 投递给每个成员。
    /// <para>
    /// 多人会话包括群聊，也包括角色互相私聊的那种私聊（两个角色在场）。
    /// 和单角色私聊最大的不同是「一次发言对应多个接收者」：单角色私聊是 <c>ChatAsync</c>
    /// 直接问那一个角色，多人会话则必须先把消息写进共享会话，再让各角色自己的通道决定
    /// 要不要被叫醒（门槛见 <see cref="QuickChatGate"/>）。所以这里不等任何人的回复，发完即返回。
    /// </para>
    /// </summary>
    async Task SendSharedMessageAsync(string conversationId, string text, IEnumerable<string>? attachmentPaths)
    {
        SyncStoreConversations();

        if (store == null)
        {
            AddMessage(UserSpeakerId, "system", "消息存储不可用，暂时无法发言。", conversationId: conversationId);
            return;
        }

        QuickChatStoredConversation? conversation = store.Find(conversationId);
        if (conversation == null)
        {
            AddMessage(UserSpeakerId, "system", "这个会话还没有准备好（可能刚被关闭，或没有成员）。", conversationId: conversationId);
            return;
        }

        List<string> pendingPaths = (attachmentPaths ?? Enumerable.Empty<string>())
            .Where(item => string.IsNullOrWhiteSpace(item) == false)
            .Select(item => Path.GetFullPath(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string outgoing = config.MarkMessageSource ? "[消息来源(QuickChat)]" + text : text;
        string display = QuickChatContentFilter.CleanOutgoingDisplayText(outgoing, pendingPaths);
        if (string.IsNullOrWhiteSpace(display))
            display = text;

        // 附件只做本地展示；群聊投递目前只送文本，避免把图片路径糊进别人的上下文。
        List<QuickChatAttachment> attachments = BuildOutgoingAttachments(pendingPaths);

        ChatRecord record;
        try
        {
            record = store.Send(
                conversationId,
                QuickChatPrincipal.Human,
                display,
                depth: 0,
                contentType: "text",
                // 多人私聊（角色互相私聊）跟着「保存私聊记录」走，群聊跟着「保存群聊记录」走。
                persist: conversation.IsGroup ? config.SaveGroupMessages : config.SaveDirectMessages);
        }
        catch (Exception exception)
        {
            QuickChatLog.Write("会话发言失败：" + exception.Message);
            AddMessage(UserSpeakerId, "system", "发言失败：" + exception.Message, conversationId: conversationId);
            return;
        }

        AddRecordMessage(record, "user", attachments, "sent");
        DeliverToChannels(conversation, record);
        await Task.CompletedTask;
    }

    /// <summary>把一条落盘记录转成前端气泡。私聊/群聊共用，保证两条路径的显示字段一致。</summary>
    void AddRecordMessage(ChatRecord record, string role, List<QuickChatAttachment>? attachments, string? deliveryState)
    {
        AddMessage(
            PetIdOf(record.Sender),
            role,
            record.Text,
            attachments,
            id: MessageIdOf(record),
            deliveryState: deliveryState,
            conversationId: record.ConversationId,
            senderName: record.SenderName);
    }

    /// <summary>落盘记录 → 前端 DTO。</summary>
    QuickChatMessage ToDisplayMessage(ChatRecord record)
    {
        return new QuickChatMessage
        {
            Id = MessageIdOf(record),
            Role = record.Sender.IsHuman ? "user" : record.Sender.IsSystem ? "system" : "assistant",
            PetId = PetIdOf(record.Sender),
            PetName = record.SenderName,
            SenderName = record.SenderName,
            ConversationId = record.ConversationId,
            Kind = IsGroupConversationId(record.ConversationId) ? "group" : "dm",
            Text = record.Text,
            CreatedAt = record.SentAt
        };
    }

    /// <summary>前端按字符串比较消息 ID，落盘用 long，这里统一成不变文化字符串。</summary>
    static string MessageIdOf(ChatRecord record) =>
        record.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    static string PetIdOf(QuickChatPrincipal principal) =>
        principal.IsAgent ? principal.AgentName : principal.IsHuman ? UserSpeakerId : QuickChatPrincipal.SystemId;

    /// <summary>
    /// 把一条私聊消息写进存储。
    /// <para>
    /// 存储不可用、会话还没登记、或者写盘失败时返回 null —— 调用方回落到纯内存回显。
    /// 私聊是主功能，不能因为落盘出问题就发不出消息。
    /// </para>
    /// </summary>
    /// <param name="conversationId">
    /// 落到哪个私聊会话。默认私聊就是角色名，多开私聊是 <c>dm:小梦#2</c>。
    /// </param>
    /// <param name="sender">发言者：人类或某个角色。</param>
    /// <param name="depth">
    /// 转发深度。角色互相私聊时，这条会被投递给对方，对方再回一条又会投递回来 ——
    /// 靠深度递增在 <see cref="QuickChatGate.RelayDepth"/>（配置「角色接话轮数上限」）处断路，
    /// 否则两个角色会互相刷屏。
    /// </param>
    ChatRecord? TryPersistDirectMessage(
        string conversationId, QuickChatPrincipal sender, string text, bool persist, int depth = 0)
    {
        if (store == null || string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            if (store.Find(conversationId) == null)
                return null;

            return store.Send(conversationId, sender, text, depth, "text", persist);
        }
        catch (Exception exception)
        {
            QuickChatLog.Write("私聊落盘失败：" + exception.Message);
            return null;
        }
    }

    /// <summary>把一条消息投递给所有成员通道。门槛由通道自己判，这里不挑人。</summary>
    void DeliverToChannels(QuickChatStoredConversation conversation, ChatRecord record)
    {
        List<QuickChatChannel> targets;
        lock (stateGate)
            targets = channels.Values.ToList();

        foreach (QuickChatChannel channel in targets)
        {
            try
            {
                channel.Enqueue(conversation, record);
            }
            catch (Exception exception)
            {
                QuickChatLog.Write("群聊投递入队失败：" + exception.Message);
            }
        }

        SendState();
    }

    /// <summary>角色通过 <c>QuickChatSend</c> 在快聊里发言。</summary>
    public void SendFromAgent(string petId, string conversationId, string text)
    {
        if (store == null)
            throw new InvalidOperationException("消息存储不可用");

        SyncStoreConversations();

        QuickChatStoredConversation? conversation = ResolveConversation(conversationId);
        if (conversation == null)
            throw new InvalidOperationException($"会话「{conversationId}」不存在，可用 QuickChatList 查看可用会话");

        QuickChatPrincipal actor = QuickChatPrincipal.Agent(petId);
        if (QuickChatStore.IsMember(conversation, actor) == false)
            throw new InvalidOperationException($"你不在会话「{conversationId}」里");

        // 这一轮已经在用发送工具发言了 → 工具返回轮的文字不再是「对刚才那次翻记录的回答」，
        // 落点必须作废。不作废的话会重演当年那个
        // 「她跟别人私聊，话却发到了上一次翻过的会话里」。
        // QuickChatDirect 最终也走到这里，所以两条发送路径都覆盖到了。
        ClearToolReplyTarget(petId);

        int depth = GetDeliveryDepth(petId);
        bool persist = conversation.IsGroup ? config.SaveGroupMessages : config.SaveDirectMessages;

        ChatRecord record = store.Send(conversation.Id, actor, text, depth, "text", persist);

        AddRecordMessage(record, "assistant", null, null);

        // 群聊，以及「除了自己还有别的角色在场」的私聊（角色互相私聊）：要把话投给对方，
        // 否则另一个角色永远不知道有人跟他说话。
        // 单角色私聊（人类 ↔ 该角色）的另一端是人，推窗口就够了。
        bool hasOtherAgents = conversation.Members
            .Select(QuickChatPrincipal.Parse)
            .Any(principal => principal.IsAgent && principal.SameAs(actor) == false);

        if (conversation.IsGroup || hasOtherAgents)
        {
            // 自己那条会被门槛挡掉（自己发的不投给自己）。
            DeliverToChannels(conversation, record);
        }
        else
        {
            SendState();
        }
    }

    /// <summary>
    /// 按 ID 找会话；找不到时再按标题 / 群名兜底。
    /// <para>
    /// 模型的惯性写法是「group:群名」，而默认公共大厅的真实 ID 是 <c>group:__lobby__</c>。
    /// 严格按 ID 匹配会让它直接吃一个「会话不存在」，然后自己换一个会话发出去
    /// —— 正好表现为跨频道乱发。所以这里做一次宽松兜底，把「group:萝卜开会」也认下来。
    /// </para>
    /// </summary>
    QuickChatStoredConversation? ResolveConversation(string? idOrTitle)
    {
        if (store == null || string.IsNullOrWhiteSpace(idOrTitle))
            return null;

        QuickChatStoredConversation? exact = store.Find(idOrTitle);
        if (exact != null)
            return exact;

        string probe = idOrTitle.Trim();
        string bare = probe.StartsWith(GroupPrefix, StringComparison.OrdinalIgnoreCase)
            ? probe[GroupPrefix.Length..].Trim()
            : probe;

        try
        {
            return store.All().FirstOrDefault(conversation =>
                conversation.Title.Equals(probe, StringComparison.OrdinalIgnoreCase)
                || conversation.Title.Equals(bare, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>列出某个角色能发言的会话。</summary>
    public string ListForAgent(string petId)
    {
        if (store == null)
            return "消息存储不可用。";

        SyncStoreConversations();
        QuickChatPrincipal actor = QuickChatPrincipal.Agent(petId);
        IReadOnlyList<QuickChatStoredConversation> conversations = store.Conversations(actor);
        if (conversations.Count == 0)
            return "暂无会话。";

        List<string> lines = new();
        foreach (QuickChatStoredConversation conversation in conversations
                     .OrderByDescending(conversation => store.LastMessageUnchecked(conversation.Id)?.Id ?? 0))
        {
            ChatRecord? last = store.LastMessageUnchecked(conversation.Id);
            string members = string.Join(", ", conversation.Members.Select(QuickChatFormat.MemberName));
            // 明确标出「公共大厅」：否则模型看到「萝卜开会」这个名字，认不出它是个群聊。
            string kind = conversation.IsGroup
                ? (conversation.Id.Equals(LobbyGroupId, StringComparison.OrdinalIgnoreCase) ? "群聊·公共大厅" : "群聊")
                : "私聊";
            lines.Add(
                $"- {conversation.Id} | {QuickChatFormat.Title(conversation, actor)} | "
                + $"{kind} | 成员：{members} | "
                + $"未读 {store.Unread(conversation.Id, actor)} | "
                + $"最近：{(last == null ? "无消息" : QuickChatFormat.Time(last.SentAt) + " " + last.SenderName + "：" + Truncate(last.Text, 40))}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 角色主动翻某个会话的记录。
    /// <para>
    /// <paramref name="before"/> 是「往前翻」的游标：只读<b>比它更早</b>的消息（不含它本身）。
    /// 传 0 表示从最新的一条开始往回读。返回文本的末尾会给出下一跳该填的值，
    /// 角色照着填就能一直翻到会话开头 —— 不需要靠调大 <paramref name="limit"/> 硬啃。
    /// </para>
    /// <para>
    /// 之所以做成游标而不是 offset：消息是持续追加的，offset 会在两次调用之间错位
    /// （新消息插进来，第二次读到的会和第一次重叠或跳过）。Id 是单调递增的，
    /// 用「比某条更早」来定位不受追加影响。
    /// </para>
    /// </summary>
    public string ReadForAgent(string petId, string conversationId, int limit, long before)
    {
        if (store == null)
            return "消息存储不可用。";

        SyncStoreConversations();
        QuickChatPrincipal actor = QuickChatPrincipal.Agent(petId);
        QuickChatStoredConversation? conversation = ResolveConversation(conversationId);
        if (conversation == null)
            return $"会话「{conversationId}」不存在。先用 QuickChatList 查看可用会话。";

        // 记下落点，留给紧接着的工具返回轮 —— 模型读完这段结果才会说出真正的回答，
        // 而那一轮默认是被整轮丢掉的（见 OnChatFinished 里的 tool 分支）。
        //
        // 两个约束：
        // ① 只认私聊。群聊的文字输出不是发言（要发言得用 QuickChatSend），
        //    落进去会把「没发出去的话」写进群记录。
        // ② 只在<b>当前回合已经有落点</b>时改写它 —— 也就是「角色正在回答某条私聊」的场合。
        //    没有落点说明这一轮不是快聊叫起来的（poke、周期报点…），
        //    此时它翻记录多半只是自己看一眼，之后说的话不该凭空冒进某个私聊里，
        //    维持旧的丢弃行为更安全。
        if (conversation.IsGroup == false)
        {
            lock (stateGate)
            {
                if (toolReplyTargets.ContainsKey(petId))
                    toolReplyTargets[petId] = conversation.Id;
            }
        }

        // limit <= 0 表示「按配置的默认条数」。默认值可配，但两者都收在 AgentReadMax 之下。
        int count = Math.Clamp(limit <= 0 ? config.AgentReadMessages : limit, 1, AgentReadMax);
        long cursor = before <= 0 ? long.MaxValue : before;

        IReadOnlyList<ChatRecord> messages;
        try
        {
            messages = store.History(conversation.Id, actor, cursor, count);
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }

        // 只把「读到的最后一条」标成已读，而不是 MarkRead（标到会话末尾）。
        // 往前翻旧记录时，如果顺手把游标推到末尾，那些还没读过的新消息就会被一起
        // 吞掉 —— 未读数字直接归零，角色再也不会被告知「群里刚有人说话」。
        // SetRead 内部取 Math.Max，所以这个调用对已经读过的地方是幂等的。
        if (messages.Count > 0)
            store.MarkReadUpTo(conversation.Id, actor, messages[^1].Id);
        SendState();

        if (messages.Count == 0)
        {
            return before <= 0
                ? "该会话暂无消息。"
                : $"没有比 #{before} 更早的消息了，这已经是这个会话的开头。";
        }

        long oldestId = messages[0].Id;

        int older;
        try
        {
            older = store.OlderCount(conversation.Id, actor, oldestId);
        }
        catch (Exception)
        {
            older = 0;
        }

        List<string> lines = new()
        {
            $"会话 {conversation.Id}（{QuickChatFormat.Title(conversation, actor)}）"
                + $" #{oldestId}~#{messages[^1].Id}，共 {messages.Count} 条：",
        };
        foreach (ChatRecord message in messages)
            lines.Add($"[#{message.Id} {QuickChatFormat.Time(message.SentAt)} {message.SenderName}] {Truncate(message.Text, 500)}");

        // 把「还有多少、下一步填什么」写进结果里。不写的话模型只会知道「还能翻」，
        // 但不知道该填哪个值，实际表现就是翻不动。
        lines.Add(older > 0
            ? $"（更早还有 {older} 条。要看就把 before 填 {oldestId} 再调一次 QuickChatRead。）"
            : "（这已经是这个会话最早的消息了。）");

        return string.Join("\n", lines);
    }

    /// <summary>把工具返回值回投给角色（走它自己的通道，不经过全局 Poke 队列）。</summary>
    public void ReturnToolResult(string petId, string tool, string text)
    {
        if (channels.TryGetValue(petId, out QuickChatChannel? channel))
            channel.EnqueueToolResult(tool, text);
    }

    internal int GetDeliveryDepth(string petId)
    {
        lock (stateGate)
            return deliveryDepths.TryGetValue(petId, out int depth) ? depth : 0;
    }

    void SetDeliveryDepth(string petId, int depth)
    {
        lock (stateGate)
            deliveryDepths[petId] = depth;
    }

    void ArmReplyCheck(string petId, IReadOnlyList<(string ConversationId, long UpToId)> conversations)
    {
        if (config.AutoCorrectMissingReply == false)
            return;

        lock (stateGate)
            pendingReplies[petId] = new List<(string ConversationId, long UpToId)>(conversations);
    }

    void BroadcastTyping(string conversationId, string agentName, bool typing)
    {
        window?.Send("typing", new { conversationId, petId = agentName, name = agentName, typing });
    }

    static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max] + "…";

    // ────────────────────────── 会话同步与提示词 ──────────────────────────

    /// <summary>
    /// 把「当前激活角色 + 配置里的群」同步成存储里的会话。
    /// <para>
    /// 群成员是活的（跟着激活状态变），所以每次都重算；但只有签名变化时才真的落盘，
    /// 避免每帧一次 <c>SendState</c> 就把 <c>conversations.json</c> 写爆。
    /// </para>
    /// </summary>
    /// <returns>会话集合是否发生了变化（变化了就要重新注入功能说明）。</returns>
    bool SyncStoreConversations()
    {
        if (store == null)
            return false;

        List<string> activeNames = chatActivitySystem.GetAllChatActivities()
            .Select(item => item.Character.Name)
            .Where(name => string.IsNullOrWhiteSpace(name) == false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<QuickChatGroupConfig> groups = config.Groups ?? new List<QuickChatGroupConfig>();
        string signature = string.Join(",", activeNames)
            + "|" + config.EnableGroupChat + "|" + config.PublicLobbyName
            + "|" + config.SaveDirectMessages + "|" + config.SaveGroupMessages
            + "|" + string.Join(";", groups.Select(group => (group?.Name ?? "") + "=" + (group?.Members ?? "")));

        lock (stateGate)
        {
            if (string.Equals(signature, storeSignature, StringComparison.Ordinal))
                return false;
            storeSignature = signature;
        }

        try
        {
            foreach (string name in activeNames)
            {
                store.Ensure(
                    name,
                    "dm",
                    name,
                    new[] { QuickChatPrincipal.Human, QuickChatPrincipal.Agent(name) },
                    config.SaveDirectMessages);
            }

            if (config.EnableGroupChat)
            {
                string lobby = string.IsNullOrWhiteSpace(config.PublicLobbyName) ? "萝卜开会" : config.PublicLobbyName.Trim();
                store.Ensure(
                    LobbyGroupId,
                    "group",
                    lobby,
                    new[] { QuickChatPrincipal.Human }.Concat(activeNames.Select(QuickChatPrincipal.Agent)),
                    config.SaveGroupMessages);

                foreach (QuickChatGroupConfig group in groups)
                {
                    string name = (group?.Name ?? string.Empty).Trim();
                    if (name.Length == 0)
                        continue;

                    List<string> members = ResolveGroupMembers(group.Members, activeNames);
                    store.Ensure(
                        GroupPrefix + name,
                        "group",
                        name,
                        new[] { QuickChatPrincipal.Human }.Concat(members.Select(QuickChatPrincipal.Agent)),
                        config.SaveGroupMessages);
                }
            }
        }
        catch (Exception exception)
        {
            QuickChatLog.Write("同步会话失败：" + exception.Message);
        }

        return true;
    }

    void RefreshAllPrompts()
    {
        foreach (string petId in promptInteractors.Keys.ToList())
            RefreshPrompt(petId);
    }

    /// <summary>
    /// 重新注入功能说明。会话结构一变（新建群、成员增减）就要刷新，
    /// 否则角色手里还是上一份会话索引，会往不存在的会话里发言。
    /// </summary>
    void RefreshPrompt(string petId)
    {
        if (promptInteractors.TryGetValue(petId, out Interactor<QuickChatModule>? interactor) == false)
            return;

        try
        {
            interactor.Prompt(BuildAgentPrompt(petId));
        }
        catch (Exception exception)
        {
            QuickChatLog.Write($"[{petId}] 注入提示词失败：{exception.Message}");
        }
    }

    string BuildAgentPrompt(string petId)
    {
        if (store == null)
            return QuickChatAttachmentPrompt;

        QuickChatPrincipal actor = QuickChatPrincipal.Agent(petId);
        List<QuickChatStoredConversation> conversations;
        try
        {
            // 私聊和群聊都要列出来。以前只列群聊，导致模型手里唯一的目标就是群聊——
            // 于是它把本该只出现在私聊里的回复也发进了群聊。
            conversations = store.Conversations(actor).ToList();
        }
        catch (Exception)
        {
            conversations = new List<QuickChatStoredConversation>();
        }

        return QuickChatAgentPrompt.Build(
            config.EnableGroupChat, QuickChatAttachmentPrompt, actor, conversations);
    }

    // ────────────────────────── 历史加载与分页 ──────────────────────────

    /// <summary>
    /// 首次打开某个会话时，从存储把最近一页历史读进内存。
    /// <para>
    /// 之所以要「首次」这个闸门：投递进来的新消息会先一步写进 <c>histories</c>，
    /// 如果每帧都无条件用磁盘覆盖，会把刚到的消息顶掉。
    /// </para>
    /// <para>
    /// 合并而不是覆盖，是因为「内存里已经攒了几条」和「磁盘上有更早的一页」常常同时成立：
    /// 用户没打开这个会话时，角色之间的对话照样会推进内存列表。
    /// 消息编号全局单调递增，所以合并后按编号排一次序就是正确的显示顺序。
    /// </para>
    /// </summary>
    void EnsureHistoryLoaded(string conversationId)
    {
        if (store == null || string.IsNullOrWhiteSpace(conversationId))
            return;

        lock (stateGate)
        {
            if (historyLoaded.Contains(conversationId))
                return;
        }

        if (store.Find(conversationId) == null)
            return;

        int pageSize = Math.Clamp(config.HistoryPageSize, 5, 200);
        IReadOnlyList<ChatRecord> records;
        try
        {
            records = store.History(conversationId, QuickChatPrincipal.Human, long.MaxValue, pageSize);
        }
        catch (Exception)
        {
            return;
        }

        lock (stateGate)
        {
            if (historyLoaded.Contains(conversationId))
                return;
            historyLoaded.Add(conversationId);

            List<QuickChatMessage> merged = records.Select(ToDisplayMessage).ToList();
            if (histories.TryGetValue(conversationId, out List<QuickChatMessage>? existing) && existing.Count > 0)
            {
                HashSet<string> known = new(merged.Select(item => item.Id), StringComparer.Ordinal);
                merged.AddRange(existing.Where(item => known.Contains(item.Id) == false));
            }

            if (merged.Count == 0)
                return;

            merged.Sort((left, right) => CompareMessageIds(left.Id, right.Id));
            histories[conversationId] = merged;
            historyCursors[conversationId] = records.Count > 0 ? records[0].Id : 0;
        }
    }

    /// <summary>消息编号是十进制字符串；万一混进了非数字 ID（旧版本回显用的 GUID），退回序数比较。</summary>
    static int CompareMessageIds(string left, string right)
    {
        bool leftNumeric = long.TryParse(
            left, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long leftValue);
        bool rightNumeric = long.TryParse(
            right, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long rightValue);

        return leftNumeric && rightNumeric
            ? leftValue.CompareTo(rightValue)
            : string.CompareOrdinal(left, right);
    }

    /// <summary>向上翻一页更早的历史，推给前端做「前插」。</summary>
    void LoadOlderHistory()
    {
        string? conversationId = selectedConversationId;
        if (store == null || string.IsNullOrWhiteSpace(conversationId))
            return;

        long before;
        lock (stateGate)
        {
            if (historyCursors.TryGetValue(conversationId, out long cursor) == false || cursor <= 0)
                return;
            before = cursor;
        }

        int pageSize = Math.Clamp(config.HistoryPageSize, 5, 200);
        IReadOnlyList<ChatRecord> records;
        try
        {
            records = store.History(conversationId, QuickChatPrincipal.Human, before, pageSize);
        }
        catch (Exception)
        {
            return;
        }

        if (records.Count == 0)
        {
            lock (stateGate)
                historyCursors[conversationId] = 0;
            window?.Send("history-prepend", new { conversationId, messages = Array.Empty<QuickChatMessage>(), hasMore = false });
            return;
        }

        List<QuickChatMessage> older = records.Select(ToDisplayMessage).ToList();
        long oldestId = records[0].Id;

        bool hasMore;
        try
        {
            hasMore = store.HasOlder(conversationId, QuickChatPrincipal.Human, oldestId);
        }
        catch (Exception)
        {
            hasMore = false;
        }

        lock (stateGate)
        {
            if (histories.TryGetValue(conversationId, out List<QuickChatMessage>? list) == false)
                histories[conversationId] = list = new List<QuickChatMessage>();

            HashSet<string> known = new(list.Select(item => item.Id), StringComparer.Ordinal);
            List<QuickChatMessage> fresh = older.Where(item => known.Contains(item.Id) == false).ToList();
            if (fresh.Count > 0)
                list.InsertRange(0, fresh);

            historyLoaded.Add(conversationId);
            historyCursors[conversationId] = hasMore ? oldestId : 0;
        }

        window?.Send("history-prepend", new { conversationId, messages = older, hasMore });
    }

    const int MaxModelImageDataUriLength = 1024 * 1024;
    static string GetImageMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };
    }

    static int GetDataUriLength(byte[] data, string mimeType)
    {
        return $"data:{mimeType};base64,".Length + Convert.ToBase64String(data).Length;
    }

    QuickChatConfig GetConfigForPet(string petId)
    {
        lock (configGate)
        {
            if (petConfigs.TryGetValue(petId, out QuickChatConfig? value))
                return value;
        }

        return config;
    }

    static string BuildImageModelText(string rawMessage, IEnumerable<string> imagePaths)
    {
        // 图片字节作为多模态内容发送；同时保留本机路径，便于模型知道图片来源。
        // 这些路径只进入模型请求，不进入 QuickChat 用户可见气泡。
        List<string> paths = imagePaths
            .Where(item => string.IsNullOrWhiteSpace(item) == false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string text = rawMessage;
        foreach (string imagePath in paths)
            text = text.Replace(imagePath, string.Empty, StringComparison.OrdinalIgnoreCase);

        text = text
            .Replace("用户发送了一张图片：", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("用户发送了一张截图：", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("用户发送了一张图片", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("用户发送了一张截图", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("[消息来源(QuickChat)]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("用户文字：", string.Empty, StringComparison.OrdinalIgnoreCase);

        while (text.Contains("\n\n\n"))
            text = text.Replace("\n\n\n", "\n\n");

        string displayText = text.Trim();
        string sourceBlock = string.Join("\n", paths.Select(path => "- " + path));

        return string.IsNullOrWhiteSpace(displayText)
            ? "用户发送了图片。\n\n图片来源路径（本机）：\n" + sourceBlock
            : displayText + "\n\n图片来源路径（本机）：\n" + sourceBlock;
    }

    static async Task<(byte[] Data, string MimeType, bool Compressed)> LoadModelImageForModelAsync(
        string imagePath)
    {
        byte[] data = await File.ReadAllBytesAsync(imagePath);
        string mimeType = GetImageMimeType(imagePath);

        if (GetDataUriLength(data, mimeType) <= MaxModelImageDataUriLength)
            return (data, mimeType, false);

        // data:...;base64, 前缀长度较小，这里留 64 字节余量。
        int targetBase64Length = MaxModelImageDataUriLength - 64;
        int targetRawLength = targetBase64Length * 3 / 4;
        byte[] compressed = await Task.Run(() => CompressImageForModel(data, targetRawLength));
        return (compressed, "image/jpeg", true);
    }

    static byte[] CompressImageForModel(byte[] sourceBytes, int targetRawLength)
    {
        using System.IO.MemoryStream input = new(sourceBytes, false);
        using System.Drawing.Bitmap source = new(input);

        for (double scale = 1.0; scale >= 0.08; scale *= 0.8)
        {
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));

            using System.Drawing.Bitmap scaled = new(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.Clear(System.Drawing.Color.White);
                graphics.DrawImage(source, 0, 0, width, height);
            }

            foreach (long quality in new[] { 92L, 85L, 75L, 65L, 55L, 45L, 35L, 25L, 15L })
            {
                byte[] candidate = EncodeJpeg(scaled, quality);
                if (candidate.Length <= targetRawLength)
                    return candidate;
            }
        }

        return EncodeJpeg(source, 5L);
    }

    static byte[] EncodeJpeg(System.Drawing.Bitmap image, long quality)
    {
        System.Drawing.Imaging.ImageCodecInfo? jpegCodec = System.Drawing.Imaging.ImageCodecInfo
            .GetImageEncoders()
            .FirstOrDefault(codec => codec.MimeType == "image/jpeg");
        if (jpegCodec == null)
            throw new NotSupportedException("系统没有可用的 JPEG 编码器。");

        using System.Drawing.Imaging.EncoderParameters parameters = new(1);
        parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, quality);

        using System.IO.MemoryStream output = new();
        image.Save(output, jpegCodec, parameters);
        return output.ToArray();
    }

    static QuickChatScreenshotImageMode NormalizeScreenshotImageMode(QuickChatScreenshotImageMode value)
    {
        return value;
    }

    async Task<string> SendTemporaryScreenshotAsync(
        ChatActivity activity,
        string outgoing,
        List<(byte[] Data, string MimeType)> images)
    {
        string requestText = outgoing +
            "\n\n[临时图片消息] 请立即完整分析上面的图片。这条消息稍后将从对话历史中删除；你的回复文本将返回给快聊。";

        ChatMessageContentItemCollection items = new()
        {
            new TextContent(requestText)
        };
        foreach ((byte[] imageData, string imageMimeType) in images)
            items.Add(new ImageContent(imageData, imageMimeType));
        ChatMessageContent tempMessage = new ChatMessageContent(AuthorRole.User, items)
        {
            Content = requestText
        };

        string aiMessage = "";
        Exception? error = null;
        await activity.ChatBot.EditChatHistoryAsync(async thread => {
            int startIndex = thread.ChatHistory.Count;
            thread.ChatHistory.Add(tempMessage);
            try
            {
                aiMessage = await activity.ChatBot.LanguageModel.ChatStreamingAsync(
                    thread,
                    exceptionThrow: exception => error = exception);
            }
            finally
            {
                int removeCount = thread.ChatHistory.Count - startIndex;
                if (removeCount > 0)
                    thread.ChatHistory.RemoveRange(startIndex, removeCount);
            }
        }, "QuickChat 临时截图分析");

        if (error != null)
            throw error;

        return aiMessage;
    }

    /// <param name="petId">发言者。私聊就是对话对象；群聊是具体成员；用户发言传 <see cref="UserSpeakerId"/>。</param>
    /// <param name="conversationId">落在哪个会话里。留空 = 私聊（等于 petId）。</param>
    /// <param name="senderName">界面上显示的名字。留空则按 petId 查角色名。</param>
    void AddMessage(
        string petId,
        string role,
        string text,
        List<QuickChatAttachment>? attachments = null,
        string? id = null,
        string? deliveryState = null,
        string? conversationId = null,
        string? senderName = null)
    {
        ChatActivity? activity = chatActivitySystem.GetAllChatActivities()
            .FirstOrDefault(item => item.Character.Name == petId);
        string petName = string.IsNullOrWhiteSpace(senderName)
            ? activity?.Character.Name ?? petId
            : senderName;

        string key = string.IsNullOrWhiteSpace(conversationId) ? petId : conversationId;

        QuickChatMessage message = new()
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
            Role = role,
            PetId = petId,
            PetName = petName,
            SenderName = petName,
            ConversationId = key,
            Kind = IsGroupConversationId(key) ? "group" : "dm",
            Text = text,
            DeliveryState = deliveryState ?? string.Empty,
            Attachments = attachments?.Where(item => string.IsNullOrWhiteSpace(item.Path) == false).ToList() ?? new(),
            CreatedAt = DateTimeOffset.Now
        };

        List<QuickChatMessage> history;
        lock (stateGate)
        {
            if (histories.TryGetValue(key, out List<QuickChatMessage>? value) == false)
            {
                value = new List<QuickChatMessage>();
                histories[key] = value;
            }

            history = value;
            history.Add(message);
        }

        if (string.Equals(selectedConversationId, key, StringComparison.OrdinalIgnoreCase))
            window?.Send("message", message);
    }

    /// <param name="petId">用来查显示名的角色。系统消息在前端按 role 渲染，名字不重要。</param>
    /// <param name="conversationId">落在哪个会话。留空 = 私聊，会话 ID 就是 petId。</param>
    void AddSystemMessage(string petId, string text, string? conversationId = null)
    {
        AddMessage(petId, "system", text, conversationId: conversationId);
    }

    void SendState()
    {
        List<QuickChatConversation> conversations = BuildConversations();
        string? selected = selectedConversationId;

        if (conversations.Count > 0 &&
            (string.IsNullOrWhiteSpace(selected) ||
             conversations.Any(item => string.Equals(item.Id, selected, StringComparison.OrdinalIgnoreCase)) == false))
        {
            selected = conversations[0].Id;
            selectedConversationId = selected;
        }

        window?.Send("state", new
        {
            conversations,
            // 书签栏只放群聊（含公共大厅）；下方的「会话记录」由前端按聊过的会话自动排。
            groups = conversations.Where(item => item.Kind == "group").ToList(),
            selectedId = selected,
            groupMode = IsGroupConversationId(selected),
            enableGroupChat = config.EnableGroupChat,
            sidebarExpandOnHover = config.SidebarExpandOnHover,
            // 「侧栏展开着没有」是窗口级状态（所有角色共用一个窗口），存在 window.json 里。
            // 前端只在页面加载后的第一条 state 上用它还原一次，之后就是前端说了算。
            sidebarExpanded = window?.SavedSidebarExpanded ?? false,
            // 清理弹窗里「同时删除原始记录消息」的默认勾选状态。
            clearIncludesOriginal = config.ClearIncludesOriginal,
            groupWindowWidth = config.GroupWindowWidth,
            groupWindowHeight = config.GroupWindowHeight,
            maxVisibleMessages = config.MaxVisibleMessages,
            showAllMessages = config.ShowAllMessages,
            hideOnEscape = config.HideOnEscape,
            autoHeight = config.AutoFitHeight,
            minWindowHeight = config.MinWindowHeight,
            maxWindowHeight = config.WindowHeight,
            theme = new
            {
                panelColor = config.PanelColor,
                panelOpacity = config.PanelOpacity,
                dragBackdropBlur = config.DragBackdropBlur,
                messageListColor = config.MessageListColor,
                messageListOpacity = config.MessageListOpacity,
                assistantBubbleColor = config.AssistantBubbleColor,
                assistantBubbleOpacity = config.AssistantBubbleOpacity,
                userBubbleColor = config.UserBubbleColor,
                userBubbleOpacity = config.UserBubbleOpacity,
                inputColor = config.InputColor,
                inputOpacity = config.InputOpacity,
                textColor = config.TextColor,
                // 侧栏两档宽度由 C# 常量统一下发，CSS 只用 var() 取值，
                // 避免「窗口按 C# 的宽度加宽、面板却按 CSS 的宽度画」这种对不齐。
                sidebarRailWidth = QuickChatConfig.SidebarRailWidth,
                sidebarPanelWidth = QuickChatConfig.SidebarPanelWidth
            }
        });
    }

    void SendSelectedHistory()
    {
        string? conversationId = selectedConversationId;
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            window?.Send("history", new
            {
                conversationId = string.Empty,
                messages = Array.Empty<QuickChatMessage>(),
                hasMore = false
            });
            return;
        }

        // 落盘的历史第一次打开时才读，读多少由「历史分页条数」决定。
        EnsureHistoryLoaded(conversationId);

        QuickChatMessage[] messages;
        bool hasMore;
        lock (stateGate)
        {
            messages = histories.TryGetValue(conversationId, out List<QuickChatMessage>? value)
                ? value.ToArray()
                : Array.Empty<QuickChatMessage>();

            hasMore = historyCursors.TryGetValue(conversationId, out long cursor) && cursor > 0;
        }

        window?.Send("history", new
        {
            conversationId,
            messages,
            hasMore
        });
    }

    /// <param name="conversationId">清哪个会话。留空 = 当前选中的那个。</param>
    /// <param name="purge">
    /// 是否连同落盘的原始记录、以及角色脑子里的记忆一起清掉。
    /// <para>
    /// <c>true</c>（默认）：磁盘上的消息擦掉、角色上下文里这段对话也删掉，重启后不会回来。
    /// <c>false</c>：只清当前窗口的显示，原始记录和角色记忆都留着 —— 相当于「换个视角重看」。
    /// </para>
    /// <para>
    /// 两种都只影响这一个会话。左侧其它会话（包括用户新开出来的）不受影响。
    /// </para>
    /// </param>
    void ClearDisplayedHistory(string? conversationId, bool purge)
    {
        string? id = string.IsNullOrWhiteSpace(conversationId) ? selectedConversationId : conversationId;
        if (string.IsNullOrWhiteSpace(id))
            return;

        if (purge)
        {
            if (store != null)
            {
                try
                {
                    store.ClearMessages(id);
                }
                catch (Exception exception)
                {
                    QuickChatLog.Write("清空会话记录失败：" + exception.Message);
                }
            }

            // 光把磁盘上的消息删掉是不够的：那段对话还在角色的上下文里，它会接着上文继续说，
            // 用户看到的就是「明明清空了，AI 却什么都没忘」。所以记忆也要一起擦。
            PurgeConversationFromContext(id);
        }

        lock (stateGate)
        {
            histories.Remove(id);
            // 清空是用户的明确意图：标记成「已定稿」，否则下一次重绘会把磁盘里的又读回来。
            historyLoaded.Add(id);
            historyCursors[id] = 0;
        }

        SendState();
        if (string.Equals(id, selectedConversationId, StringComparison.OrdinalIgnoreCase))
            SendSelectedHistory();
    }

    /// <summary>
    /// 把某个会话从「角色脑子里的对话上下文」中擦掉。
    /// <para>
    /// 快聊消息是以 <c>[QC … conv=&lt;会话&gt; …]</c> 信封 + 正文的形式投递给角色的，
    /// 角色对它的回复是紧随其后的一条 assistant 消息（中间可能还夹着 tool 结果，
    /// 而 tool 结果信封里没有 <c>conv=</c>）。所以按顺序扫一遍：遇到带 <c>conv=</c> 的消息
    /// 就把「当前会话」切过去，之后的消息都算这条会话的，直到下一条带 <c>conv=</c> 的消息。
    /// </para>
    /// <para>
    /// <b>必须保留第 0 条</b>：那是 Alife 注入的人设（<c>ResetCharacterPrompt</c> 写的），
    /// 一起删掉角色就没人格了。
    /// </para>
    /// </summary>
    void PurgeConversationFromContext(string conversationId)
    {
        List<string> agents = AgentsOfConversation(conversationId);
        if (agents.Count == 0)
            return;

        foreach (string name in agents)
        {
            ChatActivity? activity = chatActivitySystem.GetAllChatActivities()
                .FirstOrDefault(item => string.Equals(item.Character.Name, name, StringComparison.OrdinalIgnoreCase));
            if (activity == null)
                continue;

            try
            {
                activity.ChatBot.EditChatHistory(thread => {
                    ChatHistory history = thread.ChatHistory;

                    // 先算好要删哪些下标（归属判定要顺着看，边删边算会算错），再从后往前删。
                    HashSet<int> targets = new();
                    string? current = null;
                    for (int index = 0; index < history.Count; index++)
                    {
                        string? owner = QuickChatEnvelope.ConversationOf(history[index].Content);
                        if (owner != null)
                            current = owner;
                        if (current != null && current.Equals(conversationId, StringComparison.OrdinalIgnoreCase))
                            targets.Add(index);
                    }

                    for (int index = history.Count - 1; index >= 1; index--)
                    {
                        if (targets.Contains(index))
                            history.RemoveAt(index);
                    }
                }, "清空快聊会话记录");
            }
            catch (Exception exception)
            {
                QuickChatLog.Write($"清空角色上下文失败（{name}）：" + exception.Message);
            }
        }
    }

    void EnsureSelectedConversation()
    {
        List<QuickChatConversation> conversations = BuildConversations();

        if (conversations.Count == 0)
        {
            selectedConversationId = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(selectedConversationId) ||
            conversations.Any(item => string.Equals(item.Id, selectedConversationId, StringComparison.OrdinalIgnoreCase)) == false)
        {
            selectedConversationId = conversations[0].Id;
        }
    }

    // ────────────────────────── 会话列表 ──────────────────────────

    /// <summary>
    /// 会话 = 当前激活角色的私聊 + 配置里的群聊（含隐式的公共大厅）+ 自建会话。
    /// 私聊会话的 Id 就是角色名，所以历史键与旧版本完全兼容。
    /// <para>
    /// <b>会话的存在性只由「角色是否激活」和「配置里有没有这个群」决定</b>：
    /// 角色激活着就一定有私聊会话（可能没有消息，是空的）；停用了就没有。
    /// 所以这里不做任何「已删除」过滤 —— 一旦过滤，删掉私聊的角色重新激活也回不来，
    /// 而且下面的群聊成员是从这张私聊列表推出来的，会连带把那个角色踢出群。
    /// </para>
    /// </summary>
    List<QuickChatConversation> BuildConversations()
    {
        List<QuickChatConversation> conversations = new();
        // 群聊成员直接来自「当前激活的角色」，不经由上面那张私聊列表。
        // 两者看起来一样，但一旦私聊被任何东西过滤掉就会分叉 ——
        // 这正是「删了私聊，群聊里也没她了」的根因。
        List<string> activeNames = new();

        // 1) 受管私聊：每个当前激活的角色一条，ID 就是角色名。
        foreach (ChatActivity activity in chatActivitySystem.GetAllChatActivities())
        {
            string name = activity.Character.Name;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            activeNames.Add(name);
            conversations.Add(new QuickChatConversation
            {
                Id = name,
                Kind = "dm",
                Title = name,
                Members = new List<string> { name },
                Busy = busyPetIds.Contains(name),
                // 角色的私聊删不掉：它的存在性由「角色是否激活」决定。角色还激活着，
                // 这条会话就该在，哪怕一条消息都没有 —— 那是「空会话」，不是「没有会话」。
                // 真删会连带两个后果：重新激活也回不来，而且群聊成员是从激活角色推出来的，会莫名少人。
                CanDelete = false
            });
        }

        // 2) 受管群聊：公共大厅 + 配置里的小群。
        if (config.EnableGroupChat)
        {
            conversations.Add(new QuickChatConversation
            {
                Id = LobbyGroupId,
                Kind = "group",
                Title = string.IsNullOrWhiteSpace(config.PublicLobbyName) ? "萝卜开会" : config.PublicLobbyName.Trim(),
                Members = activeNames,
                IsLobby = true,
                // 公共大厅是隐式存在的公共场合，删掉所有角色就没有共同的地方了 —— 只能清空记录。
                CanDelete = false
            });

            foreach (QuickChatGroupConfig group in config.Groups ?? new List<QuickChatGroupConfig>())
            {
                string name = (group.Name ?? string.Empty).Trim();
                if (name.Length == 0 || store?.IsDeletedGroup(GroupPrefix + name) == true)
                    continue;

                conversations.Add(new QuickChatConversation
                {
                    Id = GroupPrefix + name,
                    Kind = "group",
                    Title = name,
                    Members = ResolveGroupMembers(group.Members, activeNames),
                    CanDelete = true
                });
            }
        }

        // 3) 自建会话：用户「新建会话」开出来的，以及角色自己拉群 / 开私聊建出来的。
        //    它们不在配置里，也不跟着激活状态变，所以只能从存储里捞。
        if (store != null)
        {
            try
            {
                foreach (QuickChatStoredConversation stored in store.All())
                {
                    if (stored.Managed || (stored.IsGroup && !config.EnableGroupChat))
                        continue;
                    // 受管会话已经在上面的循环里列过了（ID 可能恰好与角色名相同），别重复。
                    if (conversations.Any(item => string.Equals(item.Id, stored.Id, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    List<string> memberNames = stored.Members
                        .Select(QuickChatPrincipal.Parse)
                        .Where(principal => principal.IsAgent)
                        .Select(principal => principal.AgentName)
                        .Where(name => string.IsNullOrWhiteSpace(name) == false)
                        .ToList();

                    conversations.Add(new QuickChatConversation
                    {
                        Id = stored.Id,
                        Kind = stored.IsGroup ? "group" : "dm",
                        Title = string.IsNullOrWhiteSpace(stored.Title) ? stored.Id : stored.Title,
                        Members = memberNames,
                        IsCustom = true,
                        CanDelete = true,
                        // 群聊不标 busy：群里只要有人在回复就整条书签变「回复中」，反而看不出是谁。
                        Busy = stored.IsGroup == false && memberNames.Any(name => busyPetIds.Contains(name))
                    });
                }
            }
            catch (Exception exception)
            {
                QuickChatLog.Write("读取自建会话失败：" + exception.Message);
            }
        }

        foreach (QuickChatConversation conversation in conversations.Where(c => c.Kind == "group" && !c.IsLobby))
        {
            QuickChatStoredConversation? meta = store?.Find(conversation.Id);
            if (meta != null)
            {
                conversation.Members = meta.Members.Select(QuickChatPrincipal.Parse)
                    .Where(p => p.IsAgent).Select(p => p.AgentName).ToList();
                conversation.CreatorId = meta.CreatorId;
                conversation.MutedMembers = meta.Muted.Select(QuickChatPrincipal.Parse)
                    .Where(p => p.IsAgent).Select(p => p.AgentName).ToList();
            }
        }

        // 会话记录区要显示每个会话的最后一条消息，摘要在这里一次性算好。
        lock (stateGate)
        {
            foreach (QuickChatConversation conversation in conversations)
            {
                if (histories.TryGetValue(conversation.Id, out List<QuickChatMessage>? history) == false ||
                    history.Count == 0)
                {
                    continue;
                }

                conversation.MessageCount = history.Count;
                QuickChatMessage last = history[^1];
                conversation.LastAt = last.CreatedAt.ToString("HH:mm");
                conversation.LastText = BuildConversationPreview(last);
            }
        }

        return conversations;
    }

    /// <summary>会话记录里那一行摘要。附件优先于文本，避免「[图片]」被长文本盖掉。</summary>
    static string BuildConversationPreview(QuickChatMessage message)
    {
        if (message.Attachments != null && message.Attachments.Count > 0)
            return message.Attachments.Count == 1 ? "[附件]" : $"[{message.Attachments.Count} 个附件]";

        string text = (message.Text ?? string.Empty)
            .Replace("\r\n", " ")
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Trim();

        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);

        return text.Length <= 40 ? text : text[..40] + "…";
    }

    /// <summary>成员留空 = 当前所有已激活角色，随激活状态自动增减。</summary>
    static List<string> ResolveGroupMembers(string? members, List<string> activeNames)
    {
        List<string> parsed = (members ?? string.Empty)
            .Split(new[] { ',', '，', '、', ';', '；', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return parsed.Count > 0 ? parsed : new List<string>(activeNames);
    }

    /// <summary>当前选中的是不是群聊。</summary>
    bool SelectedIsGroup => IsGroupConversationId(selectedConversationId);

    void SelectConversation(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return;

        selectedConversationId = conversationId;
        SendState();
        ApplyConversationWindowSize();
        SendSelectedHistory();
    }

    /// <summary>
    /// 以某个会话为模板新开一条独立的会话。
    /// <para>
    /// 私聊 → <c>dm:小梦#2</c>，群聊 → <c>group:小群A#2</c>。新会话与模板并存、记录各自独立，
    /// 「清空」只动被清的那一条 —— 这就是「左侧新开的对话记录不受影响」的落点。
    /// 同一个模板反复新建会依次拿到 #2、#3……
    /// </para>
    /// </summary>
    void CreateConversation(string? sourceConversationId)
    {
        if (store == null)
            return;

        string? sourceId = string.IsNullOrWhiteSpace(sourceConversationId)
            ? selectedConversationId
            : sourceConversationId;

        QuickChatStoredConversation? source = ResolveConversation(sourceId);
        if (source == null)
        {
            if (string.IsNullOrWhiteSpace(sourceId) == false)
                AddSystemMessage(sourceId, "这个会话已经不在了，无法新建。", sourceId);
            return;
        }

        bool isGroup = source.IsGroup;
        // 标题先剥掉可能已有的「(2)」尾巴，否则反复新建会得到「小群A (2) (2) (2)」。
        string baseTitle = StripCopySuffix(
            string.IsNullOrWhiteSpace(source.Title) ? source.Id : source.Title);
        string baseId = isGroup ? GroupPrefix + baseTitle : DirectPrefix + baseTitle;

        string id = string.Empty;
        string title = string.Empty;
        for (int index = 2; index < 200; index++)
        {
            title = $"{baseTitle} ({index})";
            id = $"{baseId}#{index}";
            if (store.Find(id) == null)
                break;
        }

        if (string.IsNullOrEmpty(id))
            return;

        try
        {
            store.CreateCustom(
                id,
                isGroup ? "group" : "dm",
                title,
                source.Members.Select(QuickChatPrincipal.Parse),
                isGroup ? config.SaveGroupMessages : config.SaveDirectMessages);
        }
        catch (Exception exception)
        {
            QuickChatLog.Write("新建会话失败：" + exception.Message);
            return;
        }

        selectedConversationId = id;
        RefreshAllPrompts();
        SendState();
        SendSelectedHistory();
        ApplyConversationWindowSize();
    }

    /// <summary>把「小群A (2)」这样的复制后缀剥掉，拿到原始标题；没有后缀就原样返回。</summary>
    static string StripCopySuffix(string title)
    {
        title = (title ?? string.Empty).Trim();
        if (title.Length < 4 || title[^1] != ')')
            return title;

        int open = title.LastIndexOf('(');
        if (open < 1)
            return title;

        string inner = title[(open + 1)..^1].Trim();
        if (inner.Length == 0 || inner.All(char.IsDigit) == false)
            return title;

        string head = title[..open].TrimEnd();
        return head.Length == 0 ? title : head;
    }

    /// <summary>
    /// 删除一个会话。
    /// <para>
    /// <b>受管会话（角色的私聊、公共大厅、配置里的群）不能删除，只能清空记录。</b>
    /// 它们的存在性由「角色是否激活 / 配置里有没有这个群」决定：角色激活着，私聊会话就该在
    /// （哪怕一条消息都没有，那也是「空会话」而不是「没有会话」）。把它删掉会连带两个后果 ——
    /// 角色重新激活也回不来，而且群聊成员是从激活角色推出来的，会莫名其妙少一个人。
    /// 所以这里对受管会话退化成「清空记录」，对用户/角色自建的会话才真删。
    /// </para>
    /// </summary>
    void DeleteConversation(string? conversationId)
    {
        if (store == null || string.IsNullOrWhiteSpace(conversationId))
            return;

        string id = conversationId.Trim();

        if (id.Equals(LobbyGroupId, StringComparison.OrdinalIgnoreCase))
        {
            AddSystemMessage(id, "公共大厅不能删除，只能清空它的对话记录。", id);
            return;
        }

        QuickChatStoredConversation? stored = store.Find(id);
        // 查不到（会话还没被同步出来）时也按受管处理：宁可清空，也不要把一个角色的私聊删掉。
        bool managed = stored == null || stored.Managed;

        if (managed && stored?.IsGroup != true)
        {
            ClearDisplayedHistory(id, purge: true);
            AddSystemMessage(id, "这条会话由角色的激活状态决定，不能删除；已清空它的聊天记录。", id);
            return;
        }

        try
        {
            store.Delete(id);
        }
        catch (Exception exception)
        {
            QuickChatLog.Write("删除会话失败：" + exception.Message);
            return;
        }

        lock (stateGate)
        {
            histories.Remove(id);
            historyCursors.Remove(id);
            historyLoaded.Remove(id);
        }

        RefreshAllPrompts();
        EnsureSelectedConversation();
        SendState();
        SendSelectedHistory();
        ApplyConversationWindowSize();
    }

    /// <summary>当前激活的角色名。</summary>
    public IReadOnlyList<string> GetActiveCharacterNames() =>
        chatActivitySystem.GetAllChatActivities()
            .Select(item => item.Character.Name)
            .Where(name => string.IsNullOrWhiteSpace(name) == false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// 框架内的<b>全部</b>角色名，含未激活的。配置页的「探测角色」用它。
    /// <para>
    /// 数据来源和 Alife 自己的 <c>CharacterSystem</c> 完全一致：<c>Storage/Character</c> 下的
    /// 子目录，一个目录就是一个角色（<c>CharacterSystem</c> 构造时也是这么扫的）。
    /// 走目录而不是要求注入 <c>CharacterSystem</c>，是因为插件构造函数的参数由宿主决定，
    /// 多要一个参数就多一分「宿主版本不同就起不来」的风险 —— 而这里要的信息目录里全有。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> GetAllCharacterNames()
    {
        try
        {
            string root = Path.Combine(Alife.Foundation.AlifePath.StorageFolderPath, "Character");
            if (Directory.Exists(root) == false)
                return Array.Empty<string>();

            List<string> names = new();
            foreach (string directory in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(name) == false)
                    names.Add(name);
            }

            names.Sort(StringComparer.CurrentCulture);
            return names;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 角色自己拉一个群。
    /// <para>
    /// 建出来的是「自建会话」：不在配置里，也不随激活状态增减，只受显式删除影响。
    /// 发起者一定会被加进成员，否则它自己都发不了言。
    /// </para>
    /// </summary>
    public IReadOnlyList<QuickChatConversation> GetSmallGroups()
    {
        SyncStoreConversations();
        return store?.All().Where(c => c.IsGroup && !c.Id.Equals(LobbyGroupId, StringComparison.OrdinalIgnoreCase))
            .Select(c => new QuickChatConversation {
                Id = c.Id, Title = c.Title, Kind = "group", CreatorId = c.CreatorId,
                CanDelete = true, IsCustom = !c.Managed,
                Members = c.Members.Select(QuickChatPrincipal.Parse).Where(p => p.IsAgent).Select(p => p.AgentName).ToList(),
                MutedMembers = c.Muted.Select(QuickChatPrincipal.Parse).Where(p => p.IsAgent).Select(p => p.AgentName).ToList()
            }).ToArray() ?? Array.Empty<QuickChatConversation>();
    }
    public string UpdateSmallGroup(string id, IEnumerable<string> members, IEnumerable<string> muted) =>
        UpdateGroup(id, QuickChatPrincipal.Human,
            new[] { QuickChatPrincipal.Human }.Concat(members.Select(QuickChatPrincipal.Agent)),
            muted.Select(QuickChatPrincipal.Agent));
    public string DeleteSmallGroup(string id)
    {
        var group = store?.Find(id);
        if (group?.IsGroup != true || id.Equals(LobbyGroupId, StringComparison.OrdinalIgnoreCase))
            return "只能删除小群。";
        DeleteConversation(id);
        return store?.Find(id) == null ? "已删除小群。" : "删除失败，请查看日志。";
    }
    public string CreateSmallGroup(string name)
    {
        name = name.Trim();
        if (store == null) return "快聊尚未运行。";
        if (name.Length is < 1 or > 30 || name.IndexOfAny(new[] { ':', '#', '/', '\\' }) >= 0)
            return "群名需要 1 到 30 个字，不能包含 : # / 或反斜杠。";
        string id = GroupPrefix + name;
        if (store.Find(id) != null || store.IsDeletedGroup(id)) return "群名已使用，请换个名称。";
        store.CreateCustom(id, "group", name, new[] { QuickChatPrincipal.Human }, config.SaveGroupMessages);
        RefreshAllPrompts();
        SendState();
        return "已创建小群。";
    }

    string UpdateGroup(string id, QuickChatPrincipal actor,
        IEnumerable<QuickChatPrincipal> members, IEnumerable<QuickChatPrincipal> muted)
    {
        if (store == null) return "消息存储不可用。";
        var wanted = members.ToArray();
        var known = GetAllCharacterNames();
        if (wanted.Any(p => !p.IsHuman && (!p.IsAgent ||
            !known.Contains(p.AgentName, StringComparer.OrdinalIgnoreCase))))
            return "成员包含不存在的角色。";
        try { store.EditGroup(id, actor, wanted, muted); }
        catch (Exception exception) { return exception.Message; }
        RefreshAllPrompts();
        SendState();
        return "已更新小群成员和限制。";
    }

    public string ManageGroupFromAgent(string petId, string conversation, string character, string action)
    {
        QuickChatStoredConversation? group = ResolveConversation(conversation);
        if (group == null) return "小群不存在。";
        if (QuickChatPrincipal.TryParseHumanAlias(character, out _)) return "不能清退或限制主人。";
        var member = QuickChatPrincipal.Agent(character.Trim());
        var members = group.Members.Select(QuickChatPrincipal.Parse).ToList();
        var muted = group.Muted.Select(QuickChatPrincipal.Parse).ToList();
        if (action == "add") members.Add(member);
        else if (action == "remove") members.RemoveAll(p => p.SameAs(member));
        else if (action == "mute") muted.Add(member);
        else if (action == "unmute") muted.RemoveAll(p => p.SameAs(member));
        else return "action 只能是 add、remove、mute、unmute。";
        return UpdateGroup(group.Id, QuickChatPrincipal.Agent(petId), members, muted);
    }

    public string CreateGroupFromAgent(string petId, string name, string? members)
    {
        if (store == null)
            return "消息存储不可用。";

        name = (name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 30)
            return "群名称需要 1 到 30 个字。";
        if (name.IndexOfAny(new[] { ':', '#', '/', '\\' }) >= 0)
            return "群名称不能包含 : # / \\ 这些字符（会和会话 ID 的写法冲突）。";

        List<string> known = GetAllCharacterNames().ToList();
        List<string> active = GetActiveCharacterNames().ToList();

        List<string> wanted = (members ?? string.Empty)
            .Split(new[] { ',', '，', '、', ';', '；', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 成员留空 = 当前全部激活角色（含发起者）。
        if (wanted.Count == 0)
            wanted = new List<string>(active);

        // 过滤掉框架里不存在的名字：模型有时凭印象写一个，会建出「只有自己」的空群，
        // 然后困惑为什么没人回应。这里直接告诉它有哪些人可用。
        List<string> unknown = wanted
            .Where(item => known.Contains(item, StringComparer.OrdinalIgnoreCase) == false)
            .ToList();
        wanted = wanted
            .Where(item => known.Contains(item, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (wanted.Count == 0)
            return "成员里没有框架内存在的角色。可用角色：" + string.Join("、", known);

        if (wanted.Contains(petId, StringComparer.OrdinalIgnoreCase) == false)
            wanted.Insert(0, petId);

        string id = GroupPrefix + name;
        if (store.IsDeletedGroup(id)) return "这个群名已被删除，请换一个新群名。";
        if (store.Find(id) != null)
            return $"群「{name}」已经存在了（conversation={id}）。用 QuickChatSend 直接在里面发言即可。";

        try
        {
            store.CreateCustom(
                id,
                "group",
                name,
                new[] { QuickChatPrincipal.Human }.Concat(wanted.Select(QuickChatPrincipal.Agent)),
                config.SaveGroupMessages,
                QuickChatPrincipal.Agent(petId).Id);
        }
        catch (Exception exception)
        {
            QuickChatLog.Write("角色拉群失败：" + exception.Message);
            return "建群失败：" + exception.Message;
        }

        RefreshAllPrompts();
        SendState();

        string note = unknown.Count > 0 ? $"（忽略了框架里没有的名字：{string.Join("、", unknown)}）" : "";
        return $"已创建群「{name}」（conversation={id}），成员：{string.Join("、", wanted)}。{note}"
            + $"要发言请用 QuickChatSend，conversation 填 {id}。"
            + "不在成员里的角色不会收到群里的消息。";
    }

    /// <summary>
    /// 角色主动找另一个角色开私聊并说一句话。
    /// <para>
    /// 会话 ID 取两个角色名（按字典序拼接），保证「甲找乙」和「乙找甲」落到同一条线上，
    /// 不会各开一条互相看不见的私聊。
    /// </para>
    /// </summary>
    public string SendDirectFromAgent(string petId, string targetName, string text)
    {
        if (store == null)
            return "消息存储不可用。";

        targetName = (targetName ?? string.Empty).Trim();
        text = (text ?? string.Empty).Trim();

        if (targetName.Length == 0)
            return "缺少对方角色名。";
        if (text.Length == 0)
            return "缺少消息内容。";
        if (targetName.Equals(petId, StringComparison.OrdinalIgnoreCase))
            return "不能跟自己私聊。";

        List<string> known = GetAllCharacterNames().ToList();
        if (known.Contains(targetName, StringComparer.OrdinalIgnoreCase) == false)
            return $"框架里没有叫「{targetName}」的角色。可用角色：{string.Join("、", known)}";

        // 用目录里的真实名字，避免「小梦」和写法不同的大小写被当成两个人。
        string canonicalTarget = known.First(item => item.Equals(targetName, StringComparison.OrdinalIgnoreCase));
        string canonicalSelf = known.FirstOrDefault(item => item.Equals(petId, StringComparison.OrdinalIgnoreCase)) ?? petId;

        string first = string.CompareOrdinal(canonicalSelf, canonicalTarget) <= 0 ? canonicalSelf : canonicalTarget;
        string second = first == canonicalSelf ? canonicalTarget : canonicalSelf;
        string id = $"{DirectPrefix}{first}|{second}";
        string title = $"{first} ↔ {second}";

        try
        {
            if (store.Find(id) == null)
            {
                store.CreateCustom(
                    id,
                    "dm",
                    title,
                    new[]
                    {
                        QuickChatPrincipal.Human,
                        QuickChatPrincipal.Agent(first),
                        QuickChatPrincipal.Agent(second),
                    },
                    config.SaveDirectMessages);

                RefreshAllPrompts();
            }
        }
        catch (Exception exception)
        {
            QuickChatLog.Write("角色开私聊失败：" + exception.Message);
            return "开私聊失败：" + exception.Message;
        }

        try
        {
            SendFromAgent(petId, id, text);
        }
        catch (Exception exception)
        {
            return "发言失败：" + exception.Message;
        }

        return $"已把话发到你和「{canonicalTarget}」的私聊（conversation={id}）。"
            + $"对方收到后会看到这条消息。等对方回复时，你会收到一条 kind=dm、conv={id} 的投递 —— "
            + "那时候**直接输出文字**就是回复，不要再调这个工具。";
    }

    /// <summary>
    /// 群聊把窗口放大到群聊尺寸，切回私聊缩回私聊默认尺寸。
    /// 只调整尺寸、不改变可见性，避免「点一下书签窗口跳出来」。
    /// <para>
    /// 宽度统一走 <see cref="QuickChatConfig.EffectiveWindowWidth"/>：
    /// 开启群聊时它已经把左侧书签栏的宽度算进去了。
    /// </para>
    /// <para>
    /// <b>用户手动拖过尺寸就以他拖的为准</b>：配置里的值只是「还没拖过时的默认值」。
    /// 不这样做的话，用户拉开一次、切个会话就被打回默认，下次呼出还是那个小窗口。
    /// </para>
    /// </summary>
    void ApplyConversationWindowSize()
    {
        if (window == null)
            return;

        int sidebar = config.EnableGroupChat ? QuickChatConfig.SidebarRailWidth : 0;
        QuickChatWindowSize? saved = window.SavedSize(SelectedIsGroup);

        if (SelectedIsGroup)
        {
            int groupWidth = Math.Max(config.GroupWindowWidth, config.WindowWidth) + sidebar;
            int groupHeight = Math.Max(config.GroupWindowHeight, config.MinWindowHeight);
            if (saved is { } group)
            {
                groupWidth = group.Width + sidebar;
                groupHeight = group.Height;
            }

            window.SetSize(groupWidth, groupHeight);
            return;
        }

        // 私聊：只纠正宽度。
        // 高度归渲染层所有 —— 它按当前会话的消息内容自适应上报。这里如果按 MinWindowHeight
        // 把高度一起写掉，而渲染层在消息没变时不会重新上报（renderMessages 的签名去重会提前
        // return），窗口就会被永久钉在最小高度上，表现为「每次呼出都缩成一条，得手动拉高」。
        if (config.AutoFitHeight)
        {
            window.SetWidth(saved is { } direct ? direct.Width : config.EffectiveWindowWidth);
            return;
        }

        // 关闭自适应时高度是固定的，渲染层不会上报，只能由后端设定。
        window.SetSize(
            saved is { } directSize ? directSize.Width : config.EffectiveWindowWidth,
            saved is { } directHeight ? directHeight.Height : config.WindowHeight);
    }

    // ────────────────────────── 窗口 ──────────────────────────

    string ResolveWwwRoot() => Path.Combine(
        pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.QuickChat"),
        "Resources",
        "QuickChat");

    IQuickChatWindow EnsureWindow()
    {
        if (window == null)
            window = windowFactory.Create(logger);

        window.OnMessage -= OnRendererMessage;
        window.OnMessage += OnRendererMessage;
        return window;
    }

    /// <summary>从配置页按钮打开窗口：不切换可见性，直接显示并聚焦。</summary>
    public async Task OpenWindowAsync()
    {
        try
        {
            EnsureSelectedConversation();
            await EnsureWindow().ShowAsync(config, ResolveWwwRoot(), SelectedIsGroup);
            ApplyConversationWindowSize();
            SendState();
            SendSelectedHistory();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "QuickChat 打开窗口失败");
        }
    }

    void RegisterHotkeys()
    {
        if (disposed)
            return;

        foreach (string oldHotkey in registeredHotkeys)
            shortcutService.Unregister(oldHotkey);

        registeredHotkeys.Clear();
        registeredHotkeys.AddRange(config.Hotkeys);

        foreach (string hotkey in config.Hotkeys)
        {
            try
            {
                shortcutService.Register(hotkey, OnHotkeyPressed);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "QuickChat 快捷键注册失败：{Hotkey}", hotkey);
            }
        }
    }

    static string CreateConfigSignature(QuickChatConfig value)
    {
        return string.Join("|", value.Hotkeys)
            + $"|{value.WindowWidth}|{value.WindowHeight}"
            + $"|{value.MaxVisibleMessages}|{value.ShowAllMessages}|{value.HideOnEscape}|{value.MarkMessageSource}"
            + $"|{value.ScreenshotImageMode}"
            + $"|{value.AutoFitHeight}|{value.MinWindowHeight}|{value.ColorPreset}"
            + $"|{value.PanelColor}|{value.PanelOpacity:R}"
            + $"|{value.DragBackdropBlur:R}"
            + $"|{value.MessageListColor}|{value.MessageListOpacity:R}"
            + $"|{value.AssistantBubbleColor}|{value.AssistantBubbleOpacity:R}"
            + $"|{value.UserBubbleColor}|{value.UserBubbleOpacity:R}"
            + $"|{value.InputColor}|{value.InputOpacity:R}"
            + $"|{value.TextColor}"
            // 流式输出也要进签名：ApplyConfig 是在签名「变了」之后才 config = normalized，
            // 不进签名的话单改这个开关会被当成「配置没变」直接早退，开关点了没反应。
            + $"|{value.StreamingOutput}"
            // 群聊相关的配置改了要触发一次 SendState，否则书签栏不会刷新。
            + $"|{value.EnableGroupChat}|{value.PublicLobbyName}"
            // 悬停展开也要进签名：改了它前端得重新判定「鼠标移入是否该弹开」。
            + $"|{value.SidebarExpandOnHover}"
            + $"|{value.GroupWindowWidth}|{value.GroupWindowHeight}"
            + $"|{string.Join(";", (value.Groups ?? new List<QuickChatGroupConfig>())
                .Select(group => (group?.Name ?? string.Empty) + "=" + (group?.Members ?? string.Empty)))}"
            + $"|{value.SaveDirectMessages}|{value.SaveGroupMessages}|{value.HistoryPageSize}"
            // 角色默认读取条数也要进签名：ApplyConfig 是在签名「变了」之后才 config = normalized，
            // 不进签名的话单改这一项会被当成「配置没变」直接早退，改完没反应。
            + $"|{value.AgentReadMessages}"
            // 清理弹窗的默认勾选状态要下发给前端，改了得让它重新渲染。
            + $"|{value.ClearIncludesOriginal}"
            // 投递门槛也进签名：否则用户改了它们，签名不变 -> ApplyConfig 提前 return -> 改动被吞。
            + $"|{value.AutoDeliver}|{value.MaxBatchMessages}|{value.ContextTail}|{value.AutoCorrectMissingReply}"
            + $"|{value.GroupListenChance:R}|{value.ActiveListenChance:R}|{value.MaxRelayDepth}|{value.GroupIdleMinutes}"
            + $"|{value.FlushDelaySeconds:R}|{value.MaxDeliveriesPerMinute}";
    }

    async void OnHotkeyPressed()
    {
        try
        {
            EnsureSelectedConversation();

            string wwwRoot = Path.Combine(
                pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.QuickChat"),
                "Resources",
                "QuickChat");

            if (window == null)
                window = windowFactory.Create(logger);

            window.OnMessage -= OnRendererMessage;
            window.OnMessage += OnRendererMessage;
            await window.ToggleAtMouseAsync(config, wwwRoot, SelectedIsGroup);
            ApplyConversationWindowSize();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "QuickChat 悬浮窗切换失败");
        }
    }

    static QuickChatConfig NormalizeConfig(QuickChatConfig value)
    {
        List<string> hotkeys = value.Hotkeys?
            .Where(item => string.IsNullOrWhiteSpace(item) == false)
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        if (hotkeys.Count == 0)
            hotkeys.Add("Alt+Q");

        QuickChatConfig normalized = new QuickChatConfig
        {
            Hotkeys = hotkeys,
            WindowWidth = Math.Clamp(value.WindowWidth == 380 ? 320 : value.WindowWidth, 260, 1000),
            WindowHeight = Math.Clamp(value.WindowHeight == 520 ? 460 : value.WindowHeight, 120, 1200),
            MaxVisibleMessages = Math.Clamp(value.MaxVisibleMessages, 1, 50),
            ShowAllMessages = value.ShowAllMessages,
            AutoFitHeight = value.AutoFitHeight,
            MinWindowHeight = Math.Clamp(value.MinWindowHeight == 170 ? 96 : value.MinWindowHeight, 52, 1000),
            ColorPreset = string.IsNullOrWhiteSpace(value.ColorPreset) ? "自定义" : value.ColorPreset.Trim(),
            HideOnEscape = value.HideOnEscape,
            MarkMessageSource = value.MarkMessageSource,
            ScreenshotImageMode = NormalizeScreenshotImageMode(value.ScreenshotImageMode),
            PanelColor = NormalizeColor(value.PanelColor, "#111C28"),
            // 0.55 是旧版本的默认值。老配置里存的就是它，而它压不住桌面背景、字看不清，
            // 所以这里按「没改过」处理，抬到新默认；用户自己调过的值原样保留。
            PanelOpacity = ClampRatio(value.PanelOpacity == 0.55 ? 0.80 : value.PanelOpacity, 0.80),
            DragBackdropBlur = Math.Clamp(value.DragBackdropBlur, 0, 80),
            MessageListColor = NormalizeColor(value.MessageListColor, "#000000"),
            MessageListOpacity = ClampRatio(value.MessageListOpacity, 0),
            AssistantBubbleColor = NormalizeColor(value.AssistantBubbleColor, "#FFFFFF"),
            AssistantBubbleOpacity = ClampRatio(value.AssistantBubbleOpacity, 0.10),
            UserBubbleColor = NormalizeColor(value.UserBubbleColor, "#556DF5"),
            UserBubbleOpacity = ClampRatio(value.UserBubbleOpacity, 0.25),
            InputColor = NormalizeColor(value.InputColor, "#FFFFFF"),
            InputOpacity = ClampRatio(value.InputOpacity, 0.085),
            TextColor = NormalizeColor(value.TextColor, "#EEF1F6"),
            // 归一化是「显式逐项拷贝」，新字段必须在这里接一手；
            // 漏了的话每次 ApplyConfig 都会被重置回默认值，开关就完全失效。
            StreamingOutput = value.StreamingOutput,

            EnableGroupChat = value.EnableGroupChat,
            SidebarExpandOnHover = value.SidebarExpandOnHover,
            PublicLobbyName = string.IsNullOrWhiteSpace(value.PublicLobbyName)
                ? "萝卜开会"
                : value.PublicLobbyName.Trim(),
            GroupWindowWidth = Math.Clamp(value.GroupWindowWidth, 260, 1600),
            GroupWindowHeight = Math.Clamp(value.GroupWindowHeight, 160, 1400),
            Groups = NormalizeGroups(value.Groups),
            SaveDirectMessages = value.SaveDirectMessages,
            SaveGroupMessages = value.SaveGroupMessages,
            HistoryPageSize = Math.Clamp(value.HistoryPageSize, 5, 200),
            // 上界必须和 AgentReadMax 一致，否则会出现「配置页写 200、实际只给 100」的对不上。
            AgentReadMessages = Math.Clamp(value.AgentReadMessages, 1, AgentReadMax),
            ClearIncludesOriginal = value.ClearIncludesOriginal,

            // 投递门槛。这四个是逐字段重建时最容易漏掉的：漏了不会报错，
            // 只会让用户在配置页改完立刻被打回默认值，所以上下界要和用到处保持一致。
            AutoDeliver = value.AutoDeliver,
            MaxBatchMessages = Math.Clamp(value.MaxBatchMessages, 1, 200),
            ContextTail = Math.Clamp(value.ContextTail, 0, 40),
            AutoCorrectMissingReply = value.AutoCorrectMissingReply,

            GroupListenChance = ClampRatio(value.GroupListenChance, 0.12),
            // 「活跃会话旁听概率」和「接话轮数上限」是「她们能一直接话聊下去」的两个闸门，
            // 上下界必须和 QuickChatGate 里用到的完全一致，否则会出现
            // 「配置页显示 8、实际按 12 跑」这种对不上的情况。
            ActiveListenChance = ClampRatio(value.ActiveListenChance, 0.5),
            MaxRelayDepth = Math.Clamp(value.MaxRelayDepth, 1, 12),
            GroupIdleMinutes = Math.Clamp(value.GroupIdleMinutes, 1, 24 * 60),
            FlushDelaySeconds = Math.Clamp(value.FlushDelaySeconds, 0, 60),
            MaxDeliveriesPerMinute = Math.Clamp(value.MaxDeliveriesPerMinute, 1, 600)
        };

        ApplyColorPreset(normalized);
        return normalized;
    }

    /// <summary>群聊定义清洗：去掉空名、重名，并把成员串规整成「逗号分隔」。</summary>
    static List<QuickChatGroupConfig> NormalizeGroups(List<QuickChatGroupConfig>? groups)
    {
        List<QuickChatGroupConfig> normalized = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (QuickChatGroupConfig group in groups ?? new List<QuickChatGroupConfig>())
        {
            string name = (group?.Name ?? string.Empty).Trim();
            if (name.Length == 0 || seen.Add(name) == false)
                continue;

            normalized.Add(new QuickChatGroupConfig
            {
                Name = name,
                Members = string.Join(",", (group.Members ?? string.Empty)
                    .Split(new[] { ',', '，', '、', ';', '；', '|' },
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(item => item.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
            });
        }

        return normalized;
    }

    static void ApplyColorPreset(QuickChatConfig value)
    {
        switch (value.ColorPreset)
        {
            case "深空":
                value.PanelColor = "#111C28"; value.PanelOpacity = 0.80;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#FFFFFF"; value.AssistantBubbleOpacity = 0.10;
                value.UserBubbleColor = "#556DF5"; value.UserBubbleOpacity = 0.25;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.085;
                break;
            case "墨黑":
                value.PanelColor = "#000000"; value.PanelOpacity = 0.88;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#FFFFFF"; value.AssistantBubbleOpacity = 0.08;
                value.UserBubbleColor = "#64748B"; value.UserBubbleOpacity = 0.30;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.08;
                break;
            case "雾白":
                value.PanelColor = "#F8FAFC"; value.PanelOpacity = 0.90;
                value.MessageListColor = "#FFFFFF"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#FFFFFF"; value.AssistantBubbleOpacity = 0.36;
                value.UserBubbleColor = "#3B82F6"; value.UserBubbleOpacity = 0.24;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.38;
                break;
            case "蓝色":
                value.PanelColor = "#102A43"; value.PanelOpacity = 0.84;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#BFDBFE"; value.AssistantBubbleOpacity = 0.16;
                value.UserBubbleColor = "#2F6FED"; value.UserBubbleOpacity = 0.30;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.10;
                break;
            case "青色":
                value.PanelColor = "#062E33"; value.PanelOpacity = 0.84;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#99F6E4"; value.AssistantBubbleOpacity = 0.14;
                value.UserBubbleColor = "#06B6D4"; value.UserBubbleOpacity = 0.26;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.10;
                break;
            case "绿色":
                value.PanelColor = "#08291B"; value.PanelOpacity = 0.84;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#BBF7D0"; value.AssistantBubbleOpacity = 0.14;
                value.UserBubbleColor = "#22C55E"; value.UserBubbleOpacity = 0.26;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.10;
                break;
            case "紫色":
                value.PanelColor = "#22103D"; value.PanelOpacity = 0.84;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#DDD6FE"; value.AssistantBubbleOpacity = 0.16;
                value.UserBubbleColor = "#8B5CF6"; value.UserBubbleOpacity = 0.30;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.10;
                break;
            case "樱粉":
                value.PanelColor = "#3B1024"; value.PanelOpacity = 0.84;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#FBCFE8"; value.AssistantBubbleOpacity = 0.18;
                value.UserBubbleColor = "#EC4899"; value.UserBubbleOpacity = 0.28;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.11;
                break;
            case "暖橙":
                value.PanelColor = "#3A1B08"; value.PanelOpacity = 0.84;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#FED7AA"; value.AssistantBubbleOpacity = 0.18;
                value.UserBubbleColor = "#F97316"; value.UserBubbleOpacity = 0.28;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.11;
                break;
            case "透明":
                value.PanelColor = "#000000"; value.PanelOpacity = 0.08;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#FFFFFF"; value.AssistantBubbleOpacity = 0.06;
                value.UserBubbleColor = "#556DF5"; value.UserBubbleOpacity = 0.16;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.07;
                break;
        }
    }

    static string NormalizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        string colorName = value.Trim();
        switch (colorName.ToLowerInvariant())
        {
            case "黑":
            case "black": return "#000000";
            case "白":
            case "white": return "#ffffff";
            case "灰":
            case "gray": return "#6b7280";
            case "红":
            case "red": return "#ef4444";
            case "蓝":
            case "blue": return "#2f6fed";
            case "绿":
            case "green": return "#22a06b";
            case "黄":
            case "yellow": return "#facc15";
            case "紫":
            case "purple": return "#8b5cf6";
            case "粉":
            case "pink": return "#ff7eb6";
            case "橙":
            case "orange": return "#ff9f43";
            case "青":
            case "cyan": return "#22d3ee";
        }

        string color = colorName;
        if (color.StartsWith("#") == false)
            color = "#" + color;

        if (color.Length == 4)
            color = $"#{color[1]}{color[1]}{color[2]}{color[2]}{color[3]}{color[3]}";

        if (color.Length != 7 || color.Skip(1).Any(item => Uri.IsHexDigit(item) == false))
            return fallback;

        return color.ToLowerInvariant();
    }

    static double ClampRatio(double value, double fallback)
    {
        return double.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;
    }

    bool HasHotkeysChanged(List<string> hotkeys)
    {
        if (registeredHotkeys.Count != hotkeys.Count)
            return true;

        for (int index = 0; index < hotkeys.Count; index++)
        {
            if (string.Equals(registeredHotkeys[index], hotkeys[index], StringComparison.OrdinalIgnoreCase) == false)
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        chatActivitySystem.Activated -= OnChatActivityActivated;
        chatActivitySystem.Deactivated -= OnChatActivityDeactivated;

        foreach (string petId in chatFinishedHandlers.Keys.ToList())
            DetachChatBot(petId);

        lock (stateGate)
        {
            foreach (QuickChatChannel channel in channels.Values.ToList())
                channel.Dispose();
            channels.Clear();
            deliveryDepths.Clear();
            pendingReplies.Clear();
            streamingTurns.Clear();
            toolReplyTargets.Clear();
        }

        store?.Dispose();
        store = null;

        foreach (string hotkey in registeredHotkeys)
        shortcutService.UnregisterAll(registeredHotkeys);
        registeredHotkeys.Clear();

        window?.Dispose();
        window = null;

        lock (CurrentGate)
        {
            if (ReferenceEquals(current, this))
                current = null;

            configSources.Clear();
            sourceConfigs.Clear();
            configSource = null;
        }
    }
}

/// <summary>
/// 一条已发出、等待回执的用户消息（发送登记里的「提货单」）。
/// <para>
/// 它存在的意义：让「这条消息是不是快聊发的」变成一个<b>可查证的事实</b>，
/// 而不是一次对正文的猜测。
/// </para>
/// </summary>
sealed class PendingOutgoing
{
    public string RequestId { get; init; } = string.Empty;
    public string PetId { get; init; } = string.Empty;
    public string MessageId { get; init; } = string.Empty;

    /// <summary>
    /// 这条消息落在哪个会话。
    /// <para>
    /// 不能拿 <see cref="PetId"/> 代替：一个角色可以有多个私聊会话
    /// （默认私聊 ID = 角色名，用户「新建」出来的多开私聊 ID 形如 <c>dm:小梦#2</c>），
    /// 用角色名当键会把「在小梦 #2 里发的消息」的送达状态刷到小梦 #1 上去。
    /// </para>
    /// </summary>
    public string ConversationId { get; init; } = string.Empty;

    /// <summary>展示层文本（已剥掉路径元数据），用于回执内容核对。</summary>
    public string DisplayText { get; init; } = string.Empty;

    public DateTimeOffset RegisteredAt { get; init; }

    /// <summary>是否已被 <c>ChatSent</c> 认领（认领 ⟺ 已进入对话上下文）。</summary>
    public bool Claimed { get; set; }
}

public class QuickChatPet
{
    public QuickChatPet(string id, string name, bool busy)
    {
        Id = id;
        Name = name;
        Busy = busy;
    }
    public string Id { get; }
    public string Name { get; }
    public bool Busy { get; }
}

/// <summary>
/// 一个会话。私聊会话的 Id 就是角色名（与旧版历史键完全兼容）；
/// 群聊会话的 Id 形如 <c>group:群名</c>，公共大厅是 <c>group:__lobby__</c>。
/// </summary>
public class QuickChatConversation
{
    public string Id { get; set; } = string.Empty;

    /// <summary>dm 或 group。</summary>
    public string Kind { get; set; } = "dm";

    /// <summary>顶栏显示的名字。私聊 = 角色名；群聊 = 群名。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>群聊成员（角色名）。私聊是单元素列表。</summary>
    public List<string> Members { get; set; } = new();

    /// <summary>私聊时该角色是否正在生成回复。</summary>
    public string CreatorId { get; set; } = QuickChatPrincipal.HumanId;
    public List<string> MutedMembers { get; set; } = new();
    public bool Busy { get; set; }

    /// <summary>是否是隐式的公共大厅（不在配置里，始终存在）。</summary>
    public bool IsLobby { get; set; }

    /// <summary>
    /// 是否是「自建会话」：用户点「新建会话」开出来的，或角色调工具自己拉群/开私聊建出来的。
    /// <para>
    /// 自建会话不在配置里，也不受「当前激活角色」的增删影响，只受显式删除影响。
    /// 前端据此区分展示（例如加一个小标记）。
    /// </para>
    /// </summary>
    public bool IsCustom { get; set; }

    /// <summary>
    /// 这条会话能不能被「真的删掉」。
    /// <para>
    /// <c>false</c>（受管会话：角色的私聊、公共大厅、配置里的小群）—— 它们的<b>存在性</b>由
    /// 「角色是否激活 / 配置里有没有这个群」决定，不归窗口管。窗口里那个按钮只能是「清空」，
    /// 点下去擦掉聊天记录，会话本身留着。
    /// </para>
    /// <para>
    /// <c>true</c>（自建会话）—— 没有别的存在依据，删了就删了。
    /// </para>
    /// </summary>
    public bool CanDelete { get; set; } = true;

    /// <summary>
    /// 最后一条消息的预览文本。会话记录区要显示「上一个记录」，
    /// 但前端手里只有打开过的会话，所以由后端把摘要一起带过去。
    /// </summary>
    public string LastText { get; set; } = string.Empty;

    /// <summary>最后一条消息的时间（已格式化，前端直接显示）。</summary>
    public string LastAt { get; set; } = string.Empty;

    /// <summary>本会话已有的消息条数。</summary>
    public int MessageCount { get; set; }
}

public class QuickChatMessage
{
    public string Id { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string PetId { get; set; } = string.Empty;
    public string PetName { get; set; } = string.Empty;

    /// <summary>这条消息属于哪个会话（私聊 = 角色名，群聊 = group:群名）。</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>会话类型：dm 或 group。</summary>
    public string Kind { get; set; } = "dm";

    /// <summary>这条消息在会话里显示为谁说的。私聊等于 PetName；群聊是具体发言成员。</summary>
    public string SenderName { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    /// <summary>用户消息的送达状态：空（不适用）/ sending / sent / failed。</summary>
    public string DeliveryState { get; set; } = string.Empty;

    public List<QuickChatAttachment> Attachments { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
}

public class QuickChatAttachment
{
    public string Kind { get; set; } = "file";
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ThumbnailPath { get; set; } = string.Empty;
}

sealed class RegionSelectionForm : System.Windows.Forms.Form
{
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int keyCode);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    enum DragKind
    {
        None,
        Select,
        Move,
        Resize
    }

    enum ResizeHandle
    {
        None,
        NorthWest,
        North,
        NorthEast,
        East,
        SouthEast,
        South,
        SouthWest,
        West
    }

    const int HandleSize = 13;
    const int SelectionMinimumSize = 8;
    const uint SwpNoSize = 0x0001;
    const uint SwpNoMove = 0x0002;
    const uint SwpNoActivate = 0x0010;
    const uint SwpShowWindow = 0x0040;
    static readonly IntPtr HwndTopMost = new(-1);

    public System.Drawing.Rectangle SelectedRectangle { get; private set; } = System.Drawing.Rectangle.Empty;
    readonly System.Drawing.Rectangle monitorBounds;
    readonly System.Drawing.Bitmap screenPreview;
    System.Drawing.Rectangle currentRectangle = System.Drawing.Rectangle.Empty;
    System.Drawing.Rectangle dragSourceRectangle = System.Drawing.Rectangle.Empty;
    System.Drawing.Rectangle previousRectangle = System.Drawing.Rectangle.Empty;
    System.Drawing.Point selectionStartPoint;
    System.Drawing.Point dragStartPoint;
    DragKind dragKind = DragKind.None;
    ResizeHandle resizeHandle = ResizeHandle.None;
    bool hasSelection;
    bool showToolbar;
    bool escapeWasDown;
    bool previousHasSelection;
    bool previousShowToolbar;
    System.Drawing.Rectangle confirmButtonRectangle = System.Drawing.Rectangle.Empty;
    System.Drawing.Rectangle retryButtonRectangle = System.Drawing.Rectangle.Empty;
    System.Drawing.Rectangle cancelButtonRectangle = System.Drawing.Rectangle.Empty;
    readonly System.Windows.Forms.Timer escapeTimer = new() { Interval = 40 };
    readonly System.Windows.Forms.Timer topMostTimer = new() { Interval = 80 };

    public RegionSelectionForm(System.Drawing.Rectangle bounds, System.Drawing.Bitmap preview)
    {
        monitorBounds = bounds;
        screenPreview = preview;
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
        StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        Bounds = bounds;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = System.Drawing.Color.Black;
        Opacity = 1D;
        Cursor = System.Windows.Forms.Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "QuickChat区域截图";
        escapeWasDown = (GetAsyncKeyState(0x1B) & 0x8000) != 0;

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        KeyDown += (_, eventArgs) => {
            if (eventArgs.KeyCode == System.Windows.Forms.Keys.Escape)
                Close();
            else if (eventArgs.KeyCode == System.Windows.Forms.Keys.Enter)
                ConfirmSelection();
        };
        escapeTimer.Tick += (_, _) => {
            bool escapeDown = (GetAsyncKeyState(0x1B) & 0x8000) != 0;
            if (escapeDown && escapeWasDown == false)
                Close();
            escapeWasDown = escapeDown;
        };
        topMostTimer.Tick += (_, _) => ForceTopMost();
    }

    protected override System.Windows.Forms.CreateParams CreateParams
    {
        get
        {
            System.Windows.Forms.CreateParams createParams = base.CreateParams;
            createParams.ExStyle |= 0x00000008; // WS_EX_TOPMOST
            createParams.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return createParams;
        }
    }

    protected override void OnShown(System.EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        ForceTopMost();
        Activate();
        Focus();
        escapeTimer.Start();
        topMostTimer.Start();
    }

    protected override void OnFormClosed(System.Windows.Forms.FormClosedEventArgs eventArgs)
    {
        escapeTimer.Stop();
        escapeTimer.Dispose();
        topMostTimer.Stop();
        topMostTimer.Dispose();
        base.OnFormClosed(eventArgs);
    }

    void ForceTopMost()
    {
        if (IsHandleCreated == false)
            return;

        _ = SetWindowPos(
            Handle,
            HwndTopMost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    protected override void OnPaintBackground(System.Windows.Forms.PaintEventArgs eventArgs)
    {
        if (screenPreview == null)
        {
            base.OnPaintBackground(eventArgs);
            return;
        }

        eventArgs.Graphics.DrawImage(
            screenPreview,
            new System.Drawing.Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
    }

    void OnMouseDown(object? sender, System.Windows.Forms.MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != System.Windows.Forms.MouseButtons.Left)
            return;

        System.Drawing.Point point = eventArgs.Location;
        if (showToolbar)
        {
            if (confirmButtonRectangle.Contains(point))
            {
                ConfirmSelection();
                return;
            }
            if (cancelButtonRectangle.Contains(point))
            {
                DialogResult = System.Windows.Forms.DialogResult.Cancel;
                Close();
                return;
            }
            if (retryButtonRectangle.Contains(point))
            {
                ResetSelection();
                return;
            }
        }

        dragStartPoint = point;
        if (hasSelection)
        {
            resizeHandle = GetResizeHandle(point);
            if (resizeHandle != ResizeHandle.None)
            {
                dragKind = DragKind.Resize;
                dragSourceRectangle = currentRectangle;
                return;
            }

            if (currentRectangle.Contains(point))
            {
                dragKind = DragKind.Move;
                dragSourceRectangle = currentRectangle;
                return;
            }
        }

        previousRectangle = currentRectangle;
        previousHasSelection = hasSelection;
        previousShowToolbar = showToolbar;
        dragKind = DragKind.Select;
        selectionStartPoint = point;
        currentRectangle = System.Drawing.Rectangle.Empty;
        hasSelection = false;
        showToolbar = false;
        Invalidate();
    }

    void OnMouseMove(object? sender, System.Windows.Forms.MouseEventArgs eventArgs)
    {
        System.Drawing.Point point = eventArgs.Location;
        if (dragKind == DragKind.Select)
        {
            currentRectangle = Normalize(point);
            hasSelection = currentRectangle.Width > 4 && currentRectangle.Height > 4;
            showToolbar = false;
            Invalidate();
            return;
        }
        if (dragKind == DragKind.Move)
        {
            currentRectangle = ClampRectangle(
                dragSourceRectangle,
                point.X - dragStartPoint.X,
                point.Y - dragStartPoint.Y);
            LayoutToolbar();
            Invalidate();
            return;
        }
        if (dragKind == DragKind.Resize)
        {
            currentRectangle = ResizeRectangle(dragSourceRectangle, point, resizeHandle);
            LayoutToolbar();
            Invalidate();
            return;
        }

        resizeHandle = hasSelection ? GetResizeHandle(point) : ResizeHandle.None;
        Cursor = resizeHandle switch
        {
            ResizeHandle.NorthWest or ResizeHandle.SouthEast => System.Windows.Forms.Cursors.SizeNWSE,
            ResizeHandle.NorthEast or ResizeHandle.SouthWest => System.Windows.Forms.Cursors.SizeNESW,
            ResizeHandle.North or ResizeHandle.South => System.Windows.Forms.Cursors.SizeNS,
            ResizeHandle.East or ResizeHandle.West => System.Windows.Forms.Cursors.SizeWE,
            _ => hasSelection && currentRectangle.Contains(point)
                ? System.Windows.Forms.Cursors.SizeAll
                : System.Windows.Forms.Cursors.Cross
        };
    }

    void OnMouseUp(object? sender, System.Windows.Forms.MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != System.Windows.Forms.MouseButtons.Left)
            return;

        if (dragKind == DragKind.Select)
        {
            System.Drawing.Rectangle rectangle = Normalize(eventArgs.Location);
            if (rectangle.Width < 5 || rectangle.Height < 5)
            {
                currentRectangle = previousHasSelection ? previousRectangle : System.Drawing.Rectangle.Empty;
                hasSelection = previousHasSelection;
                showToolbar = previousShowToolbar;
                if (showToolbar)
                    LayoutToolbar();
            }
            else
            {
                currentRectangle = rectangle;
                hasSelection = true;
                showToolbar = true;
                LayoutToolbar();
            }
        }
        else if (dragKind == DragKind.Move || dragKind == DragKind.Resize)
        {
            hasSelection = currentRectangle.Width >= SelectionMinimumSize && currentRectangle.Height >= SelectionMinimumSize;
            showToolbar = true;
            LayoutToolbar();
        }

        dragKind = DragKind.None;
        resizeHandle = ResizeHandle.None;
        Invalidate();
    }

    void ConfirmSelection()
    {
        if (hasSelection == false)
            return;

        SelectedRectangle = new System.Drawing.Rectangle(
            Bounds.Left + currentRectangle.Left,
            Bounds.Top + currentRectangle.Top,
            currentRectangle.Width,
            currentRectangle.Height);
        DialogResult = System.Windows.Forms.DialogResult.OK;
        Close();
    }

    void ResetSelection()
    {
        hasSelection = false;
        showToolbar = false;
        currentRectangle = System.Drawing.Rectangle.Empty;
        dragKind = DragKind.None;
        resizeHandle = ResizeHandle.None;
        Cursor = System.Windows.Forms.Cursors.Cross;
        Invalidate();
    }

    ResizeHandle GetResizeHandle(System.Drawing.Point point)
    {
        if (hasSelection == false)
            return ResizeHandle.None;

        System.Drawing.Point[] points = GetSelectionHandles();
        ResizeHandle[] handles =
        [
            ResizeHandle.NorthWest,
            ResizeHandle.NorthEast,
            ResizeHandle.SouthWest,
            ResizeHandle.SouthEast,
            ResizeHandle.West,
            ResizeHandle.East,
            ResizeHandle.North,
            ResizeHandle.South
        ];

        for (int index = 0; index < points.Length; index++)
        {
            System.Drawing.Rectangle hitBox = new(
                points[index].X - HandleSize,
                points[index].Y - HandleSize,
                HandleSize * 2,
                HandleSize * 2);
            if (hitBox.Contains(point))
                return handles[index];
        }

        return ResizeHandle.None;
    }

    System.Drawing.Point[] GetSelectionHandles()
    {
        int left = currentRectangle.Left;
        int top = currentRectangle.Top;
        int right = currentRectangle.Right;
        int bottom = currentRectangle.Bottom;
        int middleX = (left + right) / 2;
        int middleY = (top + bottom) / 2;
        return
        [
            new(left, top),
            new(right, top),
            new(left, bottom),
            new(right, bottom),
            new(left, middleY),
            new(right, middleY),
            new(middleX, top),
            new(middleX, bottom)
        ];
    }

    System.Drawing.Rectangle Normalize(System.Drawing.Point point)
    {
        return System.Drawing.Rectangle.FromLTRB(
            System.Math.Clamp(System.Math.Min(selectionStartPoint.X, point.X), 0, ClientSize.Width),
            System.Math.Clamp(System.Math.Min(selectionStartPoint.Y, point.Y), 0, ClientSize.Height),
            System.Math.Clamp(System.Math.Max(selectionStartPoint.X, point.X), 0, ClientSize.Width),
            System.Math.Clamp(System.Math.Max(selectionStartPoint.Y, point.Y), 0, ClientSize.Height));
    }

    System.Drawing.Rectangle ClampRectangle(System.Drawing.Rectangle rectangle, int offsetX, int offsetY)
    {
        int left = System.Math.Clamp(rectangle.Left + offsetX, 0, ClientSize.Width - rectangle.Width);
        int top = System.Math.Clamp(rectangle.Top + offsetY, 0, ClientSize.Height - rectangle.Height);
        return new System.Drawing.Rectangle(left, top, rectangle.Width, rectangle.Height);
    }

    System.Drawing.Rectangle ResizeRectangle(
        System.Drawing.Rectangle source,
        System.Drawing.Point point,
        ResizeHandle handle)
    {
        int left = source.Left;
        int top = source.Top;
        int right = source.Right;
        int bottom = source.Bottom;

        if (handle == ResizeHandle.West || handle == ResizeHandle.NorthWest || handle == ResizeHandle.SouthWest)
            left = System.Math.Clamp(point.X, 0, right - SelectionMinimumSize);
        if (handle == ResizeHandle.East || handle == ResizeHandle.NorthEast || handle == ResizeHandle.SouthEast)
            right = System.Math.Clamp(point.X, left + SelectionMinimumSize, ClientSize.Width);
        if (handle == ResizeHandle.North || handle == ResizeHandle.NorthWest || handle == ResizeHandle.NorthEast)
            top = System.Math.Clamp(point.Y, 0, bottom - SelectionMinimumSize);
        if (handle == ResizeHandle.South || handle == ResizeHandle.SouthWest || handle == ResizeHandle.SouthEast)
            bottom = System.Math.Clamp(point.Y, top + SelectionMinimumSize, ClientSize.Height);

        return System.Drawing.Rectangle.FromLTRB(left, top, right, bottom);
    }

    void LayoutToolbar()
    {
        int buttonWidth = 74;
        int buttonHeight = 30;
        int spacing = 8;
        int toolbarWidth = buttonWidth * 3 + spacing * 2;
        int x = System.Math.Clamp(
            currentRectangle.Left,
            12,
            System.Math.Max(12, ClientSize.Width - toolbarWidth - 12));
        int y = currentRectangle.Bottom + 12;
        if (y + buttonHeight > ClientSize.Height - 12)
            y = System.Math.Max(12, currentRectangle.Top - buttonHeight - 12);

        confirmButtonRectangle = new System.Drawing.Rectangle(x, y, buttonWidth, buttonHeight);
        retryButtonRectangle = new System.Drawing.Rectangle(x + buttonWidth + spacing, y, buttonWidth, buttonHeight);
        cancelButtonRectangle = new System.Drawing.Rectangle(x + (buttonWidth + spacing) * 2, y, buttonWidth, buttonHeight);
    }

    protected override void OnPaint(System.Windows.Forms.PaintEventArgs eventArgs)
    {
        // Draw dimming only outside the selection. The preview background inside
        // remains the exact original screen pixels.
        if (hasSelection)
        {
            using System.Drawing.SolidBrush dimBrush =
                new(System.Drawing.Color.FromArgb(97, 0, 0, 0));

            int left = currentRectangle.Left;
            int top = currentRectangle.Top;
            int right = currentRectangle.Right;
            int bottom = currentRectangle.Bottom;

            eventArgs.Graphics.FillRectangle(dimBrush, 0, 0, ClientSize.Width, top);
            eventArgs.Graphics.FillRectangle(dimBrush, 0, bottom, ClientSize.Width, ClientSize.Height - bottom);
            eventArgs.Graphics.FillRectangle(dimBrush, 0, top, left, currentRectangle.Height);
            eventArgs.Graphics.FillRectangle(dimBrush, right, top, ClientSize.Width - right, currentRectangle.Height);

            using System.Drawing.Pen pen = new(System.Drawing.Color.White, 2.4f);
            eventArgs.Graphics.DrawRectangle(pen, currentRectangle);

            foreach (System.Drawing.Point point in GetSelectionHandles())
            {
                System.Drawing.Rectangle handleBox = new(point.X - 5, point.Y - 5, 10, 10);
                using System.Drawing.SolidBrush handleBrush = new(System.Drawing.Color.White);
                using System.Drawing.Pen handlePen = new(System.Drawing.Color.FromArgb(30, 41, 59), 1.6f);
                eventArgs.Graphics.FillRectangle(handleBrush, handleBox);
                eventArgs.Graphics.DrawRectangle(handlePen, handleBox);
            }
        }
        else
        {
            using System.Drawing.SolidBrush dimBrush =
                new(System.Drawing.Color.FromArgb(97, 0, 0, 0));
            eventArgs.Graphics.FillRectangle(dimBrush, ClientRectangle);
        }

        string hint = hasSelection
            ? "8 个白点调整大小；选区其他位置拖动移动；框选外部重新选择；Enter 确认，Esc 取消"
            : "拖拽选择区域；Esc 取消";
        System.Drawing.Color hintShadow = System.Drawing.Color.FromArgb(160, 0, 0, 0);
        System.Windows.Forms.TextRenderer.DrawText(
            eventArgs.Graphics,
            hint,
            Font,
            new System.Drawing.Point(19, 19),
            hintShadow);
        System.Windows.Forms.TextRenderer.DrawText(
            eventArgs.Graphics,
            hint,
            Font,
            new System.Drawing.Point(18, 18),
            System.Drawing.Color.White);

        if (showToolbar == false)
            return;

        DrawToolbarButton(eventArgs.Graphics, confirmButtonRectangle, "确认截图", true);
        DrawToolbarButton(eventArgs.Graphics, retryButtonRectangle, "重新框选", false);
        DrawToolbarButton(eventArgs.Graphics, cancelButtonRectangle, "取消", false);
    }

    void DrawToolbarButton(
        System.Drawing.Graphics graphics,
        System.Drawing.Rectangle rectangle,
        string text,
        bool primary)
    {
        using System.Drawing.SolidBrush backgroundBrush = new(primary
            ? System.Drawing.Color.FromArgb(236, 244, 255)
            : System.Drawing.Color.FromArgb(82, 88, 100));
        using System.Drawing.SolidBrush textBrush = new(primary
            ? System.Drawing.Color.FromArgb(14, 18, 26)
            : System.Drawing.Color.White);
        graphics.FillRectangle(backgroundBrush, rectangle);
        System.Windows.Forms.TextRenderer.DrawText(
            graphics,
            text,
            Font,
            rectangle,
            textBrush.Color,
            System.Windows.Forms.TextFormatFlags.HorizontalCenter | System.Windows.Forms.TextFormatFlags.VerticalCenter);
    }
}












