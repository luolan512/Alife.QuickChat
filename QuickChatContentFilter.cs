using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Net;

namespace Marisa.QuickChat;

/// <summary>
/// QuickChat 只展示真实对话文本；思考内容和 XML/函数调用标签都在进入 UI 前移除。
/// </summary>
public static class QuickChatContentFilter
{
    public static string CleanUserText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        string text = raw;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // 不同交互端可能注入来源和时间元数据；快聊只展示用户实际说的话。
        text = Regex.Replace(
            text,
            @"(?:^|\s)当前时间\s*[:：]\s*\[[^\]]+\]\s*",
            string.Empty,
            RegexOptions.IgnoreCase);
        text = Regex.Replace(
            text,
            @"(?:^|\s)消息来源\s*[:：]?\s*\[[^\]]+\]\s*",
            string.Empty,
            RegexOptions.IgnoreCase);
        text = Regex.Replace(
            text,
            @"^\s*\[消息来源[^\]]*\]\s*",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        // MessageFilter.MessageAppend is prompt guidance injected after the real input.
        // Keep it for the AI, but never show it as part of a QuickChat user bubble.
        text = Regex.Replace(
            text,
            @"\s*[（(]\s*注意！看清消息来源和意图[\s\S]*?[)）]\s*$",
            string.Empty,
            RegexOptions.IgnoreCase);

        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    static readonly HashSet<string> InjectedMessageSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemEventService",
        "SystemEventBoostService",
        "温柔纸条"
    };

    public static bool IsInjectedSystemMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (raw.Contains(Alife.Framework.ChatBot.PokeMessageTag, StringComparison.OrdinalIgnoreCase))
            return true;

