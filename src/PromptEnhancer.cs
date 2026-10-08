using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Text;

namespace A1IndexTTSMod;

/// <summary>Builds a consistent content-envelope prompt/schema extension without mutating game-owned JSON.</summary>
internal static class PromptEnhancer
{
    private const string Marker = "A1_TTS_STYLE_V1";
    internal sealed record Extension(string Tail, string ResponseFormatJson, string ExampleJson, string FieldDescriptions, string ContractAddition);

    public static string ReadResponseFormatJson(object value)
    {
        var type = value.GetType();
        var formattedToString = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method =>
            {
                var parameters = method.GetParameters();
                return method.Name == "ToString" && parameters.Length is 1 or 2 &&
                    parameters[0].ParameterType.IsEnum && parameters[0].ParameterType.Name == "Formatting" &&
                    (parameters.Length == 1 || parameters[1].ParameterType.Name.StartsWith("Il2CppReferenceArray`1", StringComparison.Ordinal));
            });
        if (formattedToString != null)
        {
            var arguments = formattedToString.GetParameters().Length == 1
                ? new[] { Enum.ToObject(formattedToString.GetParameters()[0].ParameterType, 0) }
                : new[] { Enum.ToObject(formattedToString.GetParameters()[0].ParameterType, 0), null };
            var json = formattedToString.Invoke(value, arguments) as string;
            if (LooksLikeJsonObject(json)) return json!;
        }
        var plain = value.ToString();
        if (LooksLikeJsonObject(plain)) return plain!;

