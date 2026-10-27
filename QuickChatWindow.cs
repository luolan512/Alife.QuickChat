using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Alife.Client;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace Marisa.QuickChat;

public sealed class AlifeQuickChatWindow : IQuickChatWindow
{
    readonly IAlifeApplication app;
    readonly QuickChatBridge bridge;
    readonly ILogger logger;
    readonly object gate = new();
    readonly SemaphoreSlim commitGate = new(1, 1);
    readonly SemaphoreSlim createGate = new(1, 1);
    readonly SemaphoreSlim visibilityGate = new(1, 1);
    readonly QuickChatWindowGeometry geometry = new(new Rectangle(100, 100, 348, 460),
        QuickChatConfig.SidebarRailWidth, QuickChatConfig.SidebarPanelWidth);
    IAlifeWindow? window;
    bool disposed, visible, dragging, sizesLoaded, savedSidebar;
    string? resizeEdge;
    Point operationOrigin;
    Rectangle operationBounds;
    long revision, appliedRevision = -1;
    Size paintedClientSize;
    QuickChatWindowSize? directSize, groupSize;

    public event Action<string, System.Text.Json.JsonElement>? OnMessage
    {
        add => bridge.OnMessage += value;
        remove => bridge.OnMessage -= value;
    }
    public bool IsWindowDragging { get { lock (gate) return dragging || resizeEdge != null; } }
    public int LayoutWidth { get { lock (gate) return geometry.WindowBounds.Width; } }
    public long LayoutRevision { get { lock (gate) return appliedRevision; } }
    public bool IsLayoutCurrent { get { lock (gate) return appliedRevision == revision; } }
    public bool SavedSidebarExpanded { get { LoadSizes(); return savedSidebar; } }
    static string StatePath => Path.Combine(Alife.Foundation.AlifePath.StorageFolderPath, "QuickChat", "window.json");

    public AlifeQuickChatWindow(IAlifeApplication app, ILogger<QuickChatModule> logger)
    {
        this.app = app;
        this.logger = logger;
        bridge = new QuickChatBridge(logger);
    }

    Point Cursor()
    {
        var point = app.GetCursorPosition();
        float dpi = Math.Max(1, app.GetDpiScale());
        return new Point((int)(point.X / dpi), (int)(point.Y / dpi));
    }

    void KeepOnCursorScreen(QuickChatWindowGeometry g)
    {
        // Work in the same logical coordinates as IAlifeWindow.SetBoundsAsync.
        var screen = System.Windows.Forms.Screen.FromPoint(app.GetCursorPosition()).WorkingArea;
        float dpi = Math.Max(1, app.GetDpiScale());
        int left = (int)Math.Floor(screen.Left / dpi), top = (int)Math.Floor(screen.Top / dpi);
        int right = (int)Math.Ceiling(screen.Right / dpi), bottom = (int)Math.Ceiling(screen.Bottom / dpi);
        var bounds = g.WindowBounds;
        int x = Math.Clamp(bounds.X, left, Math.Max(left, right - bounds.Width));
        int y = Math.Clamp(bounds.Y, top, Math.Max(top, bottom - bounds.Height));
        g.MoveTo(new Point(x + g.SidebarExtra, y));
    }

    async Task CreateAsync(QuickChatConfig config, string root, bool group)
    {
        await createGate.WaitAsync();
        try
        {
            if (disposed || window != null) return;
            var point = Cursor();
            var size = SavedSize(group);
            int chatWidth = group ? Math.Max(config.GroupWindowWidth, config.WindowWidth) : config.WindowWidth;
            int width = size?.Width ?? QuickChatWindowGeometry.DefaultBaseWidth(chatWidth, config.EnableGroupChat, QuickChatConfig.SidebarRailWidth);
            int height = size?.Height ?? (group ? config.GroupWindowHeight : config.WindowHeight);
            lock (gate)
            {
                geometry.SetBaseBounds(new Rectangle(point.X, point.Y, width, height));
                geometry.SetSidebar(config.EnableGroupChat && SavedSidebarExpanded);
                KeepOnCursorScreen(geometry);
                revision++;
            }
            string url = new Uri(Path.Combine(root, "index.html")).AbsoluteUri
                + "?channel=" + bridge.ChannelId + "&v=20261027-layout4";
            window = await app.CreateWindowAsync(new AlifeWindowOptions {
                Title = "快聊", Url = url, Bounds = geometry.WindowBounds,
                TopMost = true, Transparent = true, Resizable = false
            });
            bridge.SetWindow(window);
            await window.Launch();
            await FlushBoundsAsync();
            await window.ShowAsync();
            RepaintTransparentHost(window, true);
            visible = true;
        }
        finally { createGate.Release(); }
    }

