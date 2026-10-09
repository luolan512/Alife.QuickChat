using System;
using System.Linq;

namespace Marisa.QuickChat;

/// <summary>一次投递判定的结果。</summary>
/// <param name="Deliver">是否把这条消息投递给该角色。</param>
/// <param name="Activate">是否顺带把该会话置为「活跃」（被 @ 之后一段时间内持续接收）。</param>
public readonly record struct QuickChatDeliveryDecision(bool Deliver, bool Activate)
{
    public static readonly QuickChatDeliveryDecision Skip = new(false, false);
}

/// <summary>
/// 投递门槛。
/// <para>
/// 抽成纯函数的原因：这段判定是整个群聊最容易出错、也最难验证的地方——
/// 「哪些消息该惊动哪个角色」直接决定角色会不会刷屏、会不会漏掉 @、会不会自言自语。
/// 与 <c>Random</c> 解耦后可以逐条穷举验证，不必启动整个 Alife 运行时。
/// </para>
/// </summary>
public static class QuickChatGate
{
    /// <param name="active">该会话是否已处于「活跃」状态（此前被 @ 过，尚未过期）。</param>
    /// <param name="muted">该角色是否已把自己在这个会话里静音。</param>
    /// <param name="roll">0 到 1 之间的随机数，由调用方提供以便测试。</param>
    public static QuickChatDeliveryDecision Decide(
        QuickChatConfig config,
        QuickChatPrincipal agent,
        QuickChatStoredConversation conversation,
        ChatRecord message,
        bool active,
        bool muted,
        double roll)
    {
        // 总开关与单会话开关
        if (config.AutoDeliver == false)
            return QuickChatDeliveryDecision.Skip;
        if (conversation.AutoReply == false)
            return QuickChatDeliveryDecision.Skip;

        // 角色把自己在这个会话里静音了——只影响它自己，别人照常
        if (muted)
            return QuickChatDeliveryDecision.Skip;

        // 自己发的消息不投给自己——否则角色会被自己的话再叫醒一次，形成自激循环
        if (message.SenderId.Equals(agent.Id, StringComparison.OrdinalIgnoreCase))
            return QuickChatDeliveryDecision.Skip;

        // 系统事件（格式纠正、离线补投提示）总是投递
        if (message.Sender.IsSystem)
            return new QuickChatDeliveryDecision(true, false);

        // 主人说话 = 直接对着全体说：所有成员都必须收到，并把会话置为活跃。
        //
        // 这一条是「在群里说话，AI 完全没反应」的修复。原来人类发言也会落到下面
        // 「按概率旁听」那一支：每个角色只有 GroupListenChance（默认 0.12）的概率收到，
        // 于是主人发一句，88% 的角色根本不知道有人说过话。
        //
        // 旁听概率的用途是抑制「角色之间」互相刷屏 —— 角色甲随便说一句，
        // 不该把乙丙丁全都叫起来。主人没有这个顾虑：他说话就是在跟所有人说话。
        if (message.Sender.IsHuman)
            return new QuickChatDeliveryDecision(true, true);

        // ────────────────────── 以下都是「别的角色说的话」 ──────────────────────

        // 接话轮数上限：主人起个头之后，角色之间最多互相接 RelayDepth 轮，到顶就断。
        //
        // 这是「我起个头，她们能一直接话聊下去」的硬闸门。提示词里也写了不许没完没了地
        // 接话（见 QuickChatAgentPrompt 的群聊纪律），但提示词只是提示 —— 模型未必听，
        // 这条才是保证。人类发言在上面已经返回了，所以主人随时可以重新起个话头。
        //
        // 放在 @ 判定【之前】是故意的：@ 也是一条接力的路，放在后面的话，
        // 深链上一个 @ 就能把链条续下去，闸门形同虚设。
        if (message.Depth >= RelayDepth(config))
            return QuickChatDeliveryDecision.Skip;

        // 私聊：对方开口就投递。
        // 人类↔角色的私聊、角色↔角色的私聊（dm:甲|乙）都算 —— 私聊里没有「刷屏」问题，
        // 两个人一条线，谁说话都该被听到。
        if (conversation.IsGroup == false)
            return new QuickChatDeliveryDecision(true, false);

        // 群聊：被 @ 一定投递，并把会话置为活跃
        if (message.Mentions.Contains(agent.Id, StringComparer.OrdinalIgnoreCase))
            return new QuickChatDeliveryDecision(true, true);

        // 别的角色在群里说的话：按概率旁听。
        //
        // ⚠ 这里【不能】再写「已活跃就无条件投递」。
        // 原来就是那么写的，后果是：主人说一句话 → 全员被置为活跃（默认 20 分钟）→
        // 此后每个角色说的每一句都无条件投给所有活跃角色 → 她们能一直接话聊下去。
        // 「活跃」现在的含义收窄成「更留意这个群」，用更高的概率表达，而不是全盘接收。
        double chance = active
            ? Math.Clamp(config.ActiveListenChance, 0, 1)
            : Math.Clamp(config.GroupListenChance, 0, 1);

        return new QuickChatDeliveryDecision(chance > 0 && roll < chance, false);
    }

    /// <summary>
    /// 角色之间互相接话的轮数上限。
    /// <para>
    /// 下限 1：至少允许「别人说完我接一句」。上限 12 是防呆 —— 设成几百就等于关掉了闸门，
    /// 而那正是这个开关要防的事。
    /// </para>
    /// </summary>
    public static int RelayDepth(QuickChatConfig config) => Math.Clamp(config.MaxRelayDepth, 1, 12);
}
