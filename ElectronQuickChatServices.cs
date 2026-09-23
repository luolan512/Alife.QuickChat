using System;
using System.Collections.Generic;
using ElectronNET.API;
using Microsoft.Extensions.Logging;

namespace Marisa.QuickChat;

// Electron implementation of the shell contracts. Keep all ElectronNET.API details here.
public sealed class ElectronQuickChatWindowFactory : IQuickChatWindowFactory
{
    public IQuickChatWindow Create(ILogger<QuickChatModule> logger)
        => new ElectronQuickChatWindow(logger);
}

public sealed class ElectronQuickChatShortcutService : IQuickChatShortcutService
{
    readonly ILogger<QuickChatModule> logger;

    public ElectronQuickChatShortcutService(ILogger<QuickChatModule> logger)
    {
        this.logger = logger;
    }

    public void Register(string accelerator, Action callback)
    {
        Electron.GlobalShortcut.Register(accelerator, callback);
    }

    public void Unregister(string accelerator)
    {
        Electron.GlobalShortcut.Unregister(accelerator);
    }

    public void UnregisterAll(System.Collections.Generic.IEnumerable<string> accelerators)
    {
        foreach (string accelerator in accelerators)
        {
            try
            {
                Electron.GlobalShortcut.Unregister(accelerator);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "QuickChat 快捷键注销失败：{Accelerator}", accelerator);
            }
        }
    }
}


