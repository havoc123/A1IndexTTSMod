using System.Text.Json;

namespace A1IndexTTSMod;

/// <summary>Versioned text carrier for speech metadata inside the reply content string.</summary>
internal static class StyleEnvelopeCodec
{
    public const string OpenTag = "<a1tts_v1>";
    public const string CloseTag = "</a1tts_v1>";
    public const int MaximumFrameLength = 2048;

    internal sealed record Result(string Content, VoiceStyle? Style, string Status, string? FailureReason, int FrameCount);

    public static Result Decode(string? content)
    {
        content ??= string.Empty;
        var first = content.IndexOf(OpenTag, StringComparison.Ordinal);
        if (first < 0) return new Result(content, null, "absent", null, 0);

        var frames = new List<(int Start, int End, string Payload)>();
        var cursor = 0;
        var malformed = false;
        while (true)
        {
            var start = content.IndexOf(OpenTag, cursor, StringComparison.Ordinal);
            if (start < 0) break;
            var payloadStart = start + OpenTag.Length;
            var end = content.IndexOf(CloseTag, payloadStart, StringComparison.Ordinal);
            if (end < 0)
            {
                // The protocol is appended after dialogue. Drop only the unfinished metadata tail.
                var clean = content[..start];
                for (var i = frames.Count - 1; i >= 0; i--)
                    clean = clean.Remove(frames[i].Start, frames[i].End - frames[i].Start);
                clean = clean.Replace(OpenTag, string.Empty, StringComparison.Ordinal)
                    .Replace(CloseTag, string.Empty, StringComparison.Ordinal).TrimEnd();
                return new Result(clean, null, "invalid", "incomplete_frame", frames.Count + 1);
            }
            var frameEnd = end + CloseTag.Length;
            var payload = content[payloadStart..end];
            if (frameEnd - start > MaximumFrameLength) malformed = true;
            frames.Add((start, frameEnd, payload));
            cursor = frameEnd;
        }

        if (frames.Count == 0)
            return new Result(content, null, "invalid", "incomplete_frame", 1);

        var cleanContent = content;
        for (var i = frames.Count - 1; i >= 0; i--)
            cleanContent = cleanContent.Remove(frames[i].Start, frames[i].End - frames[i].Start);
        cleanContent = cleanContent.Replace(OpenTag, string.Empty, StringComparison.Ordinal)
            .Replace(CloseTag, string.Empty, StringComparison.Ordinal);
        cleanContent = cleanContent.TrimEnd();
        if (frames.Count != 1) return new Result(cleanContent, null, "invalid", "multiple_frames", frames.Count);
        if (malformed) return new Result(cleanContent, null, "invalid", "frame_too_long", 1);

        var frame = frames[0];
        var before = content[..frame.Start];
        var after = content[frame.End..];
        if (!string.IsNullOrWhiteSpace(after)) return new Result(cleanContent, null, "invalid", "frame_not_at_end", 1);
        try
        {
            var payload = frame.Payload.Trim();
            if (payload.StartsWith("```", StringComparison.Ordinal) && payload.EndsWith("```", StringComparison.Ordinal) && payload.Length >= 6)
            {
                payload = payload[3..^3].Trim();
                if (payload.StartsWith("json", StringComparison.OrdinalIgnoreCase) &&
                    (payload.Length == 4 || char.IsWhiteSpace(payload[4]))) payload = payload[4..].Trim();
            }
            using var document = JsonDocument.Parse(payload);
            var style = VoiceStyle.Parse(document.RootElement, out var reason);
            return style == null
                ? new Result(cleanContent, null, "invalid", reason ?? "invalid_style", 1)
                : new Result(before.TrimEnd(), style, "valid", null, 1);
        }
        catch (JsonException)
        {
            return new Result(cleanContent, null, "invalid", "invalid_json", 1);
        }
    }

    public static string EncodeExample(string content, VoiceStyle style)
    {
        var styleJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["emotion_tags"] = style.EmotionTags,
            ["delivery"] = style.Delivery,
            ["intensity"] = style.Intensity
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return content + OpenTag + styleJson + CloseTag;
    }
}
