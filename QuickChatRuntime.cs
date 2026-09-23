using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace Marisa.QuickChat;

public sealed class QuickChatRuntime : IDisposable
{
    static QuickChatRuntime? current;
    static readonly object CurrentGate = new();

    readonly ChatActivitySystem chatActivitySystem;
    readonly PluginSystem pluginSystem;
    readonly ILogger<QuickChatModule> logger;
    readonly Dictionary<string, List<QuickChatMessage>> histories = new();
    readonly HashSet<string> busyPetIds = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> busyRequestIds = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> registeredHotkeys = new();
    readonly object stateGate = new();
    readonly object configGate = new();
    readonly List<object> configSources = new();
    readonly Dictionary<object, QuickChatConfig> sourceConfigs = new();
    readonly Dictionary<object, string> sourcePetIds = new();
    readonly Dictionary<string, QuickChatConfig> petConfigs = new(StringComparer.OrdinalIgnoreCase);
    object? configSource;
    readonly Dictionary<string, ChatBot> subscribedBots = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Action<ChatContext>> chatFinishedHandlers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Action<string>> chatSentHandlers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Interactor<QuickChatModule>> promptInteractors = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<string>> pendingScreenshotAttachments = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> pendingAttachmentExpectedTexts = new(StringComparer.OrdinalIgnoreCase);

    static readonly HttpClient ImageHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly string QuickChatAttachmentPrompt = """
        在 QuickChat 快聊界面中发送图片或文件时，输出以下专用协议标记（不要用代码块包裹）：
        [[QuickChatAttachment]]完整本机路径或可访问的 http(s) 直链[[/QuickChatAttachment]]
        可输出多个标记。路径必须真实存在或可访问；不要编造路径，不要改用 Markdown 链接/图片语法。
        这是普通双中括号标记，不是 XML/HTML；不要输出 <QuickChatAttachment>，也不要写成 [QuickChatAttachment:路径]。
        图片可用完整本机路径或 http(s) 直链；文件优先使用完整本机路径。不要发送目录。
        用户发送的截图/图片/文件消息里包含完整路径，可以在需要引用时直接使用。
        """ + "\n";

    QuickChatConfig config = new();
    readonly IQuickChatWindowFactory windowFactory;
    readonly IQuickChatShortcutService shortcutService;
    IQuickChatWindow? window;
    string? selectedPetId;
    string configSignature = string.Empty;
    bool hasAppliedConfig;
    int referenceCount;
    bool disposed;

    QuickChatRuntime(
        ChatActivitySystem chatActivitySystem,
        PluginSystem pluginSystem,
        ILogger<QuickChatModule> logger,
        IQuickChatWindowFactory? windowFactory = null,
        IQuickChatShortcutService? shortcutService = null)
    {
        this.chatActivitySystem = chatActivitySystem;
        this.pluginSystem = pluginSystem;
        this.logger = logger;
        this.windowFactory = windowFactory ?? new ElectronQuickChatWindowFactory();
        this.shortcutService = shortcutService ?? new ElectronQuickChatShortcutService(logger);

        this.chatActivitySystem.Activated += OnChatActivityActivated;
        this.chatActivitySystem.Deactivated += OnChatActivityDeactivated;

        foreach (ChatActivity activity in chatActivitySystem.GetAllChatActivities().ToList())
            AttachChatBot(activity);
    }

    public static QuickChatRuntime GetOrCreate(
        ChatActivitySystem chatActivitySystem,
        PluginSystem pluginSystem,
        ILogger<QuickChatModule> logger,
        IQuickChatWindowFactory? windowFactory = null,
        IQuickChatShortcutService? shortcutService = null)
    {
        lock (CurrentGate)
        {
            if (current == null)
                current = new QuickChatRuntime(
                    chatActivitySystem,
                    pluginSystem,
                    logger,
                    windowFactory,
                    shortcutService);

            return current;
        }
    }

    public void AddReference(object? source)
    {
        if (source == null)
            return;

        lock (configGate)
        {
            referenceCount++;
            if (configSources.Contains(source) == false)
            {
                configSources.Add(source);
                sourceConfigs[source] = config;
            }

            configSource ??= source;
        }
    }

    public bool Release(object? source)
    {
        if (source == null)
            return false;

        bool shouldDispose;
        lock (configGate)
        {
            referenceCount--;
            if (referenceCount < 0)
                referenceCount = 0;

            sourceConfigs.Remove(source);
            if (sourcePetIds.Remove(source, out string? removedPetId))
                petConfigs.Remove(removedPetId);
            configSources.Remove(source);

            if (ReferenceEquals(configSource, source))
                configSource = configSources.FirstOrDefault();

            shouldDispose = referenceCount <= 0;
        }

        if (shouldDispose)
        {
            Dispose();
            return true;
        }

        return false;
    }

    public void ApplyConfig(QuickChatConfig value, object? source)
    {
        QuickChatConfig normalized = NormalizeConfig(value);

        lock (configGate)
        {
            if (source != null)
            {
                if (configSources.Contains(source) == false)
                {
                    configSources.Add(source);
                    configSource ??= source;
                }

                sourceConfigs[source] = normalized;
                if (source is ChatBehaviour behaviour)
                {
                    string petId = behaviour.Character.Name;
                    sourcePetIds[source] = petId;
                    petConfigs[petId] = normalized;
                }

                // 多个桌宠都会实例化 QuickChat 模块。全局悬浮窗只允许一个配置源，
                // 否则不同角色的配置会在每帧 Update 中互相抢占并反复 resize。
                if (ReferenceEquals(source, configSource) == false)
                    return;
            }

            string signature = CreateConfigSignature(normalized);
            if (hasAppliedConfig && string.Equals(signature, configSignature, StringComparison.Ordinal))
                return;

            config = normalized;
            configSignature = signature;
            hasAppliedConfig = true;
        }

        RegisterHotkeys();
        if (window != null)
        {
            int height = config.AutoFitHeight ? config.MinWindowHeight : config.WindowHeight;
            window.SetSize(config.WindowWidth, height);
        }

        SendState();
    }

    public Task ApplyConfigAsync(QuickChatConfig value, object? source)
    {
        ApplyConfig(value, source);
        return Task.CompletedTask;
    }

    void OnChatActivityActivated(ChatActivity activity)
    {
        AttachChatBot(activity);
        EnsureSelectedPet();
        SendState();
        SendSelectedHistory();
    }

    void OnChatActivityDeactivated(ChatActivity activity)
    {
        DetachChatBot(activity.Character.Name);
        EnsureSelectedPet();
        SendState();
        SendSelectedHistory();
    }

    void AttachChatBot(ChatActivity activity)
    {
        if (disposed || activity.ChatBot == null)
            return;

        string petId = activity.Character.Name;
        if (subscribedBots.TryGetValue(petId, out ChatBot? existing))
        {
            if (ReferenceEquals(existing, activity.ChatBot))
                return;
            DetachChatBot(petId);
        }

        Action<ChatContext> handler = context => OnChatFinished(petId, context);
        Action<string> sentHandler = text => OnChatSent(petId, text);
        activity.ChatBot.ChatFinished += handler;
        activity.ChatBot.ChatSent += sentHandler;
        subscribedBots[petId] = activity.ChatBot;
        chatFinishedHandlers[petId] = handler;
        chatSentHandlers[petId] = sentHandler;

        if (promptInteractors.ContainsKey(petId) == false)
        {
            Interactor<QuickChatModule> promptInteractor = new(activity.ChatBot);
            promptInteractor.Prompt(QuickChatAttachmentPrompt);
            promptInteractors[petId] = promptInteractor;
        }
    }

    void DetachChatBot(string? petId)
    {
        if (string.IsNullOrWhiteSpace(petId))
            return;

        if (subscribedBots.Remove(petId, out ChatBot? bot))
        {
            if (chatFinishedHandlers.Remove(petId, out Action<ChatContext>? handler))
                bot.ChatFinished -= handler;

            if (chatSentHandlers.Remove(petId, out Action<string>? sentHandler))
                bot.ChatSent -= sentHandler;
        }

        if (promptInteractors.Remove(petId, out Interactor<QuickChatModule>? promptInteractor))
            promptInteractor.Dispose();
    }

