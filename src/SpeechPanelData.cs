using System.Text.Json;

namespace A1IndexTTSMod;

internal sealed class SpeechPanelTurn
{
    public string Identity { get; init; } = "";
    public string NpcKey { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public string DisplayText { get; init; } = "";
    public string SpokenText { get; init; } = "";
    public string? Emotion { get; set; }
    public string? VoiceStyleJson { get; set; }
    public string VoiceStyleStatus { get; set; } = "未观测";
    public string? VoiceStyleSource { get; set; }
    public string? PresetVoiceStyleMatch { get; init; }
    public string? VoiceStyleFailureReason { get; set; }
    public bool VoiceStyleRequired { get; init; }
    public string PromptStatus { get; set; } = "本轮提示扩展未观测";
    public string? OutputSchema { get; set; }
    public string? PromptAddition { get; set; }
    public string? ActualReplyJson { get; set; }
    public string? TtsBackend { get; set; }
    public string? TtsRequestJson { get; set; }
    public string? TtsInstruction { get; set; }
    public string? ReferenceLabel { get; set; }
    public string TtsStatus { get; set; } = "未请求合成";
    public string? Error { get; set; }
    public string? AudioPath { get; set; }
    public long AudioBytes { get; set; }
    public string? ManualKind { get; set; }
}

internal static class SpeechPanelData
{
    private const int MaximumTurns = 20;
    private static readonly object Gate = new();
    private static readonly LinkedList<SpeechPanelTurn> Turns = new();
    private static readonly Dictionary<string, SpeechPanelTurn> ByIdentity = new(StringComparer.Ordinal);
    private static string _promptStatus = "本轮提示扩展未观测";
    private static string? _schema;
    private static string? _promptAddition;

    internal static void RecordPromptSnapshot(bool applied, string? schema, string? promptAddition, string status)
    {
        lock (Gate)
        {
            _promptStatus = status;
            _schema = Limit(schema, 24000);
            _promptAddition = applied ? Limit(promptAddition, 6000) : null;
        }
    }

    internal static SpeechPanelTurn RecordReply(string identity, string npcKey, string displayText,
        string spokenText, string? emotion, VoiceStyle? style, string? actualRaw,
        string voiceStyleStatus = "未观测", string? voiceStyleSource = null, string? voiceStyleFailureReason = null,
        string? presetVoiceStyleMatch = null)
    {
        lock (Gate)
        {
            if (ByIdentity.TryGetValue(identity, out var existing)) return existing;
            var turn = new SpeechPanelTurn
            {
                Identity = identity,
                NpcKey = npcKey,
                CreatedAt = DateTimeOffset.Now,
                DisplayText = Limit(displayText, 4000) ?? "",
                SpokenText = Limit(spokenText, 4000) ?? "",
                Emotion = emotion,
                VoiceStyleJson = style == null ? null : JsonSerializer.Serialize(style),
                VoiceStyleStatus = voiceStyleStatus,
                VoiceStyleSource = voiceStyleSource,
                PresetVoiceStyleMatch = presetVoiceStyleMatch,
                VoiceStyleFailureReason = voiceStyleFailureReason,
                VoiceStyleRequired = presetVoiceStyleMatch == null && _promptAddition != null && !string.IsNullOrWhiteSpace(spokenText),
                PromptStatus = presetVoiceStyleMatch != null ? "匹配离线预设情感库；本条未依赖模型风格生成" : _promptStatus,
                OutputSchema = presetVoiceStyleMatch != null ? null : _schema,
                PromptAddition = presetVoiceStyleMatch != null ? null : _promptAddition,
                ActualReplyJson = Limit(actualRaw, 20000)
            };
            Turns.AddFirst(turn);
            ByIdentity[identity] = turn;
            while (Turns.Count > MaximumTurns)
            {
                var oldest = Turns.Last!.Value;
                Turns.RemoveLast();
                ByIdentity.Remove(oldest.Identity);
            }
            return turn;
        }
    }

