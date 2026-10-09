using System;
using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Marisa.QuickChat;

public class QuickChatConfig
{
    [DisplayName("全局快捷键")]
    [Description("全局快捷键列表。至少保留一个；使用 Electron Accelerator 格式，例如 Alt+Q、Ctrl+Alt+Space。")]
    public List<string> Hotkeys { get; set; } = new() { "Alt+Q" };

    [DisplayName("悬浮窗宽度")]
    [Description("悬浮窗宽度。开启群聊后左侧会多出一列书签栏，窗口宽度会自动加上它，所以这里始终表示「聊天区」的宽度。")]
    public int WindowWidth { get; set; } = 320;

    [DisplayName("最大悬浮窗高度")]
    [Description("最大悬浮窗高度。开启自适应高度时，它也是展开完整历史后的高度上限。")]
    public int WindowHeight { get; set; } = 460;

    [DisplayName("自适应高度")]
    [Description("开启后窗口高度会随消息数量变化；关闭后始终使用最大悬浮窗高度。")]
    public bool AutoFitHeight { get; set; } = true;

    [DisplayName("最小悬浮窗高度")]
    [Description("自适应模式下的最小窗口高度；最小化输入条会更紧凑。")]
    public int MinWindowHeight { get; set; } = 96;

    [DisplayName("单次加载消息数")]
    [Description("初次显示最近 N 条；向上滚动到顶部时再加载 N 条，可继续查看本次对话全部记录。")]
    public int MaxVisibleMessages { get; set; } = 6;

    [DisplayName("显示所有消息")]
    [Description("开启后关闭单次加载限制，一次性显示当前角色的全部快聊记录。")]
    public bool ShowAllMessages { get; set; } = true;

    [DisplayName("Esc 隐藏窗口")]
    [Description("是否允许按 Esc 隐藏快聊窗口。")]
    public bool HideOnEscape { get; set; } = true;

    [DisplayName("标记消息来源")]
    [Description("发送时是否在内部标记消息来源为 QuickChat。不会显示在对话内容中。")]
    public bool MarkMessageSource { get; set; } = true;

    [DisplayName("截图图片输入模式")]
    [Description("临时分析：图片临时发给模型分析，不保留在主上下文；多模态：图片保留在主上下文；仅路径：只发送路径文本，适合不支持图片的模型。可选值：临时分析、多模态、仅路径。")]
    [JsonConverter(typeof(StringEnumConverter))]
    public QuickChatScreenshotImageMode ScreenshotImageMode { get; set; } = QuickChatScreenshotImageMode.多模态;

    [DisplayName("基础配色")]
    [Description("可选：自定义、深空、墨黑、雾白、蓝色、青色、绿色、紫色、樱粉、暖橙、透明。选择非自定义时会覆盖下方颜色；要单独调色请改为自定义。")]
    public string ColorPreset { get; set; } = "自定义";

    [DisplayName("磨砂底色")]
    [Description("可填 #RRGGBB，或基础色名：黑、白、灰、红、蓝、绿、黄、紫、粉、橙、青。")]
    public string PanelColor { get; set; } = "#111C28";

    [DisplayName("磨砂底色透明度")]
    [Description("范围 0 到 1，越小越透明。太小会让文字压不住桌面背景，看不清；建议 0.8 以上。")]
    public double PanelOpacity { get; set; } = 0.80;


    [DisplayName("拖放图片模糊强度")]
    [Description("拖动图片到快聊窗口时，整窗提示遮罩的高斯模糊强度。范围 0 到 80。")]
    public double DragBackdropBlur { get; set; } = 42;

    [DisplayName("消息区底色")]
    [Description("可填 #RRGGBB 或基础色名。")]
    public string MessageListColor { get; set; } = "#000000";

    [DisplayName("消息区透明度")]
    [Description("范围 0 到 1，默认 0 表示完全透明。")]
    public double MessageListOpacity { get; set; } = 0;