    async Task FlushBoundsAsync()
    {
        await commitGate.WaitAsync();
        try
        {
            while (true)
            {
                IAlifeWindow? target;
                Rectangle bounds;
                long version;
                lock (gate)
                {
                    target = window;
                    version = revision;
                    if (disposed || target == null || version == appliedRevision) return;
                    bounds = geometry.WindowBounds;
                }
                Control? focusedView = null;
                if (target is Form host && !host.IsDisposed && host.IsHandleCreated) {
                    void CaptureFocus() {
                        if (host.ContainsFocus)
                            foreach (Control child in host.Controls)
                                if (child.ContainsFocus) { focusedView = child; break; }
                    }
                    if (host.InvokeRequired) host.Invoke((Action)CaptureFocus); else CaptureFocus();
                }
                await target.SetBoundsAsync(bounds);
                RepaintTransparentHost(target, false);
                if (focusedView != null && target is Form focusedHost) {
                    void RestoreFocus() {
                        if (!focusedView.IsDisposed && Form.ActiveForm == focusedHost && !focusedView.ContainsFocus &&
                            (focusedHost.ActiveControl == null || focusedHost.ActiveControl == focusedView))
                            focusedView.Focus();
                    }
                    if (!focusedHost.IsDisposed && focusedHost.IsHandleCreated) {
                        if (focusedHost.InvokeRequired) focusedHost.Invoke((Action)RestoreFocus); else RestoreFocus();
                    }
                }
                lock (gate)
                {
                    appliedRevision = version;
                    if (version != revision) continue;
                }
                bridge.Send("window-layout", new { width = bounds.Width, height = bounds.Height, layoutRevision = version });
                return;
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "快聊窗口尺寸更新失败"); }
        finally { commitGate.Release(); }
    }

    Task ChangeGeometry(Action<QuickChatWindowGeometry> update)
    {
        lock (gate)
        {
            if (disposed) return Task.CompletedTask;
            var before = geometry.WindowBounds;
            update(geometry);
            if (before == geometry.WindowBounds) return Task.CompletedTask;
            revision++;
        }
        return FlushBoundsAsync();
    }

    void RepaintTransparentHost(IAlifeWindow target, bool force)
    {
        if (target is not Form form || form.IsDisposed || !form.IsHandleCreated) return;
        void Repaint()
        {
            if (form.IsDisposed) return;
            if (!force && paintedClientSize == form.ClientSize) return;
            // 宿主用透明键绘制背景。缩放后必须擦除新暴露的宿主区域，
            // 同时让 WebView 子窗口重绘，避免留下未刷新的黑色底边。
            // Invalidate() alone does not refresh WebView2's native composition
            // surface. Recreate its viewport size on the UI thread after growth.
            foreach (Control child in form.Controls) {
                if (child.GetType().FullName != "Microsoft.Web.WebView2.WinForms.WebView2") continue;
                var bounds = form.ClientRectangle;
                child.Bounds = new Rectangle(bounds.X, bounds.Y, bounds.Width, Math.Max(1, bounds.Height - 1));
                child.Bounds = bounds;
                child.GetType().GetProperty("DefaultBackgroundColor")?.SetValue(child, Color.Transparent);
                child.Invalidate();
            }
            form.PerformLayout();
            form.Invalidate(true);
            form.Update();
            paintedClientSize = form.ClientSize;
        }
        if (form.InvokeRequired) form.Invoke((Action)Repaint);
        else Repaint();
    }

    public async Task ToggleAtMouseAsync(QuickChatConfig config, string root, bool group)
    {
        await visibilityGate.WaitAsync();
        try
        {
            if (window == null) { await CreateAsync(config, root, group); return; }
            if (visible) { Hide(); return; }
            await ChangeGeometry(g => { g.MoveTo(Cursor()); KeepOnCursorScreen(g); });
            await ShowCoreAsync(config, root, group);
        }
        finally { visibilityGate.Release(); }
    }
    public async Task ShowAsync(QuickChatConfig config, string root, bool group)
    {
        await visibilityGate.WaitAsync();
        try { await ShowCoreAsync(config, root, group); }
        finally { visibilityGate.Release(); }
    }
    async Task ShowCoreAsync(QuickChatConfig config, string root, bool group)
    {
        if (window == null) { await CreateAsync(config, root, group); return; }
        await ChangeGeometry(KeepOnCursorScreen);
        await FlushBoundsAsync();
        await window.ShowAsync();
        RepaintTransparentHost(window, true);
        visible = true;
        bridge.Send("refresh-state");
    }

