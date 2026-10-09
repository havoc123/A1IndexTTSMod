using System.Globalization;
using System.Text.Json;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace A1IndexTTSMod;

/// <summary>Offline, character-specific performances for exact preset utterances.</summary>
internal static class PresetVoiceStyles
{
    private const string ResourceName = "A1IndexTTSMod.preset-voice-styles.zh-CN.json";
    private static readonly Dictionary<(string NpcKey, string Text), List<Match>> Entries = new();
    private static ConfigEntry<bool>? _enabled;

    internal sealed record Match(string EntryKey, string Source, string SourceId, string TopicName,
        string LibraryVersion, VoiceStyle Style)
    {
        public string Description => (Source == "base_greeting" ? "基础问候语" : Source == "persuade_opening" ? "说服开场白" : "话题开场白") +
            (TopicName.Length == 0 ? "" : " · " + TopicName) + " · " + EntryKey + " · 库版本 " + LibraryVersion;
    }

    internal static void Initialize(ManualLogSource log, ConfigEntry<bool> enabled)
    {
        _enabled = enabled;
        Entries.Clear();
        try
        {
            using var stream = typeof(PresetVoiceStyles).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidDataException("Embedded preset voice library is missing.");
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.GetProperty("schema_version").GetInt32() != 1)
                throw new InvalidDataException("Unsupported preset voice library schema.");
            var version = root.GetProperty("library_version").GetString() ?? "unknown";
            var count = 0;
            foreach (var entry in root.GetProperty("entries").EnumerateArray())
            {
                // Conditional templates, pets and stage directions are never human dialogue.
                if (entry.GetProperty("speech_policy").GetString() != "speak") continue;
                var source = entry.GetProperty("source").GetString();
                if (source is not ("base_greeting" or "topic_opening" or "topic_opening_branch" or "persuade_opening")) continue;
                var text = entry.GetProperty("text").GetString()?.Trim();
                var style = VoiceStyle.Parse(entry.GetProperty("voice_style"));
                if (string.IsNullOrWhiteSpace(text) || style == null)
                    throw new InvalidDataException("Invalid preset voice entry.");
                var npcKey = "npc:" + entry.GetProperty("npc_id").GetInt32().ToString(CultureInfo.InvariantCulture);
                var match = new Match(entry.GetProperty("key").GetString()!, source,
                    entry.GetProperty("source_id").GetString()!,
                    entry.TryGetProperty("topic_name", out var topic) ? topic.GetString() ?? "" : "", version, style);
                var key = (npcKey, text);
                if (!Entries.TryGetValue(key, out var matches)) Entries[key] = matches = new List<Match>();
                matches.Add(match);
                count++;
            }
            log.LogInfo($"Preset voice styles loaded version={version} speakableEntries={count} defaultEnabled={enabled.Value}");
        }
        catch (Exception e)
        {
            Entries.Clear();
            log.LogWarning("Preset voice styles unavailable; ordinary AI replies remain active: " + e.Message);
        }
    }

    internal static Match? Resolve(string npcKey, string text, string? activeTopicId = null)
    {
        if (_enabled?.Value != true || !Entries.TryGetValue((npcKey, text.Trim()), out var matches)) return null;
        var candidates = matches;
        if (!string.IsNullOrWhiteSpace(activeTopicId) && activeTopicId != "0")
        {
            var topicMatches = matches.Where(match => match.Source != "base_greeting" &&
                (match.SourceId == activeTopicId || match.SourceId.StartsWith(activeTopicId + ":", StringComparison.Ordinal))).ToList();
            if (topicMatches.Count > 0) candidates = topicMatches;
        }
        var first = candidates[0];
        // Identical text can occur in several topics. Without an unambiguous performance,
        // retain the existing fallback instead of borrowing another topic's emotion.
        return candidates.All(match => match.Style.Delivery == first.Style.Delivery &&
            match.Style.Intensity == first.Style.Intensity && match.Style.EmotionTags.SequenceEqual(first.Style.EmotionTags))
            ? first : null;
    }
}