    [DisplayName("桌宠气泡底色")]
    [Description("可填 #RRGGBB 或基础色名。")]
    public string AssistantBubbleColor { get; set; } = "#FFFFFF";

    [DisplayName("桌宠气泡透明度")]
    [Description("范围 0 到 1。")]
    public double AssistantBubbleOpacity { get; set; } = 0.10;

    [DisplayName("用户气泡底色")]
    [Description("可填 #RRGGBB 或基础色名。")]
    public string UserBubbleColor { get; set; } = "#556DF5";

    [DisplayName("用户气泡透明度")]
    [Description("范围 0 到 1。")]
    public double UserBubbleOpacity { get; set; } = 0.25;

    [DisplayName("输入框底色")]
    [Description("可填 #RRGGBB 或基础色名。")]
    public string InputColor { get; set; } = "#FFFFFF";

    [DisplayName("输入框透明度")]
    [Description("范围 0 到 1。")]
    public double InputOpacity { get; set; } = 0.085;

    [DisplayName("文字颜色")]
    [Description("可填 #RRGGBB 或基础色名。")]
    public string TextColor { get; set; } = "#EEF1F6";

    // ────────────────────────── 消息显示 ──────────────────────────

    /// <summary>
    /// 是否把模型的输出边生成边显示。
    /// <para>
    /// 开启：订阅 <c>ChatBot.ChatReceived</c>，每攒到一段就推给窗口，气泡边生成边长、
    /// 末尾带一个闪动光标；定稿时再换成清洗过的最终文本。
    /// </para>
    /// <para>
    /// 关闭：完全回到旧行为 —— 只在 <c>ChatFinished</c>（整轮生成结束）时才出现气泡。
    /// 输出较长时等待感很明显，所以默认开启。
    /// </para>
    /// </summary>
    [DisplayName("流式输出")]
    [Description("开启：模型边生成边显示，气泡实时增长、末尾带闪动光标；"
        + "关闭：等整轮生成结束再一次性出现。输出较长时关闭会有明显等待感。")]
    public bool StreamingOutput { get; set; } = true;

    // ────────────────────────── 群聊 ──────────────────────────

    [DisplayName("启用群聊")]
    [Description("开启后左侧书签栏会出现群聊入口（含默认的公共大厅）。关闭则只保留私聊。")]
    public bool EnableGroupChat { get; set; } = true;

    /// <summary>
    /// 鼠标扫过左侧轨道时是否自动弹开面板。
    /// <para>
    /// <b>默认关闭</b>：展开会连带把窗口向左撑宽，鼠标只是经过就被「撑开」一下很干扰。
    /// 关掉之后，只能用点击轨道来开关。
    /// </para>
    /// <para>
    /// ⚠ 键名与 4.7.x 的 <c>SidebarHoverExpand</c> 不同，这是<b>故意换的</b>：
    /// 老版本里这个开关默认是开的，存量配置里存着 <c>true</c>；如果沿用同一个键名，
    /// 「改成默认关闭」对老用户完全失效 —— 他们会继续被自动展开干扰。
    /// 换键之后老值自然失效（反序列化时找不到这个属性），要开的人自己去配置页打开。
    /// </para>
    /// </summary>
    [DisplayName("侧栏悬停展开")]
    [Description("开启：鼠标扫过左侧轨道就自动弹开面板（默认关闭）。"
        + "关闭时只能用点击轨道来开关；默认关闭是因为窗口会跟着向左变宽，鼠标只是经过时被「撑开」一下很干扰。")]
    public bool SidebarExpandOnHover { get; set; }

    [DisplayName("公共大厅名称")]
    [Description("默认群聊的名字。当前所有已激活的角色都在里面，相当于一个公共大厅。")]
    public string PublicLobbyName { get; set; } = "萝卜开会";

    [DisplayName("群聊窗口宽度")]
    [Description("切到群聊时窗口自动放大到的宽度；点回私聊会缩回「悬浮窗宽度」。")]
    public int GroupWindowWidth { get; set; } = 620;