    void OnChatSent(string petId, string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
            return;

        // Poke/SystemEventService/温柔纸条等是模块注入，不是用户主动输入。
        if (QuickChatContentFilter.IsInjectedSystemMessage(rawMessage))
            return;

        List<string> pendingPaths = TakePendingScreenshotAttachments(petId, rawMessage);
        if (pendingPaths.Count > 0)
        {
            string displayText = QuickChatContentFilter.CleanOutgoingDisplayText(rawMessage, pendingPaths);
            string imageDirectory = Path.Combine(
                pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.QuickChat"),
                "Temp",
                "Images");

            List<QuickChatAttachment> attachments = new();
            foreach (string path in pendingPaths)
            {
                var attachment = new QuickChatAttachment {
                    Kind = IsImagePath(path) ? "image" : "file",
                    Path = path,
                    Name = Path.GetFileName(path),
                    ThumbnailPath = string.Empty
                };

                try
                {
                    if (IsImagePath(path))
                        attachment.ThumbnailPath = CreateScreenshotThumbnail(path, imageDirectory);
                }
                catch
                {
                    // Thumbnail failure still leaves the original attachment visible.
                }

                attachments.Add(attachment);
            }

            // Empty display text is valid: attachment cards should stand alone.
            AddMessage(petId, "user", displayText, attachments);
            return;
        }

        string text = QuickChatContentFilter.CleanUserText(rawMessage);
        if (string.IsNullOrWhiteSpace(text) == false)
            AddMessage(petId, "user", text);
    }

    void OnChatFinished(string petId, ChatContext context)
    {
        _ = AddAssistantMessageAsync(petId, context);
    }

