using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace A1IndexTTSMod;

internal enum PromptEnhancementAbi
{
    LegacyTenArguments,
    ContextElevenArguments
}

internal sealed record PromptEnhancementHookResolution(MethodInfo? Target, PromptEnhancementAbi? Abi,
    string Status, IReadOnlyList<string> ObservedSignatures)
{
    public bool IsResolved => Target != null && Abi != null;
}

internal sealed record TtsGameCompatibilityInspection(
    TtsGameFingerprint Fingerprint,
    NpcReplyHookResolution ReplyHook,
    PromptEnhancementHookResolution PromptHook,
    string Status);

internal static class PromptEnhancementHookCompatibility
{
    internal static PromptEnhancementHookResolution Resolve(IEnumerable<MethodInfo> methods)
    {
        var named = methods.Where(method => method.Name == "CompleteBudgeted").ToArray();
        var observed = named.Select(NpcReplyHookCompatibility.FormatSignature).ToArray();
        var recognized = named.Select(method => (Method: method, Abi: Classify(method)))
            .Where(candidate => candidate.Abi != null).ToArray();
        if (recognized.Length == 0) return new(null, null, named.Length == 0 ? "missing" : "unsupported", observed);
        if (recognized.Length != 1) return new(null, null, "ambiguous", observed);
        return new(recognized[0].Method, recognized[0].Abi, "supported", observed);
    }

    internal static PromptEnhancementAbi? Classify(MethodInfo method)
    {
        if (method.Name != "CompleteBudgeted") return null;
        var p = method.GetParameters();
        if (p.Length == 10 &&
            Has(p[0], "Game.Model.NpcModel", "npc") && Has(p[1], "JNGame.Ainpc.Prompt.ModeParams", "mode") &&
            HasString(p[2], "purpose") && HasString(p[3], "prefix") && HasString(p[4], "memory") &&
            HasMessageList(p[5], "history") &&
            HasString(p[6], "tail") && HasString(p[7], "input") &&
            Has(p[8], "JNGame.Ainpc.Llm.LlmCallOptions", "options") &&
            Has(p[9], "JNGame.Ainpc.Prompt.PromptDocument", "doc"))
            return PromptEnhancementAbi.LegacyTenArguments;

        if (p.Length == 11 &&
            Has(p[0], "Game.Model.NpcModel", "npc") && Has(p[1], "JNGame.Ainpc.Prompt.ModeParams", "mode") &&
            HasString(p[2], "purpose") && HasString(p[3], "prefix") && HasString(p[4], "memory") && HasString(p[5], "context") &&
            HasMessageList(p[6], "history") &&
            HasString(p[7], "tail") && HasString(p[8], "input") &&
            Has(p[9], "JNGame.Ainpc.Llm.LlmCallOptions", "options") &&
            Has(p[10], "JNGame.Ainpc.Prompt.PromptDocument", "doc"))
            return PromptEnhancementAbi.ContextElevenArguments;

        return null;
    }

    private static bool Has(ParameterInfo parameter, string type, string name) =>
        parameter.ParameterType.FullName == type && parameter.Name == name;

    private static bool HasMessageList(ParameterInfo parameter, string name) =>
        parameter.Name == name && parameter.ParameterType.IsGenericType &&
        parameter.ParameterType.GetGenericTypeDefinition().FullName == "Il2CppSystem.Collections.Generic.IReadOnlyList`1" &&
        parameter.ParameterType.GetGenericArguments() is { Length: 1 } arguments &&
        arguments[0].FullName == "JNGame.Ainpc.Llm.LlmMessage";

    private static bool HasString(ParameterInfo parameter, string name) =>
        parameter.ParameterType == typeof(string) && parameter.Name == name;
}

internal static class TtsGameCompatibility
{
    private const string PreviousKnownBuildGuid = "0acccbcdb9a14aa3a528bd4d850c3202";
    private const string CurrentObservedBuildGuid = "e972746e34f34a57aa8e2a1f34c4085f";
    private static readonly Regex GuidPattern = new("(?im)^build-guid\\s*=\\s*([0-9a-f]{32})\\s*$", RegexOptions.Compiled);

    internal static TtsGameCompatibilityInspection Inspect(string gameRoot, string pluginAssemblyPath)
    {
        var gameAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "Game");
        var npcType = gameAssembly?.GetType("Game.Model.NpcModel");
        var replyHook = NpcReplyHookCompatibility.Resolve(npcType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            ?? Array.Empty<MethodInfo>());
        var runtimeType = gameAssembly?.GetType("Game.Model.AinpcRuntime");
        var promptHook = PromptEnhancementHookCompatibility.Resolve(runtimeType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            ?? Array.Empty<MethodInfo>());

        var buildGuid = ReadBuildGuid(gameRoot);
        var metadata = HashFile(Path.Combine(gameRoot, "WorldApart_Data", "il2cpp_data", "Metadata", "global-metadata.dat"));
        var interopPath = Path.Combine(gameRoot, "BepInEx", "interop", "Game.dll");
        var interop = HashFile(interopPath);
        var plugin = HashFile(pluginAssemblyPath);
        var captureSignature = replyHook.IsResolved
            ? NpcReplyHookCompatibility.FormatSignature(replyHook.Target!)
            : string.Join(";", replyHook.ObservedSignatures);
        var promptStatus = promptHook.IsResolved ? promptHook.Abi!.Value.ToString() : promptHook.Status;
        var fingerprint = new TtsGameFingerprint(buildGuid, metadata, interop, plugin, captureSignature, promptStatus);

        var knownBuild = buildGuid.Equals(PreviousKnownBuildGuid, StringComparison.OrdinalIgnoreCase) ||
                         buildGuid.Equals(CurrentObservedBuildGuid, StringComparison.OrdinalIgnoreCase);
        var status = !replyHook.IsResolved
            ? replyHook.Status == "ambiguous" ? "必要回复捕获钩子存在歧义，TTS 已降级" : "必要回复捕获签名未受支持，TTS 已降级"
            : knownBuild
                ? "游戏结构签名受支持；需以实际 NPC 回复确认本次运行捕获"
                : "检测到未登记的游戏构建，但回复方法参数语义符合已知 ABI；需实际捕获确认";
        return new TtsGameCompatibilityInspection(fingerprint, replyHook, promptHook, status);
    }

    private static string ReadBuildGuid(string gameRoot)
    {
        try
        {
            var path = Path.Combine(gameRoot, "WorldApart_Data", "boot.config");
            var match = GuidPattern.Match(File.ReadAllText(path));
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : "unknown";
        }
        catch { return "unavailable"; }
    }

    private static string HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream));
        }
        catch { return "unavailable"; }
    }
}