    [DisplayName("群聊窗口高度")]
    [Description("切到群聊时窗口自动放大到的高度上限。")]
    public int GroupWindowHeight { get; set; } = 560;

    [DisplayName("群聊列表")]
    [Description("除了公共大厅之外，还可以自己拉小群。每个群填一个名字和成员（成员留空 = 当前所有已激活角色）。")]
    public List<QuickChatGroupConfig> Groups { get; set; } = new();

    // ────────────────────────── 记录与分页 ──────────────────────────

    [DisplayName("保存私聊记录")]
    [Description("开启后私聊会话会落盘，重启后仍能翻看历史；关闭则关窗即丢（保持旧行为）。")]
    public bool SaveDirectMessages { get; set; } = true;

    [DisplayName("保存群聊记录")]
    [Description("开启后群聊会话会落盘。临时开会那种场合可以关掉。")]
    public bool SaveGroupMessages { get; set; } = true;

    [DisplayName("历史分页条数")]
    [Description("每次从磁盘加载多少条历史消息；向上滚动到顶部时再加载一页。")]
    public int HistoryPageSize { get; set; } = 20;

    /// <summary>
    /// 角色调 <c>QuickChatRead</c> 且没填条数时读多少条。
    /// <para>
    /// 这个值<b>只影响默认值</b>：角色自己填了条数就按它填的来（上限 100），
    /// 想读更早的记录也不用靠调大它 —— 用返回里给出的 <c>before</c> 游标往前翻就行。
    /// 所以调大它的收益有限，反而更容易一次把上下文灌满。
    /// </para>
    /// </summary>
    [DisplayName("角色读取历史默认条数")]
    [Description("角色主动翻记录（QuickChatRead）时不填条数就按这个值读。范围 1 到 100。"
        + "更早的记录不用靠调大它，用返回里给出的 before 游标继续往前翻即可。")]
    public int AgentReadMessages { get; set; } = 30;

    [DisplayName("清理默认包含原始记录")]
    [Description("快聊窗口右上角「清理」的确认弹窗里，那个勾选框的默认状态。"
        + "开启（默认）＝默认连磁盘上的原始记录一起删掉；关闭＝默认只清当前窗口的显示。"
        + "两种都只影响当前这一个会话，左侧其它会话的记录不受影响。")]
    public bool ClearIncludesOriginal { get; set; } = true;

    // ────────────────────────── 群聊投递门槛 ──────────────────────────

    [DisplayName("启用自动投递")]
    [Description("收到群聊消息时主动唤醒角色（主人发言、被 @、旁听命中都会触发）。关闭后角色仍可自己用工具查看和发言。")]
    public bool AutoDeliver { get; set; } = true;

    [DisplayName("单批最大消息数")]
    [Description("一次合并投递最多携带多少条消息，超出部分留到下一批。防止上下文被一次灌满。")]
    public int MaxBatchMessages { get; set; } = 16;

    [DisplayName("投递时附带上下文")]
    [Description("投递时附带该会话此前的若干条消息，帮助角色快速进入语境。0 表示不附带。")]
    public int ContextTail { get; set; } = 0;

    [DisplayName("漏回复时自动提醒")]
    [Description("角色收到了群聊消息却没有用 QuickChatSend 发言时，自动提醒它补上，避免「明明回了却没发出来」。")]
    public bool AutoCorrectMissingReply { get; set; } = true;

    [DisplayName("群聊旁听概率")]
    [Description("**只用于角色之间**：别的角色在群里说话、又没 @ 到你时，按此概率仍然把消息投递给你。"
        + "0 表示只在被 @ 时收到。主人自己的发言不受它影响 —— 主人说话一定会投递给全部成员。"
        + "被 @ 过之后的「活跃」状态不用这个值，用下面的「活跃会话旁听概率」。")]
    public double GroupListenChance { get; set; } = 0.12;

