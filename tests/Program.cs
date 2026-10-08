using A1IndexTTSMod;
using HarmonyLib;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;

var table = Path.Combine(BepInEx.Paths.GameRootPath, "A1IndexTTSMod", "references", "npcs", "npc_id_name.csv");
var rows = File.ReadLines(table).Skip(1)
    .Select(line => line.Split(','))
    .Where(parts => parts.Length == 4)
    .ToArray();

string Find(string audio, string gender) => rows.First(parts => parts[2] == audio && parts[3] == gender)[0];

void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var harmonyPath = Path.GetDirectoryName(typeof(Harmony).Assembly.Location)!;
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var candidate = Path.Combine(harmonyPath, name.Name + ".dll");
    return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};
var harmony = new Harmony("a1index.test.by-value-prefix");
var harmonyTarget = AccessTools.Method(typeof(HarmonyByValueFixture), nameof(HarmonyByValueFixture.Target))!;
var argsPrefix = AccessTools.Method(typeof(HarmonyByValueFixture), nameof(HarmonyByValueFixture.ReplaceViaArgs))!;
harmony.Patch(harmonyTarget, prefix: new HarmonyMethod(argsPrefix));
HarmonyByValueFixture.Target("original-tail", "original-schema");
Assert(HarmonyByValueFixture.ObservedTail == "original-tail" && HarmonyByValueFixture.ObservedSchema == "original-schema",
    "Expected this game's Harmony to demonstrate that __args replacement does not write back by-value parameters");
var refPrefix = AccessTools.Method(typeof(HarmonyByValueFixture), nameof(HarmonyByValueFixture.ReplaceByRef))!;
var refHarmony = new Harmony("a1index.test.by-ref-prefix");
var refTarget = AccessTools.Method(typeof(HarmonyByValueFixture), nameof(HarmonyByValueFixture.TargetRef))!;
refHarmony.Patch(refTarget, prefix: new HarmonyMethod(refPrefix));
var originalOptions = new FakeHarmonyOptions { TimeoutSeconds = 42, Temperature = 0.35, ResponseFormat = "original-schema" };
var originalDocument = new FakeHarmonyDocument("original-document");
HarmonyByValueFixture.TargetRef("original-tail", originalOptions, originalDocument);
Assert(HarmonyByValueFixture.ObservedTail == "enhanced-tail" && HarmonyByValueFixture.ObservedSchema == "enhanced-schema" &&
    HarmonyByValueFixture.ObservedTimeout == 42 && HarmonyByValueFixture.ObservedTemperature == 0.35 &&
    HarmonyByValueFixture.ObservedDocument == "enhanced-document" && originalOptions.ResponseFormat == "original-schema",
    $"Harmony explicit ref parameter replacement did not reach the original by-value parameters: calls={HarmonyByValueFixture.PrefixCalls}, tail={HarmonyByValueFixture.ObservedTail}, schema={HarmonyByValueFixture.ObservedSchema}");
harmony.UnpatchSelf();
refHarmony.UnpatchSelf();

var maleId = Find("0", "1");
var male = NpcVoiceResolver.Resolve("npc:" + maleId);
Assert(!male.Skip && male.Label == "default_male.wav" && File.Exists(male.Path), "male audio=0 fallback failed");

var femaleId = Find("0", "2");
var female = NpcVoiceResolver.Resolve("npc:" + femaleId);
Assert(!female.Skip && female.Label == "default_female.wav" && File.Exists(female.Path), "female audio=0 fallback failed");

var unknownId = Find("0", "3");
var unknown = NpcVoiceResolver.Resolve("npc:" + unknownId);
Assert(unknown.Skip && unknown.Path == null, "unknown audio=0 should be silent");

var dedicated = NpcVoiceResolver.Resolve("npc:100000");
Assert(!dedicated.Skip && dedicated.Label == "100000.wav" && File.Exists(dedicated.Path), "dedicated NPC WAV failed");

