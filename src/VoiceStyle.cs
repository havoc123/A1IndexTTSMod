using System.Text.Json;
using System.Text.Json.Serialization;

namespace A1IndexTTSMod;

/// <summary>Per-utterance voice performance from a reply or a matched offline preset.</summary>
internal sealed record VoiceStyle(
    [property: JsonPropertyName("emotion_tags")] IReadOnlyList<string> EmotionTags,
    [property: JsonPropertyName("delivery")] string Delivery,
    [property: JsonPropertyName("intensity")] double Intensity)
{
    public static VoiceStyle? Parse(JsonElement element) => Parse(element, out _);

    public static VoiceStyle? Parse(JsonElement element, out string? failureReason)
    {
        failureReason = null;
        if (element.ValueKind != JsonValueKind.Object) { failureReason = "expected_object"; return null; }
        var fields = element.EnumerateObject().ToArray();
        if (fields.Length != 3 || fields.Any(property => property.Name is not ("emotion_tags" or "delivery" or "intensity")))
        { failureReason = "unexpected_or_missing_fields"; return null; }
        if (!element.TryGetProperty("emotion_tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        { failureReason = "emotion_tags_must_be_array"; return null; }
        if (!element.TryGetProperty("delivery", out var delivery) || delivery.ValueKind != JsonValueKind.String)
        { failureReason = "delivery_must_be_string"; return null; }
        if (!element.TryGetProperty("intensity", out var intensity) || intensity.ValueKind != JsonValueKind.Number ||
            !intensity.TryGetDouble(out var strength))
        { failureReason = "intensity_must_be_number"; return null; }

        var values = new List<string>();
        foreach (var tag in tags.EnumerateArray())
        {
            if (tag.ValueKind != JsonValueKind.String) { failureReason = "emotion_tag_must_be_string"; return null; }
            var value = tag.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Length > 16) { failureReason = "emotion_tag_empty_or_too_long"; return null; }
            values.Add(value);
        }

        var spokenDelivery = delivery.GetString()?.Trim();
        if (values.Count is < 1 or > 3 || string.IsNullOrWhiteSpace(spokenDelivery) ||
            spokenDelivery.Length > 80 || !double.IsFinite(strength) || strength is < 0 or > 1)
        {
            failureReason = values.Count is < 1 or > 3 ? "emotion_tag_count_out_of_range" :
                string.IsNullOrWhiteSpace(spokenDelivery) || spokenDelivery.Length > 80 ? "delivery_empty_or_too_long" :
                "intensity_out_of_range";
            return null;
        }

        return new VoiceStyle(values.AsReadOnly(), spokenDelivery, strength);
    }
}

internal sealed record SpeechRequest(
    string NpcKey,
    string DisplayText,
    string SpokenText,
    string? LegacyEmotion,
    VoiceStyle? VoiceStyle,
    long Generation,
    string DisplayIdentity,
    string? ReferencePathOverride = null,
    byte[]? ReferenceAudioSnapshot = null,
    string? ReferenceLabelSnapshot = null,
    bool ReferenceSkip = false);

internal enum TtsBackend
{
    IndexTtsAudioCpp,
    IndexTtsLegacyApi,
    CosyVoiceAudioCpp
}

/// <summary>A single startup snapshot of BepInEx TTS settings for adapters/lifecycle.</summary>
internal sealed record TtsSettings(
    TtsBackend Backend,
    Uri Endpoint,
    string ReferenceId,
    string ModelId,
    int TimeoutSeconds,
    bool AutoStart,
    string Precision,
    string GpuBackend,
    int GpuDevice);

internal static class CosyVoiceInstruction
{
    public static string Format(VoiceStyle? style)
    {
        if (style == null) return string.Empty;
        var tags = string.Join("、", style.EmotionTags);
        return $"情绪标签：{tags}。说话方式：{style.Delivery}。情绪强度：{style.Intensity.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}（0 到 1）。";
    }

    public static string BuildPayload(string text, VoiceStyle? style, object? voiceReference, string? instructionOverride = null)
    {
        var options = new Dictionary<string, object> { ["template_name"] = "instruct" };
        var instruction = instructionOverride ?? Format(style);
        if (instruction.Length > 0) options["instruction"] = instruction;
        var request = new Dictionary<string, object>
        {
            ["model"] = "cosyvoice3",
            ["input"] = text,
            ["response_format"] = "wav",
            ["options"] = options
        };
        if (voiceReference != null) request["voice_ref"] = voiceReference;
        return System.Text.Json.JsonSerializer.Serialize(request);
    }
}

internal static class AudioCppVoiceReference
{
    private const int MaxInlineBytes = 5 * 1024 * 1024;

    public static object? FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var bytes = File.ReadAllBytes(path);
        return bytes.Length is > 0 and <= MaxInlineBytes
            ? new Dictionary<string, string> { ["type"] = "base64", ["data"] = Convert.ToBase64String(bytes) }
            : path;
    }

    public static object? FromBytes(byte[] bytes) => bytes.Length is > 0 and <= MaxInlineBytes
        ? new Dictionary<string, string> { ["type"] = "base64", ["data"] = Convert.ToBase64String(bytes) }
        : null;

}

internal static class DisplayTurnIdentity
{
    public static string? Create(string npcKey, string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(npcKey) || npcKey == "unknown" || string.IsNullOrWhiteSpace(timestamp) || timestamp == "0")
            return null;
        return "npc-turn:" + npcKey + ":" + timestamp;
    }
}

internal static class DisplayStyleDedupPolicy
{
    public static bool ShouldUpgrade(bool existingHasStyle, bool incomingHasStyle, bool sameRequestPending, bool playbackStarted) =>
        !existingHasStyle && incomingHasStyle && sameRequestPending && !playbackStarted;
}

internal static class BackendConfigMigration
{
    public static string? InferLegacyBackend(bool backendWasConfigured, string? ttsUrl)
    {
        if (backendWasConfigured || !Uri.TryCreate(ttsUrl, UriKind.Absolute, out var endpoint)) return null;
        return endpoint.AbsolutePath == "/v1/tts" ? "IndexTtsLegacyApi" : null;
    }
}

internal static class AudioCppOwnershipPolicy
{
    public static bool KeepOwnedForReadyEndpoint(bool isOwned, bool supervisorAlive, int ownedPort, int endpointPort) =>
        isOwned && supervisorAlive && ownedPort == endpointPort;
}