        // IL2CPP Newtonsoft proxy ToString() can fall back to the native class name;
        // use its JSON.NET serializer, when present, to format the wrapped JObject.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var convert = assembly.GetType("Newtonsoft.Json.JsonConvert") ?? assembly.GetType("Il2CppNewtonsoft.Json.JsonConvert");
            if (convert == null) continue;
            var formatter = convert.Assembly.GetTypes().FirstOrDefault(candidate => candidate.Name == "Formatting");
            if (formatter == null || !formatter.IsEnum) continue;
            var serialize = convert.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "SerializeObject" && method.GetParameters() is { Length: 2 } parameters &&
                    parameters[0].ParameterType == typeof(object) && parameters[1].ParameterType == formatter);
            if (serialize == null) continue;
            var json = serialize.Invoke(null, new[] { value, Enum.ToObject(formatter, 0) }) as string;
            if (LooksLikeJsonObject(json)) return json!;
        }
        throw new JsonException($"Could not serialize response format object of type {type.FullName} to JSON.");
    }

    private static bool LooksLikeJsonObject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { return JsonNode.Parse(value) is JsonObject; }
        catch (JsonException) { return false; }
    }

    public static Extension Extend(string tail, string? responseFormatJson, string exampleJson, string fieldDescriptions,
        string enhancementText)
    {
        JsonObject? responseFormat = null;
        if (responseFormatJson != null)
        {
            responseFormat = JsonNode.Parse(responseFormatJson) as JsonObject
                ?? throw new JsonException("The game's response format root must be an object.");
            var schema = responseFormat;
            if (responseFormat["json_schema"] is JsonObject wrapped && wrapped["schema"] is JsonObject innerSchema)
                schema = innerSchema;
            if (schema["properties"] is not JsonObject properties)
                throw new JsonException("The game's response schema has no object properties to extend.");
            if (properties["content"] is not JsonObject contentSchema)
                throw new JsonException("The game's response schema has no object content property to describe the text envelope.");
            var envelopeDescription = "玩家可见正文；启用本 Mod 的语音风格传输时，正文末尾可追加一个 <a1tts_v1>{JSON}</a1tts_v1> 帧。帧内 JSON 仅含 emotion_tags（1–3 个简短中文标签）、delivery（可听见的说话方式）和 intensity（0–1）；无风格时不追加帧。Mod 会在显示、历史与下游提示前剥离该帧。";
            var originalContentDescription = contentSchema["description"]?.GetValue<string>();
            contentSchema["description"] = string.IsNullOrWhiteSpace(originalContentDescription)
                ? envelopeDescription
                : originalContentDescription.TrimEnd() + " " + envelopeDescription;
        }

        var example = JsonNode.Parse(exampleJson) as JsonObject
            ?? throw new JsonException("The game's response example root must be an object.");
        var exampleStyle = new VoiceStyle(new[] { "克制的关切", "故作冷淡" }, "声音稍低，语速略慢，句尾收住", 0.4);
        example["content"] = StyleEnvelopeCodec.EncodeExample(example["content"]?.GetValue<string>() ?? "你来了。", exampleStyle);
        var descriptions = AppendOnce(fieldDescriptions,
            "content：玩家可见正文；语音风格若可判断，末尾追加单个 <a1tts_v1>{JSON}</a1tts_v1> 帧，JSON 字段为 emotion_tags、delivery、intensity；没有风格时不追加帧。其余游戏字段沿用原契约。");
        var contractAddition = "content 可在台词末尾携带独立语音元数据帧 <a1tts_v1>{JSON}</a1tts_v1>；帧仅含 emotion_tags、delivery、intensity，解析后不会进入游戏正文。无风格时不追加帧。";
        var tailAddition = enhancementText.Trim() + "\n\n输出字段说明：\n" + descriptions;
        var enhancedTail = TryExtractReplyExample(tail, out _, out var tailWithExample,
            JsonNode.Parse(example.ToJsonString()) as JsonObject)
            ? AppendOnce(tailWithExample, tailAddition)
            : AppendOnce(tail, tailAddition + "\n\n完整 JSON 示例（沿用原示例字段和值，并在 content 末尾追加语音风格帧）：\n" + example.ToJsonString());
        return new Extension(enhancedTail, responseFormat?.ToJsonString() ?? "", example.ToJsonString(), descriptions, contractAddition);
    }

    public static string LoadInstructions()
    {
        var assembly = typeof(PromptEnhancer).Assembly;
        const string suffix = ".npc-emotion-enhancement.zh-CN.md";
        var resourceName = assembly.GetManifestResourceNames().SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = resourceName == null ? null : assembly.GetManifestResourceStream(resourceName);
        if (stream == null) throw new InvalidOperationException("Embedded NPC emotion prompt resource is missing.");
        using var reader = new StreamReader(stream);
        return ExtractInstructions(reader.ReadToEnd());
    }

    public static string ExtractInstructions(string markdown)
    {
        markdown = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        const string start = "```text\n";
        var begin = markdown.IndexOf(start, StringComparison.Ordinal);
        if (begin < 0) throw new InvalidDataException("Prompt resource has no text block.");
        begin += start.Length;
        var end = markdown.IndexOf("\n```", begin, StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException("Prompt resource text block is not closed.");
        return markdown[begin..end].Trim();
    }

    public static bool TryCreateRequestSnapshot(object options, string tail, string exampleJson, string fieldDescriptions,
        out object? requestOptions, out Extension? extension)
    {
        requestOptions = null;
        extension = null;
        var stage = "options_type";
        try
        {
        var optionsType = options.GetType();
        stage = "response_property";
        var responseProperty = optionsType.GetProperty("ResponseFormat", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var original = responseProperty?.GetValue(options);
        if (responseProperty == null) return false;
        if (original == null)
        {
            // Custom providers may use the textual output contract without a schema.
            // Enhance that contract while preserving the original null ResponseFormat.
            extension = Extend(tail, null, exampleJson, fieldDescriptions, LoadInstructions());
            requestOptions = options;
            return true;
        }

        stage = "serialize_response_format";
        var originalJson = ReadResponseFormatJson(original);
        stage = "extend_prompt_and_schema";
        var result = Extend(tail, originalJson, exampleJson, fieldDescriptions, LoadInstructions());
        stage = "parse_extended_schema";
        var parseMethod = original.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(method => method.Name == "Parse" && method.GetParameters() is { Length: 1 } parameters && parameters[0].ParameterType == typeof(string));
        var parsed = parseMethod?.Invoke(null, new object[] { result.ResponseFormatJson });
        if (parsed == null || !responseProperty.CanWrite) return false;

        stage = "construct_native_options";
        object clone;
        if (optionsType.FullName == "JNGame.Ainpc.Llm.LlmCallOptions")
        {
            var helper = typeof(PromptEnhancer).Assembly.GetType("A1IndexTTSMod.NativeLlmCallOptionsSnapshot")
                ?? throw new InvalidOperationException("Native LlmCallOptions snapshot helper is missing.");
            var create = helper.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("Native LlmCallOptions snapshot helper has no Create method.");
            stage = "native_options_snapshot";
            clone = create.Invoke(null, new[] { options, result.ResponseFormatJson })
                ?? throw new InvalidOperationException("Native LlmCallOptions snapshot helper returned null.");
        }
        else
        {
            clone = Activator.CreateInstance(optionsType) ?? throw new InvalidOperationException("Could not construct request options snapshot.");
            foreach (var property in optionsType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0) continue;
                property.SetValue(clone, property.Name == responseProperty.Name ? parsed : property.GetValue(options));
            }
        }
        requestOptions = clone;
        extension = result;
        return true;
        }
        catch (Exception exception)
        {
            while (exception is TargetInvocationException invocation && invocation.InnerException is Exception inner) exception = inner;
            throw new InvalidOperationException(stage + ": " + exception.GetType().FullName + ": " + exception.Message + " | " + exception.StackTrace, exception);
        }
    }

    public static string CreateFieldDescriptionsFromResponseFormat(object options)
    {
        var responseProperty = options.GetType().GetProperty("ResponseFormat", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new JsonException("LlmCallOptions has no ResponseFormat property.");
        var responseValue = responseProperty.GetValue(options);
        if (responseValue == null) return "沿用游戏原始文本输出契约中的字段与说明。";
        var response = JsonNode.Parse(ReadResponseFormatJson(responseValue)) as JsonObject
            ?? throw new JsonException("ResponseFormat is not a JSON object.");
        var schema = response["json_schema"] is JsonObject wrapped && wrapped["schema"] is JsonObject inner ? inner : response;
        if (schema["properties"] is not JsonObject properties) throw new JsonException("Response schema has no properties.");
        return string.Join("\n", properties.Select(property =>
            property.Key + "：" + (property.Value?["description"]?.GetValue<string>() ?? "按原输出契约生成。")));
    }

    public static bool TryExtractReplyExample(string contract, out string exampleJson, out string updatedContract, JsonObject? styleExample = null)
    {
        exampleJson = "";
        updatedContract = "";
        var selectedStart = -1;
        var selectedLength = 0;
        for (var start = 0; start < contract.Length; start++)
        {
            if (contract[start] != '{') continue;
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var end = start; end < contract.Length; end++)
            {
                var ch = contract[end];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') { inString = true; continue; }
                if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0)
                {
                    var candidate = contract[start..(end + 1)];
                    try
                    {
                        using var parsed = JsonDocument.Parse(candidate);
                        var root = parsed.RootElement;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("content", out _) && candidate.Length > selectedLength)
                        { selectedStart = start; selectedLength = candidate.Length; }
                    }
                    catch (JsonException) { }
                    break;
                }
            }
        }
        if (selectedStart < 0) return false;
        var source = contract.Substring(selectedStart, selectedLength);
        var example = JsonNode.Parse(source) as JsonObject;
        if (example == null) return false;
        if (styleExample != null)
        {
            if (styleExample["content"] is JsonValue contentValue && contentValue.TryGetValue<string>(out var content))
                example["content"] = JsonValue.Create(content);
        }
        exampleJson = example.ToJsonString();
        updatedContract = contract[..selectedStart] + exampleJson + contract[(selectedStart + selectedLength)..];
        return true;
    }

    private static string AppendOnce(string source, string addition, bool addMarker = false)
    {
        if (addMarker && source.Contains(Marker, StringComparison.Ordinal)) return source;
        if (source.Contains(addition.Trim(), StringComparison.Ordinal)) return source;
        var suffix = addition.Contains(Marker, StringComparison.Ordinal) || !addMarker
            ? addition.Trim()
            : Marker + "\n" + addition.Trim();
        return string.IsNullOrWhiteSpace(source) ? suffix : source.TrimEnd() + "\n\n" + suffix;
    }
}

