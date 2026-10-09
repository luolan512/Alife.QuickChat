using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Marisa.QuickChat;

/// <summary>
/// 快聊消息信封。
/// <para>
/// 语言模型只有 system / user / assistant / tool 四种角色，表达不了「第三方参与者」，
/// 所以群聊里「谁说了什么」不能靠自然语言前缀让模型去猜，必须做成机器可读的结构化信封。
/// </para>
/// <para>
/// 格式：<c>[QC v=1 conv=&lt;会话&gt; kind=&lt;dm|group&gt; from=&lt;主体&gt; name="显示名" id=&lt;编号&gt; type=&lt;text|batch&gt; mention="甲,乙"]</c>
/// </para>
/// <para>
/// 标签刻意用 <c>QC</c> 而不是别的插件的标签，避免和 LocalChat 之类的同族插件在同一段
/// 上下文里互相误判。主体一律走 <c>user</c> 角色（诚实待在不可信层），
/// 绝不使用 <c>system</c> 承载他人发言——那会把不可信内容提升到指令层，放大注入攻击面。
/// </para>
/// </summary>
public static class QuickChatEnvelope
{
    public const string Version = "1";
    public const string OpenTag = "[QC ";
    public const string CloseTag = "]";

    /// <summary>系统事件（非人类、非角色发言）在信封中的主体标记。</summary>
    public const string SystemSource = "system";

    // ⚠️ 这里刻意【不用】 [GeneratedRegex]。
    // Alife 编译插件走的是裸 Roslyn（CSharpCompiler.Compile → CSharpCompilation.Create），
    // 它只把源码解析成语法树，**不会运行任何源生成器**。
    // 用 [GeneratedRegex] 声明 partial 方法会因为没有生成实现而在 Alife 里报
    //   CS8795: 分部方法「…」必须具有实现部分，因为它具有可访问性修饰符
    // ——本地 dotnet build 能过，部署到 Storage\Plugins 后 Alife 编不出来。
    //
    // 同理也刻意【不加】 RegexOptions.Compiled：那要求运行时动态生成 IL，
    // 一旦宿主环境不支持就是启动即崩。这两个正则只匹配很短的字符串，
    // 解释执行的开销可以忽略，可靠性远比这点性能重要。
    static readonly Regex EnvelopeMatcher = new(
        @"^\[QC\s(?<attrs>[^\]]*)\]\s*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    static readonly Regex AttributeMatcher = new(
        @"(?<key>[A-Za-z_][A-Za-z0-9_]*)=(?<value>""(?:[^""\\]|\\.)*""|[^\s""]+)",
        RegexOptions.CultureInvariant);

