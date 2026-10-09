using System;

namespace Marisa.QuickChat;

/// <summary>快聊里一次发言的主体类别。</summary>
public enum QuickChatPrincipalKind
{
    /// <summary>本机用户。</summary>
    Human,
    /// <summary>一个 Alife 角色。</summary>
    Agent,
    /// <summary>快聊自身产生的系统事件（投递提示、错误、格式纠正等）。</summary>
    System,
}

/// <summary>
/// 主体标识。约定 <c>human</c> / <c>agent:角色名</c> / <c>system</c>。
/// <para>
/// 群聊的成员、未读、发言归属都以此为准，而不是散落各处的裸字符串比较。
/// 之所以不用裸字符串：角色名可能和会话 ID、和系统标记重名，一旦混用，
/// 「这条消息是谁发的」就会变成一次猜测。
/// </para>
/// </summary>
public readonly record struct QuickChatPrincipal(string Id)
{
    public const string AgentPrefix = "agent:";
    public const string HumanId = "human";
    public const string SystemId = "system";

    public static QuickChatPrincipal Human { get; } = new(HumanId);
    public static QuickChatPrincipal System { get; } = new(SystemId);

    public static QuickChatPrincipal Agent(string name) => new(AgentPrefix + name);

    public static QuickChatPrincipal Parse(string? id) =>
        new(string.IsNullOrWhiteSpace(id) ? SystemId : id.Trim());

    /// <summary>把「人类」的各种输入写法归一化。</summary>
    public static bool TryParseHumanAlias(string? text, out QuickChatPrincipal principal)
    {
        principal = Human;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string value = text.Trim();
        return value.Equals("human", StringComparison.OrdinalIgnoreCase)
            || value is "我" or "主人" or "人类" or "本机用户";
    }

    public bool IsHuman => Id.Equals(HumanId, StringComparison.OrdinalIgnoreCase);
    public bool IsSystem => Id.Equals(SystemId, StringComparison.OrdinalIgnoreCase);
    public bool IsAgent => Id.StartsWith(AgentPrefix, StringComparison.OrdinalIgnoreCase);

    public QuickChatPrincipalKind Kind =>
        IsHuman ? QuickChatPrincipalKind.Human : IsAgent ? QuickChatPrincipalKind.Agent : QuickChatPrincipalKind.System;

    /// <summary>去掉前缀后的显示名。</summary>
    public string Name => IsHuman ? "人类" : IsAgent ? Id[AgentPrefix.Length..] : Id;

    /// <summary>角色名，仅当 <see cref="IsAgent"/> 时有意义。</summary>
    public string AgentName => IsAgent ? Id[AgentPrefix.Length..] : "";

    public bool SameAs(QuickChatPrincipal other) => Id.Equals(other.Id, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Id;
}
