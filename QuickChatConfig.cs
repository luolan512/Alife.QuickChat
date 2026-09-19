using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Marisa.QuickChat;

public class QuickChatConfig
{
    [DisplayName("全局快捷键")]
    [Description("全局快捷键列表。至少保留一个；使用 Electron Accelerator 格式，例如 Alt+Q、Ctrl+Alt+Space。")]
    public List<string> Hotkeys { get; set; } = new() { "Alt+Q" };

    [DisplayName("悬浮窗宽度")]
    [Description("悬浮窗宽度。")]
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

    [DisplayName("基础配色")]
    [Description("可选：自定义、深空、墨黑、雾白、蓝色、青色、绿色、紫色、樱粉、暖橙、透明。选择非自定义时会覆盖下方颜色；要单独调色请改为自定义。")]
    public string ColorPreset { get; set; } = "自定义";

    [DisplayName("磨砂底色")]
    [Description("可填 #RRGGBB，或基础色名：黑、白、灰、红、蓝、绿、黄、紫、粉、橙、青。")]
    public string PanelColor { get; set; } = "#111C28";

    [DisplayName("磨砂底色透明度")]
    [Description("范围 0 到 1，越小越透明。")]
    public double PanelOpacity { get; set; } = 0.55;


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
}