var instructions = PromptEnhancer.LoadInstructions();
Assert(instructions.Contains("A1_TTS_STYLE_V1", StringComparison.Ordinal), "embedded LF/CRLF prompt extraction failed");
var crlfInstructions = PromptEnhancer.ExtractInstructions(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "prompts", "npc-emotion-enhancement.zh-CN.md")).Replace("\n", "\r\n", StringComparison.Ordinal));
Assert(crlfInstructions == instructions, "CRLF prompt extraction differs from embedded resource");
var schema = """{"type":"json_schema","json_schema":{"name":"reply","strict":true,"schema":{"type":"object","properties":{"content":{"type":"string"},"emotion":{"type":"string"},"observe":{"type":"object","properties":{"topic_goal_met":{"type":"boolean"}},"required":["topic_goal_met"],"additionalProperties":false},"intent":{"type":"object","properties":{"persuasion_delta":{"type":"integer"}},"required":["persuasion_delta"],"additionalProperties":false}},"required":["content","emotion","observe","intent"],"additionalProperties":false}}}""";
var example = """{"content":"示例台词","emotion":"smile","observe":{"topic_goal_met":false},"intent":{"persuasion_delta":0}}""";
var extended = PromptEnhancer.Extend("original tail", schema, example, "original field descriptions", instructions);
using (var format = JsonDocument.Parse(extended.ResponseFormatJson))
{
    var root = format.RootElement;
    Assert(root.GetProperty("json_schema").GetProperty("strict").GetBoolean(), "strict response format was not preserved");
    var outputSchema = root.GetProperty("json_schema").GetProperty("schema");
    Assert(outputSchema.GetProperty("additionalProperties").GetBoolean() == false, "root additionalProperties changed");
    Assert(!outputSchema.GetProperty("required").EnumerateArray().Any(item => item.GetString() == "voice_style"), "new response schema must not depend on a top-level voice_style field");
    Assert(outputSchema.GetProperty("properties").TryGetProperty("observe", out _) && outputSchema.GetProperty("properties").TryGetProperty("intent", out _), "mode-specific fields were lost");
    Assert(outputSchema.GetProperty("properties").GetProperty("content").GetProperty("description").GetString()!.Contains("<a1tts_v1>", StringComparison.Ordinal), "content schema description omitted the transport contract");
}
using (var sample = JsonDocument.Parse(extended.ExampleJson))
{
    Assert(sample.RootElement.GetProperty("observe").GetProperty("topic_goal_met").GetBoolean() == false, "example observe fields changed");
    Assert(sample.RootElement.GetProperty("intent").GetProperty("persuasion_delta").GetInt32() == 0, "example persuasion fields changed");
    var content = sample.RootElement.GetProperty("content").GetString()!;
    var exampleFrame = StyleEnvelopeCodec.Decode(content);
    Assert(exampleFrame.Style?.EmotionTags.Count == 2 && exampleFrame.Content == "示例台词", "content envelope example missing or not readable");
}
var twice = PromptEnhancer.Extend(extended.Tail, extended.ResponseFormatJson, extended.ExampleJson, extended.FieldDescriptions, instructions);
Assert(twice.Tail.Split("A1_TTS_STYLE_V1", StringSplitOptions.None).Length == 2, "prompt retry duplicated enhancement marker");
Assert(PromptEnhancer.TryExtractReplyExample("原契约示例：\n" + example, out var extractedExample, out var updatedContract,
    JsonNode.Parse(extended.ExampleJson) as JsonObject), "real output-contract example was not found");
using (var preserved = JsonDocument.Parse(extractedExample))
{
    var preservedContent = StyleEnvelopeCodec.Decode(preserved.RootElement.GetProperty("content").GetString());
    Assert(preservedContent.Content == "示例台词" && preservedContent.Style?.Delivery != null && preserved.RootElement.GetProperty("emotion").GetString() == "smile", "updated output-contract example lost the v1 frame or changed game fields");
    Assert(preserved.RootElement.GetProperty("observe").GetProperty("topic_goal_met").GetBoolean() == false, "extended example lost original nested fields");
}
Assert(updatedContract.Contains(extractedExample, StringComparison.Ordinal), "updated contract does not carry the synchronized full example");

