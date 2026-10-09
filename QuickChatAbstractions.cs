using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace Marisa.QuickChat;

/// <summary>
/// 用户手动拖出来的窗口尺寸，坐标是「侧栏收起」的基准几何（不含展开时弹出去的那部分）。
/// <para>
/// 私聊和群聊分开记：它们的默认尺寸本来就差很多（群聊宽得多），用同一个值会让
/// 「只在私聊里拉过一次」把群聊也一起改窄。
/// </para>
/// </summary>
public readonly record struct QuickChatWindowSize(int Width, int Height);

// Shell-neutral contract used by QuickChatRuntime. Electron/Tauri adapters implement this.
public interface IQuickChatWindow : IDisposable
{
    event Action<string, System.Text.Json.JsonElement>? OnMessage;

    bool IsWindowDragging { get; }

    /// <param name="isGroup">
    /// 当前选中的是不是群聊。决定「用户手动拖过的尺寸」取哪一份（私聊和群聊分开记）。
    /// </param>
    Task ToggleAtMouseAsync(QuickChatConfig config, string wwwRoot, bool isGroup);

    /// <summary>从配置页按钮打开：已存在就显示并聚焦，不存在就按配置尺寸创建。</summary>
    Task ShowAsync(QuickChatConfig config, string wwwRoot, bool isGroup);

    Task BeginManualResizeAsync(string edge);
    Task UpdateManualResizeAsync();

    /// <summary>
    /// 手动拖拽结束。
    /// <para>
    /// <paramref name="isGroup"/> 决定把这次尺寸记到私聊还是群聊名下。必须记下来：
    /// 用户拉开一次，下次呼出、切换会话都该保持这个大小，否则每次都要重新拉。
    /// </para>
    /// </summary>
    void EndManualResize(bool isGroup);

    /// <summary>
    /// 用户手动拖过的尺寸（「侧栏收起」基准的宽高）。没拖过返回 <c>null</c>，
    /// 调用方回落到配置里的默认尺寸。
    /// </summary>
    QuickChatWindowSize? SavedSize(bool isGroup);

    /// <summary>
    /// 用户上次把侧栏留在「展开」还是「收起」。
    /// <para>
    /// 和窗口尺寸一样是<b>窗口级</b>状态（所有角色共用一个窗口），存在同一个文件里。
    /// 下次呼出要按它还原：用户上次开着，这次打开就该还开着。
    /// </para>
    /// </summary>
    bool SavedSidebarExpanded { get; }

    /// <summary>
    /// 记下用户对侧栏展开状态的明确选择。只在用户点了轨道时调用 ——
    /// 自动收起（选中会话、最小化、关掉群聊）不该覆盖用户的偏好。
    /// </summary>
    void RememberSidebarExpanded(bool expanded);

    Task BeginWindowDragAsync();
    Task UpdateWindowDragAsync();
    void EndWindowDrag();
    Task SetCompactHeightAsync(int desiredHeight, int minHeight);
    Task SetAdaptiveHeightAsync(int width, int desiredHeight, int minHeight, int maxHeight, bool autoFit);

    /// <summary>
    /// 侧栏展开/收起。展开时窗口向左加宽并左移，让面板「往外弹」，聊天区宽度保持不变。
    /// </summary>
    Task SetSidebarExpandedAsync(bool expanded);
    /// <summary>
    /// 只改「侧栏收起」基准宽度，高度保持不变。
    /// <para>
    /// 私聊窗口的高度归渲染层所有（它按消息内容自适应），后端只负责把宽度纠正到配置值。
    /// 用 <see cref="SetSize"/> 会把高度一起写掉，把已经自适应好的窗口压回最小高度。
    /// </para>
    /// </summary>
    void SetWidth(int width);
    void ShowAndFocus();
    void SetSize(int width, int height);
    void Hide();
    void Send(string type, object? payload = null);
}

public interface IQuickChatWindowFactory
{
    IQuickChatWindow Create(ILogger<QuickChatModule> logger);
}

public interface IQuickChatShortcutService
{
    void Register(string accelerator, Action callback);
    void Unregister(string accelerator);
    void UnregisterAll(System.Collections.Generic.IEnumerable<string> accelerators);
}