        // System modules inject prompts/status through source tags. Keep ChatWindow messages,
        // whose source marker is removed later, but never display module injections as user bubbles.
        if (InjectedMessageSources.Any(source =>
                raw.Contains("[消息来源(" + source + ")]", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("消息来源:[" + source + "]", StringComparison.OrdinalIgnoreCase)))
            return true;

        return raw.Contains("[来自系统", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("[功能说明(", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("[工具文档(", StringComparison.OrdinalIgnoreCase);
    }

    public static string CleanOutgoingDisplayText(string? raw, IEnumerable<string> attachmentPaths)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        string text = raw;
        foreach (string path in attachmentPaths
            .Where(item => string.IsNullOrWhiteSpace(item) == false)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            text = text.Replace(path, "\n", StringComparison.OrdinalIgnoreCase);
        }

        // QuickChat frontend packages attachments as text labels. The UI renders
        // structured attachments separately, so strip these labels before display.
        text = Regex.Replace(
            text,
            @"^\s*(?:\[消息来源[^\]]+\]\s*)?用户发送了(?:一张|\d+\s*张)图片\s*[:：]?\s*\r?\n?",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        text = Regex.Replace(
            text,
            @"^\s*(?:\[消息来源[^\]]+\]\s*)?用户发送了(?:一个|\d+\s*个)文件\s*[:：]?\s*\r?\n?",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        text = Regex.Replace(
            text,
            @"^\s*用户文字\s*[:：]\s*\r?\n?",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        // Defensive cleanup for older hosts that left a truncated label.
        text = Regex.Replace(
            text,
            @"^\s*用户发送了\s*\r?\n?$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        // 多模态请求中的路径元数据只给模型，不应在快聊气泡里展示。
        // 用状态机而不是跨行正则，避免宿主正则/换行差异把残留带到 UI。
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        System.Collections.Generic.List<string> displayLines = new();
        bool skippingImageSourceBlock = false;
        foreach (string sourceLine in lines)
        {
            string line = sourceLine.Trim();
            if (line.StartsWith("图片来源路径", StringComparison.OrdinalIgnoreCase))
            {
                skippingImageSourceBlock = true;
                continue;
            }

            if (skippingImageSourceBlock)
            {
                if (line.StartsWith("- ") || line.StartsWith("-\t") || line.Equals("-", StringComparison.Ordinal))
                    continue;

                skippingImageSourceBlock = false;
            }

            displayLines.Add(sourceLine);
        }

        text = string.Join("\n", displayLines);
        string cleaned = CleanUserText(text);

        // 附件消息清洗后为空时保留占位符。
        // 否则 message.Content 为空：主聊天记录里整条消息变成空白，
        // 快聊 OnChatSent 收到空文本直接早退，图片附件跟着一起丢——
        // 表现为只有第一条带文字的图片消息能正常显示，后续纯图片消息全部消失。
        if (string.IsNullOrWhiteSpace(cleaned) == false)
            return cleaned;

        List<string> paths = attachmentPaths?
            .Where(item => string.IsNullOrWhiteSpace(item) == false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        if (paths.Count == 0)
            return cleaned;

        int imageCount = paths.Count(item => LooksLikeImagePath(item));
        return imageCount == paths.Count
            ? "[图片]"
            : imageCount == 0 ? "[文件]" : "[图片和文件]";
    }

    static bool LooksLikeImagePath(string? path) =>
        Regex.IsMatch(path ?? string.Empty, @"\.(?:png|jpe?g|gif|webp|bmp)$", RegexOptions.IgnoreCase);

    static readonly Regex QuickChatAttachmentMarkerRegex = new(
        @"\[\s*\[\s*QuickChat(?:Attachment|Image|File)\s*\]\]\s*([\s\S]*?)\[\s*\[\s*/\s*QuickChat(?:Attachment|Image|File)\s*\]\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex QuickChatAttachmentTagRegex = new(
        @"<\s*QuickChat(?:Attachment|Image|File)\b(?:""[^""]*""|'[^']*'|[^>])*(?:/>|>[\s\S]*?<\s*/\s*QuickChat(?:Attachment|Image|File)\s*>)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex QuickChatAttachmentAttributeRegex = new(
        @"\b(?:path|src|url)\s*=\s*(?:""(?<quoted>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s/>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<string> ExtractQuickChatAttachmentSources(string? raw, out string text)
    {
        text = raw ?? string.Empty;
        List<string> sources = new();

        foreach (Match match in QuickChatAttachmentMarkerRegex.Matches(text))
        {
            string source = match.Groups[1].Value.Trim().Trim('"', '\'', '<', '>');
            if (string.IsNullOrWhiteSpace(source) == false)
                sources.Add(source);
        }

        text = QuickChatAttachmentMarkerRegex.Replace(text, string.Empty);

        foreach (Match match in QuickChatAttachmentTagRegex.Matches(text))
        {
            string tag = match.Value;
            Match attribute = QuickChatAttachmentAttributeRegex.Match(tag);
            string source = attribute.Success
                ? attribute.Groups["quoted"].Success
                    ? attribute.Groups["quoted"].Value
                    : attribute.Groups["single"].Success
                        ? attribute.Groups["single"].Value
                        : attribute.Groups["bare"].Value
                : Regex.Replace(Regex.Replace(tag, @"<[^>]+>", string.Empty), @"\s+", " ").Trim();

            source = WebUtility.HtmlDecode(source).Trim().Trim('"', '\'', '<', '>');
            if (string.IsNullOrWhiteSpace(source) == false)
                sources.Add(source);
        }

        text = QuickChatAttachmentTagRegex.Replace(text, string.Empty);
        return sources;
    }

    public static string CleanAssistantText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        string text = raw;

        text = Regex.Replace(
            text,
            @"<\s*(think|thinking|reasoning)\b[^>]*>.*?<\s*/\s*\1\s*>",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        text = Regex.Replace(
            text,
            @"<\s*(?:think|thinking|reasoning)\b[^>]*/>",
            string.Empty,
            RegexOptions.IgnoreCase);

        // 函数调用 XML（python、expression、motion 等）不是展示文本。
        // 反复移除，兼容嵌套标签；<speak> 内容后续单独提取。
        string previousXml;
        do
        {
            previousXml = text;
            text = Regex.Replace(
                text,
                @"<\s*(?!speak\b)([A-Za-z_][\w:.-]*)\b(?:[^>\""]|\""[^\""]*\""|'[^']*')*>[\s\S]*?<\s*/\s*\1\s*>",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }
        while (text != previousXml);

        MatchCollection speakMatches = Regex.Matches(
            text,
            @"<\s*speak\b[^>]*>(.*?)<\s*/\s*speak\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (speakMatches.Count > 0)
        {
            text = string.Join(
                "\n",
                speakMatches.Select(match => match.Groups[1].Value.Trim()));
        }

        text = Regex.Replace(text, @"<\?[\s\S]*?\?>", string.Empty);
        text = Regex.Replace(text, @"<!--[\s\S]*?-->", string.Empty);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"<[^>\n]{0,300}>", string.Empty);

        text = Regex.Replace(
            text,
            @"^\s*\[(?:消息来源|功能说明|工具文档|来自系统的杂项消息推送)[^\]]*\]\s*",
            string.Empty,
            RegexOptions.Multiline);

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");

        return text.Trim();
    }
}