var validRaw = """{"content":"台词","emotion":"smile","control":"none","observe":{"topic_goal_met":false},"intent":{"persuasion_delta":1},"voice_style":{"emotion_tags":["克制的关切"],"delivery":"声音轻柔","intensity":0.4}}""";
var separated = NpcReplyPayload.Separate(validRaw) ?? throw new Exception("valid NPC payload did not separate");
Assert(separated.VoiceStyle?.EmotionTags.Single() == "克制的关切", "open emotion tag was not preserved");
Assert(separated.VoiceStyleStatus == "valid" && separated.VoiceStyleJsonType == "object", "valid raw voice_style status was not recorded");
using (var gameReply = JsonDocument.Parse(separated.GameJson))
{
    Assert(!gameReply.RootElement.TryGetProperty("voice_style", out _), "added field was not removed from game payload");
    Assert(gameReply.RootElement.GetProperty("emotion").GetString() == "smile" && gameReply.RootElement.GetProperty("observe").GetProperty("topic_goal_met").GetBoolean() == false, "original game fields were not preserved");
    Assert(gameReply.RootElement.GetProperty("intent").GetProperty("persuasion_delta").GetInt32() == 1, "persuasion value changed");
}
var invalidRaw = """{"content":"台词","emotion":"normal","voice_style":{"emotion_tags":[],"delivery":"太长描述","intensity":1.4,"unknown":true}}""";
var invalidSeparated = NpcReplyPayload.Separate(invalidRaw) ?? throw new Exception("invalid style blocked original game payload");
Assert(invalidSeparated.VoiceStyle == null, "invalid voice_style was accepted");
Assert(invalidSeparated.VoiceStyleStatus == "invalid" && invalidSeparated.VoiceStyleFailureReason != null, "invalid raw voice_style was not distinguished with a reason");
using (var gameReply = JsonDocument.Parse(invalidSeparated.GameJson))
    Assert(gameReply.RootElement.GetProperty("content").GetString() == "台词" && gameReply.RootElement.GetProperty("emotion").GetString() == "normal", "invalid style damaged original reply");
var absentStyle = NpcReplyPayload.Separate("""{"content":"台词"}""")!;
var nullStyle = NpcReplyPayload.Separate("""{"content":"台词","voice_style":null}""")!;
Assert(absentStyle.VoiceStyleStatus == "absent" && nullStyle.VoiceStyleStatus == "null", "absent and explicit-null voice_style were conflated");

var frameStyle = new VoiceStyle(new[] { "压低的关切" }, "声音放轻，语速稍缓", 0.3);
var screenshotText = "怎么，答不出？ <a1tts_v1>{\"emotion_tags\":[\"略带调侃\",\"温和\"],\"delivery\":\"语速平稳，尾音带着点笑意\",\"intensity\":0.4}</a1tts_v1>";
Assert(SpeechTextFilter.RemoveParentheticals(screenshotText) == "怎么，答不出？", "TTS filter leaked a content-carried voice-style frame into speech");
var sourcePcm = new ByteArrayWaveProvider(new WaveFormat(24000, 16, 1), new byte[] { 0x00, 0x40, 0x00, 0xC0 });
var volumePcm = new PcmVolumeWaveProvider(sourcePcm, 100);
Assert(volumePcm.WaveFormat.Encoding == sourcePcm.WaveFormat.Encoding &&
    volumePcm.WaveFormat.BitsPerSample == sourcePcm.WaveFormat.BitsPerSample,
    "speech volume provider must preserve the WAV format passed to WASAPI");
var outputPcm = new byte[4];
Assert(volumePcm.Read(outputPcm, 0, outputPcm.Length) == 4 && outputPcm.SequenceEqual(sourcePcm.Bytes),
    "100% speech volume must pass PCM bytes through unchanged");
var quieterPcm = new PcmVolumeWaveProvider(new ByteArrayWaveProvider(new WaveFormat(24000, 16, 1), sourcePcm.Bytes), 50);
var quieterBytes = new byte[4];
quieterPcm.Read(quieterBytes, 0, quieterBytes.Length);
Assert(BitConverter.ToInt16(quieterBytes, 0) == 8192 && BitConverter.ToInt16(quieterBytes, 2) == -8192,
    "speech volume attenuation must scale signed PCM16 samples without changing their format");
