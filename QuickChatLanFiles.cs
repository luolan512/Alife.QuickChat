using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Marisa.QuickChat;

internal sealed class QuickChatLanFiles : IDisposable
{
    public const long MaxFileBytes = 20L * 1024 * 1024;
    const long SessionLimit = 200L * 1024 * 1024;
    static readonly ConcurrentDictionary<string, string> displayNames = new(StringComparer.OrdinalIgnoreCase);
    public static string DisplayName(string path) => displayNames.TryGetValue(path, out var name) ? name : System.IO.Path.GetFileName(path);
    readonly object gate = new();
    readonly Dictionary<string, Upload> uploads = new(StringComparer.Ordinal);
    readonly string temporaryRoot, folder;
    long reserved;
    bool disposed;
    internal sealed record Upload(string Id, string ConversationId, string Path, string Name, long Size, string Kind)
    {
        public object Public => new { Id, Name, Size, Kind, Url = "/api/files/" + Id };
    }
    public QuickChatLanFiles(string temporaryRoot)
    {
        this.temporaryRoot = System.IO.Path.GetFullPath(temporaryRoot);
        folder = System.IO.Path.Combine(this.temporaryRoot, "LanUploads", Guid.NewGuid().ToString("N"));
    }
    public async Task<Upload> SaveAsync(IFormFile file, string conversationId, CancellationToken cancellationToken)
    {
        if (file.Length > MaxFileBytes) throw new InvalidDataException("单个文件不能超过 20 MB");
        string name = System.IO.Path.GetFileName(file.FileName.Replace('\\', '/'));
        name = new string(name.Where(c => !System.IO.Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim().TrimEnd('.', ' ');
        if (name.Length > 120) name = name[..100] + System.IO.Path.GetExtension(name)[..Math.Min(20, System.IO.Path.GetExtension(name).Length)];
        if (string.IsNullOrEmpty(name)) name = "attachment.bin";
        string stem = System.IO.Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))) name = "_" + name;
        string id = Guid.NewGuid().ToString("N"), directory = System.IO.Path.Combine(folder, id);
        // The host recursively compiles *.cs beneath plugin directories. Store
        // executable plugin material as inert data, preserving its displayed name.
        string diskName = System.IO.Path.GetExtension(name).ToLowerInvariant() is ".cs" or ".csx" or ".dll" or ".razor" or ".props" or ".targets" ? name + ".upload" : name;
        string path = System.IO.Path.Combine(directory, diskName);
        lock (gate) {
            if (disposed) throw new ObjectDisposedException(nameof(QuickChatLanFiles));
            if (reserved + file.Length > SessionLimit) throw new InvalidDataException("临时附件空间已满，请结束局域网服务后重试");
            reserved += file.Length;
        }
        try {
            Directory.CreateDirectory(directory);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await file.CopyToAsync(output, cancellationToken);
            var upload = new Upload(id, conversationId, path, name, file.Length, IsImage(path) ? "image" : "file");
            lock (gate) {
                if (disposed) throw new ObjectDisposedException(nameof(QuickChatLanFiles));
                uploads.Add(id, upload);
                displayNames[path] = name;
            }
            return upload;
        } catch {
            lock (gate) reserved -= file.Length;
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }
    public Upload? Find(string id) { lock (gate) return uploads.TryGetValue(id, out var upload) && File.Exists(upload.Path) ? upload : null; }
    public string[]? Resolve(string conversationId, string[] ids)
    {
        if (ids.Length > 8) return null;
        lock (gate) {
            var result = new List<string>();
            foreach (string id in ids.Distinct(StringComparer.Ordinal)) {
                if (string.IsNullOrWhiteSpace(id) || !uploads.TryGetValue(id, out var file) || file.ConversationId != conversationId || !File.Exists(file.Path)) return null;
                result.Add(file.Path);
            }
            return result.ToArray();
        }
    }
    public static bool IsImage(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
    public static async Task ServeAsync(HttpContext context, string path)
    {
        if (!File.Exists(path)) { context.Response.StatusCode = 404; return; }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.ContentType = System.IO.Path.GetExtension(path).ToLowerInvariant() switch {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp", _ => "application/octet-stream"
        };
        if (!IsImage(path)) context.Response.Headers.ContentDisposition = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(DisplayName(path));
        await context.Response.SendFileAsync(path, context.RequestAborted);
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; foreach (var upload in uploads.Values) displayNames.TryRemove(upload.Path, out _); uploads.Clear(); }
        // Only the random session folder created by this instance may be removed.
        string resolved = System.IO.Path.GetFullPath(folder);
        string allowed = System.IO.Path.Combine(temporaryRoot, "LanUploads") + System.IO.Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) return;
        try { if (Directory.Exists(resolved)) Directory.Delete(resolved, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
