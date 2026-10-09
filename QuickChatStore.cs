using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marisa.QuickChat;

/// <summary>
/// 快聊的会话元数据（落盘形态）。
/// <para>
/// 注意与前端 DTO <see cref="QuickChatConversation"/> 区分：那个是给渲染层用的展示对象，
/// 这个是存储层的记录，多了成员静音、自动回复开关这些持久化字段。
/// </para>
/// </summary>
public sealed class QuickChatStoredConversation
{
    public string Id { get; set; } = "";
    /// <summary><c>dm</c> 或 <c>group</c>。</summary>
    public string Kind { get; set; } = "dm";
    public string Title { get; set; } = "";
    public List<string> Members { get; set; } = new();
    public string CreatorId { get; set; } = QuickChatPrincipal.HumanId;
    public bool MembersEdited { get; set; }

    /// <summary>会话级自动回复总开关，用户在窗口里控制。</summary>
    public bool AutoReply { get; set; } = true;
    /// <summary>被静音的成员主体。角色可以静音自己，只影响它自己，不影响别人。</summary>
    public List<string> Muted { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// 是否由「会话同步」逻辑管理。
    /// <para>
    /// <c>true</c>：运行时按「当前激活角色 + 配置里的群」自动建出来（每个角色的私聊、
    /// 公共大厅、配置里的小群）。<c>false</c>：用户点「新建会话」或角色调工具自建的会话，
    /// 同步逻辑既不会建它也不会删它，只受显式删除影响。
    /// </para>
    /// <para>
    /// 缺省 <c>true</c> 是为了兼容旧数据：老 <c>conversations.json</c> 里没有这个字段，
    /// 反序列化后取默认值，正好等于「旧会话都是受管的」。
    /// </para>
    /// </summary>
    public bool Managed { get; set; } = true;

    [JsonIgnore] public bool IsGroup => Kind.Equals("group", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 一条快聊消息（落盘形态）。
/// <para>
/// 名字刻意叫 <c>ChatRecord</c> 而不是 <c>QuickChatMessage</c>：后者是推给前端的 DTO，
/// 带附件、送达状态这些渲染字段；这个是纯数据，只关心「谁在哪个会话说了什么、编号多少」。
/// 两者混用会让「消息 ID」在不同层有不同含义。
/// </para>
/// </summary>
public sealed class ChatRecord
{
    public long Id { get; set; }
    public string ConversationId { get; set; } = "";
    public string SenderId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary><c>text</c> / <c>emote</c> / <c>batch</c> / <c>hint</c> / <c>notice</c>。</summary>
    public string ContentType { get; set; } = "text";
    public List<string> Mentions { get; set; } = new();
    public DateTimeOffset SentAt { get; set; }
    /// <summary>投递深度。用户发言为 0，每经过一次角色转发 +1，用于阻断角色互相无限对话。</summary>
    public int Depth { get; set; }

    [JsonIgnore] public QuickChatPrincipal Sender => QuickChatPrincipal.Parse(SenderId);
    [JsonIgnore] public bool IsEmote => ContentType.Equals("emote", StringComparison.OrdinalIgnoreCase);
}

sealed class QuickChatConversationStructure
{
    public long NextMessageId { get; set; } = 1;
    public List<string> DeletedGroups { get; set; } = new();
    public List<QuickChatStoredConversation> Conversations { get; set; } = new();
}

/// <summary>
/// 快聊消息存储。
/// <para>
/// 按会话分文件 + 会话内索引：结构变更（建会话/改名/开关）写 <c>conversations.json</c>，
/// 读游标写 <c>reads.json</c>，消息追加写 <c>messages/&lt;会话&gt;.jsonl</c>。
/// 消息编号 <see cref="ChatRecord.Id"/> 全局单调递增，所以「渲染顺序 = 编号顺序」，
/// 不需要给消息加锁，也不受「谁先回复谁后回复」影响。
/// </para>
/// <para>
/// <b>是否落盘</b>由会话自己的 <see cref="ConversationData.Persistent"/> 决定：
/// 用户关掉「保存私聊记录 / 保存群聊记录」时，会话仍然存在于内存里（编号、未读照常），
/// 只是不写磁盘，重启即丢。
/// </para>
/// </summary>
public sealed class QuickChatStore : IDisposable
{
    public QuickChatStore(string root)
    {
        this.root = root;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(MessagesRoot);

        structurePath = Path.Combine(root, "conversations.json");
        readsPath = Path.Combine(root, "reads.json");

        structure = ReadJson<QuickChatConversationStructure>(structurePath) ?? new QuickChatConversationStructure();
        reads = ReadJson<Dictionary<string, Dictionary<string, long>>>(readsPath)
                ?? new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);

        TryDeleteLegacyHiddenFile();

        long maxId = 0;
        foreach (QuickChatStoredConversation conversation in structure.Conversations)
        {
            ConversationData data = new(conversation, MessagesRoot) { Persistent = true };
            if (reads.TryGetValue(conversation.Id, out Dictionary<string, long>? cursors))
                data.Reads = new Dictionary<string, long>(cursors, StringComparer.OrdinalIgnoreCase);
            conversations[conversation.Id] = data;
            Load(data);
            maxId = Math.Max(maxId, data.LastId);
        }

        structure.NextMessageId = Math.Max(structure.NextMessageId, maxId + 1);
    }

    // ────────────────────────── 会话 ──────────────────────────

    public IReadOnlyList<QuickChatStoredConversation> All()
    {
        lock (gate)
            return structure.Conversations.ToArray();
    }

    public IReadOnlyList<QuickChatStoredConversation> Conversations(QuickChatPrincipal member)
    {
        lock (gate)
            return structure.Conversations
                .Where(conversation => HasMember(conversation, member))
                .ToArray();
    }

    public QuickChatStoredConversation? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        lock (gate)
            return conversations.TryGetValue(id.Trim(), out ConversationData? data) ? data.Meta : null;
    }

    /// <summary>
    /// 确保会话存在，并把标题/成员同步成给定值。
    /// <para>
    /// 群聊成员跟着「当前激活的角色」走，所以每次同步都可能变化；只有真的变了才写盘，
    /// 否则每帧一次 <c>SendState</c> 就会把 <c>conversations.json</c> 写爆。
    /// </para>
    /// </summary>
    /// <param name="managed">
    /// 该会话是否由同步逻辑管理（见 <see cref="QuickChatStoredConversation.Managed"/>）。
    /// </param>
    public QuickChatStoredConversation? Ensure(
        string id,
        string kind,
        string title,
        IEnumerable<QuickChatPrincipal> members,
        bool persistent,
        bool managed = true)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0)
            throw new InvalidOperationException("会话 ID 不能为空");