StyleStore.Store("npc:style-store-a", "同一句", frameStyle, "hash-a", "raw-a", "valid", null, "test-a");
StyleStore.Store("npc:style-store-b", "同一句", null, "hash-b", "raw-b", "absent", null, "test-b");
Assert(StyleStore.Take("npc:style-store-b", "同一句")?.Source == "test-b", "pending style crossed NPC identities");
Assert(StyleStore.Take("npc:style-store-a", "同一句")?.Source == "test-a", "pending style was not recovered for matching NPC and content");
StyleStore.Store("npc:style-store-fifo", "重复对白", frameStyle, "hash-first", "raw-first", "valid", null, "first");
StyleStore.Store("npc:style-store-fifo", "重复对白", null, "hash-second", "raw-second", "absent", null, "second");
Assert(StyleStore.Take("npc:style-store-fifo", "重复对白")?.Source == "first" &&
    StyleStore.Take("npc:style-store-fifo", "重复对白")?.Source == "second", "same-reply pending styles were not consumed FIFO");
var encodedFrame = StyleEnvelopeCodec.EncodeExample("你来了。", frameStyle);
var decodedFrame = StyleEnvelopeCodec.Decode(encodedFrame);
Assert(decodedFrame.Status == "valid" && decodedFrame.Content == "你来了。" && decodedFrame.Style?.EmotionTags.Single() == "压低的关切" && decodedFrame.Style.Intensity == 0.3, "valid v1 content frame did not round trip");
Assert(StyleEnvelopeCodec.Decode("自然对白 <a1tts_v2>{}</a1tts_v2>").Content == "自然对白 <a1tts_v2>{}</a1tts_v2>", "unknown protocol-like text was changed");
Assert(StyleEnvelopeCodec.Decode("普通对白</a1tts_v1>").Content == "普通对白</a1tts_v1>", "a closing-tag substring without the v1 opener was changed");
var invalidFrame = StyleEnvelopeCodec.Decode("保留台词<a1tts_v1>{坏 JSON}</a1tts_v1>");
Assert(invalidFrame.Status == "invalid" && invalidFrame.Content == "保留台词" && invalidFrame.Style == null, "invalid frame leaked or fabricated a style");
var duplicateFrames = StyleEnvelopeCodec.Decode(encodedFrame + StyleEnvelopeCodec.OpenTag + "{}" + StyleEnvelopeCodec.CloseTag);
Assert(duplicateFrames.Status == "invalid" && duplicateFrames.Content == "你来了。" && duplicateFrames.Style == null, "duplicate frames were accepted or leaked");
var incompleteFrame = StyleEnvelopeCodec.Decode("保留台词" + StyleEnvelopeCodec.OpenTag + "{\"emotion_tags\":[]");
Assert(incompleteFrame.Status == "invalid" && incompleteFrame.Content == "保留台词", "incomplete frame leaked into dialogue");
var oversizedFrame = StyleEnvelopeCodec.Decode("台词" + StyleEnvelopeCodec.OpenTag + new string('x', StyleEnvelopeCodec.MaximumFrameLength) + StyleEnvelopeCodec.CloseTag);
Assert(oversizedFrame.Status == "invalid" && oversizedFrame.Content == "台词" && oversizedFrame.FailureReason == "frame_too_long", "oversized frame was accepted or leaked");
var trailingFrameText = StyleEnvelopeCodec.Decode(encodedFrame + "尾随");
Assert(trailingFrameText.Status == "invalid" && trailingFrameText.Content == "你来了。尾随" && trailingFrameText.Style == null, "text after the frame was lost or style accepted");
var fencedFrame = StyleEnvelopeCodec.Decode("台词" + StyleEnvelopeCodec.OpenTag + "```json\n{\"emotion_tags\":[\"克制\"],\"delivery\":\"轻声\",\"intensity\":0.2}\n```" + StyleEnvelopeCodec.CloseTag);
Assert(fencedFrame.Status == "valid" && fencedFrame.Content == "台词", "a fenced JSON frame was not safely unwrapped");
Assert(StyleEnvelopeCodec.Decode("只有对白，没有风格帧").Status == "absent", "style-less dialogue was not reported as absent");
var frameRaw = """{"content":"台词<a1tts_v1>{\"emotion_tags\":[\"克制的关切\"],\"delivery\":\"声音轻柔\",\"intensity\":0.4}</a1tts_v1>","emotion":"normal"}""";
var frameSeparated = NpcReplyPayload.Separate(frameRaw) ?? throw new Exception("content frame reply did not parse");
Assert(frameSeparated.VoiceStyleSource == "content_envelope_v1" && frameSeparated.VoiceStyle?.EmotionTags.Single() == "克制的关切", "content-frame voice style was not extracted");
using (var cleanFrameReply = JsonDocument.Parse(frameSeparated.GameJson))
    Assert(cleanFrameReply.RootElement.GetProperty("content").GetString() == "台词", "content protocol frame was not removed before the game payload");
