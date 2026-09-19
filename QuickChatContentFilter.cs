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

    public static bool IsInjectedSystemMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (raw.Contains(Alife.Framework.ChatBot.PokeMessageTag, StringComparison.OrdinalIgnoreCase))
            return true;

        // SystemEventService and other system modules inject source tags. Keep ChatWindow messages,
        // whose source marker is removed later, but never display module injections as user bubbles.
        return raw.Contains("[消息来源(SystemEventService)]", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("消息来源:[SystemEventService]", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("[来自系统", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("[功能说明(", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("[工具文档(", StringComparison.OrdinalIgnoreCase);
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



