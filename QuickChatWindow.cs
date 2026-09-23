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

    public async Task ToggleAtMouseAsync(QuickChatConfig config, string wwwRoot)
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

        int width = Math.Clamp(config.WindowWidth, 260, Math.Max(260, workArea.Width - 24));
        int preferredHeight = config.AutoFitHeight
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
            await CreateAsync(config, wwwRoot, x, y, height);
            return;
        }

        window.SetPosition(x, y);
        if (config.AutoFitHeight == false)
            ResizeWindow(width, height);
        ShowAndFocus();
    }

    async Task CreateAsync(QuickChatConfig config, string wwwRoot, int x, int y, int height)
    {
        string url = new Uri(Path.Combine(wwwRoot, "index.html")).AbsoluteUri
                     + $"?channel={bridge.ChannelId}&v={DateTime.UtcNow.Ticks}";

        BrowserWindowOptions options = new()
        {
            Title = "QuickChat",
            X = x,
            Y = y,
            Width = Math.Clamp(config.WindowWidth, 260, 1000),
            Height = height,
            Show = false,
            Frame = false,
            Transparent = true,
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
                NodeIntegration = true,
                ContextIsolation = false,
                Sandbox = false,
                DevTools = true
            }
        };


        window = await Electron.WindowManager.CreateWindowAsync(options, url);
        bridge.SetWindow(window);
        window.OnClosed += OnWindowClosed;
        ShowAndFocus();

    }

    public async Task BeginManualResizeAsync(string edge)
    {
        if (window == null || await window.IsDestroyedAsync())
            return;

        Interlocked.Increment(ref operationEpoch);

        Rectangle bounds = await window.GetBoundsAsync();
        ElectronNET.API.Entities.Point cursor = await Electron.Screen.GetCursorScreenPointAsync();
        Display display = await Electron.Screen.GetDisplayNearestPointAsync(cursor);
        manualResize = new ManualResizeState(edge, cursor.X, cursor.Y, bounds, display.WorkArea);
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

        SetWindowBounds(new Rectangle { X = x, Y = y, Width = width, Height = height });
    }

    public void EndManualResize()
    {
        manualResize = null;
    }

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

            windowDrag = new WindowDragState(cursor.X, cursor.Y, bounds, display.WorkArea);
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

        x = Math.Clamp(x, state.WorkArea.X, Math.Max(state.WorkArea.X, state.WorkArea.X + state.WorkArea.Width - state.Bounds.Width));
        y = Math.Clamp(y, state.WorkArea.Y, Math.Max(state.WorkArea.Y, state.WorkArea.Y + state.WorkArea.Height - state.Bounds.Height));

        if (x != state.Bounds.X || y != state.Bounds.Y)
            window.SetPosition(x, y);
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

    void ResizeWindow(int width, int height)
    {
        // Resizable=false prevents native edge resizing, but can also block programmatic SetSize.
        window.SetResizable(true);
        window.SetSize(width, height);
        window.SetResizable(false);
    }

    void SetWindowBounds(Rectangle bounds)
    {
        window.SetResizable(true);
        window.SetBounds(bounds);
        window.SetResizable(false);
    }

    public async Task SetCompactHeightAsync(int desiredHeight, int minHeight)
    {
        if (window == null || await window.IsDestroyedAsync())
            return;

        // 紧凑模式必须强制收缩，不被普通自适应高度或残留拖拽状态阻塞。
        Interlocked.Increment(ref operationEpoch);

        Rectangle bounds = await window.GetBoundsAsync();
        var cursor = new ElectronNET.API.Entities.Point { X = Math.Max(0, bounds.X), Y = Math.Max(0, bounds.Y) };
        Display display = await Electron.Screen.GetDisplayNearestPointAsync(cursor);
        Rectangle workArea = display.WorkArea;

        int allowedMax = Math.Max(minHeight, Math.Min(Math.Max(desiredHeight, minHeight), workArea.Height - 24));
        int height = Math.Clamp(desiredHeight, minHeight, allowedMax);
        int y = bounds.Y;
        if (y + height > workArea.Y + workArea.Height)
            y = Math.Max(workArea.Y, workArea.Y + workArea.Height - height);

        int targetWidth = bounds.Width;
        if (bounds.Width == targetWidth && bounds.Height == height && bounds.Y == y)
            return;

        if (bounds.Y != y)
            window.SetPosition(bounds.X, y);

        ResizeWindow(targetWidth, height);
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

        var cursor = new ElectronNET.API.Entities.Point { X = Math.Max(0, bounds.X), Y = Math.Max(0, bounds.Y) };
        Display display = await Electron.Screen.GetDisplayNearestPointAsync(cursor);
        if (IsWindowDragging || Volatile.Read(ref operationEpoch) != epoch)
            return;

        Rectangle workArea = display.WorkArea;

        int allowedMax = Math.Max(minHeight, Math.Min(maxHeight, workArea.Height - 24));
        int height = Math.Clamp(desiredHeight, Math.Min(minHeight, allowedMax), allowedMax);

        int y = bounds.Y;
        if (y + height > workArea.Y + workArea.Height)
            y = Math.Max(workArea.Y, workArea.Y + workArea.Height - height);

        int targetWidth = Math.Clamp(bounds.Width, 240, Math.Max(240, workArea.Width - 24));
        if (bounds.Width == targetWidth && bounds.Height == height && bounds.Y == y)
            return;

        if (IsWindowDragging || Volatile.Read(ref operationEpoch) != epoch)
            return;

        if (bounds.Y != y)
            window.SetPosition(bounds.X, y);

        if (bounds.Width != targetWidth || bounds.Height != height)
            ResizeWindow(targetWidth, height);
    }
    public void ShowAndFocus()
    {
        if (window == null)
            return;

        window.SetAlwaysOnTop(true);
        window.Show();
        window.Focus();
    }

    public void SetSize(int width, int height)
    {
        window?.SetSize(width, height);
    }

    public void Hide()
    {
        window?.Hide();
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