    /// <summary>生成一条快聊消息的信封首行。</summary>
    public static string Encode(
        ChatRecord message,
        string conversationKind,
        IReadOnlyList<QuickChatPrincipal>? mentions = null)
    {
        StringBuilder builder = new(96);
        builder.Append(OpenTag);
        builder.Append("v=").Append(Version);
        builder.Append(" conv=").Append(message.ConversationId);
        builder.Append(" kind=").Append(conversationKind);
        builder.Append(" from=").Append(message.SenderId);
        builder.Append(" name=").Append(Quote(message.SenderName));
        builder.Append(" id=").Append(message.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(" type=").Append(string.IsNullOrEmpty(message.ContentType) ? "text" : message.ContentType);

        if (mentions is { Count: > 0 })
        {
            builder.Append(" mention=")
                .Append(Quote(string.Join(',', mentions.Select(mention => mention.Name))));
        }

        builder.Append(CloseTag);
        return builder.ToString();
    }

    /// <summary>生成一条系统事件信封（投递提示、错误、纠正等）。</summary>
    public static string EncodeSystem(string conversationId, string conversationKind, string text)
    {
        ChatRecord synthetic = new()
        {
            ConversationId = conversationId,
            SenderId = SystemSource,
            SenderName = "系统",
            ContentType = "text",
            Text = text,
        };
        return Encode(synthetic, conversationKind);
    }

    /// <summary>生成一条工具返回值信封。</summary>
    public static string EncodeToolResult(string tool) =>
        $"{OpenTag}v={Version} kind=tool from={SystemSource} name=系统 type=tool result={tool}{CloseTag}";

    /// <summary>
    /// 生成一条「主人发言」的信封。
    /// <para>
    /// 私聊投递也要带信封，否则模型收到的就是一段**没有任何频道标记的裸文本**，
    /// 而系统提示词里又写着「要在快聊里发言必须调用 QuickChatSend」——
    /// 模型只能猜自己在哪个频道，于是会把私聊的回复也发进群聊。
    /// 带上 <c>kind=dm</c> 之后，「我在哪个频道」变成一条可读的正面信号，不必靠猜。
    /// </para>
    /// </summary>
    public static string EncodeHuman(string conversationId, string conversationKind, long messageId, string senderName)
    {
        ChatRecord synthetic = new()
        {
            Id = messageId,
            ConversationId = conversationId,
            SenderId = QuickChatPrincipal.HumanId,
            SenderName = senderName,
            ContentType = "text",
        };
        return Encode(synthetic, conversationKind);
    }

    /// <summary>
    /// 读出一段投递文本的信封类型（<c>dm</c> / <c>group</c> / <c>tool</c>…），
    /// 没有信封时返回 <c>null</c>。
    /// <para>
    /// 只解析第一行：投递格式固定是「信封首行 + 正文」，正文里出现的方括号不该被误读。
    /// </para>
    /// </summary>
    public static string? KindOf(string? message) => AttributeOf(message, "kind");

    /// <summary>
    /// 读出一段投递文本的信封里的会话 ID（<c>conv=</c>），没有信封时返回 <c>null</c>。
    /// <para>
    /// 角色的私聊回复靠它落回正确的会话：一个角色可以有多条私聊线（默认私聊 ID 就是
    /// 角色名，用户「新建」出来的是 <c>dm:小梦#2</c>），只认角色名的话，
    /// 在 #2 里说的话会被回复到默认私聊里。
    /// </para>
    /// </summary>
    public static string? ConversationOf(string? message) => AttributeOf(message, "conv");

    /// <summary>
    /// 取信封首行的某个属性。
    /// <para>
    /// 只解析第一行：投递格式固定是「信封首行 + 正文」，正文里出现的方括号不该被误读。
    /// </para>
    /// </summary>
    static string? AttributeOf(string? message, string key)
    {
        if (string.IsNullOrEmpty(message))
            return null;

        int newline = message.IndexOf('\n');
        string head = newline < 0 ? message : message[..newline];
        Dictionary<string, string>? attributes = TryParse(head);
        return attributes != null && attributes.TryGetValue(key, out string? value) ? value : null;
    }

    /// <summary>把信封和正文拼成投递给模型的一段文本。</summary>
    public static string Compose(string envelope, string body) =>
        string.IsNullOrEmpty(body) ? envelope : envelope + "\n" + body;

    /// <summary>解析信封属性，解析失败返回 null。</summary>
    public static Dictionary<string, string>? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        Match head = EnvelopeMatcher.Match(line.Trim());
        if (head.Success == false)
            return null;

        Dictionary<string, string> attributes = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributeMatcher.Matches(head.Groups["attrs"].Value))
        {
            string value = match.Groups["value"].Value;
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            attributes[match.Groups["key"].Value] = value;
        }

        return attributes.Count == 0 ? null : attributes;
    }

    /// <summary>
    /// 从消息正文里解析 @ 提及。
    /// <para>
    /// 候选按名字长度降序匹配，保证最长优先——否则「@小雨花」会误命中「小雨」。
    /// 名字后必须是非名字字符（空白、标点、结尾），避免前缀误命中。
    /// </para>
    /// </summary>
    public static List<QuickChatPrincipal> ParseMentions(string? text, IEnumerable<QuickChatPrincipal> candidates)
    {
        List<QuickChatPrincipal> found = new();
        if (string.IsNullOrEmpty(text))
            return found;

        foreach (QuickChatPrincipal candidate in candidates
                     .Where(candidate => candidate.IsAgent && candidate.Name.Length > 0)
                     .OrderByDescending(candidate => candidate.Name.Length))
        {
            if (found.Any(item => item.SameAs(candidate)))
                continue;
            if (ContainsMention(text, candidate.Name))
                found.Add(candidate);
        }

        return found;
    }

    static bool ContainsMention(string text, string name)
    {
        int from = 0;
        while (from < text.Length)
        {
            int at = text.IndexOf('@', from);
            if (at < 0)
                return false;

            int start = at + 1;
            if (start + name.Length <= text.Length &&
                string.Compare(text, start, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                int end = start + name.Length;
                if (end >= text.Length || IsNameChar(text[end]) == false)
                    return true;
            }

            from = at + 1;
        }

        return false;
    }

    /// <summary>名字字符：字母、数字、下划线（含 CJK）——用于判断 <c>@名字</c> 的右边界。</summary>
    static bool IsNameChar(char value) => char.IsLetterOrDigit(value) || value == '_';

    static string Quote(string value) =>
        value.Contains(' ') || value.Contains('"')
            ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : value;
}
