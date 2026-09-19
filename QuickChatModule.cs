using System;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace Marisa.QuickChat;

[Module(
    "快聊",
    "通过全局快捷键在鼠标位置呼出磨砂半透明对话框，可快速与已激活桌宠对话。",
    defaultCategory: "Marisa")]
public class QuickChatModule(
    ChatActivitySystem chatActivitySystem,
    PluginSystem pluginSystem,
    ILogger<QuickChatModule> logger) :
    ChatBehaviour,
    IConfigurable<QuickChatConfig>
{
    public QuickChatConfig Configuration { get; set; } = new();

    QuickChatRuntime? runtime;

    protected override async Task OnAwake()
    {
        runtime = QuickChatRuntime.GetOrCreate(chatActivitySystem, pluginSystem, logger);
        runtime.AddReference(this);
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
}