var outerFenced = NpcReplyPayload.Separate("```json\n" + frameRaw + "\n```");
Assert(outerFenced?.VoiceStyle?.EmotionTags.Single() == "克制的关切", "outer JSON markdown fence was not handled");
Assert(NpcReplyPayload.Separate("```json\n{\"content\":\"损坏\",\n```") == null, "damaged outer JSON was guessed or repaired");
var bothSources = NpcReplyPayload.Separate("""{"content":"台词<a1tts_v1>{\"emotion_tags\":[\"帧标签\"],\"delivery\":\"帧\",\"intensity\":0.2}</a1tts_v1>","voice_style":{"emotion_tags":["旧字段"],"delivery":"旧版","intensity":0.8}}""")!;
Assert(bothSources.VoiceStyleSource == "legacy_voice_style" && bothSources.VoiceStyle?.EmotionTags.Single() == "旧字段", "valid legacy field must deterministically win when both sources exist");
var nullFallback = NpcReplyPayload.Separate("""{"content":"台词<a1tts_v1>{\"emotion_tags\":[\"帧标签\"],\"delivery\":\"帧\",\"intensity\":0.2}</a1tts_v1>","voice_style":null}""")!;
Assert(nullFallback.VoiceStyleSource == "content_envelope_v1", "null legacy field must fall back to a valid content frame");
var invalidLegacyFallback = NpcReplyPayload.Separate("""{"content":"台词<a1tts_v1>{\"emotion_tags\":[\"帧标签\"],\"delivery\":\"帧\",\"intensity\":0.2}</a1tts_v1>","voice_style":{"emotion_tags":[],"delivery":"坏","intensity":2}}""")!;
Assert(invalidLegacyFallback.VoiceStyleSource == "content_envelope_v1", "invalid legacy field must fall back to a valid content frame");

var sharedOptions = new FakeLlmOptions
{
    ResponseFormat = new FakeJObject(schema),
    Temperature = 0.3,
    TimeoutSeconds = 42
};
var generatedExample = example;
var schemaDescriptions = PromptEnhancer.CreateFieldDescriptionsFromResponseFormat(sharedOptions);
Assert(schemaDescriptions.Contains("content", StringComparison.Ordinal) && schemaDescriptions.Contains("observe", StringComparison.Ordinal), "original response field descriptions were not derived from schema");
Assert(PromptEnhancer.ReadResponseFormatJson(sharedOptions.ResponseFormat) == schema, "response format JSON serializer changed or failed the response format");
var nativeLike = new NativeLikeJObject(schema);
Assert(PromptEnhancer.ReadResponseFormatJson(nativeLike) == schema, "IL2CPP JObject two-argument formatting overload was not used");
Assert(PromptEnhancer.TryCreateRequestSnapshot(sharedOptions, "tail", generatedExample, "existing output contract", out var scopedObject, out var scopedExtension), "request-scoped options snapshot failed");
Assert(scopedObject is FakeLlmOptions scoped && !ReferenceEquals(sharedOptions, scoped) && sharedOptions.Temperature == 0.3 && sharedOptions.TimeoutSeconds == 42, "shared LLM options were mutated or not copied");
Assert(sharedOptions.ResponseFormat.ToString() == schema && scopedExtension!.Tail.Contains("A1_TTS_STYLE_V1", StringComparison.Ordinal), "request prompt/schema were not committed together");
Assert(scopedExtension!.FieldDescriptions.Contains("existing output contract", StringComparison.Ordinal) && scopedExtension.FieldDescriptions.Contains("<a1tts_v1>", StringComparison.Ordinal), "original and new output field descriptions were not kept together");
var tailWithContract = "任务背景\n输出示例：\n" + example + "\n继续要求";
var tailExtended = PromptEnhancer.Extend(tailWithContract, schema, example, "字段", instructions);
Assert(tailExtended.Tail.Contains("输出示例：\n" + tailExtended.ExampleJson, StringComparison.Ordinal), "tail-embedded contract example was not updated in place");
Assert(tailExtended.Tail.Split("完整 JSON 示例", StringSplitOptions.None).Length == 1, "tail contract update appended a duplicate, potentially conflicting example");

