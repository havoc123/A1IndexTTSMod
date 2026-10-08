using System.Security.Cryptography;
using System.Text;

namespace A1IndexTTSMod;

/// <summary>Short-lived, independent bridge from the pre-mapping reply hook to display/TTS turns.</summary>
internal static class StyleStore
{
    private const int MaximumKeys = 256;
    private const int MaximumSameReplyQueue = 4;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Queue<PendingStyle>> Entries = new(StringComparer.Ordinal);
    private static long _writes;

    internal sealed record PendingStyle(VoiceStyle? Style, DateTime ExpiresUtc, string SourceRawSha256,
        string RawReply, string Status, string? FailureReason, string Source);

    internal static void Store(string npcKey, string content, VoiceStyle? style, string sourceRawSha256,
        string rawReply, string status, string? failureReason, string source)
    {
        var key = Key(npcKey, content);
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var queue)) Entries[key] = queue = new Queue<PendingStyle>();
            queue.Enqueue(new PendingStyle(style, now + Lifetime, sourceRawSha256, rawReply, status, failureReason, source));
            while (queue.Count > MaximumSameReplyQueue) queue.Dequeue();
            if (++_writes % 16 == 0 || Entries.Count > MaximumKeys) Prune(now);
        }
    }

    internal static PendingStyle? Take(string npcKey, string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var key = Key(npcKey, content);
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var queue) || queue.Count == 0) return null;
            var pending = queue.Dequeue();
            if (queue.Count == 0) Entries.Remove(key);
            return pending.ExpiresUtc > DateTime.UtcNow ? pending : null;
        }
    }

    private static void Prune(DateTime now)
    {
        foreach (var key in Entries.Keys.ToArray())
        {
            var queue = Entries[key];
            while (queue.Count > 0 && queue.Peek().ExpiresUtc <= now) queue.Dequeue();
            if (queue.Count == 0) Entries.Remove(key);
        }
        if (Entries.Count <= MaximumKeys) return;
        foreach (var key in Entries.OrderBy(pair => pair.Value.Count == 0 ? DateTime.MinValue : pair.Value.Peek().ExpiresUtc)
                     .Take(Entries.Count - MaximumKeys).Select(pair => pair.Key).ToArray()) Entries.Remove(key);
    }

    private static string Key(string npcKey, string content) => npcKey + "\u001f" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
