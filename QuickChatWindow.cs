using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ElectronNET.API;
using ElectronNET.API.Entities;
using Microsoft.Extensions.Logging;
using Rectangle = ElectronNET.API.Entities.Rectangle;

namespace Marisa.QuickChat;

public sealed class ElectronQuickChatWindow : IQuickChatWindow
{
    BrowserWindow? window;
    ManualResizeState? manualResize;
    WindowDragState? windowDrag;
    int operationEpoch;
    int dragGeneration;
    bool windowDragPending;
    bool sidebarExpanded;
    readonly QuickChatBridge bridge;
    readonly ILogger<QuickChatModule> logger;

    public event Action<string, System.Text.Json.JsonElement>? OnMessage
    {
        add => bridge.OnMessage += value;
        remove => bridge.OnMessage -= value;
    }

    public ElectronQuickChatWindow(ILogger<QuickChatModule> logger)
    {
        this.logger = logger;
        bridge = new QuickChatBridge(logger);
    }

    public async Task ToggleAtMouseAsync(QuickChatConfig config, string wwwRoot, bool isGroup)
    {
        if (window != null && await window.IsDestroyedAsync())
            window = null;

        if (window != null && await window.IsVisibleAsync())
        {
            Hide();
            return;
        }

        ElectronNET.API.Entities.Point cursor = await Electron.Screen.GetCursorScreenPointAsync();
        Display display = await Electron.Screen.GetDisplayNearestPointAsync(cursor);
        Rectangle workArea = display.WorkArea;

        // 宽度用 EffectiveWindowWidth：开启群聊时它已经把左侧书签栏算进去了。
        QuickChatWindowSize? saved = SavedSize(isGroup);
        int width = Math.Clamp(
            saved is { } savedWidth ? savedWidth.Width : config.EffectiveWindowWidth,
            260,
            Math.Max(260, workArea.Width - 24));

        // 用户手动拖过就以他拖的高度出生，免得先弹一条小窗口、等渲染层上报才长开。
        int preferredHeight = saved is { } savedHeight
            ? savedHeight.Height
            : config.AutoFitHeight
                ? config.MinWindowHeight
                : config.WindowHeight;
        int height = Math.Clamp(preferredHeight, 120, Math.Max(120, workArea.Height - 24));

        bool placeRight = cursor.X < workArea.X + workArea.Width / 2;
        bool placeBelow = cursor.Y < workArea.Y + workArea.Height / 2;

        int x = placeRight
            ? cursor.X + 12
            : cursor.X - width - 12;

        int y = placeBelow
            ? cursor.Y + 12
            : cursor.Y - height - 12;

        x = Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - width));
        y = Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Y + workArea.Height - height));

        if (window == null)
        {
            await CreateAsync(config, wwwRoot, x, y, width, height);
            return;
        }

        // x / width 都是「侧栏收起」的基准几何；ToWindowBounds 负责把展开时那部分加回去。
        Rectangle current = await window.GetBoundsAsync();
        Rectangle baseBounds = ToBaseBounds(current);
        int targetHeight = config.AutoFitHeight ? baseBounds.Height : height;

        SetWindowBounds(ClampToWorkArea(ToWindowBounds(x, y, width, targetHeight), workArea));
        ShowAndFocus();
    }

    /// <summary>
    /// 从配置页按钮打开窗口：不切换可见性，直接显示并聚焦。
    /// 已存在就只调整尺寸，不存在才创建（创建位置取光标所在显示器中央，避免贴到屏幕边缘）。
    /// </summary>
    public async Task ShowAsync(QuickChatConfig config, string wwwRoot, bool isGroup)
    {
        if (window != null && await window.IsDestroyedAsync())
            window = null;

        QuickChatWindowSize? saved = SavedSize(isGroup);
        int width = saved is { } savedWidth ? savedWidth.Width : config.EffectiveWindowWidth;
        int preferredHeight = saved is { } savedHeight
            ? savedHeight.Height
            : config.AutoFitHeight
                ? config.MinWindowHeight
                : config.WindowHeight;

        if (window != null)
        {
            Rectangle current = await window.GetBoundsAsync();
            Rectangle baseBounds = ToBaseBounds(current);
            // 自适应高度时不能动高度：渲染层才知道当前内容有多高。写成 MinWindowHeight
            // 会把已经撑开的窗口压回最小高度，而消息没变时渲染层不会重新上报
            // （renderMessages 的签名去重会提前 return），窗口就被钉在最小高度上。
            int targetHeight = config.AutoFitHeight ? baseBounds.Height : preferredHeight;
            SetWindowBounds(ToWindowBounds(baseBounds.X, baseBounds.Y, width, targetHeight));
            ShowAndFocus();
            return;
        }

        ElectronNET.API.Entities.Point cursor = await Electron.Screen.GetCursorScreenPointAsync();
        Display display = await Electron.Screen.GetDisplayNearestPointAsync(cursor);
        Rectangle workArea = display.WorkArea;

        int safeWidth = Math.Clamp(width, 260, Math.Max(260, workArea.Width - 24));
        int height = Math.Clamp(preferredHeight, 120, Math.Max(120, workArea.Height - 24));

        int x = Math.Clamp(
            cursor.X - safeWidth / 2,
            workArea.X,
            Math.Max(workArea.X, workArea.X + workArea.Width - safeWidth));
        int y = Math.Clamp(
            cursor.Y - height / 2,
            workArea.Y,
            Math.Max(workArea.Y, workArea.Y + workArea.Height - height));

        await CreateAsync(config, wwwRoot, x, y, width, height);
    }

    async Task CreateAsync(QuickChatConfig config, string wwwRoot, int x, int y, int width, int height)
    {
        // 按用户上次留下的侧栏状态出生。
        // ToWindowBounds 会据此把窗口向左加宽、左边缘左移，所以第一帧就是最终几何，
        // 不会先窄一下再撑开；渲染层随后会收到 state 里的 sidebarExpanded，把 pinned 类补上，
        // 两边从第一帧起就是一致的。
        sidebarExpanded = SavedSidebarExpanded;

        Rectangle initial = ToWindowBounds(x, y, Math.Clamp(width, 260, 1400), height);

        string url = new Uri(Path.Combine(wwwRoot, "index.html")).AbsoluteUri
                     + $"?channel={bridge.ChannelId}&v={DateTime.UtcNow.Ticks}";

        BrowserWindowOptions options = new()
        {
            Title = "QuickChat",
            X = initial.X,
            Y = initial.Y,
            Width = initial.Width,
            Height = initial.Height,
            // Show 必须是 false：Show=true 时 Electron 会在渲染进程画出第一帧【之前】
            // 就把窗口显示出来，那一瞬间铺的是 BrowserWindow 的默认背景色（白 #FFFFFF），
            // 于是「白底 → 深色面板」就是每次打开都能看到的那下闪屏。
            // 配合 BackgroundColor 全透明 + OnReadyToShow 再 Show()，窗口一出现就是最终外观。
            // 同样的组合见 Alife.Function.DeskChat/DeskChatWindow.cs。
            Show = false,
            Frame = false,
            Transparent = true,
            // 透明窗口的背景色必须显式写成全透明。留默认值的话，除了首帧会闪白，
            // Hide() 之后再次 Show() 也会让 DWM 重新合成出那层白底。
            BackgroundColor = "#00000000",
            Resizable = false,
            MinWidth = 240,
            MinHeight = 56,
            Movable = true,
            SkipTaskbar = true,
            AlwaysOnTop = true,
            HasShadow = false,
            AcceptFirstMouse = true,
            AutoHideMenuBar = true,
            Fullscreenable = false,
            WebPreferences = new WebPreferences
            {
                BackgroundThrottling = false,
                NodeIntegration = true,
                ContextIsolation = false,
                Sandbox = false,
                DevTools = true
            }
        };


        window = await Electron.WindowManager.CreateWindowAsync(options, url);
        bridge.SetWindow(window);
        window.OnClosed += OnWindowClosed;
        window.OnShow += () => bridge.Send("refresh-state");

        // 窗口还没显示，所以这里不能直接 ShowAndFocus()（那会把 Show=false 的意义抵消掉）。
        // 挂到 OnReadyToShow：渲染进程画出第一帧之后才 Show()，用户看到的第一眼就是
        // 已经上完主题色的面板，不再有白底那一帧。
        // 注意 ShowAndFocus 本身保留 —— ToggleAtMouseAsync / ShowAsync 复用已存在窗口时还要用它。
        window.OnReadyToShow += () => ShowAndFocus();
        bridge.Send("refresh-state");
    }

    public async Task BeginManualResizeAsync(string edge)
    {
        if (window == null || await window.IsDestroyedAsync())
            return;

        Interlocked.Increment(ref operationEpoch);

        Rectangle bounds = await window.GetBoundsAsync();
        ElectronNET.API.Entities.Point cursor = await Electron.Screen.GetCursorScreenPointAsync();
        Display display = await Electron.Screen.GetDisplayNearestPointAsync(cursor);
        // 存基准几何：拖边缘时算的是「收起状态」的尺寸，展开的那部分由 ToWindowBounds 补回去。
        manualResize = new ManualResizeState(edge, cursor.X, cursor.Y, ToBaseBounds(bounds), display.WorkArea);
    }

    public async Task UpdateManualResizeAsync()
    {
        if (window == null || manualResize == null || await window.IsDestroyedAsync())
            return;

        ElectronNET.API.Entities.Point cursor = await Electron.Screen.GetCursorScreenPointAsync();
        ManualResizeState state = manualResize;
        int deltaX = cursor.X - state.CursorX;
        int deltaY = cursor.Y - state.CursorY;
        int width = state.Bounds.Width;
        int height = state.Bounds.Height;
        int x = state.Bounds.X;
        int y = state.Bounds.Y;

        if (state.Edge.Contains('e'))
            width = state.Bounds.Width + deltaX;
        if (state.Edge.Contains('w'))
            width = state.Bounds.Width - deltaX;
        if (state.Edge.Contains('s'))
            height = state.Bounds.Height + deltaY;
        if (state.Edge.Contains('n'))
            height = state.Bounds.Height - deltaY;

        int maxWidth = Math.Max(240, state.WorkArea.Width - 16);
        int maxHeight = Math.Max(56, state.WorkArea.Height - 16);
        width = Math.Clamp(width, 240, maxWidth);
        height = Math.Clamp(height, 56, maxHeight);

        if (state.Edge.Contains('w'))
            x = state.Bounds.X + state.Bounds.Width - width;
        if (state.Edge.Contains('n'))
            y = state.Bounds.Y + state.Bounds.Height - height;

        // state.Bounds 是基准几何，x/y/width/height 都在基准坐标系里算，最后统一补回展开宽度。
        SetWindowBounds(ToWindowBounds(x, y, width, height));
    }

    public void EndManualResize(bool isGroup)
    {
        bool wasResizing = manualResize != null;
        manualResize = null;

        // 只在这一轮真的拖过之后才记。EndManualResize 也会在「没拖动」的情况下被调用
        // （比如 pointerup 落在别处），那种时候把当前尺寸当成「用户的选择」会污染存档。
        if (wasResizing)
            _ = RememberSizeAsync(isGroup);
    }

    /// <summary>把当前窗口的「基准尺寸」存下来，供下次呼出 / 切换会话使用。</summary>
    async Task RememberSizeAsync(bool isGroup)
    {
        try
        {
            if (window == null || await window.IsDestroyedAsync())
                return;

            Rectangle bounds = await window.GetBoundsAsync();
            Rectangle baseBounds = ToBaseBounds(bounds);
            SetSavedSize(isGroup, new QuickChatWindowSize(baseBounds.Width, baseBounds.Height));
        }
        catch (Exception exception)
        {
            logger.LogDebug("记录窗口尺寸失败：" + exception.Message);
        }
    }

    public QuickChatWindowSize? SavedSize(bool isGroup)
    {
        EnsureSavedSizesLoaded();
        return isGroup ? savedGroupSize : savedDirectSize;
    }

    public bool SavedSidebarExpanded
    {
        get
        {
            EnsureSavedSizesLoaded();
            return savedSidebarExpanded;
        }
    }

    public void RememberSidebarExpanded(bool expanded)
    {
        EnsureSavedSizesLoaded();
        if (savedSidebarExpanded == expanded)
            return;

        savedSidebarExpanded = expanded;
        SaveWindowState();
    }

    // ────────────────────────── 窗口状态存档（尺寸 + 侧栏）──────────────────────────
    //
    // 存 <Storage>/QuickChat/window.json。为什么不放配置里：QuickChat 的配置是「按角色」
    // 存的（ChatActivity 用 character.StorageKey 取配置），而窗口只有一个、所有角色共用，
    // 放进某个角色的配置里既语义不对，也会出现「激活哪个角色就变成哪个大小」。

    /// <summary>窗口状态存档路径。跟会话数据放在同一个目录下。</summary>
    static string SavedSizePath =>
        Path.Combine(Alife.Foundation.AlifePath.StorageFolderPath, "QuickChat", "window.json");

    void EnsureSavedSizesLoaded()
    {
        if (savedSizesLoaded)
            return;
        savedSizesLoaded = true;

        try
        {
            string path = SavedSizePath;
            if (File.Exists(path) == false)
                return;

            using System.Text.Json.JsonDocument document =
                System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            savedDirectSize = ReadSize(document.RootElement, "direct");
            savedGroupSize = ReadSize(document.RootElement, "group");

            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                document.RootElement.TryGetProperty("sidebarExpanded", out System.Text.Json.JsonElement sidebar) &&
                (sidebar.ValueKind == System.Text.Json.JsonValueKind.True ||
                 sidebar.ValueKind == System.Text.Json.JsonValueKind.False))
            {
                savedSidebarExpanded = sidebar.GetBoolean();
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug("读取窗口状态失败：" + exception.Message);
        }
    }

    static QuickChatWindowSize? ReadSize(System.Text.Json.JsonElement root, string key)
    {
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
            root.TryGetProperty(key, out System.Text.Json.JsonElement node) == false ||
            node.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        if (node.TryGetProperty("width", out System.Text.Json.JsonElement widthNode) == false ||
            node.TryGetProperty("height", out System.Text.Json.JsonElement heightNode) == false ||
            widthNode.TryGetInt32(out int width) == false ||
            heightNode.TryGetInt32(out int height) == false)
        {
            return null;
        }

        return width >= 240 && height >= 56 ? new QuickChatWindowSize(width, height) : null;
    }

    void SetSavedSize(bool isGroup, QuickChatWindowSize size)
    {
        EnsureSavedSizesLoaded();

        if (isGroup)
            savedGroupSize = size;
        else
            savedDirectSize = size;

        SaveWindowState();
    }

    void SaveWindowState()
    {
        try
        {
            string path = SavedSizePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                direct = savedDirectSize is { } direct ? new { width = direct.Width, height = direct.Height } : null,
                group = savedGroupSize is { } group ? new { width = group.Width, height = group.Height } : null,
                // 侧栏「开着还是关着」。用户点了轨道才写，下次呼出按它还原。
                sidebarExpanded = savedSidebarExpanded,
            }));
        }
        catch (Exception exception)
        {
            logger.LogDebug("写入窗口状态失败：" + exception.Message);
        }
    }

    QuickChatWindowSize? savedDirectSize;
    QuickChatWindowSize? savedGroupSize;
    bool savedSidebarExpanded;
    bool savedSizesLoaded;

    sealed class ManualResizeState
    {
        public ManualResizeState(string edge, int cursorX, int cursorY, Rectangle bounds, Rectangle workArea)
        {
            Edge = edge.ToLowerInvariant();
            CursorX = cursorX;
            CursorY = cursorY;
            Bounds = bounds;
            WorkArea = workArea;
        }

        public string Edge { get; }
        public int CursorX { get; }
        public int CursorY { get; }
        public Rectangle Bounds { get; }
        public Rectangle WorkArea { get; }
    }

    public bool IsWindowDragging => windowDrag != null || windowDragPending;

    public async Task BeginWindowDragAsync()
    {
        int dragId = Interlocked.Increment(ref dragGeneration);
        windowDragPending = true;
        Interlocked.Increment(ref operationEpoch);

        try
        {
            if (window == null || await window.IsDestroyedAsync())
                return;

            ElectronNET.API.Entities.Point cursor = await Electron.Screen.GetCursorScreenPointAsync();
            Rectangle bounds = await window.GetBoundsAsync();
            Display display = await Electron.Screen.GetDisplayNearestPointAsync(cursor);

            // pointerup 可能在 Begin 的几个异步 IPC 返回之前先到达。
            // EndWindowDrag 会递增 dragGeneration，旧 Begin 不能再设置拖拽状态。
            if (Volatile.Read(ref dragGeneration) != dragId || windowDragPending == false)
                return;

            // 同样存基准几何，拖动时按基准算位置，再补回展开宽度。
            windowDrag = new WindowDragState(cursor.X, cursor.Y, ToBaseBounds(bounds), display.WorkArea);
        }
        finally
        {
            if (windowDrag == null && Volatile.Read(ref dragGeneration) == dragId)
                windowDragPending = false;
        }
    }

    public async Task UpdateWindowDragAsync()
    {
        if (window == null || windowDrag == null || await window.IsDestroyedAsync())
            return;

        ElectronNET.API.Entities.Point cursor = await Electron.Screen.GetCursorScreenPointAsync();
        WindowDragState state = windowDrag;
        int x = state.Bounds.X + cursor.X - state.CursorX;
        int y = state.Bounds.Y + cursor.Y - state.CursorY;

        // 基准坐标：实际窗口左边缘要再减掉展开宽度，夹取范围也要跟着挪同样的距离。
        int extra = SidebarExtra;
        int minX = state.WorkArea.X + extra;
        int maxX = Math.Max(minX, state.WorkArea.X + state.WorkArea.Width - state.Bounds.Width);
        x = Math.Clamp(x, minX, maxX);
        y = Math.Clamp(
            y,
            state.WorkArea.Y,
            Math.Max(state.WorkArea.Y, state.WorkArea.Y + state.WorkArea.Height - state.Bounds.Height));

        if (x != state.Bounds.X || y != state.Bounds.Y)
            window.SetPosition(x - extra, y);
    }

    public void EndWindowDrag()
    {
        Interlocked.Increment(ref dragGeneration);
        windowDrag = null;
        windowDragPending = false;
    }

    sealed class WindowDragState
    {
        public WindowDragState(int cursorX, int cursorY, Rectangle bounds, Rectangle workArea)
        {
            CursorX = cursorX;
            CursorY = cursorY;
            Bounds = bounds;
            WorkArea = workArea;
        }

        public int CursorX { get; }
        public int CursorY { get; }
        public Rectangle Bounds { get; }
        public Rectangle WorkArea { get; }
    }

    // Resizable=false 会挡住原生边缘拖拽，也会挡住程序化改尺寸，所以每次都要临时放开。
    void SetWindowBounds(Rectangle bounds)
    {
        window.SetResizable(true);
        window.SetBounds(bounds);
        window.SetResizable(false);
    }

    // ────────────────────────── 侧栏「往外弹」的几何换算 ──────────────────────────

    /// <summary>侧栏展开时面板额外占用的宽度，也就是窗口要向左长出去的部分。</summary>
    int SidebarExtra => sidebarExpanded
        ? Math.Max(0, QuickChatConfig.SidebarPanelWidth - QuickChatConfig.SidebarRailWidth)
        : 0;

    /// <summary>
    /// 真实窗口矩形 → 「侧栏收起」的基准几何。
    /// <para>
    /// 窗口几何一律以基准来记，展开/收起只改这一个 offset。这样就不用去每一处
    /// <c>SetPosition</c> / <c>SetSize</c> 调用点上重复加减 —— 漏掉任何一处，
    /// 窗口都会在展开状态下缩回原宽，把聊天区重新挤窄。
    /// </para>
    /// </summary>
    Rectangle ToBaseBounds(Rectangle bounds)
    {
        int extra = SidebarExtra;
        return new Rectangle
        {
            X = bounds.X + extra,
            Y = bounds.Y,
            Width = Math.Max(QuickChatConfig.SidebarRailWidth, bounds.Width - extra),
            Height = bounds.Height,
        };
    }

    /// <summary>
    /// 基准几何 → 真实窗口矩形：展开时左边缘外移、宽度加宽，<b>右边缘不动</b>，
    /// 所以聊天区的位置和宽度都保持不变，只有面板从窗口左侧「弹」出去。
    /// </summary>
    Rectangle ToWindowBounds(int baseX, int baseY, int baseWidth, int baseHeight)
    {
        int extra = SidebarExtra;
        return new Rectangle
        {
            X = baseX - extra,
            Y = baseY,
            Width = Math.Max(QuickChatConfig.SidebarRailWidth, baseWidth) + extra,
            Height = baseHeight,
        };
    }

    static Rectangle ClampToWorkArea(Rectangle bounds, Rectangle workArea)
    {
        bounds.X = Math.Clamp(
            bounds.X, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - bounds.Width));
        bounds.Y = Math.Clamp(
            bounds.Y, workArea.Y, Math.Max(workArea.Y, workArea.Y + workArea.Height - bounds.Height));
        return bounds;
    }

    /// <summary>
    /// 侧栏展开/收起。展开时窗口向左加宽、左边缘左移，聊天区原地不动。
    /// </summary>
    public async Task SetSidebarExpandedAsync(bool expanded)
    {
        if (window == null || await window.IsDestroyedAsync())
            return;
        if (sidebarExpanded == expanded)
            return;

        // 先按「旧」的 offset 把真实窗口换算回基准，再翻转状态；
        // 顺序反了就会把同一段宽度加减两次。
        Rectangle bounds = await window.GetBoundsAsync();
        Rectangle baseBounds = ToBaseBounds(bounds);
        sidebarExpanded = expanded;

        Rectangle target = ToWindowBounds(baseBounds.X, baseBounds.Y, baseBounds.Width, baseBounds.Height);
        var anchor = new ElectronNET.API.Entities.Point
        {
            X = Math.Max(0, baseBounds.X),
            Y = Math.Max(0, baseBounds.Y),
        };
        Display display = await Electron.Screen.GetDisplayNearestPointAsync(anchor);

        SetWindowBounds(ClampToWorkArea(target, display.WorkArea));
    }

    public async Task SetCompactHeightAsync(int desiredHeight, int minHeight)
    {
        if (window == null || await window.IsDestroyedAsync())
            return;

        // 紧凑模式必须强制收缩，不被普通自适应高度或残留拖拽状态阻塞。
        Interlocked.Increment(ref operationEpoch);

        Rectangle bounds = await window.GetBoundsAsync();
        Rectangle baseBounds = ToBaseBounds(bounds);
        var cursor = new ElectronNET.API.Entities.Point
        {
            X = Math.Max(0, baseBounds.X),
            Y = Math.Max(0, baseBounds.Y),
        };
        Display display = await Electron.Screen.GetDisplayNearestPointAsync(cursor);
        Rectangle workArea = display.WorkArea;

        int allowedMax = Math.Max(minHeight, Math.Min(Math.Max(desiredHeight, minHeight), workArea.Height - 24));
        int height = Math.Clamp(desiredHeight, minHeight, allowedMax);
        int y = baseBounds.Y;
        if (y + height > workArea.Y + workArea.Height)
            y = Math.Max(workArea.Y, workArea.Y + workArea.Height - height);

        // 只改高度，宽度（含展开时的面板）保持不动。
        if (baseBounds.Y == y && baseBounds.Height == height)
            return;

        SetWindowBounds(ToWindowBounds(baseBounds.X, y, baseBounds.Width, height));
    }

    public async Task SetAdaptiveHeightAsync(        int width,
        int desiredHeight,
        int minHeight,
        int maxHeight,
        bool autoFit)
    {
        if (window == null || IsWindowDragging || await window.IsDestroyedAsync())
            return;

        int epoch = Volatile.Read(ref operationEpoch);

        if (autoFit == false)
            desiredHeight = maxHeight;

        Rectangle bounds = await window.GetBoundsAsync();
        if (IsWindowDragging || Volatile.Read(ref operationEpoch) != epoch)
            return;

        Rectangle baseBounds = ToBaseBounds(bounds);
        var cursor = new ElectronNET.API.Entities.Point
        {
            X = Math.Max(0, baseBounds.X),
            Y = Math.Max(0, baseBounds.Y),
        };
        Display display = await Electron.Screen.GetDisplayNearestPointAsync(cursor);
        if (IsWindowDragging || Volatile.Read(ref operationEpoch) != epoch)
            return;

        Rectangle workArea = display.WorkArea;

        int allowedMax = Math.Max(minHeight, Math.Min(maxHeight, workArea.Height - 24));
        int height = Math.Clamp(desiredHeight, Math.Min(minHeight, allowedMax), allowedMax);

        int y = baseBounds.Y;
        if (y + height > workArea.Y + workArea.Height)
            y = Math.Max(workArea.Y, workArea.Y + workArea.Height - height);

        // 宽度用基准（聊天区 + 轨道）；展开时面板那部分由 ToWindowBounds 补回去。
        int targetWidth = Math.Clamp(baseBounds.Width, 240, Math.Max(240, workArea.Width - 24));
        if (baseBounds.Width == targetWidth && baseBounds.Height == height && baseBounds.Y == y)
            return;

        if (IsWindowDragging || Volatile.Read(ref operationEpoch) != epoch)
            return;

        SetWindowBounds(ToWindowBounds(baseBounds.X, y, targetWidth, height));
    }
    public void ShowAndFocus()
    {
        if (window == null)
            return;

        window.SetAlwaysOnTop(true);
        window.Show();
        window.Focus();
    }

    /// <summary>
    /// 只改「侧栏收起」的基准宽度，高度原样保留。
    /// <para>
    /// 私聊窗口的高度由渲染层按消息内容自适应上报，后端不该插手。切会话时如果把高度
    /// 一起写掉（比如写成 MinWindowHeight），而渲染层因为消息没变而不再上报
    /// （<c>renderMessages</c> 的签名去重会提前 return），窗口就会被钉在最小高度上，
    /// 每次呼出都得手动拉高。
    /// </para>
    /// </summary>
    public void SetWidth(int width)
    {
        if (window == null)
            return;

        _ = ApplyBaseWidthAsync(width);
    }

    async Task ApplyBaseWidthAsync(int width)
    {
        try
        {
            if (window == null || await window.IsDestroyedAsync())
                return;

            Rectangle bounds = await window.GetBoundsAsync();
            Rectangle baseBounds = ToBaseBounds(bounds);
            if (baseBounds.Width == width)
                return;

            SetWindowBounds(ToWindowBounds(baseBounds.X, baseBounds.Y, width, baseBounds.Height));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "QuickChat 调整窗口宽度失败");
        }
    }

    /// <summary>
    /// 设置「侧栏收起」的基准尺寸。展开时面板那部分会被自动加回去 ——
    /// 否则切会话时窗口会缩回原宽，把刚弹出去的面板又压回聊天区里。
    /// </summary>
    public void SetSize(int width, int height)
    {
        if (window == null)
            return;

        _ = ApplyBaseSizeAsync(width, height);
    }

    async Task ApplyBaseSizeAsync(int width, int height)
    {
        try
        {
            if (window == null || await window.IsDestroyedAsync())
                return;

            Rectangle bounds = await window.GetBoundsAsync();
            Rectangle baseBounds = ToBaseBounds(bounds);
            if (baseBounds.Width == width && baseBounds.Height == height)
                return;

            SetWindowBounds(ToWindowBounds(baseBounds.X, baseBounds.Y, width, height));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "QuickChat 调整窗口尺寸失败");
        }
    }

    public void Hide()
    {
        if (window == null)
            return;

        // 这里刻意【不】把侧栏收回。
        // 侧栏「开着还是关着」是用户的偏好，存在 window.json 里；隐藏窗口只是看不见，
        // 不是「用户把它关了」。收起它会让下次呼出与用户的选择不一致 ——
        // 用户明确要求过「展开就保持展开，关闭就保持关闭，下次打开也清楚」。
        // 窗口几何也不用改：ToggleAtMouseAsync 走 ToWindowBounds，会把展开那部分原样补回来。
        window.Hide();
    }

    public void Send(string type, object? payload = null)
    {
        bridge.Send(type, payload);
    }

    void OnWindowClosed()
    {
        bridge.SetWindow(null);
        window = null;
    }

    public void Dispose()
    {
        bridge.SetWindow(null);
        bridge.Dispose();

        if (window == null)
            return;

        window.OnClosed -= OnWindowClosed;
        window.Destroy();
        window = null;
    }
}