        List<string> wanted = members
            .Select(member => member.Id)
            .Where(value => string.IsNullOrWhiteSpace(value) == false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (gate)
        {
            if (structure.DeletedGroups.Contains(id, StringComparer.OrdinalIgnoreCase))
                return null;
            bool changed = false;

            if (conversations.TryGetValue(id, out ConversationData? existing) == false)
            {
                QuickChatStoredConversation created = new()
                {
                    Id = id,
                    Kind = string.IsNullOrWhiteSpace(kind) ? "dm" : kind.Trim(),
                    Title = title ?? "",
                    Members = wanted,
                    Managed = managed,
                };
                ConversationData data = new(created, MessagesRoot) { Persistent = persistent };
                conversations[id] = data;
                structure.Conversations.Add(created);
                Load(data);
                if (persistent)
                    SaveStructure();
                return created;
            }

            QuickChatStoredConversation meta = existing.Meta;
            if (existing.Persistent != persistent)
            {
                existing.Persistent = persistent;
                changed = true;
            }

            if (meta.Managed != managed)
            {
                meta.Managed = managed;
                changed = true;
            }

            if (string.Equals(meta.Kind, kind, StringComparison.OrdinalIgnoreCase) == false && string.IsNullOrWhiteSpace(kind) == false)
            {
                meta.Kind = kind.Trim();
                changed = true;
            }

            if (string.Equals(meta.Title, title ?? "", StringComparison.Ordinal) == false)
            {
                meta.Title = title ?? "";
                changed = true;
            }

            if (meta.MembersEdited == false && (meta.Members.Count != wanted.Count ||
                meta.Members.Zip(wanted, (left, right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase)).Any(equal => equal == false)))
            {
                meta.Members = wanted;
                changed = true;
            }

            if (changed && persistent)
                SaveStructure();

            return meta;
        }
    }

    public void RenameGroup(string conversationId, string title)
    {
        title = (title ?? "").Trim();
        if (title.Length is < 1 or > 60)
            throw new InvalidOperationException("群名称长度需为 1 到 60 字");

        lock (gate)
        {
            ConversationData data = Require(conversationId);
            if (data.Meta.IsGroup == false)
                throw new InvalidOperationException("只有群聊可以改名");
            data.Meta.Title = title;
            SaveStructure();
        }
    }