    internal static IReadOnlyList<SpeechPanelTurn> GetRecent(string? npcKey = null)
    {
        lock (Gate)
            return Turns.Where(turn => npcKey == null || turn.NpcKey == npcKey).ToArray();
    }

    internal static SpeechPanelTurn CreateManualTurn(SpeechPanelTurn source, string kind)
    {
        lock (Gate)
        {
            var turn = new SpeechPanelTurn
            {
                Identity = "manual-" + Guid.NewGuid().ToString("N"),
                NpcKey = source.NpcKey,
                CreatedAt = DateTimeOffset.Now,
                DisplayText = source.DisplayText,
                SpokenText = source.SpokenText,
                Emotion = source.Emotion,
                VoiceStyleJson = source.VoiceStyleJson,
                VoiceStyleStatus = source.VoiceStyleStatus,
                VoiceStyleSource = source.VoiceStyleSource,
                PresetVoiceStyleMatch = source.PresetVoiceStyleMatch,
                VoiceStyleFailureReason = source.VoiceStyleFailureReason,
                VoiceStyleRequired = source.VoiceStyleRequired,
                PromptStatus = source.PromptStatus,
                OutputSchema = source.OutputSchema,
                PromptAddition = source.PromptAddition,
                ActualReplyJson = source.ActualReplyJson,
                ManualKind = kind,
                TtsStatus = "等待本地合成"
            };
            Turns.AddFirst(turn);
            ByIdentity[turn.Identity] = turn;
            while (Turns.Count > MaximumTurns)
            {
                var oldest = Turns.Last!.Value;
                Turns.RemoveLast();
                ByIdentity.Remove(oldest.Identity);
            }
            return turn;
        }
    }

    internal static SpeechPanelTurn? GetTurn(string? npcKey)
    {
        lock (Gate) return Turns.FirstOrDefault(turn => npcKey == null || turn.NpcKey == npcKey);
    }

    internal static void UpdateTtsRequest(string identity, string backend, string? requestJson,
        string? referenceLabel, string? instruction)
    {
        lock (Gate)
        {
            if (!ByIdentity.TryGetValue(identity, out var turn)) return;
            turn.TtsBackend = backend;
            turn.TtsRequestJson = Limit(SanitizeJson(requestJson), 12000);
            turn.ReferenceLabel = referenceLabel;
            turn.TtsInstruction = instruction;
            turn.TtsStatus = "正在合成";
            turn.Error = null;
        }
    }

    internal static void UpdateTtsStatus(string identity, string status, string? error = null,
        string? audioPath = null, long audioBytes = 0)
    {
        lock (Gate)
        {
            if (!ByIdentity.TryGetValue(identity, out var turn)) return;
            turn.TtsStatus = status;
            turn.Error = Limit(error, 400);
            if (audioPath != null)
            {
                turn.AudioPath = audioPath;
                turn.AudioBytes = audioBytes;
            }
        }
    }

    internal static void SetAudioMissing(string path)
    {
        lock (Gate)
            foreach (var turn in Turns.Where(turn => string.Equals(turn.AudioPath, path, StringComparison.OrdinalIgnoreCase)))
                turn.AudioPath = null;
    }

    private static string? SanitizeJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        try
        {
            using var doc = JsonDocument.Parse(value);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms)) WriteSanitized(doc.RootElement, writer);
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch { return value; }
    }

    private static void WriteSanitized(JsonElement item, Utf8JsonWriter writer)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in item.EnumerateObject())
            {
                var key = property.Name.ToLowerInvariant();
                writer.WritePropertyName(property.Name);
                if (new[] { "authorization", "api_key", "apikey", "token", "secret", "cookie", "password", "voice_ref", "audio" }.Any(key.Contains))
                    writer.WriteStringValue(key is "voice_ref" or "audio" ? "[音频载荷已省略]" : "[已隐藏]");
                else WriteSanitized(property.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (item.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var child in item.EnumerateArray()) WriteSanitized(child, writer);
            writer.WriteEndArray();
        }
        else item.WriteTo(writer);
    }

    private static string? Limit(string? value, int max) => value == null || value.Length <= max ? value : value[..max] + "…[已截断]";
}
