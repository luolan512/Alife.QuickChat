using System;
using System.Text.Json;
using System.Threading.Tasks;
using Alife.Client;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Marisa.QuickChat;

public sealed class QuickChatBridge : IDisposable
{
    public event Action<string, JsonElement>? OnMessage;

    public string ChannelId { get; } = "marisa-quick-chat-" + Guid.NewGuid().ToString("N");

    IAlifeWindow? window;
    readonly ILogger<QuickChatModule> logger;
    static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    public QuickChatBridge(ILogger<QuickChatModule> logger)
    {
        this.logger = logger;

    }

    public void SetWindow(IAlifeWindow? value)
    {
        if (window != null) window.MessageReceived -= OnIpcMessage;
        window = value;
        if (window != null) window.MessageReceived += OnIpcMessage;
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

            _ = window.PostMessageAsync(envelope.ToString(Formatting.None));
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

            if (type == "save-browser-file")
            {
                string requestId = root.GetProperty("requestId").GetString() ?? "";
                try {
                    string name = System.IO.Path.GetFileName(root.GetProperty("name").GetString() ?? "attachment.bin");
                    string encoded = root.GetProperty("data").GetString() ?? "";
                    string folder = System.IO.Path.Combine(Alife.Foundation.AlifePath.StorageFolderPath, "Plugins", "Marisa.QuickChat", "Temp", "Images");
                    System.IO.Directory.CreateDirectory(folder);
                    string target = System.IO.Path.Combine(folder, Guid.NewGuid().ToString("N") + "-" + name);
                    System.IO.File.WriteAllBytes(target, Convert.FromBase64String(encoded));
                    Send("browser-file-saved", new { requestId, path = target });
                } catch (Exception ex) { Send("browser-file-saved", new { requestId, error = ex.Message }); }
                return;
            }
            if (type == "open-local-file") {
                string path = root.GetProperty("path").GetString() ?? "";
                if (System.IO.File.Exists(path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute=true });
                return;
            }
            OnMessage?.Invoke(type, root);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "QuickChat 解析渲染进程消息失败");
        }
    }

    public void Dispose()
    {
        SetWindow(null);
    }
}