    [DisplayName("活跃会话旁听概率")]
    [Description("被 @ 到（或被主人点名）之后的一段时间里，别的角色在群里说话时按这个概率投递给你 —— "
        + "刚被叫到过，理应更留意这个群。未活跃时用「群聊旁听概率」。"
        + "**刻意不设成 1**：一旦无条件接收，主人随便起个头就会让所有角色互相把每句话都听全，"
        + "她们能一直接话聊下去。")]
    public double ActiveListenChance { get; set; } = 0.5;

    [DisplayName("角色接话轮数上限")]
    [Description("群聊里主人起个头之后，角色之间最多互相接这么多轮，到顶就自动断路，"
        + "不会再没完没了地接下去。主人自己的发言**不受这个限制**，随时可以重新起个话头。"
        + "设成 1 就等于「只有主人说话时才会把角色叫起来」；设得越大越热闹，也越容易刷屏。")]
    public int MaxRelayDepth { get; set; } = 3;

    [DisplayName("群聊静默时长（分钟）")]
    [Description("被 @ 激活后，超过该时长没有新的 @ 或主人发言，就退回普通旁听概率，不再享受「活跃会话旁听概率」。")]
    public int GroupIdleMinutes { get; set; } = 20;

    [DisplayName("消息合并窗口（秒）")]
    [Description("这段时间内到达的连续群消息会合并成一次投递，避免一条消息唤醒一次角色。")]
    public double FlushDelaySeconds { get; set; } = 2.5;

    [DisplayName("每分钟最大投递轮次")]
    [Description("超出后改为延迟投递而不是丢弃消息，避免角色之间互相刷屏。")]
    public int MaxDeliveriesPerMinute { get; set; } = 12;

    // ────────────────────────── 派生值 ──────────────────────────

    /// <summary>
    /// 左侧轨道宽度（收起状态）。必须与 style.css 里 <c>#sidebar</c> 的宽度保持一致。
    /// <para>
    /// 展开后的面板用宽度动画撑开，会临时挤窄聊天区 —— 这是有意的：
    /// 浮层方案会盖住输入框和顶栏，鼠标移过去时「＋」按钮点不到。
    /// 所以窗口宽度只按收起状态算，弹出那一下的挤压是临时的。
    /// </para>
    /// </summary>
    public const int SidebarRailWidth = 28;

    /// <summary>
    /// 侧栏展开后的面板宽度。必须与 style.css 里 <c>--sidebar-panel-width</c> 保持一致。
    /// <para>
    /// 展开不是「把聊天区挤窄」，而是<b>把窗口向左加宽这么多</b>
    /// （见 <c>ElectronQuickChatWindow.SetSidebarExpandedAsync</c>）：面板从窗口左边缘往外长出去，
    /// 窗口左边缘同步左移，聊天区宽度自始至终不变。
    /// </para>
    /// <para>
    /// 宽度写死成常量而不是用百分比，是为了断开反馈环：窗口宽度会随展开变化，
    /// 如果用 <c>%</c>，面板宽度就会跟着窗口宽度变，两边永远对不齐。
    /// </para>
    /// </summary>
    public const int SidebarPanelWidth = 176;

    /// <summary>
    /// 实际窗口宽度。开启群聊后左边多一条轨道，窗口要相应加宽，
    /// 否则默认 320 的窗口留给聊天区只剩 292px。
    /// 加宽后「悬浮窗宽度」始终表示聊天区的宽度，语义不变。
    /// </summary>
    [JsonIgnore]
    public int EffectiveWindowWidth => WindowWidth + (EnableGroupChat ? SidebarRailWidth : 0);
}

/// <summary>一个群聊的定义。成员留空表示「当前所有已激活角色」。</summary>
public class QuickChatGroupConfig
{
    [DisplayName("群名")]
    [Description("显示在书签栏上的名字。")]
    public string Name { get; set; } = "";

    [DisplayName("成员")]
    [Description("角色名，用逗号分隔。留空 = 当前所有已激活角色，成员随激活状态自动增减。")]
    public string Members { get; set; } = "";
}



public enum QuickChatScreenshotImageMode
{
    临时分析,
    多模态,
    仅路径
}

