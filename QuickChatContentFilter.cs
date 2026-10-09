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

    /// <summary>
    /// 判断一条消息正文看起来是不是系统注入。
    /// <para>
    /// ⚠️ <b>不要再拿它当归属判定的主依据。</b>
    /// 快聊已经改用「发送登记」（<c>QuickChatRuntime.ClaimPendingOutgoing</c>）——
    /// 只有登记过的那条才进私聊流，认领不到的一律不进。
    /// </para>
    /// <para>
    /// 为什么不能用它当主依据：这里的标记全是<b>用户可编辑的提示词</b>
    /// （MessageFilter 的时间戳/尾注、SystemEventBoost 的各类 Prompt、
    /// 快聊自己的 [消息来源(QuickChat)] 前缀），改一个字黑名单就失效；
    /// 而失效的代价是把用户自己的话整条吞掉。而且 <c>ChatBot.Poke</c> 会把
    /// 多个插件的内容拼成同一条消息，只要里面有一个标记，整条就没了。
    /// </para>
    /// <para>保留它只作日志/兜底用途。</para>
    /// </summary>
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

    /// <summary>附件标记只剩开标签时的样子（闭标签还没流到）。</summary>
    static readonly Regex UnclosedAttachmentMarkerRegex = new(
        @"\[{2}\s*\[?\s*QuickChat(?:Attachment|Image|File)\s*\]*\s*\]{2}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 流式过程中的即时清洗。
    /// <para>
    /// 规则和 <see cref="CleanAssistantText"/> 完全一致，额外只做一件事：把「还没流完的尾巴」抹掉。
    /// 完整正则要求标签成对（<c>&lt;speak&gt;…&lt;/speak&gt;</c>）、附件标记也要求成对，
    /// 而流式期间随时可能停在半个标签上 —— 那一刻正则匹配不到，原文就会被显示出来闪一下。
    /// </para>
    /// <para>
    /// 注意：这里只用于「边生成边显示」的临时气泡。定稿仍然走
    /// <see cref="ExtractQuickChatAttachmentSources"/> + <see cref="CleanAssistantText"/>，
    /// 两条路径不能互相替代。
    /// </para>
    /// </summary>
    public static string CleanStreamingText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        // 完整的附件标记先摘掉；摘不掉的（只有开标签）在下面按「未闭合」截断。
        ExtractQuickChatAttachmentSources(raw, out string withoutAttachments);
        string text = CleanAssistantText(withoutAttachments);

        // 附件标记的开标签已经到齐、闭标签还没到：从这里往后全是路径，整段丢掉。
        Match unclosed = UnclosedAttachmentMarkerRegex.Match(text);
        if (unclosed.Success)
            text = text.Substring(0, unclosed.Index);

        // 标签 / 标记只流了一半（`<QuickChat`、`[[QuickCha`）：
        // 完整正则都匹配不上，留着就会把半截源码显示出来。
        text = Regex.Replace(text, @"<[A-Za-z_/!?][^>\n]*$", string.Empty);
        text = Regex.Replace(text, @"\[{2,}[^\]\n]*$", string.Empty);

        return text.TrimEnd();
    }
}




