using System.Reflection;

namespace A1IndexTTSMod;

internal enum NpcReplyCaptureAbi
{
    LegacyTwoStrings,
    PromptTurnThreeArguments
}

internal sealed record NpcReplyHookResolution(
    MethodInfo? Target,
    NpcReplyCaptureAbi? Abi,
    string Status,
    IReadOnlyList<string> ObservedSignatures,
    IReadOnlyList<string> IgnoredOverloads)
{
    public bool IsResolved => Target != null && Abi != null;
}

/// <summary>
/// Recognizes only the reply-capture signatures whose parameter meanings were checked
/// against game interop metadata. Unknown overloads are reported, never patched by name.
/// </summary>
internal static class NpcReplyHookCompatibility
{
    internal static NpcReplyHookResolution Resolve(IEnumerable<MethodInfo> methods)
    {
        var named = methods.Where(method => method.Name == "AddNpcChatMessage").ToArray();
        var observed = named.Select(FormatSignature).ToArray();
        var recognized = named
            .Select(method => (Method: method, Abi: Classify(method)))
            .Where(candidate => candidate.Abi != null)
            .ToArray();
        var ignored = named
            .Where(method => Classify(method) == null)
            .Select(FormatSignature)
            .ToArray();

        if (recognized.Length == 0)
            return new(null, null, named.Length == 0 ? "missing" : "unsupported", observed, ignored);

        // A build that exposes both known contracts is ambiguous: neither can be assumed
        // to be the sole canonical game entrypoint without runtime evidence.
        if (recognized.Length != 1)
            return new(null, null, "ambiguous", observed, ignored);

        return new(recognized[0].Method, recognized[0].Abi,
            ignored.Length == 0 ? "supported" : "supported_with_unknown_overloads",
            observed, ignored);
    }

    internal static NpcReplyCaptureAbi? Classify(MethodInfo method)
    {
        if (method.Name != "AddNpcChatMessage" || method.ReturnType != typeof(void)) return null;
        var parameters = method.GetParameters();
        if (parameters.Length == 2 &&
            IsString(parameters[0], "messageText") &&
            IsString(parameters[1], "npcRawOutput"))
            return NpcReplyCaptureAbi.LegacyTwoStrings;

        if (parameters.Length == 3 &&
            IsString(parameters[0], "messageText") &&
            IsString(parameters[1], "npcRawOutput") &&
            parameters[2].ParameterType == typeof(int) &&
            parameters[2].Name == "promptTurnId")
            return NpcReplyCaptureAbi.PromptTurnThreeArguments;

        return null;
    }

    internal static string FormatSignature(MethodInfo method) =>
        $"{method.DeclaringType?.FullName ?? "?"}.{method.Name}({string.Join(",", method.GetParameters().Select(parameter =>
            $"{parameter.ParameterType.FullName ?? parameter.ParameterType.Name} {parameter.Name}"))})";

    private static bool IsString(ParameterInfo parameter, string expectedName) =>
        parameter.ParameterType == typeof(string) && parameter.Name == expectedName;
}