    public void SetAutoReply(string conversationId, bool enabled)
    {
        lock (gate)
        {
            ConversationData data = Require(conversationId);
            data.Meta.AutoReply = enabled;
            if (data.Persistent)
                SaveStructure();
        }
    }

    /// <summary>静音/恢复某个成员自己。只影响该成员，不影响会话里其他人。</summary>
    public void SetMuted(string conversationId, QuickChatPrincipal member, bool muted)
    {
        lock (gate)
        {
            ConversationData data = Require(conversationId);
            if (HasMember(data.Meta, member) == false)
                throw new InvalidOperationException("你不是此会话的成员");

            bool changed;
            if (muted)
            {
                changed = data.Meta.Muted.Contains(member.Id, StringComparer.OrdinalIgnoreCase) == false;
                if (changed)
                    data.Meta.Muted.Add(member.Id);
            }
            else
            {
                changed = data.Meta.Muted.RemoveAll(
                    id => id.Equals(member.Id, StringComparison.OrdinalIgnoreCase)) > 0;
            }

            if (changed && data.Persistent)
                SaveStructure();
        }
    }

    /// <summary>
    /// 删除会话及其消息文件。
    /// <para>
    /// 只有<b>自建会话</b>（用户点「新建」或角色自己拉群/开私聊建出来的，
    /// <see cref="QuickChatStoredConversation.Managed"/> = <c>false</c>）才该走到这里。
    /// 受管会话（角色的私聊、公共大厅、配置里的群）的存在性由「角色是否激活 / 配置里有没有」
    /// 决定，删掉也会被下一帧同步建回来 —— 对它们该用
    /// <see cref="ClearMessages"/>（清空记录、保留会话），调用方负责分流。
    /// </para>
    /// </summary>
    public void Delete(string conversationId)
    {
        lock (gate)
        {
            if (conversations.Remove(conversationId, out ConversationData? data) == false)
                return;

            if (data.Meta.IsGroup && data.Meta.Managed && !conversationId.Equals("group:__lobby__", StringComparison.OrdinalIgnoreCase))
                structure.DeletedGroups.Add(conversationId);
            structure.Conversations.RemoveAll(conversation => conversation.Id == conversationId);
            reads.Remove(conversationId);
            SaveStructure();
            SaveReads();

            try
            {
                if (File.Exists(data.Path))
                    File.Delete(data.Path);
            }
            catch (IOException)
            {
                // 文件被占用时保留；结构里已移除，下次启动不会加载到。
            }
        }
    }

