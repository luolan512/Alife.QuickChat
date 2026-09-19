using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alife.Framework;
using ElectronNET.API;
using Microsoft.Extensions.Logging;

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
    object? configSource;
    readonly Dictionary<string, ChatBot> subscribedBots = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Action<ChatContext>> chatFinishedHandlers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Action<string>> chatSentHandlers = new(StringComparer.OrdinalIgnoreCase);

    QuickChatConfig config = new();
    QuickChatWindow? window;
    string? selectedPetId;
    string configSignature = string.Empty;
    bool hasAppliedConfig;
    int referenceCount;
    bool disposed;

    QuickChatRuntime(
        ChatActivitySystem chatActivitySystem,
        PluginSystem pluginSystem,
        ILogger<QuickChatModule> logger)
    {
        this.chatActivitySystem = chatActivitySystem;
        this.pluginSystem = pluginSystem;
        this.logger = logger;

        this.chatActivitySystem.Activated += OnChatActivityActivated;
        this.chatActivitySystem.Deactivated += OnChatActivityDeactivated;

        foreach (ChatActivity activity in chatActivitySystem.GetAllChatActivities().ToList())
            AttachChatBot(activity);
    }

    public static QuickChatRuntime GetOrCreate(
        ChatActivitySystem chatActivitySystem,
        PluginSystem pluginSystem,
        ILogger<QuickChatModule> logger)
    {
        lock (CurrentGate)
        {
            if (current == null)
                current = new QuickChatRuntime(chatActivitySystem, pluginSystem, logger);

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
    }

    void OnChatSent(string petId, string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
            return;

        // Poke/SystemEventService 是插件注入的系统提示，不是用户主动输入。
        if (QuickChatContentFilter.IsInjectedSystemMessage(rawMessage))
            return;

        string text = QuickChatContentFilter.CleanUserText(rawMessage);
        if (string.IsNullOrWhiteSpace(text) == false)
            AddMessage(petId, "user", text);
    }

    void OnChatFinished(string petId, ChatContext context)
    {
        string reply = QuickChatContentFilter.CleanAssistantText(context.AIMessage);
        if (string.IsNullOrWhiteSpace(reply) == false)
            AddMessage(petId, "assistant", reply);

        // ChatFinished 表示文字回复已经结束。此时可能仍在等待 XmlFunctionCaller/GSV 语音等
        // ChatFinishedAsync 处理器，所以这里提前解除 QuickChat 的输入锁定。
        if (MarkRequestCompleted(petId))
            SendState();
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
                    _ = SendMessageAsync(petIdElement.GetString(), textElement.GetString());
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

            case "hide":
                window?.Hide();
                break;
        }
    }

    void SelectPet(string? petId)
    {
        if (string.IsNullOrWhiteSpace(petId))
            return;

        selectedPetId = petId;
        SendState();
        SendSelectedHistory();
    }

    async Task SendMessageAsync(string? petId, string? rawText)
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

        string requestId = Guid.NewGuid().ToString("N");
        lock (stateGate)
        {
            busyPetIds.Add(petId);
            busyRequestIds[petId] = requestId;
        }

        SendState();

        try
        {
            string outgoing = config.MarkMessageSource
                ? "[消息来源(QuickChat)]" + text
                : text;

            var result = await activity.ChatBot.ChatAsync(outgoing, true);

            if (result.Exception != null)
            {
                logger.LogError(result.Exception, "QuickChat 对话失败：{PetId}", petId);
                AddSystemMessage(petId, "回复失败，请检查模型或网络配置。");
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
            bool changed = MarkRequestCompleted(petId, requestId);
            if (changed)
                SendState();
        }
    }

    void AddMessage(string petId, string role, string text)
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
            Electron.GlobalShortcut.Unregister(oldHotkey);

        registeredHotkeys.Clear();
        registeredHotkeys.AddRange(config.Hotkeys);

        foreach (string hotkey in config.Hotkeys)
        {
            try
            {
                Electron.GlobalShortcut.Register(hotkey, OnHotkeyPressed);
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
                window = new QuickChatWindow(logger);

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
            Electron.GlobalShortcut.Unregister(hotkey);
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
    public DateTimeOffset CreatedAt { get; set; }
}






