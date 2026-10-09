using System.Text.Json;

namespace A1IndexTTSMod;

/// <summary>Admit structured NPC replies or verified, exact fixed NPC openings.</summary>
internal static class PersuadeSpeechPolicy
{
    internal sealed record Reply(string Text, string ReplyContent, string? Emotion,
        NpcReplyPayload.Separated? Payload, PresetVoiceStyles.Match? Preset);

    internal static Reply? Resolve(string npcKey, string? displayText, string? raw,
        string? systemMarker, bool npcOnlyCallback, string? topicId)
    {
        if (!npcKey.StartsWith("npc:", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(systemMarker) || string.IsNullOrWhiteSpace(displayText)) return null;
        var text = SpeechTextFilter.RemoveVoiceStyleEnvelope(displayText);
        var separated = NpcReplyPayload.Separate(raw ?? "");
        var preset = PresetVoiceStyles.Resolve(npcKey, text, topicId);
        if (separated != null)
        {
            using var document = JsonDocument.Parse(separated.GameJson);
            var root = document.RootElement;
            var contentValue = root.GetProperty("content");
            if (contentValue.ValueKind != JsonValueKind.String) return null;
            var content = contentValue.GetString();
            if (string.IsNullOrWhiteSpace(content)) return null;
            var emotion = root.TryGetProperty("emotion", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            return new Reply(text, content, emotion, separated, preset);
        }
        // The general callback also receives player submissions with NPC sender IDs.
        // Only the game's NPC-specific display branch may admit a text-only opening,
        // and even there it must match this character's fixed persuasion source.
        return npcOnlyCallback && preset?.Source == "persuade_opening" &&
            (string.IsNullOrWhiteSpace(topicId) || topicId == "0" || preset.SourceId == topicId)
            ? new Reply(text, text, "normal", null, preset) : null;
    }
}