    /// <summary>
    /// 清空一个会话的全部消息，保留会话本身（标题、成员、开关都不动）。
    /// <para>
    /// 与 <see cref="Delete"/> 的区别：删完会话还在，只是空的。用户在「清理」弹窗里勾上
    /// 「同时删除原始记录消息」时走这里 —— 意图是「把这条对话线擦干净」，不是「把这条线删掉」。
    /// 只影响这一个会话，左侧其它会话的记录不受影响。
    /// </para>
    /// </summary>
    public void ClearMessages(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return;

        lock (gate)
        {
            if (conversations.TryGetValue(conversationId, out ConversationData? data) == false)
                return;

            Load(data);
            data.Messages.Clear();
            data.Reads.Clear();
            reads.Remove(conversationId);
            SaveReads();

            try
            {
                if (File.Exists(data.Path))
                    File.Delete(data.Path);
            }
            catch (IOException)
            {
                // 删不掉（被别的进程占着）就退而求其次重写成空文件：
                // 只清内存不清磁盘的话，下次启动会把旧消息整段读回来，「清理」白做。
                try
                {
                    File.WriteAllText(data.Path, string.Empty);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>
    /// 新建一个「自建会话」：用户点「新建会话」或角色调工具建群走这里。
    /// <para>
    /// 与受管会话的区别是 <see cref="QuickChatStoredConversation.Managed"/> = <c>false</c>：
    /// 同步逻辑既不会建它也不会删它，只受显式删除影响。
    /// </para>
    /// </summary>
    public QuickChatStoredConversation CreateCustom(
        string id,
        string kind,
        string title,
        IEnumerable<QuickChatPrincipal> members,
        bool persistent,
        string creatorId = QuickChatPrincipal.HumanId)
    {
        if (kind.Equals("group", StringComparison.OrdinalIgnoreCase) && Find(id) != null)
            throw new InvalidOperationException("会话已存在");
        QuickChatStoredConversation? created = Ensure(id, kind, title, members, persistent, managed: false);

        if (created == null)
            throw new InvalidOperationException("会话创建失败");

        lock (gate)
        {
            created.CreatorId = creatorId;
            SaveStructure();
        }
        return created;
    }

    public bool IsDeletedGroup(string id)
    {
        lock (gate)
            return structure.DeletedGroups.Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    public void EditGroup(string id, QuickChatPrincipal actor,
        IEnumerable<QuickChatPrincipal> members, IEnumerable<QuickChatPrincipal> muted)
    {
        lock (gate)
        {
            ConversationData data = Require(id);
            if (!data.Meta.IsGroup || id.Equals("group:__lobby__", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("只能管理小群");
            if (!actor.IsHuman && (!HasMember(data.Meta, actor) ||
                !data.Meta.CreatorId.Equals(actor.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("只有主人和建群角色可以管理成员");
            List<string> wanted = members.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!wanted.Contains(QuickChatPrincipal.HumanId, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能清退主人");
            List<string> quiet = muted.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (quiet.Contains(QuickChatPrincipal.HumanId, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能限制主人");
            data.Meta.Members = wanted;
            data.Meta.Muted = quiet.Where(p => wanted.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
            data.Meta.MembersEdited = true;
            SaveStructure();
        }
    }

    // ────────────────────────── 消息 ──────────────────────────

    public ChatRecord Send(
        string conversationId,
        QuickChatPrincipal sender,
        string text,
        int depth = 0,
        string contentType = "text",
        bool persist = true)
    {
        text = (text ?? "").Trim();
        if (text.Length is < 1 or > 4000)
            throw new InvalidOperationException("消息长度需为 1 到 4000 字");

        lock (gate)
        {
            ConversationData data = Require(conversationId);
            if (HasMember(data.Meta, sender) == false)
                throw new InvalidOperationException("你不是此会话的成员");

            if (data.Meta.IsGroup && sender.IsAgent &&
                data.Meta.Muted.Contains(sender.Id, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("你在此群的回复已被限制");

            ChatRecord message = new()
            {
                Id = structure.NextMessageId,
                ConversationId = conversationId,
                SenderId = sender.Id,
                SenderName = sender.IsHuman ? "我" : sender.Name,
                Text = text,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "text" : contentType,
                SentAt = DateTimeOffset.Now,
                Depth = Math.Clamp(depth, 0, 32),
            };
            message.Mentions = QuickChatEnvelope
                .ParseMentions(text, data.Meta.Members.Select(QuickChatPrincipal.Parse))
                .Select(mention => mention.Id)
                .ToList();

            Append(data, message, persist);
            structure.NextMessageId++;

            SetRead(data, sender, message.Id);
            SaveReads();
            return message;
        }
    }

    public IReadOnlyList<ChatRecord> History(
        string conversationId, QuickChatPrincipal member, long before = long.MaxValue, int limit = 80)
    {
        lock (gate)
        {
            ConversationData data = RequireReadable(conversationId, member);
            int end = before == long.MaxValue ? data.Messages.Count : LowerBound(data.Messages, before);
            int count = Math.Clamp(limit, 1, 400);
            int start = Math.Max(0, end - count);
            return data.Messages.GetRange(start, end - start);
        }
    }

    /// <summary>该会话是否还有比 <paramref name="before"/> 更早的消息（用于分页「还有更多」判定）。</summary>
    public bool HasOlder(string conversationId, QuickChatPrincipal member, long before)
    {
        lock (gate)
        {
            ConversationData data = RequireReadable(conversationId, member);
            return LowerBound(data.Messages, before) > 0;
        }
    }

    /// <summary>
    /// 比 <paramref name="before"/> 更早的消息条数。
    /// <para>
    /// 和 <see cref="HasOlder"/> 是同一个下标，只是把「有没有」换成了「有多少」——
    /// 角色靠它决定还值不值得继续往前翻，只说「还有更多」的话它会一直翻下去。
    /// </para>
    /// </summary>
    public int OlderCount(string conversationId, QuickChatPrincipal member, long before)
    {
        lock (gate)
        {
            ConversationData data = RequireReadable(conversationId, member);
            return LowerBound(data.Messages, before);
        }
    }

    public IReadOnlyList<ChatRecord> Search(
        string conversationId, QuickChatPrincipal member, string query, int limit = 100)
    {
        query = (query ?? "").Trim();
        lock (gate)
        {
            ConversationData data = RequireReadable(conversationId, member);
            if (query.Length == 0)
                return History(conversationId, member, long.MaxValue, Math.Clamp(limit, 1, 200));

            List<ChatRecord> hits = new();
            for (int index = data.Messages.Count - 1; index >= 0 && hits.Count < Math.Clamp(limit, 1, 200); index--)
            {
                if (data.Messages[index].Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                    hits.Add(data.Messages[index]);
            }
            hits.Reverse();
            return hits;
        }
    }

    public ChatRecord? LastMessage(string conversationId, QuickChatPrincipal member)
    {
        lock (gate)
        {
            ConversationData data = RequireReadable(conversationId, member);
            return data.Messages.Count == 0 ? null : data.Messages[^1];
        }
    }

    /// <summary>最后一条消息，不校验成员身份（用于会话记录摘要）。</summary>
    public ChatRecord? LastMessageUnchecked(string conversationId)
    {
        lock (gate)
        {
            if (conversations.TryGetValue(conversationId, out ConversationData? data) == false)
                return null;
            Load(data);
            return data.Messages.Count == 0 ? null : data.Messages[^1];
        }
    }

    public int MessageCount(string conversationId)
    {
        lock (gate)
            return conversations.TryGetValue(conversationId, out ConversationData? data)
                ? Load(data).Messages.Count
                : 0;
    }

    // ────────────────────────── 未读 ──────────────────────────

    public int Unread(string conversationId, QuickChatPrincipal member)
    {
        lock (gate)
        {
            if (conversations.TryGetValue(conversationId, out ConversationData? data) == false)
                return 0;
            if (HasMember(data.Meta, member) == false)
                return 0;

            Load(data);
            long read = ReadCursor(data, member);
            int count = 0;
            for (int index = data.Messages.Count - 1; index >= 0; index--)
            {
                ChatRecord message = data.Messages[index];
                if (message.Id <= read)
                    break;
                if (message.SenderId.Equals(member.Id, StringComparison.OrdinalIgnoreCase) == false)
                    count++;
            }
            return count;
        }
    }

    public IReadOnlyList<ChatRecord> UnreadMessages(string conversationId, QuickChatPrincipal member)
    {
        lock (gate)
        {
            ConversationData data = RequireReadable(conversationId, member);
            long read = ReadCursor(data, member);
            List<ChatRecord> result = new();
            for (int index = data.Messages.Count - 1; index >= 0; index--)
            {
                ChatRecord message = data.Messages[index];
                if (message.Id <= read)
                    break;
                if (message.SenderId.Equals(member.Id, StringComparison.OrdinalIgnoreCase) == false)
                    result.Add(message);
            }
            result.Reverse();
            return result;
        }
    }

    public void MarkRead(string conversationId, QuickChatPrincipal member)
    {
        lock (gate)
        {
            ConversationData data = RequireReadable(conversationId, member);
            SetRead(data, member, data.LastId);
            SaveReads();
        }
    }

    public void MarkReadUpTo(string conversationId, QuickChatPrincipal member, long messageId)
    {
        lock (gate)
        {
            ConversationData data = RequireReadable(conversationId, member);
            SetRead(data, member, messageId);
            SaveReads();
        }
    }

    // ────────────────────────── 内部 ──────────────────────────

    static bool HasMember(QuickChatStoredConversation conversation, QuickChatPrincipal member) =>
        conversation.Members.Contains(member.Id, StringComparer.OrdinalIgnoreCase);

    public static bool IsMember(QuickChatStoredConversation conversation, QuickChatPrincipal member) =>
        HasMember(conversation, member);

    /// <summary>返回第一条 Id &gt;= value 的下标（消息按 Id 升序）。</summary>
    static int LowerBound(List<ChatRecord> messages, long value)
    {
        int low = 0, high = messages.Count;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            if (messages[middle].Id < value)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    ConversationData Require(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || conversations.TryGetValue(conversationId, out ConversationData? data) == false)
            throw new InvalidOperationException("会话不存在");
        Load(data);
        return data;
    }

    ConversationData RequireReadable(string conversationId, QuickChatPrincipal member)
    {
        ConversationData data = Require(conversationId);
        if (HasMember(data.Meta, member) == false)
            throw new InvalidOperationException("你不是此会话的成员");
        return data;
    }

    ConversationData Load(ConversationData data)
    {
        if (data.Loaded)
            return data;
        data.Loaded = true;

        if (File.Exists(data.Path) == false)
            return data;

        foreach (string line in File.ReadLines(data.Path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                ChatRecord? item = JsonSerializer.Deserialize<ChatRecord>(line, StoreJson);
                if (item != null)
                    data.Messages.Add(item);
            }
            catch (JsonException)
            {
                // 末行写入中断时不能让前面的消息一起消失
            }
        }

        RepairTrailingNewline(data.Path);
        return data;
    }

    static void RepairTrailingNewline(string path)
    {
        try
        {
            using FileStream tail = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            if (tail.Length == 0)
                return;
            tail.Seek(-1, SeekOrigin.End);
            if (tail.ReadByte() == '\n')
                return;
            tail.Seek(0, SeekOrigin.End);
            tail.WriteByte((byte)'\n');
            tail.Flush(true);
        }
        catch (IOException)
        {
            // 无法修复也无妨：下一行仍可解析
        }
    }

    void Append(ConversationData data, ChatRecord message, bool persist)
    {
        if (persist && data.Persistent)
        {
            string json = JsonSerializer.Serialize(message, StoreJson);
            using (FileStream stream = new(data.Path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
                stream.Write(bytes);
                stream.Flush(true);
            }
        }

        data.Messages.Add(message);
    }

    static long ReadCursor(ConversationData data, QuickChatPrincipal member) =>
        data.Reads.TryGetValue(member.Id, out long value) ? value : 0;

    void SetRead(ConversationData data, QuickChatPrincipal member, long messageId)
    {
        long current = ReadCursor(data, member);
        long next = Math.Max(current, messageId);
        if (next == current)
            return;
        data.Reads[member.Id] = next;
        reads[data.Meta.Id] = data.Reads;
    }

    void SaveStructure()
    {
        WriteJsonAtomic(structurePath, structure);
    }

    void SaveReads()
    {
        WriteJsonAtomic(readsPath, reads);
    }

    /// <summary>
    /// 清掉旧版本留下的 <c>hidden.json</c>。
    /// <para>
    /// 早期版本用一份「已删除名单」来阻止受管会话被同步逻辑重建。那条路是错的：
    /// 受管会话（角色的私聊、公共大厅、配置里的群）的存在性只应该由「角色是否激活 /
    /// 配置里有没有」决定。名单一旦写下，删掉私聊的角色<b>重新激活也回不来</b>，
    /// 而且群聊成员是从私聊列表推出来的，会连带少人。
    /// </para>
    /// <para>
    /// 现在「删除受管会话」退化成「清空记录」，不再需要名单，所以这个文件是一次性垃圾。
    /// 留着它只会让删过的会话继续消失，必须在启动时删掉。
    /// </para>
    /// </summary>
    void TryDeleteLegacyHiddenFile()
    {
        try
        {
            string legacy = Path.Combine(root, "hidden.json");
            if (File.Exists(legacy))
                File.Delete(legacy);
        }
        catch (IOException)
        {
            // 删不掉也不影响：这份文件已经没有任何代码会读它了。
        }
    }

    static void WriteJsonAtomic<T>(string path, T value)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }

    static T? ReadJson<T>(string path) where T : class
    {
        if (File.Exists(path) == false)
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), StoreJson);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static string SafeFileName(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char character in value)
            builder.Append(Path.GetInvalidFileNameChars().Contains(character) ? '_' : character);
        return builder.ToString();
    }

    public void Dispose()
    {
        lock (gate)
        {
            SaveReads();
        }
    }

    sealed class ConversationData(QuickChatStoredConversation meta, string messagesRoot)
    {
        public QuickChatStoredConversation Meta { get; } = meta;
        public List<ChatRecord> Messages { get; } = new();
        public Dictionary<string, long> Reads { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Loaded { get; set; }
        public bool Persistent { get; set; } = true;
        public long LastId => Messages.Count == 0 ? 0 : Messages[^1].Id;
        public string Path { get; } = System.IO.Path.Combine(messagesRoot, SafeFileName(meta.Id) + ".jsonl");
    }

    internal static readonly JsonSerializerOptions StoreJson = new() { PropertyNameCaseInsensitive = true };

    readonly object gate = new();
    readonly string root;
    readonly string structurePath;
    readonly string readsPath;
    readonly Dictionary<string, ConversationData> conversations = new(StringComparer.OrdinalIgnoreCase);
    QuickChatConversationStructure structure = new();
    Dictionary<string, Dictionary<string, long>> reads = new(StringComparer.OrdinalIgnoreCase);

    string MessagesRoot => Path.Combine(root, "messages");
}