    public Task BeginManualResizeAsync(string edge)
    {
        lock (gate) { resizeEdge = edge; operationOrigin = Cursor(); operationBounds = geometry.WindowBounds; }
        return Task.CompletedTask;
    }
    public Task UpdateManualResizeAsync()
    {
        return ChangeGeometry(g => {
            if (resizeEdge == null) return;
            var point = Cursor();
            int dx = point.X - operationOrigin.X, dy = point.Y - operationOrigin.Y;
            Rectangle result = operationBounds;
            int minWidth = 240 + g.SidebarExtra;
            if (resizeEdge.Contains('e')) result.Width = Math.Max(minWidth, operationBounds.Width + dx);
            if (resizeEdge.Contains('s')) result.Height = Math.Max(96, operationBounds.Height + dy);
            if (resizeEdge.Contains('w')) { result.Width = Math.Max(minWidth, operationBounds.Width - dx); result.X = operationBounds.Right - result.Width; }
            if (resizeEdge.Contains('n')) { result.Height = Math.Max(96, operationBounds.Height - dy); result.Y = operationBounds.Bottom - result.Height; }
            g.SetPhysicalBounds(result);
        });
    }
    public void EndManualResize(bool group)
    {
        lock (gate)
        {
            if (resizeEdge == null) return;
            resizeEdge = null;
            var bounds = geometry.BaseBounds;
            var size = new QuickChatWindowSize(bounds.Width, bounds.Height);
            if (group) groupSize = size; else directSize = size;
        }
        SaveSizes();
    }
    public Task BeginWindowDragAsync()
    {
        lock (gate) { dragging = true; operationOrigin = Cursor(); operationBounds = geometry.BaseBounds; }
        return Task.CompletedTask;
    }
    public Task UpdateWindowDragAsync() => ChangeGeometry(g => {
        if (!dragging) return;
        var point = Cursor();
        g.MoveTo(new Point(operationBounds.X + point.X - operationOrigin.X, operationBounds.Y + point.Y - operationOrigin.Y));
    });
    public void EndWindowDrag() { lock (gate) dragging = false; }

    public Task SetSidebarExpandedAsync(bool expanded) => ChangeGeometry(g => g.SetSidebar(expanded));
    public Task SetCompactHeightAsync(int height, int min)
        => ChangeGeometry(g => g.SetHeight(Math.Max(min, height)));
    public Task SetAdaptiveHeightAsync(int width, int desired, int min, int max, bool autoFit)
    {
        return ChangeGeometry(g => {
            if (dragging || resizeEdge != null) return;
            g.SetHeight(autoFit ? Math.Clamp(desired, min, Math.Max(min, max)) : max);
        });
    }
    public void SetWidth(int width) => _ = ChangeGeometry(g => g.SetSize(width, g.BaseBounds.Height));
    public void SetSize(int width, int height) => _ = ChangeGeometry(g => g.SetSize(width, height));
    public void ShowAndFocus() { if (window != null) { visible = true; _ = window.ShowAsync(); } }
    public void Hide() { if (window != null) { visible = false; _ = window.HideAsync(); } }
    public void Send(string type, object? payload = null) => bridge.Send(type, payload);

    public QuickChatWindowSize? SavedSize(bool group) { LoadSizes(); return group ? groupSize : directSize; }
    public void RememberSidebarExpanded(bool expanded) { LoadSizes(); savedSidebar = expanded; SaveSizes(); }
    void LoadSizes()
    {
        if (sizesLoaded) return;
        sizesLoaded = true;
        try
        {
            if (!File.Exists(StatePath)) return;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(StatePath));
            var root = document.RootElement;
            QuickChatWindowSize? Read(string key)
            {
                if (!root.TryGetProperty(key, out var node) || node.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !node.TryGetProperty("width", out var w) || !node.TryGetProperty("height", out var h)
                    || !w.TryGetInt32(out int width) || !h.TryGetInt32(out int height)) return null;
                return width >= 240 && height >= 56 ? new QuickChatWindowSize(width, height) : null;
            }
            directSize = Read("direct"); groupSize = Read("group");
            if (root.TryGetProperty("sidebarExpanded", out var side) && (side.ValueKind == System.Text.Json.JsonValueKind.True || side.ValueKind == System.Text.Json.JsonValueKind.False)) savedSidebar = side.GetBoolean();
        }
        catch (Exception ex) { logger.LogWarning(ex, "读取快聊窗口状态失败"); }
    }
    void SaveSizes()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, System.Text.Json.JsonSerializer.Serialize(new {
                direct = directSize is { } d ? new { width = d.Width, height = d.Height } : null,
                group = groupSize is { } g ? new { width = g.Width, height = g.Height } : null,
                sidebarExpanded = savedSidebar
            }));
        }
        catch (Exception ex) { logger.LogWarning(ex, "保存快聊窗口状态失败"); }
    }
    public void Dispose()
    {
        lock (gate) disposed = true;
        bridge.Dispose();
        if (window != null) { _ = window.DisposeAsync(); window = null; }
    }
}