/// <summary>Validates the extension and returns game JSON with only the added field removed.</summary>
internal static class NpcReplyPayload
{
    internal sealed record Separated(string GameJson, VoiceStyle? VoiceStyle, string VoiceStyleStatus,
        string VoiceStyleJsonType, string? VoiceStyleFailureReason, string VoiceStyleSource);

    public static string WithVoiceStyle(string gameJson, VoiceStyle style)
    {
        var root = JsonNode.Parse(gameJson) as JsonObject ?? throw new JsonException("NPC reply must be a JSON object.");
        var tags = new JsonArray();
        foreach (var tag in style.EmotionTags) tags.Add(JsonValue.Create(tag));
        root["voice_style"] = new JsonObject
        {
            ["emotion_tags"] = tags,
            ["delivery"] = JsonValue.Create(style.Delivery),
            ["intensity"] = JsonValue.Create(style.Intensity)
        };
        return root.ToJsonString();
    }

    public static Separated? Separate(string raw)
    {
        try
        {
            var candidate = raw;
            try { using var _ = JsonDocument.Parse(candidate); }
            catch (JsonException)
            {
                candidate = UnwrapJsonFence(raw);
                if (candidate == raw) return null;
            }
            using var document = JsonDocument.Parse(candidate);
            var rootElement = document.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object) return null;
            VoiceStyle? style = null;
            var status = "absent";
            var jsonType = "absent";
            var source = "none";
            string? failureReason = null;
            if (rootElement.TryGetProperty("voice_style", out var styleElement))
            {
                jsonType = styleElement.ValueKind.ToString().ToLowerInvariant();
                if (styleElement.ValueKind == JsonValueKind.Null) status = "null";
                else
                {
                    style = VoiceStyle.Parse(styleElement, out failureReason);
                    status = style == null ? "invalid" : "valid";
                    if (style != null) source = "legacy_voice_style";
                }
            }
            var root = JsonNode.Parse(candidate) as JsonObject;
            if (root == null || !root.ContainsKey("content")) return null;
            if (root["content"] is JsonValue contentNode && contentNode.TryGetValue<string>(out var content))
            {
                var envelope = StyleEnvelopeCodec.Decode(content);
                root["content"] = JsonValue.Create(envelope.Content);
                if (style == null && envelope.Style != null)
                {
                    style = envelope.Style;
                    status = "valid";
                    jsonType = "object";
                    failureReason = null;
                    source = "content_envelope_v1";
                }
                else if (style == null && envelope.Status == "invalid")
                {
                    status = "invalid";
                    jsonType = "content_envelope";
                    failureReason = envelope.FailureReason;
                }
            }
            root.Remove("voice_style");
            return new Separated(root.ToJsonString(), style, status, jsonType, failureReason, source);
        }
        catch (JsonException) { return null; }
    }

    private static string UnwrapJsonFence(string raw)
    {
        var value = raw.Trim();
        if (!value.StartsWith("```", StringComparison.Ordinal) || !value.EndsWith("```", StringComparison.Ordinal) || value.Length < 6)
            return raw;
        value = value[3..^3].Trim();
        if (value.StartsWith("json", StringComparison.OrdinalIgnoreCase) &&
            (value.Length == 4 || char.IsWhiteSpace(value[4]))) value = value[4..].Trim();
        try { return JsonDocument.Parse(value).RootElement.ValueKind == JsonValueKind.Object ? value : raw; }
        catch (JsonException) { return raw; }
    }
}
