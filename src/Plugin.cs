using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace A1IndexTTSMod;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
[BepInProcess("WorldApart.exe")]
public sealed class Plugin : BasePlugin
{
    private Harmony? _harmony;
    public override void Load()
    {
        try
        {
            var diagnostics = Config.Bind("Stage2A", "Enabled", false,
                "Opt in to local diagnostic dialogue capture. Leave disabled for normal play.");
            var capture = Config.Bind("Stage2A", "CaptureFullPrompt", false,
                "Explicitly save client-side messages/schema to .state/stage2a. Contains private game dialogue.");
            var maxChars = Config.Bind("Stage2A", "MaxRecordChars", 120000, "Maximum characters copied from one text field.");
            var dir = Config.Bind("Stage2A", "CaptureDirectory", ".state/stage2a", "Capture path relative to the mod project directory.");
            if (diagnostics.Value) Probe.Start(Log, capture, maxChars, dir);
            SpeechMvp.Start(
                Log,
                Config.Bind("Stage3Mvp", "Enabled", true, "Speak structured NPC AI replies through the local IndexTTS API."),
                Config.Bind("Stage3Mvp", "TtsUrl", "http://127.0.0.1:8892/v1/audio/speech", "Local TTS endpoint; audio.cpp /v1/audio/speech by default, or legacy /v1/tts."),
                Config.Bind("Stage3Mvp", "ReferenceId", "demo", "Reference audio ID under indextts25/voices."),
                Config.Bind("Stage3Mvp", "AudioCppModelId", "indextts25", "Model ID in the local audio.cpp server config. Model precision is chosen by the server launcher."),
                Config.Bind("Stage3Mvp", "TimeoutSeconds", 180, "Maximum wait for one synthesis request."),
                Config.Bind("Stage3Mvp", "AutoStartAudioCpp", true, "Start a visible project audio.cpp window and close it when this game exits."),
                Config.Bind("Stage3Mvp", "AudioCppPrecision", "q8_0", "Auto-start model precision: q8_0, f16, or orig (model file must already exist)."));
            Probe.SetSpeechLog(Log);
            _harmony = new Harmony(PluginInfo.Guid);
            Patch("Game", "Game.Model.NpcModel", "AddNpcChatMessage", "OnNpcDisplay");
            Patch("Game", "Game.NpcPersuadePanel", "OnReceivePersuadeResponse", "OnPersuadeResponse");
            if (diagnostics.Value)
            {
                Patch("Game", "Game.Model.NpcModel", "SendChatMessage", "OnPlayerInput", prefix:true);
                Patch("Game", "Game.Model.AinpcRuntime", "LogRequest", "OnRequest");
                Patch("A1Ainpc.Runtime", "JNGame.Ainpc.Llm.UnityWebRequestTransport", "PostJsonAsync", "OnOfficialTransportRequest", prefix:true);
            }
            Log.LogInfo("Stage2A diagnostic capture is " + (diagnostics.Value ? (capture.Value ? "ON (full prompt)" : "ON") : "OFF"));
        }
        catch (Exception e) { Log.LogError("Stage2A initialization failed safely: " + e.GetType().Name + ": " + e.Message); }
    }

    private void Patch(string assembly, string type, string method, string callback, bool prefix=false)
    {
        var targetType = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly)?.GetType(type);
        var target = targetType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == method);
        var postfix = typeof(Probe).GetMethod(callback, BindingFlags.Public | BindingFlags.Static)!;
        if (target == null) { Log.LogWarning($"Probe target not found: {type}.{method}"); return; }
        if (prefix) _harmony!.Patch(target, prefix: new HarmonyMethod(postfix));
        else _harmony!.Patch(target, postfix: new HarmonyMethod(postfix));
        Log.LogInfo($"Probe hooked {target.DeclaringType?.FullName}.{target.Name}({string.Join(",", target.GetParameters().Select(p => p.ParameterType.Name))})");
    }
}