var cosyStyle = new VoiceStyle(new[] { "克制的关切", "故作冷淡" }, "声音稍低，语速略慢，句尾收住", 0.4);
var voiceRefBytes = System.Text.Encoding.UTF8.GetBytes("RIFF-test-wave");
var inlineVoice = new Dictionary<string, string> { ["type"] = "base64", ["data"] = Convert.ToBase64String(voiceRefBytes) };
var cosyPayload = JsonDocument.Parse(CosyVoiceInstruction.BuildPayload("你好。", cosyStyle, inlineVoice));
Assert(cosyPayload.RootElement.GetProperty("model").GetString() == "cosyvoice3", "CosyVoice model ID missing");
Assert(cosyPayload.RootElement.GetProperty("response_format").GetString() == "wav", "CosyVoice WAV response missing");
Assert(cosyPayload.RootElement.GetProperty("voice_ref").GetProperty("type").GetString() == "base64" &&
    Convert.FromBase64String(cosyPayload.RootElement.GetProperty("voice_ref").GetProperty("data").GetString()!).SequenceEqual(voiceRefBytes), "CosyVoice inline voice_ref missing or corrupted");
var cosyOptions = cosyPayload.RootElement.GetProperty("options");
Assert(cosyOptions.GetProperty("template_name").GetString() == "instruct", "CosyVoice instruct template missing");
Assert(cosyOptions.GetProperty("instruction").GetString()!.Contains("克制的关切、故作冷淡", StringComparison.Ordinal), "CosyVoice style tags were not formatted");
var neutralPayload = JsonDocument.Parse(CosyVoiceInstruction.BuildPayload("你好。", null, null));
Assert(!neutralPayload.RootElement.TryGetProperty("voice_ref", out _) && !neutralPayload.RootElement.GetProperty("options").TryGetProperty("instruction", out _), "Missing style should not fabricate an instruction");
Assert(DisplayTurnIdentity.Create("npc:100000", "1712345678901") == DisplayTurnIdentity.Create("npc:100000", "1712345678901"), "ordinary and persuade callbacks do not share message identity");
Assert(DisplayTurnIdentity.Create("npc:100000", "1712345678901") != DisplayTurnIdentity.Create("npc:100000", "1712345678902"), "distinct native message timestamps collided");
Assert(DisplayTurnIdentity.Create("npc:100000", "0") == null, "missing native identity should not be replaced by a synthetic ID");
Assert(DisplayStyleDedupPolicy.ShouldUpgrade(false, true, true, false), "later full-style callback should upgrade the same not-yet-playing display");
Assert(!DisplayStyleDedupPolicy.ShouldUpgrade(true, false, true, false) && !DisplayStyleDedupPolicy.ShouldUpgrade(false, true, false, false) && !DisplayStyleDedupPolicy.ShouldUpgrade(false, true, true, true), "dedupe should keep a full-style first callback and must not revive finished, superseded, or already-playing audio");
Assert(BackendConfigMigration.InferLegacyBackend(false, "http://127.0.0.1:8892/v1/tts") == "IndexTtsLegacyApi", "legacy endpoint migration failed");
Assert(BackendConfigMigration.InferLegacyBackend(false, "http://127.0.0.1:8892/v1/audio/speech") == null, "default audio.cpp endpoint was misclassified");
Assert(BackendConfigMigration.InferLegacyBackend(true, "http://127.0.0.1:8892/v1/tts") == null, "explicit backend was overridden by migration");
Assert(AudioCppOwnershipPolicy.KeepOwnedForReadyEndpoint(true, true, 8892, 8892), "live owned endpoint ownership was lost");
Assert(!AudioCppOwnershipPolicy.KeepOwnedForReadyEndpoint(false, true, 8892, 8892), "external service was incorrectly claimed");

