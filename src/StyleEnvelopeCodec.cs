using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace A1IndexTTSMod;

/// <summary>Versioned text carrier for speech metadata inside the reply content string.</summary>
internal static class StyleEnvelopeCodec
{
    public const string OpenTag = "<a1tts_v1>";
    public const string CloseTag = "</a1tts_v1>";
    public const int MaximumFrameLength = 2048;
    private static readonly Regex ClosingPayloadPrefix = new(
        "^\\s*(?<fence>```(?:json)?\\s*)?(?<json>\\{)\\s*\"(?:emotion_tags|delivery|intensity)\"\\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal sealed record Result(string Content, VoiceStyle? Style, string Status, string? FailureReason, int FrameCount);

    public static Result Decode(string? content)
    {
        content ??= string.Empty;
        content = NormalizeClosingTagPayloads(content);
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

    private static string NormalizeClosingTagPayloads(string content)
    {
        // Some replies use the closing tag as the opening marker. Only repair a
        // marker immediately followed by a known style property; ordinary tags
        // and unrelated JSON remain dialogue. The regular decoder still applies
        // all schema, frame-count, length and terminal-position checks.
        var normalized = new StringBuilder();
        var copied = 0;
        var search = 0;
        while (true)
        {
            var start = content.IndexOf(CloseTag, search, StringComparison.Ordinal);
            if (start < 0) break;
            var payloadStart = start + CloseTag.Length;
            var prefix = ClosingPayloadPrefix.Match(content[payloadStart..]);
            if (!prefix.Success) { search = payloadStart; continue; }
            var jsonStart = payloadStart + prefix.Groups["json"].Index;
            var bytes = Encoding.UTF8.GetBytes(content[jsonStart..]);
            var payloadEnd = content.Length;
            var complete = false;
            try
            {
                var reader = new Utf8JsonReader(bytes);
                using var document = JsonDocument.ParseValue(ref reader);
                payloadEnd = jsonStart + Encoding.UTF8.GetCharCount(bytes, 0, (int)reader.BytesConsumed);
                complete = true;
            }
            catch (JsonException) { }
            if (complete && prefix.Groups["fence"].Success)
            {
                while (payloadEnd < content.Length && char.IsWhiteSpace(content[payloadEnd])) payloadEnd++;
                if (content.AsSpan(payloadEnd).StartsWith("```", StringComparison.Ordinal)) payloadEnd += 3;
                else { complete = false; payloadEnd = content.Length; }
            }
            normalized.Append(content, copied, start - copied).Append(OpenTag)
                .Append(content, payloadStart, payloadEnd - payloadStart);
            if (!complete) return normalized.ToString(); // existing incomplete-frame handling
            normalized.Append(CloseTag);
            copied = payloadEnd;
            var possibleClose = copied;
            while (possibleClose < content.Length && char.IsWhiteSpace(content[possibleClose])) possibleClose++;
            if (content.AsSpan(possibleClose).StartsWith(CloseTag, StringComparison.Ordinal) &&
                !ClosingPayloadPrefix.IsMatch(content[(possibleClose + CloseTag.Length)..]))
                copied = possibleClose + CloseTag.Length;
            search = copied;
        }
        return copied == 0 ? content : normalized.Append(content, copied, content.Length - copied).ToString();
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