internal static class Probe
{
    private static readonly string RunId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")[..8];
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 256);
    private static long _seq;
    private static int _maxChars = 120000;
    private static bool _full;
    private static bool _captureEnabled;
    private static long _lastInputSeq;
    private static long _lastRequestSeq;
    private static string _runDir = "";
    private static BepInEx.Logging.ManualLogSource? _log;
    private static BepInEx.Logging.ManualLogSource? _speechLog;

    public static void SetSpeechLog(BepInEx.Logging.ManualLogSource log) => _speechLog = log;

    public static void Start(BepInEx.Logging.ManualLogSource log, ConfigEntry<bool> full, ConfigEntry<int> maxChars, ConfigEntry<string> dir)
    {
        _log = log; _full = full.Value; _captureEnabled = true; _maxChars = Math.Clamp(maxChars.Value, 1024, 500000);
        var project = Path.GetFullPath(Path.Combine(Paths.GameRootPath, "A1IndexTTSMod"));
        _runDir = Path.GetFullPath(Path.Combine(project, dir.Value, RunId));
        if (!_runDir.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Capture path escapes project directory.");
        Directory.CreateDirectory(_runDir);
        _ = Task.Run(Writer);
        Emit(new { kind="run_start", captureFullPrompt=_full, pid=Environment.ProcessId, gameRoot=Paths.GameRootPath });
    }

    public static void OnPlayerInput(object __instance, object[] __args)
    {
        Safe(() => { var text = __args.OfType<string>().FirstOrDefault() ?? ""; _lastInputSeq = EmitText("player_input", __instance, text); });
    }
    public static void OnNpcDisplay(object __instance, object[] __args)
    {
        Safe(() =>
        {
            var text = __args.OfType<string>().FirstOrDefault() ?? "";
            var raw = __args.OfType<string>().Skip(1).FirstOrDefault();
            if (_captureEnabled) EmitText("npc_display", __instance, text, raw);
            // Only structured AI replies are spoken. Notices and player messages stay silent.
            var reply = ExtractNpcReply(raw ?? "");
            if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(reply?.Content))
                SpeechMvp.OnNpcReply(NpcId(__instance), text, reply.Emotion);
        });
    }

    public static void OnPersuadeResponse(object __instance, object[] __args)
    {
        try
        {
            var message = __args.FirstOrDefault(a => a?.GetType().FullName == "Game.Model.ChatMessage");
            if (message == null) return;
            var messageType = message.GetType();
            var text = messageType.GetProperty("MessageText")?.GetValue(message) as string;
            if (string.IsNullOrWhiteSpace(text)) return;
            var raw = messageType.GetProperty("NpcRawOutput")?.GetValue(message) as string;
            var marker = messageType.GetProperty("SystemMarker")?.GetValue(message) as string;
            // Player submissions can carry the NPC's sender ID in this callback.
            // A structured NPC payload is the reliable discriminator here.
            var reply = ExtractNpcReply(raw ?? "");
            if (!string.IsNullOrEmpty(marker) || string.IsNullOrWhiteSpace(reply?.Content)) return;
            var panelType = __instance.GetType();
            var npc = panelType.GetProperty("_subscribedNpc", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(__instance);
            var npcKey = NpcId(npc);
            if (!npcKey.StartsWith("npc:", StringComparison.Ordinal))
            {
                var configId = panelType.GetProperty("_npcId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(__instance);
                var configValue = configId?.GetType().GetProperty("Value")?.GetValue(configId);
                if (configValue is int id && id > 0) npcKey = "npc:" + id;
            }
            _speechLog?.LogInfo($"Stage3 persuade NPC response npc={npcKey} chars={text.Length} rawChars={raw?.Length ?? 0}");
            SpeechMvp.OnNpcReply(npcKey, text, reply.Emotion ?? "normal");
        }
        catch (Exception e)
        {
            _speechLog?.LogWarning("Stage3 persuade response observer error: " + e.GetType().Name);
        }
    }

    public static void OnRequest(object[] __args)
    {
        Safe(() =>
        {
            var npc = __args.FirstOrDefault(a => a?.GetType().FullName == "Game.Model.NpcModel");
            var messages = __args.FirstOrDefault(a => a?.GetType().IsGenericType == true && a.GetType().GetGenericArguments().Any(t => t.FullName == "JNGame.Ainpc.Llm.LlmMessage"));
            var schema = __args.OfType<string>().LastOrDefault(s => s.Length > 64) ?? "";
            var fields = new List<object>();
            var messageList = messages as Il2CppSystem.Collections.Generic.IReadOnlyList<JNGame.Ainpc.Llm.LlmMessage>;
            if (messageList != null)
            {
                // IReadOnlyList<T> and IReadOnlyCollection<T> are separate generated wrappers
                // over the same IL2CPP object; the former supplies the indexer, the latter Count.
                var collection = new Il2CppSystem.Collections.Generic.IReadOnlyCollection<JNGame.Ainpc.Llm.LlmMessage>(messageList.Pointer);
                for (var i=0; i < Math.Min(collection.Count, 128); i++)
                {
                    var message = messageList[i];
                    if (message == null) continue;
                    var role = message.Role ?? "unknown";
                    var content = message.Content ?? "";
                    fields.Add(new { role, length=content.Length, sha256=Hash(content), content=_full ? Limit(content) : null });
                }
            }
            var argTypes = __args.Select((a,i) => new { index=i, type=a?.GetType().FullName ?? "null" }).ToArray();
            _lastRequestSeq = Emit(new { kind="client_request_context", runId=RunId, npcId=NpcId(npc), mode="unknown", requestForInputSeq=_lastInputSeq, messagesCount=fields.Count, messagesType=messages?.GetType().FullName, messages=fields, schemaLength=schema.Length, schemaSha256=Hash(schema), schema=_full ? Limit(RedactCredentialFields(schema)) : null, args=argTypes });
        });
    }

    public static void OnOfficialTransportRequest(object __instance, object[] __args)
    {
        Safe(() =>
        {
            if (!IsOfficialTransport(__instance)) return;
            var body = __args.Length > 1 ? __args[1] as string : null;
            if (string.IsNullOrEmpty(body)) return;
            var safeBody = RedactCredentialFields(body);
            var omitted = safeBody.StartsWith("[omitted:", StringComparison.Ordinal);
            Emit(new
            {
                kind = "official_llm_outbound_request",
                runId = RunId,
                linkedInputSeq = _lastInputSeq,
                linkedContextSeq = _lastRequestSeq,
                bodyLength = safeBody.Length,
                bodySha256 = Hash(safeBody),
                body = _full && !omitted ? Limit(safeBody) : null,
                payloadOmitted = omitted,
                credentialFieldsRedacted = !omitted,
                endpoint = "[not recorded]"
            });
        });
    }

    private static long EmitText(string kind, object npc, string text, string? raw=null)
    {
        var structured = kind == "npc_display" && !string.IsNullOrEmpty(raw) ? ExtractNpcReply(raw) : null;
        return Emit(new
        {
            kind, runId=RunId, npcId=NpcId(npc), linkedRequestSeq=kind == "npc_display" ? _lastRequestSeq : (long?)null,
            length=text.Length, sha256=Hash(text), text=Limit(text),
            rawLength=raw?.Length, rawSha256=raw == null ? null : Hash(raw),
            emotion=structured?.Emotion,
            structuredReply=_full ? structured : null
        });
    }

    private sealed record NpcReplyFields(string? Content, string? Emotion, string? Control, bool? GiveGift, string? GiftItem);
    private static NpcReplyFields? ExtractNpcReply(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out _)) return null;
            var content = root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var emotion = root.TryGetProperty("emotion", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var control = root.TryGetProperty("control", out var ctl) && ctl.ValueKind == JsonValueKind.String ? ctl.GetString() : null;
            bool? giveGift = null;
            string? giftItem = null;
            if (root.TryGetProperty("intent", out var intent) && intent.ValueKind == JsonValueKind.Object)
            {
                if (intent.TryGetProperty("give_gift", out var g) && g.ValueKind is JsonValueKind.True or JsonValueKind.False) giveGift = g.GetBoolean();
                if (intent.TryGetProperty("gift_item", out var item) && item.ValueKind == JsonValueKind.String) giftItem = item.GetString();
            }
            return new NpcReplyFields(content, emotion, control, giveGift, giftItem);
        }
        catch { return null; }
    }

    private static bool IsOfficialTransport(object transport)
    {
        try
        {
            var runtime = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Game")?.GetType("Game.Model.AinpcRuntime");
            var official = runtime?.GetProperty("Official", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (official == null) return false;
            var providerType = official.GetType();
            var officialTransport = providerType.GetProperty("_transport", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(official)
                ?? providerType.GetField("_transport", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(official);
            if (officialTransport == null) return false;
            var expected = NativePointer(officialTransport);
            var actual = NativePointer(transport);
            return expected.HasValue && actual.HasValue && expected.Value == actual.Value;
        }
        catch { return false; }
    }

    private static long? NativePointer(object? value)
    {
        try
        {
            var type = value?.GetType();
            var pointer = type?.GetProperty("Pointer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value)
                ?? type?.GetField("Pointer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value);
            return pointer switch { IntPtr p => p.ToInt64(), long n => n, ulong n => unchecked((long)n), _ => null };
        }
        catch { return null; }
    }
    private static string NpcId(object? npc)
    {
        try
        {
            var type = npc?.GetType();
            // NpcCfgId.Value is the game's numeric NPC configuration ID, not the save entity ID.
            var configId = type?.GetProperty("NpcCfgId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(npc);
            var configValue = configId?.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(configId);
            if (configValue is int id && id > 0) return "npc:" + id;
            var pointer = type?.GetProperty("Pointer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(npc)
                ?? type?.GetField("Pointer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(npc);
            return pointer?.ToString() is { Length: > 0 } value ? "ptr:" + value : "unknown";
        }
        catch { return "unknown"; }
    }
    private static string RedactCredentialFields(string source)
    {
        if (source.Length == 0) return source;
        try
        {
            using var document = JsonDocument.Parse(source);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) WriteRedacted(document.RootElement, writer);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch { return "[omitted: schema was not valid JSON]"; }
    }
    private static void WriteRedacted(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    var key = property.Name.ToLowerInvariant();
                    if (new[] { "authorization", "api_key", "apikey", "token", "secret", "cookie", "password" }.Any(key.Contains)) writer.WriteStringValue("[REDACTED]");
                    else WriteRedacted(property.Value, writer);
                }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array: writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) WriteRedacted(item, writer); writer.WriteEndArray(); break;
            default: element.WriteTo(writer); break;
        }
    }
    private static string Limit(string s) => s.Length <= _maxChars ? s : s[.._maxChars] + "[TRUNCATED]";
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static void Safe(Action action) { try { action(); } catch (Exception e) { try { _log?.LogWarning("Stage2A observer error: " + e.GetType().Name); } catch { } } }
    private static long Emit(object record)
    {
        var seq = Interlocked.Increment(ref _seq);
        var json = JsonSerializer.Serialize(new { utc=DateTime.UtcNow, seq, pid=Environment.ProcessId, record });
        if (!Queue.TryAdd(json)) _log?.LogWarning("Stage2A event queue full; event dropped.");
        _log?.LogInfo($"Stage2A seq={seq} eventBytes={Encoding.UTF8.GetByteCount(json)} eventSha256={Hash(json)} file={Path.Combine(_runDir, "events.jsonl")}");
        return seq;
    }
    private static void Writer()
    {
        try { using var writer = new StreamWriter(Path.Combine(_runDir, "events.jsonl"), append:true, new UTF8Encoding(false)); foreach (var line in Queue.GetConsumingEnumerable()) { writer.WriteLine(line); writer.Flush(); } }
        catch (Exception e) { try { _log?.LogError("Stage2A writer failed: " + e.GetType().Name); } catch { } }
    }
}

internal static class PluginInfo
{
    public const string Guid = "org.a1indextts.mod";
    public const string Name = "A1 IndexTTS Mod";
    public const string Version = "0.5.9";
}