Console.WriteLine($"PASS male={maleId} female={femaleId} unknown={unknownId} dedicated=100000");

sealed class ByteArrayWaveProvider : IWaveProvider
{
    private int _position;
    public ByteArrayWaveProvider(WaveFormat waveFormat, byte[] bytes) { WaveFormat = waveFormat; Bytes = bytes; }
    public WaveFormat WaveFormat { get; }
    public byte[] Bytes { get; }
    public int Read(byte[] buffer, int offset, int count)
    {
        var read = Math.Min(count, Bytes.Length - _position);
        Array.Copy(Bytes, _position, buffer, offset, read);
        _position += read;
        return read;
    }
}

static class HarmonyByValueFixture
{
    public static string? ObservedTail { get; private set; }
    public static string? ObservedSchema { get; private set; }
    public static int ObservedTimeout { get; private set; }
    public static double ObservedTemperature { get; private set; }
    public static string? ObservedDocument { get; private set; }
    public static int PrefixCalls { get; private set; }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void Target(string tail, string schema)
    {
        ObservedTail = tail;
        ObservedSchema = schema;
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void TargetRef(string tail, FakeHarmonyOptions options, FakeHarmonyDocument doc)
    {
        ObservedTail = tail;
        ObservedSchema = options.ResponseFormat;
        ObservedTimeout = options.TimeoutSeconds;
        ObservedTemperature = options.Temperature;
        ObservedDocument = doc.Text;
    }
    public static void ReplaceViaArgs(object[] __args)
    {
        __args[0] = "enhanced-tail";
        __args[1] = "enhanced-schema";
    }
    public static void ReplaceByRef(ref string __0, ref FakeHarmonyOptions __1, ref FakeHarmonyDocument __2)
    {
        PrefixCalls++;
        __0 = "enhanced-tail";
        __1 = new FakeHarmonyOptions { TimeoutSeconds = __1.TimeoutSeconds, Temperature = __1.Temperature, ResponseFormat = "enhanced-schema" };
        __2 = new FakeHarmonyDocument("enhanced-document");
    }
}

sealed class FakeHarmonyOptions
{
    public int TimeoutSeconds { get; init; }
    public double Temperature { get; init; }
    public string ResponseFormat { get; init; } = "";
}

sealed record FakeHarmonyDocument(string Text);

namespace BepInEx
{
    public static class Paths
    {
        public static string GameRootPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}

sealed class FakeJObject
{
    private readonly string _json;
    public FakeJObject(string json) => _json = json;
    public static FakeJObject Parse(string json) => new(json);
    public override string ToString() => _json;
}

sealed class FakeLlmOptions
{
    public FakeLlmOptions() { }
    public FakeJObject ResponseFormat { get; set; } = FakeJObject.Parse("{}");
    public double Temperature { get; set; }
    public int TimeoutSeconds { get; set; }
}

enum Formatting { None }
sealed class JsonConverter { }
sealed class Il2CppReferenceArray<T> { }
sealed class NativeLikeJObject
{
    private readonly string _json;
    public NativeLikeJObject(string json) => _json = json;
    public override string ToString() => "Newtonsoft.Json.Linq.JObject";
    public string ToString(Formatting formatting, Il2CppReferenceArray<JsonConverter>? converters) => _json;
}