    async Task AddAssistantMessageAsync(string petId, ChatContext context)
    {
        try
        {
            List<string> attachmentSources = QuickChatContentFilter.ExtractQuickChatAttachmentSources(
                context.AIMessage, out string textWithoutAttachmentTags);
            string reply = QuickChatContentFilter.CleanAssistantText(textWithoutAttachmentTags);
            List<QuickChatAttachment> attachments = await ResolveQuickChatAttachmentsAsync(attachmentSources);

            if (string.IsNullOrWhiteSpace(reply) == false || attachments.Count > 0)
                AddMessage(petId, "assistant", reply, attachments);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "QuickChat 处理 AI 回复失败：{PetId}", petId);
        }
        finally
        {
            // ChatFinished 表示文字回复已经结束。此时可能仍在等待 XmlFunctionCaller/GSV 语音等
            // ChatFinishedAsync 处理器，所以这里提前解除 QuickChat 的输入锁定。
            if (MarkRequestCompleted(petId))
                SendState();
        }
    }

    async Task<List<QuickChatAttachment>> ResolveQuickChatAttachmentsAsync(IEnumerable<string> sources)
    {
        List<QuickChatAttachment> attachments = new();
        if (sources.Any() == false)
            return attachments;

        string imageDirectory = Path.Combine(
            pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.QuickChat"),
            "Temp",
            "Images");
        Directory.CreateDirectory(imageDirectory);

        foreach (string source in sources.Where(item => string.IsNullOrWhiteSpace(item) == false).Distinct(StringComparer.Ordinal))
        {
            try
            {
                QuickChatAttachment? attachment = await ResolveQuickChatAttachmentAsync(source, imageDirectory);
                if (attachment != null)
                    attachments.Add(attachment);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "QuickChat 加载 AI 附件失败：{Source}", source);
            }
        }

        return attachments;
    }

    static async Task<QuickChatAttachment?> ResolveQuickChatAttachmentAsync(string source, string imageDirectory)
    {
        string value = source.Trim().Trim('"', '\'', '<', '>');
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string localPath;
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            string extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
                extension = ".bin";

            byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(value);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(pathBytes));
            localPath = Path.Combine(imageDirectory, "ai-" + hash + extension);

            if (File.Exists(localPath) == false)
            {
                byte[] data = await ImageHttpClient.GetByteArrayAsync(uri);
                if (data.Length == 0)
                    return null;
                await File.WriteAllBytesAsync(localPath, data);
            }
        }
        else
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? fileUri) && fileUri.IsFile)
                value = fileUri.LocalPath;

            localPath = Path.GetFullPath(value);
            if (File.Exists(localPath) == false)
                return null;
        }

        if (File.GetAttributes(localPath).HasFlag(FileAttributes.Directory))
            return null;

        bool isImage = IsImagePath(localPath);
        return new QuickChatAttachment {
            Kind = isImage ? "image" : "file",
            Path = localPath,
            Name = Path.GetFileName(localPath),
            ThumbnailPath = isImage ? CreateScreenshotThumbnail(localPath, imageDirectory) : string.Empty
        };
    }

    static bool IsImagePath(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" => true,
            _ => false
        };
    }

    bool MarkRequestCompleted(string petId, string? requestId = null)
    {
        lock (stateGate)
        {
            if (string.IsNullOrWhiteSpace(requestId) == false &&
                busyRequestIds.TryGetValue(petId, out string? currentId) &&
                string.Equals(currentId, requestId, StringComparison.Ordinal) == false)
            {
                return false;
            }

            busyRequestIds.Remove(petId);
            return busyPetIds.Remove(petId);
        }
    }

    void OnRendererMessage(string type, System.Text.Json.JsonElement payload)
    {
        switch (type)
        {
            case "ready":
                EnsureSelectedPet();
                SendState();
                SendSelectedHistory();
                break;

            case "select":
                if (payload.TryGetProperty("id", out System.Text.Json.JsonElement idElement))
                    SelectPet(idElement.GetString());
                break;

            case "send":
                if (payload.TryGetProperty("petId", out System.Text.Json.JsonElement petIdElement) &&
                    payload.TryGetProperty("text", out System.Text.Json.JsonElement textElement))
                {
                    List<string> attachmentPaths = new();
                    if (payload.TryGetProperty("attachmentPaths", out System.Text.Json.JsonElement attachmentPathsElement) &&
                        attachmentPathsElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (System.Text.Json.JsonElement item in attachmentPathsElement.EnumerateArray())
                        {
                            string? attachmentPath = item.GetString();
                            if (string.IsNullOrWhiteSpace(attachmentPath) == false)
                                attachmentPaths.Add(attachmentPath);
                        }
                    }

                    _ = SendMessageAsync(petIdElement.GetString(), textElement.GetString(), attachmentPaths);
                }
                break;

            case "resize":
                if (window != null && window.IsWindowDragging == false && payload.TryGetProperty("height", out System.Text.Json.JsonElement heightElement))
                {
                    bool compact = payload.TryGetProperty("compact", out System.Text.Json.JsonElement compactElement)
                        && compactElement.GetBoolean();
                    int minHeight = compact
                        ? Math.Min(config.MinWindowHeight, 56)
                        : config.MinWindowHeight;

                    if (compact)
                        _ = window.SetCompactHeightAsync(heightElement.GetInt32(), minHeight);
                    else
                        _ = window.SetAdaptiveHeightAsync(
                            config.WindowWidth,
                            heightElement.GetInt32(),
                            minHeight,
                            config.WindowHeight,
                            config.AutoFitHeight);
                }
                break;

            case "resize-start":
                if (window != null && payload.TryGetProperty("edge", out System.Text.Json.JsonElement edgeElement))
                {
                    _ = window.BeginManualResizeAsync(edgeElement.GetString() ?? "se");
                }
                break;

            case "resize-move":
                _ = window?.UpdateManualResizeAsync();
                break;

            case "resize-end":
                window?.EndManualResize();
                break;

            case "window-drag-start":
                _ = window?.BeginWindowDragAsync();
                break;

            case "window-drag-move":
                _ = window?.UpdateWindowDragAsync();
                break;

            case "window-drag-end":
                window?.EndWindowDrag();
                break;

            case "clear":
                ClearDisplayedHistory();
                break;

            case "screenshot-request":
                _ = CaptureScreenshotAsync(
                    payload.TryGetProperty("petId", out System.Text.Json.JsonElement screenshotPetElement)
                        ? screenshotPetElement.GetString()
                        : selectedPetId,
                    false);
                break;

            case "screenshot-region-request":
                _ = CaptureScreenshotAsync(
                    payload.TryGetProperty("petId", out System.Text.Json.JsonElement screenshotRegionPetElement)
                        ? screenshotRegionPetElement.GetString()
                        : selectedPetId,
                    true);
                break;

            case "screenshot-taken":
                if (payload.TryGetProperty("petId", out System.Text.Json.JsonElement screenshotTakenPetElement) &&
                    payload.TryGetProperty("path", out System.Text.Json.JsonElement screenshotPathElement))
                {
                    string screenshotPetId = screenshotTakenPetElement.GetString() ?? string.Empty;
                    string screenshotPath = screenshotPathElement.GetString() ?? string.Empty;
                    string screenshotInfo = payload.TryGetProperty("text", out System.Text.Json.JsonElement screenshotTextElement)
                        ? screenshotTextElement.GetString() ?? "QuickChat全屏截图"
                        : "QuickChat全屏截图";

                    if (string.IsNullOrWhiteSpace(screenshotPetId) == false && string.IsNullOrWhiteSpace(screenshotPath) == false)
                    {
                        window?.ShowAndFocus();
                        string outgoingScreenshot = "用户发送了一张图片：\n" + screenshotPath +
                            "\n\n用户文字：\n" + screenshotInfo;
                        _ = SendMessageAsync(screenshotPetId, outgoingScreenshot, new[] { screenshotPath });
                    }
                }
                break;

            case "screenshot-failed":
                if (payload.TryGetProperty("petId", out System.Text.Json.JsonElement failedScreenshotPetElement))
                {
                    string failedScreenshotPetId = failedScreenshotPetElement.GetString() ?? string.Empty;
                    string failedScreenshotMessage = payload.TryGetProperty("message", out System.Text.Json.JsonElement failedMessageElement)
                        ? failedMessageElement.GetString() ?? "未知错误"
                        : "未知错误";
                    window?.ShowAndFocus();
                    if (string.IsNullOrWhiteSpace(failedScreenshotPetId) == false)
                        AddSystemMessage(failedScreenshotPetId, "截图失败：" + failedScreenshotMessage);
                }
                break;

            case "hide":
                window?.Hide();
                break;
        }
    }

    void LogRegionDebug(string message, Exception? exception = null)
    {
        try
        {
            string debugDirectory = Path.Combine(
                pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.QuickChat"),
                "Temp");
            Directory.CreateDirectory(debugDirectory);
            string debugPath = Path.Combine(debugDirectory, "region-debug.log");
            string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
            if (exception != null)
                text += Environment.NewLine + exception;
            File.AppendAllText(debugPath, text + Environment.NewLine);
        }
        catch
        {
            // 诊断日志失败不能影响截图。
        }
    }

    async Task CaptureScreenshotAsync(string? petId, bool region)
    {
        LogRegionDebug($"Capture requested: region={region}, petId={petId ?? "<null>"}, windowNull={window == null}");
        if (window == null || string.IsNullOrWhiteSpace(petId))
        {
            LogRegionDebug($"Capture rejected: windowNull={window == null}, petId={petId ?? "<null>"}");
            window?.Send("screenshot-failed", new { petId, message = "当前没有可发送的桌宠。" });
            return;
        }

        IQuickChatWindow? screenshotWindow = window;
        try
        {
            string imageDirectory = Path.Combine(
                pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.QuickChat"),
                "Temp",
                "Images");
            Directory.CreateDirectory(imageDirectory);

            LogRegionDebug("Capture window hiding");
            screenshotWindow.Hide();
            await Task.Delay(260);

            IntPtr previousDpiContext = IntPtr.Zero;
            try
            {
                try
                {
                    previousDpiContext = SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
                }
                catch (EntryPointNotFoundException)
                {
                    // Older Windows fallback uses logical screen bounds below.
                }

                if (previousDpiContext == IntPtr.Zero ||
                    TryGetPhysicalMonitorBounds(out System.Drawing.Rectangle monitorBounds) == false)
                {
                    System.Windows.Forms.Screen screen =
                        System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
                    monitorBounds = screen.Bounds;
                }

                LogRegionDebug($"Monitor bounds: {monitorBounds}");
                System.Drawing.Rectangle captureBounds = monitorBounds;
                if (region)
                {
                    LogRegionDebug("Region selection starting");
                    using System.Drawing.Bitmap screenPreview =
                        new(monitorBounds.Width, monitorBounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (System.Drawing.Graphics previewGraphics = System.Drawing.Graphics.FromImage(screenPreview))
                    {
                        previewGraphics.CopyFromScreen(
                            monitorBounds.Left,
                            monitorBounds.Top,
                            0,
                            0,
                            monitorBounds.Size);
                    }

                    System.Drawing.Rectangle? selectedBounds = await SelectScreenRegionAsync(
                        monitorBounds,
                        message => LogRegionDebug(message),
                        screenPreview);
                    LogRegionDebug($"Region selection returned: hasValue={selectedBounds.HasValue}, value={selectedBounds?.ToString() ?? "<null>"}");
                    if (selectedBounds == null)
                    {
                        screenshotWindow.ShowAndFocus();
                        screenshotWindow.Send("screenshot-complete", new { mode = "region", canceled = true });
                        return;
                    }

                    captureBounds = selectedBounds.Value;
                    await Task.Delay(90);
                }

                string modeName = region ? "区域截图" : "全屏截图";
                DateTime capturedAt = DateTime.Now;
                string screenshotInfo = $"QuickChat{modeName} {capturedAt:yyyy-MM-dd HH:mm:ss}";
                string filePath = Path.Combine(
                    imageDirectory,
                    $"QuickChat{modeName}_{capturedAt:yyyy-MM-dd_HH-mm-ss}.png");

                using (System.Drawing.Bitmap bitmap =
                    new System.Drawing.Bitmap(captureBounds.Width, captureBounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(captureBounds.Left, captureBounds.Top, 0, 0, captureBounds.Size);
                    bitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
                }

                LogRegionDebug($"Screenshot saved: {filePath}");
                CreateScreenshotThumbnail(filePath, imageDirectory);
                AddSystemMessage(petId, $"{modeName}已保存，正在上传给 AI…");
                screenshotWindow.ShowAndFocus();
                screenshotWindow.Send("screenshot-uploading", new { mode = region ? "region" : "fullscreen" });

                string outgoingScreenshot = "用户发送了一张图片：\n" + filePath +
                    "\n\n用户文字：\n" + screenshotInfo;
                await SendMessageAsync(petId, outgoingScreenshot, new[] { filePath });
                screenshotWindow.Send("screenshot-complete");
            }
            finally
            {
                if (previousDpiContext != IntPtr.Zero)
                    _ = SetThreadDpiAwarenessContext(previousDpiContext);
            }
        }
        catch (Exception exception)
        {
            LogRegionDebug("Capture failed", exception);
            logger.LogError(exception, "QuickChat 截图失败");
            screenshotWindow.ShowAndFocus();
            screenshotWindow.Send("screenshot-failed", new
            {
                petId,
                message = exception.Message
            });
        }
    }

    static bool TryGetPhysicalMonitorBounds(out System.Drawing.Rectangle bounds)
    {
        bounds = System.Drawing.Rectangle.Empty;

        if (GetCursorPos(out NativePoint cursor) == false)
            return false;

        IntPtr monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return false;

        MonitorInfo info = new()
        {
            CbSize = (uint)Marshal.SizeOf<MonitorInfo>()
        };

        if (GetMonitorInfoW(monitor, ref info) == false)
            return false;

        bounds = System.Drawing.Rectangle.FromLTRB(
            info.RcMonitor.Left,
            info.RcMonitor.Top,
            info.RcMonitor.Right,
            info.RcMonitor.Bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    static string CreateScreenshotThumbnail(string sourcePath, string imageDirectory)
    {
        byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath));
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(pathBytes))
            .ToLowerInvariant()[..16];
        string thumbnailPath = Path.Combine(imageDirectory, "thumb-" + hash + ".jpg");

        using System.Drawing.Bitmap source = new(sourcePath);
        double scale = Math.Min(1.0, 320.0 / Math.Max(source.Width, Math.Max(source.Height, 1)));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using System.Drawing.Bitmap thumbnail = new(width, height);
        using System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(thumbnail);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);
        thumbnail.Save(thumbnailPath, System.Drawing.Imaging.ImageFormat.Jpeg);
        return thumbnailPath;
    }

    static async Task<System.Drawing.Rectangle?> SelectScreenRegionAsync(
        System.Drawing.Rectangle monitorBounds,
        Action<string>? log,
        System.Drawing.Bitmap screenPreview)
    {
        TaskCompletionSource<System.Drawing.Rectangle?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread staThread = new(() => {
            try
            {
                log?.Invoke("Region STA thread started");
                System.Windows.Forms.Application.EnableVisualStyles();
                IntPtr previousDpiContext = SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
                try
                {
                    log?.Invoke($"Region form creating: {monitorBounds}");
                    using RegionSelectionForm form = new(monitorBounds, screenPreview);
                    log?.Invoke("Region form showing dialog");
                    System.Windows.Forms.DialogResult result = form.ShowDialog();
                    log?.Invoke($"Region form dialog returned: {result}, selected={form.SelectedRectangle}");
                    completion.SetResult(result == System.Windows.Forms.DialogResult.OK ? form.SelectedRectangle : null);
                }
                finally
                {
                    if (previousDpiContext != IntPtr.Zero)
                        _ = SetThreadDpiAwarenessContext(previousDpiContext);
                }
            }
            catch (Exception exception)
            {
                log?.Invoke("Region form failed: " + exception);
                completion.SetException(exception);
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        return await completion.Task;
    }

    static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);
    const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        public uint CbSize;
        public NativeRect RcMonitor;
        public NativeRect RcWork;
        public uint Flags;
    }

    void SelectPet(string? petId)
    {
        if (string.IsNullOrWhiteSpace(petId))
            return;

        selectedPetId = petId;
        SendState();
        SendSelectedHistory();
    }

    async Task SendMessageAsync(string? petId, string? rawText, IEnumerable<string>? attachmentPaths = null)
    {
        string text = rawText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(petId) || text.Length == 0)
            return;

        ChatActivity? activity = chatActivitySystem.GetAllChatActivities()
            .FirstOrDefault(item => item.Character.Name == petId);

        if (activity == null)
        {
            AddSystemMessage(petId, "该桌宠已停止活动。");
            return;
        }

        QuickChatConfig messageConfig = GetConfigForPet(petId);

        string requestId = Guid.NewGuid().ToString("N");
        lock (stateGate)
        {
            busyPetIds.Add(petId);
            busyRequestIds[petId] = requestId;
        }

        SendState();

        bool hasPendingAttachments = false;
        List<string>? myPendingPaths = null;
        try
        {
            List<string> pendingPaths = (attachmentPaths ?? Enumerable.Empty<string>())
                .Where(item => string.IsNullOrWhiteSpace(item) == false)
                .Select(item => Path.GetFullPath(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string outgoing = messageConfig.MarkMessageSource
                ? "[消息来源(QuickChat)]" + text
                : text;


            QuickChatScreenshotImageMode imageMode = NormalizeScreenshotImageMode(messageConfig.ScreenshotImageMode);
            List<string> validImagePaths = pendingPaths
                .Where(path => File.Exists(path) && IsImagePath(path))
                .ToList();

            List<(byte[] Data, string MimeType)> modelImages = new();
            bool anyImageCompressed = false;
            if (validImagePaths.Count > 0 && imageMode != QuickChatScreenshotImageMode.仅路径)
            {
                foreach (string imagePath in validImagePaths)
                {
                    (byte[] data, string mimeType, bool compressed) =
                        await LoadModelImageForModelAsync(imagePath);
                    modelImages.Add((data, mimeType));
                    anyImageCompressed |= compressed;
                }

                if (anyImageCompressed)
                    AddSystemMessage(petId, "图片 base64 超过 1MB，已压缩后发送给 AI。");
            }

            // 展示层永远不带路径元数据；AI 请求里的 modelText 才带路径。
            string outgoingDisplay = QuickChatContentFilter.CleanOutgoingDisplayText(outgoing, pendingPaths);

            // 图片加载完成后才登记待处理附件，并记录本条消息的期望文本。
            // ChatSent 触发时按内容匹配消费，防止上一条消息的延迟事件
            // 或后台消息偷走附件（表现为只有第一张图片能显示）。
            if (pendingPaths.Count > 0)
            {
                string expectedText = validImagePaths.Count > 0 && imageMode == QuickChatScreenshotImageMode.多模态
                    ? outgoingDisplay
                    : QuickChatContentFilter.CleanOutgoingDisplayText(outgoing, Enumerable.Empty<string>());
                lock (stateGate)
                {
                    pendingScreenshotAttachments[petId] = pendingPaths;
                    pendingAttachmentExpectedTexts[petId] = expectedText;
                }
                myPendingPaths = pendingPaths;
                hasPendingAttachments = true;
            }

            string modelText = outgoing;
            if (validImagePaths.Count > 0 && imageMode != QuickChatScreenshotImageMode.仅路径)
            {
                modelText = BuildImageModelText(outgoing, validImagePaths);
            }

            if (validImagePaths.Count > 0 && imageMode == QuickChatScreenshotImageMode.临时分析)
            {
                // 临时模式不调用 ChatAsync，避免图片消息永久进入主历史。
                OnChatSent(petId, outgoing);
                string analysis = await SendTemporaryScreenshotAsync(activity, modelText, modelImages);
                if (string.IsNullOrWhiteSpace(analysis))
                {
                    AddSystemMessage(petId, "模型没有返回图片分析结果。");
                }
                else
                {
                    await AddAssistantMessageAsync(petId, new ChatContext {
                        UserMessage = modelText,
                        AIMessage = analysis,
                        CancellationToken = CancellationToken.None
                    });
                }
            }
            else
            {
                ChatMessageContent outgoingMessage;
                if (modelImages.Count > 0 && imageMode == QuickChatScreenshotImageMode.多模态)
                {
                    ChatMessageContentItemCollection items = new()
                    {
                        new TextContent(modelText)
                    };
                    foreach ((byte[] data, string mimeType) in modelImages)
                        items.Add(new ImageContent(data, mimeType));

                    // ChatBot.ChatSent 会读取 Content，所以这里保持干净展示文本。
                    // 带路径的 modelText 已作为 TextContent item 发给模型。
                    outgoingMessage = new ChatMessageContent(AuthorRole.User, items) {
                        Content = outgoingDisplay
                    };
                }
                else
                {
                    outgoingMessage = new ChatMessageContent(AuthorRole.User, outgoing);
                }

                var result = await activity.ChatBot.ChatAsync(outgoingMessage, true);

                if (result.Exception != null)
                {
                    logger.LogError(result.Exception, "QuickChat 对话失败：{PetId}", petId);
                    AddSystemMessage(petId, "回复失败，请检查模型或网络配置。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 用户发送新消息时会通过 ChatAsync(breakLast:true) 打断旧请求，
            // 旧请求被取消属于正常流程，不应显示为回复失败。
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "QuickChat 对话异常：{PetId}", petId);
            AddSystemMessage(petId, "回复失败，请查看 Alife 日志。");
        }
        finally
        {
            if (hasPendingAttachments)
            {
                lock (stateGate)
                {
                    // 只清理本请求登记的附件；已被消费或被新请求覆盖时不动，
                    // 避免误删下一条图片消息的待处理附件。
                    if (myPendingPaths != null &&
                        pendingScreenshotAttachments.TryGetValue(petId, out List<string>? current) &&
                        ReferenceEquals(current, myPendingPaths))
                    {
                        pendingScreenshotAttachments.Remove(petId);
                        pendingAttachmentExpectedTexts.Remove(petId);
                    }
                }
            }

            bool changed = MarkRequestCompleted(petId, requestId);
            if (changed)
                SendState();
        }
    }

    const int MaxModelImageDataUriLength = 1024 * 1024;

    static string GetImageMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };
    }

    static int GetDataUriLength(byte[] data, string mimeType)
    {
        return $"data:{mimeType};base64,".Length + Convert.ToBase64String(data).Length;
    }

    QuickChatConfig GetConfigForPet(string petId)
    {
        lock (configGate)
        {
            if (petConfigs.TryGetValue(petId, out QuickChatConfig? value))
                return value;
        }

        return config;
    }

    static string BuildImageModelText(string rawMessage, IEnumerable<string> imagePaths)
    {
        // 图片字节作为多模态内容发送；同时保留本机路径，便于模型知道图片来源。
        // 这些路径只进入模型请求，不进入 QuickChat 用户可见气泡。
        List<string> paths = imagePaths
            .Where(item => string.IsNullOrWhiteSpace(item) == false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string text = rawMessage;
        foreach (string imagePath in paths)
            text = text.Replace(imagePath, string.Empty, StringComparison.OrdinalIgnoreCase);

        text = text
            .Replace("用户发送了一张图片：", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("用户发送了一张截图：", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("用户发送了一张图片", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("用户发送了一张截图", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("[消息来源(QuickChat)]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("用户文字：", string.Empty, StringComparison.OrdinalIgnoreCase);

        while (text.Contains("\n\n\n"))
            text = text.Replace("\n\n\n", "\n\n");

        string displayText = text.Trim();
        string sourceBlock = string.Join("\n", paths.Select(path => "- " + path));

        return string.IsNullOrWhiteSpace(displayText)
            ? "用户发送了图片。\n\n图片来源路径（本机）：\n" + sourceBlock
            : displayText + "\n\n图片来源路径（本机）：\n" + sourceBlock;
    }

    static async Task<(byte[] Data, string MimeType, bool Compressed)> LoadModelImageForModelAsync(
        string imagePath)
    {
        byte[] data = await File.ReadAllBytesAsync(imagePath);
        string mimeType = GetImageMimeType(imagePath);

        if (GetDataUriLength(data, mimeType) <= MaxModelImageDataUriLength)
            return (data, mimeType, false);

        // data:...;base64, 前缀长度较小，这里留 64 字节余量。
        int targetBase64Length = MaxModelImageDataUriLength - 64;
        int targetRawLength = targetBase64Length * 3 / 4;
        byte[] compressed = await Task.Run(() => CompressImageForModel(data, targetRawLength));
        return (compressed, "image/jpeg", true);
    }

    static byte[] CompressImageForModel(byte[] sourceBytes, int targetRawLength)
    {
        using System.IO.MemoryStream input = new(sourceBytes, false);
        using System.Drawing.Bitmap source = new(input);

        for (double scale = 1.0; scale >= 0.08; scale *= 0.8)
        {
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));

            using System.Drawing.Bitmap scaled = new(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.Clear(System.Drawing.Color.White);
                graphics.DrawImage(source, 0, 0, width, height);
            }

            foreach (long quality in new[] { 92L, 85L, 75L, 65L, 55L, 45L, 35L, 25L, 15L })
            {
                byte[] candidate = EncodeJpeg(scaled, quality);
                if (candidate.Length <= targetRawLength)
                    return candidate;
            }
        }

        return EncodeJpeg(source, 5L);
    }

    static byte[] EncodeJpeg(System.Drawing.Bitmap image, long quality)
    {
        System.Drawing.Imaging.ImageCodecInfo? jpegCodec = System.Drawing.Imaging.ImageCodecInfo
            .GetImageEncoders()
            .FirstOrDefault(codec => codec.MimeType == "image/jpeg");
        if (jpegCodec == null)
            throw new NotSupportedException("系统没有可用的 JPEG 编码器。");

        using System.Drawing.Imaging.EncoderParameters parameters = new(1);
        parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, quality);

        using System.IO.MemoryStream output = new();
        image.Save(output, jpegCodec, parameters);
        return output.ToArray();
    }

    static QuickChatScreenshotImageMode NormalizeScreenshotImageMode(QuickChatScreenshotImageMode value)
    {
        return value;
    }

    async Task<string> SendTemporaryScreenshotAsync(
        ChatActivity activity,
        string outgoing,
        List<(byte[] Data, string MimeType)> images)
    {
        string requestText = outgoing +
            "\n\n[临时图片消息] 请立即完整分析上面的图片。这条消息稍后将从对话历史中删除；你的回复文本将返回给快聊。";

        ChatMessageContentItemCollection items = new()
        {
            new TextContent(requestText)
        };
        foreach ((byte[] imageData, string imageMimeType) in images)
            items.Add(new ImageContent(imageData, imageMimeType));
        ChatMessageContent tempMessage = new ChatMessageContent(AuthorRole.User, items)
        {
            Content = requestText
        };

        string aiMessage = "";
        Exception? error = null;
        await activity.ChatBot.EditChatHistoryAsync(async thread => {
            int startIndex = thread.ChatHistory.Count;
            thread.ChatHistory.Add(tempMessage);
            try
            {
                aiMessage = await activity.ChatBot.LanguageModel.ChatStreamingAsync(
                    thread,
                    exceptionThrow: exception => error = exception);
            }
            finally
            {
                int removeCount = thread.ChatHistory.Count - startIndex;
                if (removeCount > 0)
                    thread.ChatHistory.RemoveRange(startIndex, removeCount);
            }
        }, "QuickChat 临时截图分析");

        if (error != null)
            throw error;

        return aiMessage;
    }

    List<string> TakePendingScreenshotAttachments(string petId, string? rawMessage)
    {
        lock (stateGate)
        {
            if (pendingScreenshotAttachments.TryGetValue(petId, out List<string>? paths) == false)
                return new();

            // 按内容匹配消费：只有 ChatSent 载荷与本条图片消息一致时才取走附件。
            // 并发/后台消息（周期报点、记忆总结等）不得偷走附件。
            if (rawMessage == null ||
                pendingAttachmentExpectedTexts.TryGetValue(petId, out string? expected) == false ||
                string.IsNullOrEmpty(expected))
                return new();

            string cleanedForMatch = QuickChatContentFilter.CleanOutgoingDisplayText(
                rawMessage, Enumerable.Empty<string>());
            if (string.Equals(cleanedForMatch, expected, StringComparison.Ordinal) == false)
                return new();

            pendingScreenshotAttachments.Remove(petId);
            pendingAttachmentExpectedTexts.Remove(petId);
            return paths;
        }
    }

    void AddMessage(string petId, string role, string text, List<QuickChatAttachment>? attachments = null)
    {
        ChatActivity? activity = chatActivitySystem.GetAllChatActivities()
            .FirstOrDefault(item => item.Character.Name == petId);
        string petName = activity?.Character.Name ?? petId;

        QuickChatMessage message = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Role = role,
            PetId = petId,
            PetName = petName,
            Text = text,
            Attachments = attachments?.Where(item => string.IsNullOrWhiteSpace(item.Path) == false).ToList() ?? new(),
            CreatedAt = DateTimeOffset.Now
        };

        List<QuickChatMessage> history;
        lock (stateGate)
        {
            if (histories.TryGetValue(petId, out List<QuickChatMessage>? value) == false)
            {
                value = new List<QuickChatMessage>();
                histories[petId] = value;
            }

            history = value;
            history.Add(message);
        }

        if (selectedPetId == petId)
            window?.Send("message", message);
    }

    void AddSystemMessage(string petId, string text)
    {
        AddMessage(petId, "system", text);
    }

    void SendState()
    {
        List<QuickChatPet> pets = chatActivitySystem.GetAllChatActivities()
            .Select(activity => new QuickChatPet(
                activity.Character.Name,
                activity.Character.Name,
                busyPetIds.Contains(activity.Character.Name)))
            .ToList();

        window?.Send("state", new
        {
            pets,
            selectedId = selectedPetId,
            maxVisibleMessages = config.MaxVisibleMessages,
            showAllMessages = config.ShowAllMessages,
            hideOnEscape = config.HideOnEscape,
            autoHeight = config.AutoFitHeight,
            minWindowHeight = config.MinWindowHeight,
            maxWindowHeight = config.WindowHeight,
            theme = new
            {
                panelColor = config.PanelColor,
                panelOpacity = config.PanelOpacity,
                dragBackdropBlur = config.DragBackdropBlur,
                messageListColor = config.MessageListColor,
                messageListOpacity = config.MessageListOpacity,
                assistantBubbleColor = config.AssistantBubbleColor,
                assistantBubbleOpacity = config.AssistantBubbleOpacity,
                userBubbleColor = config.UserBubbleColor,
                userBubbleOpacity = config.UserBubbleOpacity,
                inputColor = config.InputColor,
                inputOpacity = config.InputOpacity,
                textColor = config.TextColor
            }
        });
    }

    void SendSelectedHistory()
    {
        if (string.IsNullOrWhiteSpace(selectedPetId))
        {
            window?.Send("history", new { petId = string.Empty, messages = Array.Empty<QuickChatMessage>() });
            return;
        }

        QuickChatMessage[] messages;
        lock (stateGate)
        {
            messages = histories.TryGetValue(selectedPetId, out List<QuickChatMessage>? value)
                ? value.ToArray()
                : Array.Empty<QuickChatMessage>();
        }

        window?.Send("history", new
        {
            petId = selectedPetId,
            messages
        });
    }

    void ClearDisplayedHistory()
    {
        if (string.IsNullOrWhiteSpace(selectedPetId))
            return;

        lock (stateGate)
        {
            histories.Remove(selectedPetId);
        }

        SendSelectedHistory();
    }

    void EnsureSelectedPet()
    {
        List<string> petIds = chatActivitySystem.GetAllChatActivities()
            .Select(activity => activity.Character.Name)
            .ToList();

        if (petIds.Count == 0)
        {
            selectedPetId = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(selectedPetId) || petIds.Contains(selectedPetId) == false)
            selectedPetId = petIds[0];
    }

    void RegisterHotkeys()
    {
        if (disposed)
            return;

        foreach (string oldHotkey in registeredHotkeys)
            shortcutService.Unregister(oldHotkey);

        registeredHotkeys.Clear();
        registeredHotkeys.AddRange(config.Hotkeys);

        foreach (string hotkey in config.Hotkeys)
        {
            try
            {
                shortcutService.Register(hotkey, OnHotkeyPressed);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "QuickChat 快捷键注册失败：{Hotkey}", hotkey);
            }
        }
    }

    static string CreateConfigSignature(QuickChatConfig value)
    {
        return string.Join("|", value.Hotkeys)
            + $"|{value.WindowWidth}|{value.WindowHeight}"
            + $"|{value.MaxVisibleMessages}|{value.ShowAllMessages}|{value.HideOnEscape}|{value.MarkMessageSource}"
            + $"|{value.ScreenshotImageMode}"
            + $"|{value.AutoFitHeight}|{value.MinWindowHeight}|{value.ColorPreset}"
            + $"|{value.PanelColor}|{value.PanelOpacity:R}"
            + $"|{value.DragBackdropBlur:R}"
            + $"|{value.MessageListColor}|{value.MessageListOpacity:R}"
            + $"|{value.AssistantBubbleColor}|{value.AssistantBubbleOpacity:R}"
            + $"|{value.UserBubbleColor}|{value.UserBubbleOpacity:R}"
            + $"|{value.InputColor}|{value.InputOpacity:R}"
            + $"|{value.TextColor}";
    }

    async void OnHotkeyPressed()
    {
        try
        {
            EnsureSelectedPet();

            string wwwRoot = Path.Combine(
                pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.QuickChat"),
                "Resources",
                "QuickChat");

            if (window == null)
                window = windowFactory.Create(logger);

            window.OnMessage -= OnRendererMessage;
            window.OnMessage += OnRendererMessage;
            await window.ToggleAtMouseAsync(config, wwwRoot);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "QuickChat 悬浮窗切换失败");
        }
    }

    static QuickChatConfig NormalizeConfig(QuickChatConfig value)
    {
        List<string> hotkeys = value.Hotkeys?
            .Where(item => string.IsNullOrWhiteSpace(item) == false)
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        if (hotkeys.Count == 0)
            hotkeys.Add("Alt+Q");

        QuickChatConfig normalized = new QuickChatConfig
        {
            Hotkeys = hotkeys,
            WindowWidth = Math.Clamp(value.WindowWidth == 380 ? 320 : value.WindowWidth, 260, 1000),
            WindowHeight = Math.Clamp(value.WindowHeight == 520 ? 460 : value.WindowHeight, 120, 1200),
            MaxVisibleMessages = Math.Clamp(value.MaxVisibleMessages, 1, 50),
            ShowAllMessages = value.ShowAllMessages,
            AutoFitHeight = value.AutoFitHeight,
            MinWindowHeight = Math.Clamp(value.MinWindowHeight == 170 ? 96 : value.MinWindowHeight, 52, 1000),
            ColorPreset = string.IsNullOrWhiteSpace(value.ColorPreset) ? "自定义" : value.ColorPreset.Trim(),
            HideOnEscape = value.HideOnEscape,
            MarkMessageSource = value.MarkMessageSource,
            ScreenshotImageMode = NormalizeScreenshotImageMode(value.ScreenshotImageMode),
            PanelColor = NormalizeColor(value.PanelColor, "#111C28"),
            PanelOpacity = ClampRatio(value.PanelOpacity, 0.55),
            DragBackdropBlur = Math.Clamp(value.DragBackdropBlur, 0, 80),
            MessageListColor = NormalizeColor(value.MessageListColor, "#000000"),
            MessageListOpacity = ClampRatio(value.MessageListOpacity, 0),
            AssistantBubbleColor = NormalizeColor(value.AssistantBubbleColor, "#FFFFFF"),
            AssistantBubbleOpacity = ClampRatio(value.AssistantBubbleOpacity, 0.10),
            UserBubbleColor = NormalizeColor(value.UserBubbleColor, "#556DF5"),
            UserBubbleOpacity = ClampRatio(value.UserBubbleOpacity, 0.25),
            InputColor = NormalizeColor(value.InputColor, "#FFFFFF"),
            InputOpacity = ClampRatio(value.InputOpacity, 0.085),
            TextColor = NormalizeColor(value.TextColor, "#EEF1F6")
        };

        ApplyColorPreset(normalized);
        return normalized;
    }

    static void ApplyColorPreset(QuickChatConfig value)
    {
        switch (value.ColorPreset)
        {
            case "深空":
                value.PanelColor = "#111C28"; value.PanelOpacity = 0.55;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#FFFFFF"; value.AssistantBubbleOpacity = 0.10;
                value.UserBubbleColor = "#556DF5"; value.UserBubbleOpacity = 0.25;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.085;
                break;
            case "墨黑":
                value.PanelColor = "#000000"; value.PanelOpacity = 0.68;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#FFFFFF"; value.AssistantBubbleOpacity = 0.08;
                value.UserBubbleColor = "#64748B"; value.UserBubbleOpacity = 0.30;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.08;
                break;
            case "雾白":
                value.PanelColor = "#F8FAFC"; value.PanelOpacity = 0.74;
                value.MessageListColor = "#FFFFFF"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#FFFFFF"; value.AssistantBubbleOpacity = 0.36;
                value.UserBubbleColor = "#3B82F6"; value.UserBubbleOpacity = 0.24;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.38;
                break;
            case "蓝色":
                value.PanelColor = "#102A43"; value.PanelOpacity = 0.62;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#BFDBFE"; value.AssistantBubbleOpacity = 0.16;
                value.UserBubbleColor = "#2F6FED"; value.UserBubbleOpacity = 0.30;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.10;
                break;
            case "青色":
                value.PanelColor = "#062E33"; value.PanelOpacity = 0.62;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#99F6E4"; value.AssistantBubbleOpacity = 0.14;
                value.UserBubbleColor = "#06B6D4"; value.UserBubbleOpacity = 0.26;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.10;
                break;
            case "绿色":
                value.PanelColor = "#08291B"; value.PanelOpacity = 0.62;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#BBF7D0"; value.AssistantBubbleOpacity = 0.14;
                value.UserBubbleColor = "#22C55E"; value.UserBubbleOpacity = 0.26;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.10;
                break;
            case "紫色":
                value.PanelColor = "#22103D"; value.PanelOpacity = 0.64;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#DDD6FE"; value.AssistantBubbleOpacity = 0.16;
                value.UserBubbleColor = "#8B5CF6"; value.UserBubbleOpacity = 0.30;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.10;
                break;
            case "樱粉":
                value.PanelColor = "#3B1024"; value.PanelOpacity = 0.62;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#FBCFE8"; value.AssistantBubbleOpacity = 0.18;
                value.UserBubbleColor = "#EC4899"; value.UserBubbleOpacity = 0.28;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.11;
                break;
            case "暖橙":
                value.PanelColor = "#3A1B08"; value.PanelOpacity = 0.62;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#FED7AA"; value.AssistantBubbleOpacity = 0.18;
                value.UserBubbleColor = "#F97316"; value.UserBubbleOpacity = 0.28;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.11;
                break;
            case "透明":
                value.PanelColor = "#000000"; value.PanelOpacity = 0.08;
                value.MessageListColor = "#000000"; value.MessageListOpacity = 0;
                value.AssistantBubbleColor = "#FFFFFF"; value.AssistantBubbleOpacity = 0.06;
                value.UserBubbleColor = "#556DF5"; value.UserBubbleOpacity = 0.16;
                value.InputColor = "#FFFFFF"; value.InputOpacity = 0.07;
                break;
        }
    }

    static string NormalizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        string colorName = value.Trim();
        switch (colorName.ToLowerInvariant())
        {
            case "黑":
            case "black": return "#000000";
            case "白":
            case "white": return "#ffffff";
            case "灰":
            case "gray": return "#6b7280";
            case "红":
            case "red": return "#ef4444";
            case "蓝":
            case "blue": return "#2f6fed";
            case "绿":
            case "green": return "#22a06b";
            case "黄":
            case "yellow": return "#facc15";
            case "紫":
            case "purple": return "#8b5cf6";
            case "粉":
            case "pink": return "#ff7eb6";
            case "橙":
            case "orange": return "#ff9f43";
            case "青":
            case "cyan": return "#22d3ee";
        }

        string color = colorName;
        if (color.StartsWith("#") == false)
            color = "#" + color;

        if (color.Length == 4)
            color = $"#{color[1]}{color[1]}{color[2]}{color[2]}{color[3]}{color[3]}";

        if (color.Length != 7 || color.Skip(1).Any(item => Uri.IsHexDigit(item) == false))
            return fallback;

        return color.ToLowerInvariant();
    }

    static double ClampRatio(double value, double fallback)
    {
        return double.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;
    }

    bool HasHotkeysChanged(List<string> hotkeys)
    {
        if (registeredHotkeys.Count != hotkeys.Count)
            return true;

        for (int index = 0; index < hotkeys.Count; index++)
        {
            if (string.Equals(registeredHotkeys[index], hotkeys[index], StringComparison.OrdinalIgnoreCase) == false)
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        chatActivitySystem.Activated -= OnChatActivityActivated;
        chatActivitySystem.Deactivated -= OnChatActivityDeactivated;

        foreach (string petId in chatFinishedHandlers.Keys.ToList())
            DetachChatBot(petId);

        foreach (string hotkey in registeredHotkeys)
        shortcutService.UnregisterAll(registeredHotkeys);
        registeredHotkeys.Clear();

        window?.Dispose();
        window = null;

        lock (CurrentGate)
        {
            if (ReferenceEquals(current, this))
                current = null;

            configSources.Clear();
            sourceConfigs.Clear();
            configSource = null;
        }
    }
}

public class QuickChatPet
{
    public QuickChatPet(string id, string name, bool busy)
    {
        Id = id;
        Name = name;
        Busy = busy;
    }

    public string Id { get; }
    public string Name { get; }
    public bool Busy { get; }
}

public class QuickChatMessage
{
    public string Id { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string PetId { get; set; } = string.Empty;
    public string PetName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public List<QuickChatAttachment> Attachments { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
}

public class QuickChatAttachment
{
    public string Kind { get; set; } = "file";
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ThumbnailPath { get; set; } = string.Empty;
}

sealed class RegionSelectionForm : System.Windows.Forms.Form
{
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int keyCode);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    enum DragKind
    {
        None,
        Select,
        Move,
        Resize
    }

    enum ResizeHandle
    {
        None,
        NorthWest,
        North,
        NorthEast,
        East,
        SouthEast,
        South,
        SouthWest,
        West
    }

    const int HandleSize = 13;
    const int SelectionMinimumSize = 8;
    const uint SwpNoSize = 0x0001;
    const uint SwpNoMove = 0x0002;
    const uint SwpNoActivate = 0x0010;
    const uint SwpShowWindow = 0x0040;
    static readonly IntPtr HwndTopMost = new(-1);

    public System.Drawing.Rectangle SelectedRectangle { get; private set; } = System.Drawing.Rectangle.Empty;
    readonly System.Drawing.Rectangle monitorBounds;
    readonly System.Drawing.Bitmap screenPreview;
    System.Drawing.Rectangle currentRectangle = System.Drawing.Rectangle.Empty;
    System.Drawing.Rectangle dragSourceRectangle = System.Drawing.Rectangle.Empty;
    System.Drawing.Rectangle previousRectangle = System.Drawing.Rectangle.Empty;
    System.Drawing.Point selectionStartPoint;
    System.Drawing.Point dragStartPoint;
    DragKind dragKind = DragKind.None;
    ResizeHandle resizeHandle = ResizeHandle.None;
    bool hasSelection;
    bool showToolbar;
    bool escapeWasDown;
    bool previousHasSelection;
    bool previousShowToolbar;
    System.Drawing.Rectangle confirmButtonRectangle = System.Drawing.Rectangle.Empty;
    System.Drawing.Rectangle retryButtonRectangle = System.Drawing.Rectangle.Empty;
    System.Drawing.Rectangle cancelButtonRectangle = System.Drawing.Rectangle.Empty;
    readonly System.Windows.Forms.Timer escapeTimer = new() { Interval = 40 };
    readonly System.Windows.Forms.Timer topMostTimer = new() { Interval = 80 };

    public RegionSelectionForm(System.Drawing.Rectangle bounds, System.Drawing.Bitmap preview)
    {
        monitorBounds = bounds;
        screenPreview = preview;
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
        StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        Bounds = bounds;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = System.Drawing.Color.Black;
        Opacity = 1D;
        Cursor = System.Windows.Forms.Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "QuickChat区域截图";
        escapeWasDown = (GetAsyncKeyState(0x1B) & 0x8000) != 0;

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        KeyDown += (_, eventArgs) => {
            if (eventArgs.KeyCode == System.Windows.Forms.Keys.Escape)
                Close();
            else if (eventArgs.KeyCode == System.Windows.Forms.Keys.Enter)
                ConfirmSelection();
        };
        escapeTimer.Tick += (_, _) => {
            bool escapeDown = (GetAsyncKeyState(0x1B) & 0x8000) != 0;
            if (escapeDown && escapeWasDown == false)
                Close();
            escapeWasDown = escapeDown;
        };
        topMostTimer.Tick += (_, _) => ForceTopMost();
    }

    protected override System.Windows.Forms.CreateParams CreateParams
    {
        get
        {
            System.Windows.Forms.CreateParams createParams = base.CreateParams;
            createParams.ExStyle |= 0x00000008; // WS_EX_TOPMOST
            createParams.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return createParams;
        }
    }

    protected override void OnShown(System.EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        ForceTopMost();
        Activate();
        Focus();
        escapeTimer.Start();
        topMostTimer.Start();
    }

    protected override void OnFormClosed(System.Windows.Forms.FormClosedEventArgs eventArgs)
    {
        escapeTimer.Stop();
        escapeTimer.Dispose();
        topMostTimer.Stop();
        topMostTimer.Dispose();
        base.OnFormClosed(eventArgs);
    }

    void ForceTopMost()
    {
        if (IsHandleCreated == false)
            return;

        _ = SetWindowPos(
            Handle,
            HwndTopMost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    protected override void OnPaintBackground(System.Windows.Forms.PaintEventArgs eventArgs)
    {
        if (screenPreview == null)
        {
            base.OnPaintBackground(eventArgs);
            return;
        }

        eventArgs.Graphics.DrawImage(
            screenPreview,
            new System.Drawing.Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
    }

    void OnMouseDown(object? sender, System.Windows.Forms.MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != System.Windows.Forms.MouseButtons.Left)
            return;

        System.Drawing.Point point = eventArgs.Location;
        if (showToolbar)
        {
            if (confirmButtonRectangle.Contains(point))
            {
                ConfirmSelection();
                return;
            }
            if (cancelButtonRectangle.Contains(point))
            {
                DialogResult = System.Windows.Forms.DialogResult.Cancel;
                Close();
                return;
            }
            if (retryButtonRectangle.Contains(point))
            {
                ResetSelection();
                return;
            }
        }

        dragStartPoint = point;
        if (hasSelection)
        {
            resizeHandle = GetResizeHandle(point);
            if (resizeHandle != ResizeHandle.None)
            {
                dragKind = DragKind.Resize;
                dragSourceRectangle = currentRectangle;
                return;
            }

            if (currentRectangle.Contains(point))
            {
                dragKind = DragKind.Move;
                dragSourceRectangle = currentRectangle;
                return;
            }
        }

        previousRectangle = currentRectangle;
        previousHasSelection = hasSelection;
        previousShowToolbar = showToolbar;
        dragKind = DragKind.Select;
        selectionStartPoint = point;
        currentRectangle = System.Drawing.Rectangle.Empty;
        hasSelection = false;
        showToolbar = false;
        Invalidate();
    }

    void OnMouseMove(object? sender, System.Windows.Forms.MouseEventArgs eventArgs)
    {
        System.Drawing.Point point = eventArgs.Location;
        if (dragKind == DragKind.Select)
        {
            currentRectangle = Normalize(point);
            hasSelection = currentRectangle.Width > 4 && currentRectangle.Height > 4;
            showToolbar = false;
            Invalidate();
            return;
        }
        if (dragKind == DragKind.Move)
        {
            currentRectangle = ClampRectangle(
                dragSourceRectangle,
                point.X - dragStartPoint.X,
                point.Y - dragStartPoint.Y);
            LayoutToolbar();
            Invalidate();
            return;
        }
        if (dragKind == DragKind.Resize)
        {
            currentRectangle = ResizeRectangle(dragSourceRectangle, point, resizeHandle);
            LayoutToolbar();
            Invalidate();
            return;
        }

        resizeHandle = hasSelection ? GetResizeHandle(point) : ResizeHandle.None;
        Cursor = resizeHandle switch
        {
            ResizeHandle.NorthWest or ResizeHandle.SouthEast => System.Windows.Forms.Cursors.SizeNWSE,
            ResizeHandle.NorthEast or ResizeHandle.SouthWest => System.Windows.Forms.Cursors.SizeNESW,
            ResizeHandle.North or ResizeHandle.South => System.Windows.Forms.Cursors.SizeNS,
            ResizeHandle.East or ResizeHandle.West => System.Windows.Forms.Cursors.SizeWE,
            _ => hasSelection && currentRectangle.Contains(point)
                ? System.Windows.Forms.Cursors.SizeAll
                : System.Windows.Forms.Cursors.Cross
        };
    }

    void OnMouseUp(object? sender, System.Windows.Forms.MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != System.Windows.Forms.MouseButtons.Left)
            return;

        if (dragKind == DragKind.Select)
        {
            System.Drawing.Rectangle rectangle = Normalize(eventArgs.Location);
            if (rectangle.Width < 5 || rectangle.Height < 5)
            {
                currentRectangle = previousHasSelection ? previousRectangle : System.Drawing.Rectangle.Empty;
                hasSelection = previousHasSelection;
                showToolbar = previousShowToolbar;
                if (showToolbar)
                    LayoutToolbar();
            }
            else
            {
                currentRectangle = rectangle;
                hasSelection = true;
                showToolbar = true;
                LayoutToolbar();
            }
        }
        else if (dragKind == DragKind.Move || dragKind == DragKind.Resize)
        {
            hasSelection = currentRectangle.Width >= SelectionMinimumSize && currentRectangle.Height >= SelectionMinimumSize;
            showToolbar = true;
            LayoutToolbar();
        }

        dragKind = DragKind.None;
        resizeHandle = ResizeHandle.None;
        Invalidate();
    }

    void ConfirmSelection()
    {
        if (hasSelection == false)
            return;

        SelectedRectangle = new System.Drawing.Rectangle(
            Bounds.Left + currentRectangle.Left,
            Bounds.Top + currentRectangle.Top,
            currentRectangle.Width,
            currentRectangle.Height);
        DialogResult = System.Windows.Forms.DialogResult.OK;
        Close();
    }

    void ResetSelection()
    {
        hasSelection = false;
        showToolbar = false;
        currentRectangle = System.Drawing.Rectangle.Empty;
        dragKind = DragKind.None;
        resizeHandle = ResizeHandle.None;
        Cursor = System.Windows.Forms.Cursors.Cross;
        Invalidate();
    }

    ResizeHandle GetResizeHandle(System.Drawing.Point point)
    {
        if (hasSelection == false)
            return ResizeHandle.None;

        System.Drawing.Point[] points = GetSelectionHandles();
        ResizeHandle[] handles =
        [
            ResizeHandle.NorthWest,
            ResizeHandle.NorthEast,
            ResizeHandle.SouthWest,
            ResizeHandle.SouthEast,
            ResizeHandle.West,
            ResizeHandle.East,
            ResizeHandle.North,
            ResizeHandle.South
        ];

        for (int index = 0; index < points.Length; index++)
        {
            System.Drawing.Rectangle hitBox = new(
                points[index].X - HandleSize,
                points[index].Y - HandleSize,
                HandleSize * 2,
                HandleSize * 2);
            if (hitBox.Contains(point))
                return handles[index];
        }

        return ResizeHandle.None;
    }

    System.Drawing.Point[] GetSelectionHandles()
    {
        int left = currentRectangle.Left;
        int top = currentRectangle.Top;
        int right = currentRectangle.Right;
        int bottom = currentRectangle.Bottom;
        int middleX = (left + right) / 2;
        int middleY = (top + bottom) / 2;
        return
        [
            new(left, top),
            new(right, top),
            new(left, bottom),
            new(right, bottom),
            new(left, middleY),
            new(right, middleY),
            new(middleX, top),
            new(middleX, bottom)
        ];
    }

    System.Drawing.Rectangle Normalize(System.Drawing.Point point)
    {
        return System.Drawing.Rectangle.FromLTRB(
            System.Math.Clamp(System.Math.Min(selectionStartPoint.X, point.X), 0, ClientSize.Width),
            System.Math.Clamp(System.Math.Min(selectionStartPoint.Y, point.Y), 0, ClientSize.Height),
            System.Math.Clamp(System.Math.Max(selectionStartPoint.X, point.X), 0, ClientSize.Width),
            System.Math.Clamp(System.Math.Max(selectionStartPoint.Y, point.Y), 0, ClientSize.Height));
    }

    System.Drawing.Rectangle ClampRectangle(System.Drawing.Rectangle rectangle, int offsetX, int offsetY)
    {
        int left = System.Math.Clamp(rectangle.Left + offsetX, 0, ClientSize.Width - rectangle.Width);
        int top = System.Math.Clamp(rectangle.Top + offsetY, 0, ClientSize.Height - rectangle.Height);
        return new System.Drawing.Rectangle(left, top, rectangle.Width, rectangle.Height);
    }

    System.Drawing.Rectangle ResizeRectangle(
        System.Drawing.Rectangle source,
        System.Drawing.Point point,
        ResizeHandle handle)
    {
        int left = source.Left;
        int top = source.Top;
        int right = source.Right;
        int bottom = source.Bottom;

        if (handle == ResizeHandle.West || handle == ResizeHandle.NorthWest || handle == ResizeHandle.SouthWest)
            left = System.Math.Clamp(point.X, 0, right - SelectionMinimumSize);
        if (handle == ResizeHandle.East || handle == ResizeHandle.NorthEast || handle == ResizeHandle.SouthEast)
            right = System.Math.Clamp(point.X, left + SelectionMinimumSize, ClientSize.Width);
        if (handle == ResizeHandle.North || handle == ResizeHandle.NorthWest || handle == ResizeHandle.NorthEast)
            top = System.Math.Clamp(point.Y, 0, bottom - SelectionMinimumSize);
        if (handle == ResizeHandle.South || handle == ResizeHandle.SouthWest || handle == ResizeHandle.SouthEast)
            bottom = System.Math.Clamp(point.Y, top + SelectionMinimumSize, ClientSize.Height);

        return System.Drawing.Rectangle.FromLTRB(left, top, right, bottom);
    }

    void LayoutToolbar()
    {
        int buttonWidth = 74;
        int buttonHeight = 30;
        int spacing = 8;
        int toolbarWidth = buttonWidth * 3 + spacing * 2;
        int x = System.Math.Clamp(
            currentRectangle.Left,
            12,
            System.Math.Max(12, ClientSize.Width - toolbarWidth - 12));
        int y = currentRectangle.Bottom + 12;
        if (y + buttonHeight > ClientSize.Height - 12)
            y = System.Math.Max(12, currentRectangle.Top - buttonHeight - 12);

        confirmButtonRectangle = new System.Drawing.Rectangle(x, y, buttonWidth, buttonHeight);
        retryButtonRectangle = new System.Drawing.Rectangle(x + buttonWidth + spacing, y, buttonWidth, buttonHeight);
        cancelButtonRectangle = new System.Drawing.Rectangle(x + (buttonWidth + spacing) * 2, y, buttonWidth, buttonHeight);
    }

    protected override void OnPaint(System.Windows.Forms.PaintEventArgs eventArgs)
    {
        // Draw dimming only outside the selection. The preview background inside
        // remains the exact original screen pixels.
        if (hasSelection)
        {
            using System.Drawing.SolidBrush dimBrush =
                new(System.Drawing.Color.FromArgb(97, 0, 0, 0));

            int left = currentRectangle.Left;
            int top = currentRectangle.Top;
            int right = currentRectangle.Right;
            int bottom = currentRectangle.Bottom;

            eventArgs.Graphics.FillRectangle(dimBrush, 0, 0, ClientSize.Width, top);
            eventArgs.Graphics.FillRectangle(dimBrush, 0, bottom, ClientSize.Width, ClientSize.Height - bottom);
            eventArgs.Graphics.FillRectangle(dimBrush, 0, top, left, currentRectangle.Height);
            eventArgs.Graphics.FillRectangle(dimBrush, right, top, ClientSize.Width - right, currentRectangle.Height);

            using System.Drawing.Pen pen = new(System.Drawing.Color.White, 2.4f);
            eventArgs.Graphics.DrawRectangle(pen, currentRectangle);

            foreach (System.Drawing.Point point in GetSelectionHandles())
            {
                System.Drawing.Rectangle handleBox = new(point.X - 5, point.Y - 5, 10, 10);
                using System.Drawing.SolidBrush handleBrush = new(System.Drawing.Color.White);
                using System.Drawing.Pen handlePen = new(System.Drawing.Color.FromArgb(30, 41, 59), 1.6f);
                eventArgs.Graphics.FillRectangle(handleBrush, handleBox);
                eventArgs.Graphics.DrawRectangle(handlePen, handleBox);
            }
        }
        else
        {
            using System.Drawing.SolidBrush dimBrush =
                new(System.Drawing.Color.FromArgb(97, 0, 0, 0));
            eventArgs.Graphics.FillRectangle(dimBrush, ClientRectangle);
        }

        string hint = hasSelection
            ? "8 个白点调整大小；选区其他位置拖动移动；框选外部重新选择；Enter 确认，Esc 取消"
            : "拖拽选择区域；Esc 取消";
        System.Drawing.Color hintShadow = System.Drawing.Color.FromArgb(160, 0, 0, 0);
        System.Windows.Forms.TextRenderer.DrawText(
            eventArgs.Graphics,
            hint,
            Font,
            new System.Drawing.Point(19, 19),
            hintShadow);
        System.Windows.Forms.TextRenderer.DrawText(
            eventArgs.Graphics,
            hint,
            Font,
            new System.Drawing.Point(18, 18),
            System.Drawing.Color.White);

        if (showToolbar == false)
            return;

        DrawToolbarButton(eventArgs.Graphics, confirmButtonRectangle, "确认截图", true);
        DrawToolbarButton(eventArgs.Graphics, retryButtonRectangle, "重新框选", false);
        DrawToolbarButton(eventArgs.Graphics, cancelButtonRectangle, "取消", false);
    }

    void DrawToolbarButton(
        System.Drawing.Graphics graphics,
        System.Drawing.Rectangle rectangle,
        string text,
        bool primary)
    {
        using System.Drawing.SolidBrush backgroundBrush = new(primary
            ? System.Drawing.Color.FromArgb(236, 244, 255)
            : System.Drawing.Color.FromArgb(82, 88, 100));
        using System.Drawing.SolidBrush textBrush = new(primary
            ? System.Drawing.Color.FromArgb(14, 18, 26)
            : System.Drawing.Color.White);
        graphics.FillRectangle(backgroundBrush, rectangle);
        System.Windows.Forms.TextRenderer.DrawText(
            graphics,
            text,
            Font,
            rectangle,
            textBrush.Color,
            System.Windows.Forms.TextFormatFlags.HorizontalCenter | System.Windows.Forms.TextFormatFlags.VerticalCenter);
    }
}












