using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace Marisa.QuickChat;

// Shell-neutral contract used by QuickChatRuntime. Electron/Tauri adapters implement this.
public interface IQuickChatWindow : IDisposable
{
    event Action<string, System.Text.Json.JsonElement>? OnMessage;

    bool IsWindowDragging { get; }

    Task ToggleAtMouseAsync(QuickChatConfig config, string wwwRoot);
    Task BeginManualResizeAsync(string edge);
    Task UpdateManualResizeAsync();
    void EndManualResize();
    Task BeginWindowDragAsync();
    Task UpdateWindowDragAsync();
    void EndWindowDrag();
    Task SetCompactHeightAsync(int desiredHeight, int minHeight);
    Task SetAdaptiveHeightAsync(int width, int desiredHeight, int minHeight, int maxHeight, bool autoFit);
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



