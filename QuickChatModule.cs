using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace Marisa.QuickChat;

[Module(
    "快聊",
    "通过全局快捷键在鼠标位置呼出磨砂半透明对话框，可快速与已激活桌宠对话。",
    defaultCategory: "Marisa", editorUI: typeof(QuickChatConfigUI))]
public class QuickChatModule(
    ChatActivitySystem chatActivitySystem,
    PluginSystem pluginSystem,
    XmlFunctionCaller functionCaller,
    ILogger<QuickChatModule> logger) :
    ChatBehaviour,
    IConfigurable<QuickChatConfig>
{
    public QuickChatConfig Configuration { get; set; } = new();

    QuickChatRuntime? runtime;
    XmlHandler? handler;

    protected override async Task OnAwake()
    {
        runtime = QuickChatRuntime.GetOrCreate(chatActivitySystem, pluginSystem, logger);
        runtime.AddReference(this);

        // 群聊发言工具必须注册在每个角色自己的函数调用器上，AI 才「知道有这回事」。
        // XmlHandler 走反射扫 [XmlFunction]，和 LocalChat 用的是同一套机制。
        handler = new XmlHandler(this);
        functionCaller.RegisterHandlerWithoutDocument(handler, DestroyCancellationToken);
        functionCaller.AddPlainAreas(PlainAreas);

        await runtime.ApplyConfigAsync(Configuration, this);
    }

    protected override Task OnUpdate()
    {
        runtime?.ApplyConfig(Configuration, this);
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        if (runtime == null)
            return Task.CompletedTask;

        if (runtime.Release(this))
            runtime = null;

        return Task.CompletedTask;
    }

    /// <summary>
    /// 从配置页的按钮打开快聊窗口。
    /// 配置页在 Alife 的模块设置里，窗口却是 Electron 的独立悬浮窗，
    /// 给个按钮省得每次都要记全局快捷键。
    /// </summary>
    public Task OpenWindowAsync() => runtime?.OpenWindowAsync() ?? Task.CompletedTask;

    // ────────────────────────── 工具 ──────────────────────────

    /// <summary>
    /// 带正文的标签。它们必须在 <c>Start</c> 之前登记为纯文本区，否则正文里的
    /// <c>&lt;</c> / <c>&amp;</c> 会被当成 XML 解析。
    /// </summary>
    public static readonly string[] PlainAreas =
    [
        nameof(QuickChatSend),
        nameof(QuickChatDirect),
    ];

    // ────────────────────────── 配置页用 ──────────────────────────

    /// <summary>框架内全部角色名（含未激活）。配置页的「探测角色」用它。</summary>
    public IReadOnlyList<string> GetAllCharacterNames() => QuickChatRuntime.GetAllCharacterNames();

    /// <summary>当前已激活的角色名。</summary>
    public IReadOnlyList<string> GetActiveCharacterNames() =>
        runtime?.GetActiveCharacterNames() ?? (IReadOnlyList<string>)Array.Empty<string>();

    public IReadOnlyList<QuickChatConversation> GetSmallGroups() =>
        runtime?.GetSmallGroups() ?? Array.Empty<QuickChatConversation>();
    public string UpdateSmallGroup(string id, IEnumerable<string> members, IEnumerable<string> muted) =>
        runtime?.UpdateSmallGroup(id, members, muted) ?? "快聊尚未运行。";
    public string DeleteSmallGroup(string id) => runtime?.DeleteSmallGroup(id) ?? "快聊尚未运行。";
    public string CreateSmallGroup(string name) => runtime?.CreateSmallGroup(name) ?? "快聊尚未运行。";

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出你在快聊里的全部会话：会话 ID、标题、是私聊还是群聊、成员、未读数。"
        + "发言前先用它确认 conversation 该填什么。")]
    public void QuickChatList()
    {
        if (runtime == null)
            return;
        runtime.ReturnToolResult(Character.Name, nameof(QuickChatList), runtime.ListForAgent(Character.Name));
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("读取快聊里一个会话的聊天记录。conversation 填会话 ID（用 QuickChatList 查）。"
        + "默认读最近的若干条；想知道更早聊过什么，把返回里给出的 before 值原样填进来再调一次，"
        + "可以一直往前翻到这个会话的开头。")]
    public void QuickChatRead(
        string conversation,
        [Description("最多读多少条，1 到 100；不填或填 0 = 用配置里的默认条数")]
        int limit = 0,
        [Description("可选，只看编号小于它的更早消息（翻页用）。不填 = 从最新的开始；"
            + "往前翻时填上一次返回里给出的那个 before 值")]
        long before = 0)
    {
        if (runtime == null)
            return;
        runtime.ReturnToolResult(Character.Name, nameof(QuickChatRead),
            runtime.ReadForAgent(Character.Name, conversation, limit, before));
    }

    [XmlFunction(FunctionMode.Content)]
    [Description("在快聊的**群聊**里发送一条消息。conversation 必须原样照抄 QuickChatList 给出的会话 ID"
        + "（群聊形如 group:xxx，默认公共大厅是 group:__lobby__），不要自己拼 ID。"
        + "私聊（kind=dm）不要用它——**当那条私聊正是刚才叫醒你的会话时**，直接输出文字就是发言。"
        + "标签正文就是消息内容。")]
    public void QuickChatSend(XmlExecutorContext context, string conversation, [XmlContent] string text)
    {
        if (context.CallMode != CallMode.Closing)
            return;

        try
        {
            if (runtime == null)
                throw new InvalidOperationException("快聊尚未运行");
            if (string.IsNullOrWhiteSpace(conversation))
                throw new InvalidOperationException("缺少 conversation 参数");

            // 注意：流式解析下 context.Content 在 Closing 时是空的，
            // 累积正文在 FullContent（AboveContent + Content）里。
            runtime.SendFromAgent(Character.Name, conversation.Trim(), context.FullContent.Trim());
        }
        catch (Exception exception)
        {
            runtime?.ReturnToolResult(Character.Name, "QuickChatSendError", "发送失败：" + exception.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("创建一个快聊群聊，把角色拉进来。name 是群名；members 是角色名（逗号分隔），"
        + "留空表示把当前所有已激活的角色都拉进来。建好之后用 QuickChatSend 在群里发言。")]
    public void QuickChatGroupCreate(
        string name,
        [Description("成员角色名，逗号分隔；留空 = 当前所有已激活角色")] string members = "")
    {
        if (runtime == null)
            return;

        runtime.ReturnToolResult(
            Character.Name,
            nameof(QuickChatGroupCreate),
            runtime.CreateGroupFromAgent(Character.Name, name, members));
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("管理自己创建的小群。conversation 填群 ID；character 填角色名；action 为 add 拉人、remove 清退、mute 限制回复、unmute 解除限制。不能清退或限制主人。")]
    public void QuickChatGroupManage(string conversation, string character, string action)
    {
        runtime?.ReturnToolResult(Character.Name, nameof(QuickChatGroupManage),
            runtime.ManageGroupFromAgent(Character.Name, conversation, character, action));
    }

    [XmlFunction(FunctionMode.Content)]
    [Description("主动找另一个角色说话：把消息发给指定角色，对方会收到。character 是对方角色名，"
        + "标签正文是消息内容。第一次会自动开一条你们俩的私聊线，之后可以继续用同一个 conversation 发言。"
        + "**只有「想主动找某个角色」时才用它** —— 如果你正被某条私聊叫醒（kind=dm），"
        + "在那条私聊里直接输出文字就是回复，不必用它；想跟主人说话也不要用它，主人会自己来找你。")]
    public void QuickChatDirect(XmlExecutorContext context, string character, [XmlContent] string text)
    {
        if (context.CallMode != CallMode.Closing)
            return;

        try
        {
            if (runtime == null)
                throw new InvalidOperationException("快聊尚未运行");

            runtime.ReturnToolResult(
                Character.Name,
                nameof(QuickChatDirect),
                runtime.SendDirectFromAgent(Character.Name, character, context.FullContent.Trim()));
        }
        catch (Exception exception)
        {
            runtime?.ReturnToolResult(Character.Name, "QuickChatDirectError", "发送失败：" + exception.Message);
        }
    }
}
