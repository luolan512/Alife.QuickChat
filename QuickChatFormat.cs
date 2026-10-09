using System;
using System.IO;

namespace Marisa.QuickChat;

/// <summary>快聊里的展示格式统一在这里，避免各处各写一套。</summary>
public static class QuickChatFormat
{
    /// <summary>会话在某个主体眼中的标题：私聊显示对方，群聊显示群名。</summary>
    public static string Title(QuickChatStoredConversation conversation, QuickChatPrincipal viewer)
    {
        if (conversation.IsGroup)
            return string.IsNullOrWhiteSpace(conversation.Title) ? "群聊" : conversation.Title;

        foreach (string id in conversation.Members)
        {
            QuickChatPrincipal member = QuickChatPrincipal.Parse(id);
            if (member.SameAs(viewer) == false)
                return member.IsHuman ? "我" : member.Name;
        }

        return "私聊";
    }

    public static string MemberName(string memberId) => QuickChatPrincipal.Parse(memberId).Name;

    public static string Time(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm");
}

/// <summary>快聊自有日志。带体积上限，避免长期运行把磁盘写满。</summary>
public static class QuickChatLog
{
    const long MaxBytes = 512 * 1024;

    public static void SetRoot(string root) => path = Path.Combine(root, "quickchat.log");

    public static void Write(string message)
    {
        Console.WriteLine("[QuickChat] " + message);
        if (path == null)
            return;

        lock (Gate)
        {
            try
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
                File.AppendAllText(path, line);

                if (size < 0)
                    size = File.Exists(path) ? new FileInfo(path).Length : 0;
                else
                    size += line.Length;

                if (size <= MaxBytes)
                    return;

                // 超出上限只保留后半段
                string[] lines = File.ReadAllLines(path);
                int keep = lines.Length / 2;
                File.WriteAllLines(path, lines[keep..]);
                size = new FileInfo(path).Length;
            }
            catch (Exception)
            {
                size = -1;
            }
        }
    }

    static readonly object Gate = new();
    static string? path;
    static long size = -1;
}
