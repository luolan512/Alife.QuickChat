using System;
using System.Text.Json;
using System.Threading.Tasks;
using ElectronNET.API;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Marisa.QuickChat;

public sealed class QuickChatBridge : IDisposable
{
    public event Action<string, JsonElement>? OnMessage;

    public string ChannelId { get; } = "marisa-quick-chat-" + Guid.NewGuid().ToString("N");

    BrowserWindow? window;
    readonly ILogger<QuickChatModule> logger;
    static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    public QuickChatBridge(ILogger<QuickChatModule> logger)
    {
        this.logger = logger;
        Electron.IpcMain.On(ChannelId, OnIpcMessage);
    }

    public void SetWindow(BrowserWindow? value)
    {
        window = value;
    }

    public void Send(string type, object? payload = null)
    {
        if (window == null)
            return;

        try
        {
            JObject envelope = new()
            {
                ["type"] = type
            };

            if (payload != null)
            {
                JObject payloadObject = JObject.FromObject(payload, Newtonsoft.Json.JsonSerializer.Create(JsonSettings));
                foreach (var pair in payloadObject.Properties())
                    envelope[pair.Name] = pair.Value;
            }

            Electron.IpcMain.Send(window, ChannelId, envelope.ToString(Formatting.None));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "QuickChat 发送渲染进程消息失败");
        }
    }

    void OnIpcMessage(object? payload)
    {
        if (payload is not string json || string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("type", out JsonElement typeElement) == false)
                return;

            string? type = typeElement.GetString();
            if (string.IsNullOrWhiteSpace(type))
                return;

            OnMessage?.Invoke(type, root);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "QuickChat 解析渲染进程消息失败");
        }
    }

    public void Dispose()
    {
        Electron.IpcMain.RemoveAllListeners(ChannelId);
        SetWindow(null);
    }
}



