using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Marisa.QuickChat;

/// <summary>手机网页只暴露会话读取和发送，不复用桌面窗口控制接口。</summary>
internal sealed class QuickChatLanServer : IAsyncDisposable
{
    readonly Func<object> conversations;
    readonly Func<string, object?> history;
    readonly Func<string, string, string[], Task> send;
    readonly Func<string, string, int, string?> attachment;
    readonly QuickChatLanFiles files;
    readonly string pagePath;
    WebApplication? app;
    public QuickChatLanServer(string pagePath, Func<object> conversations, Func<string, object?> history, Func<string, string, Task> send)
        : this(pagePath, conversations, history, (id, text, _) => send(id, text), (_, _, _) => null, null) { }
    public QuickChatLanServer(string pagePath, Func<object> conversations, Func<string, object?> history,
        Func<string, string, string[], Task> send, Func<string, string, int, string?> attachment, string? temporaryRoot)
    {
        this.pagePath = pagePath; this.conversations = conversations; this.history = history; this.send = send;
        this.attachment = attachment; files = new QuickChatLanFiles(temporaryRoot ?? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pagePath)!, "..", "..", "Temp", "Images")));
    }
    public Func<string, string?>? CreateConversation { get; set; }
    public Action<string, bool>? ClearConversation { get; set; }
    public int Port { get; private set; }
    public string Status { get; private set; } = "未启动";

    static IPAddress[] PrivateAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
        .Where(a => { byte[] b=a.GetAddressBytes(); return b[0]==10 || (b[0]==172 && b[1]>=16 && b[1]<=31) || (b[0]==192 && b[1]==168) || (b[0]==169 && b[1]==254); })
        .Distinct().ToArray();
    public static string[] Addresses(int port) => PrivateAddresses().Select(a => $"http://{a}:{port}/").ToArray();

    public async Task StartAsync(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = typeof(QuickChatLanServer).Assembly.GetName().Name, ContentRootPath = Path.GetDirectoryName(pagePath) });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => {
            options.Limits.MaxRequestBodySize = QuickChatLanFiles.MaxFileBytes + 1024 * 1024;
            // A hotspot adapter may appear after this service starts. Listening
            // on IPv4 Any covers new adapters without restarting the service.
            options.Listen(IPAddress.Any, port);
        });
        var server = builder.Build();
        // Preserve LAN-only access even though the listener follows new adapters.
        server.Use(async (context, next) => {
            if (!IsLanAddress(context.Connection.LocalIpAddress) || !IsLanAddress(context.Connection.RemoteIpAddress)) {
                context.Response.StatusCode = 403; return;
            }
            await next(context);
        });
        server.UseRouting();
        server.MapGet("/", async context => {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(await File.ReadAllTextAsync(pagePath));
        });
        foreach (string asset in new[] { "themes.css", "themes.js", "mobile.css", "mobile.js" }) {
            string assetPath = Path.Combine(Path.GetDirectoryName(pagePath)!, asset);
            string contentType = asset.EndsWith(".css") ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8";
            server.MapGet("/" + asset, async context => {
                if (!File.Exists(assetPath)) { context.Response.StatusCode = 404; return; }
                context.Response.ContentType = contentType;
                context.Response.Headers.CacheControl = "no-cache";
                await context.Response.WriteAsync(await File.ReadAllTextAsync(assetPath));
            });
        }
        server.MapGet("/api/conversations", async context => { context.Response.Headers.CacheControl = "no-store"; await context.Response.WriteAsJsonAsync(conversations()); });
        server.MapGet("/api/history", async context => {
            string id = context.Request.Query["id"].ToString();
            object? result = history(id);
            if (result == null) { context.Response.StatusCode = 404; return; }
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(result);
        });
        server.MapPost("/api/upload", async context => {
            if (!SameOrigin(context) || !context.Request.HasFormContentType) { context.Response.StatusCode = 403; return; }
            string id = context.Request.Query["conversationId"].ToString();
            if (history(id) == null) { context.Response.StatusCode = 404; return; }
            try {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                if (form.Files.Count != 1) { context.Response.StatusCode = 400; return; }
                var uploaded = await files.SaveAsync(form.Files[0], id, context.RequestAborted);
                await context.Response.WriteAsJsonAsync(uploaded.Public);
            } catch (InvalidDataException e) { context.Response.StatusCode = 413; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
            catch (BadHttpRequestException) { context.Response.StatusCode = 413; }
        });
        server.MapGet("/api/files/{id}", async context => {
            var file = files.Find(context.Request.RouteValues["id"]?.ToString() ?? "");
            if (file == null) { context.Response.StatusCode = 404; return; }
            await QuickChatLanFiles.ServeAsync(context, file.Path);
        });
        server.MapGet("/api/attachment", async context => {
            string id = context.Request.Query["conversationId"].ToString(), message = context.Request.Query["messageId"].ToString();
            if (!int.TryParse(context.Request.Query["index"], out int index) || index < 0 || history(id) == null) { context.Response.StatusCode = 404; return; }
            string? path = attachment(id, message, index);
            if (string.IsNullOrEmpty(path)) { context.Response.StatusCode = 404; return; }
            await QuickChatLanFiles.ServeAsync(context, path);
        });
        server.MapPost("/api/send", async context => {
            if (context.Request.ContentLength is > 16384) { context.Response.StatusCode = 413; return; }
            if (!context.Request.HasJsonContentType() || !SameOrigin(context)) { context.Response.StatusCode = 403; return; }
            LanMessage? input;
            try { input = await context.Request.ReadFromJsonAsync<LanMessage>(); }
            catch { context.Response.StatusCode = 400; return; }
            string id = input?.ConversationId?.Trim() ?? "", text = input?.Text?.Trim() ?? "";
            string[]? paths = files.Resolve(id, input?.AttachmentIds ?? Array.Empty<string>());
            if (id.Length == 0 || (text.Length == 0 && paths?.Length == 0) || text.Length > 4000 || paths == null || history(id) == null)
            { context.Response.StatusCode = 400; return; }
            _ = DispatchAsync(id, text, paths);
            context.Response.StatusCode = 202;
        });
        foreach (string action in new[] { "create", "clear" }) {
            server.MapPost("/api/conversation/" + action, async context => {
                if (!SameOrigin(context) || !context.Request.HasJsonContentType()) { context.Response.StatusCode = 403; return; }
                if (context.Request.ContentLength is > 16384) { context.Response.StatusCode = 413; return; }
                ConversationAction? input;
                try { input = await context.Request.ReadFromJsonAsync<ConversationAction>(); }
                catch { context.Response.StatusCode = 400; return; }
                string id = input?.ConversationId?.Trim() ?? "";
                if (id.Length == 0 || history(id) == null) { context.Response.StatusCode = 404; return; }
                if (action == "create") {
                    if (CreateConversation == null) { context.Response.StatusCode = 501; return; }
                    string? created = CreateConversation(id);
                    if (created == null) { context.Response.StatusCode = 409; return; }
                    await context.Response.WriteAsJsonAsync(new { conversationId = created });
                } else {
                    if (ClearConversation == null) { context.Response.StatusCode = 501; return; }
                    ClearConversation(id, input!.Purge);
                    context.Response.StatusCode = 204;
                }
            });
        }
        server.UseEndpoints(_ => { });
        try { await server.StartAsync(); app = server; Port = port; Status = "已启动"; }
        catch { await server.DisposeAsync(); throw; }
    }
    static bool IsLanAddress(IPAddress? address)
    {
        if (address == null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
    }
    static bool SameOrigin(HttpContext context) => string.Equals(context.Request.Headers.Origin.ToString(), $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase);
    async Task DispatchAsync(string id, string text, string[] paths)
    { try { await send(id, text, paths); } catch (Exception e) { QuickChatLog.Write("局域网发送失败：" + e); } }
    public async ValueTask DisposeAsync()
    {
        var server = app; app = null; Port = 0; Status = "已关闭";
        try { if (server != null) { await server.StopAsync(); await server.DisposeAsync(); } }
        finally { files.Dispose(); }
    }
    sealed class ConversationAction { public string? ConversationId { get; set; } public bool Purge { get; set; } }
    sealed class LanMessage { public string? ConversationId { get; set; } public string? Text { get; set; } public string[]? AttachmentIds { get; set; } }
}
