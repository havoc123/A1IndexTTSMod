using A1IndexTTSMod;
using System.Reflection;
using System.Runtime.Loader;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

void CheckNpcCaptureAbi()
{
    var legacy = NpcReplyHookCompatibility.Resolve(typeof(LegacyNpcModel).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    Check(legacy.IsResolved && legacy.Abi == NpcReplyCaptureAbi.LegacyTwoStrings, "known legacy two-string ABI did not resolve");

    var updated = NpcReplyHookCompatibility.Resolve(typeof(UpdatedNpcModel).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    Check(updated.IsResolved && updated.Abi == NpcReplyCaptureAbi.PromptTurnThreeArguments, "known promptTurnId ABI did not resolve");

    var missing = NpcReplyHookCompatibility.Resolve(typeof(NoNpcModel).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    Check(!missing.IsResolved && missing.Status == "missing", "missing target was not reported as unavailable");

    var unknown = NpcReplyHookCompatibility.Resolve(typeof(UnknownNpcModel).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    Check(!unknown.IsResolved && unknown.Status == "unsupported", "unknown same-name overload was patched");

    var extraArgument = NpcReplyHookCompatibility.Resolve(typeof(ExtraArgumentNpcModel).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    Check(!extraArgument.IsResolved && extraArgument.Status == "unsupported", "unreviewed additional argument was treated as a known ABI");

    var ambiguous = NpcReplyHookCompatibility.Resolve(typeof(AmbiguousNpcModel).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    Check(!ambiguous.IsResolved && ambiguous.Status == "ambiguous", "coexisting known ABI variants were not downgraded safely");

    var knownWithUnknown = NpcReplyHookCompatibility.Resolve(typeof(KnownAndUnknownNpcModel).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    Check(knownWithUnknown.IsResolved && knownWithUnknown.Status == "supported_with_unknown_overloads" && knownWithUnknown.IgnoredOverloads.Count == 1,
        "known ABI plus unknown overload did not select only the known semantic target");

    var wrongNames = NpcReplyHookCompatibility.Resolve(typeof(WrongNamesNpcModel).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    Check(!wrongNames.IsResolved && wrongNames.Status == "unsupported", "parameter-name semantic mapping accepted an unknown parameter contract");
    Console.WriteLine("NPC capture ABI matrix passed: missing, legacy, promptTurnId, unknown, extra parameter, ambiguous, and ignored overload.");
}

void CheckPromptHookAbi()
{
    var legacy = PromptEnhancementHookCompatibility.Resolve(typeof(LegacyPromptRuntime).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    Check(legacy.IsResolved && legacy.Abi == PromptEnhancementAbi.LegacyTenArguments, "known legacy prompt ABI did not resolve");
    var current = PromptEnhancementHookCompatibility.Resolve(typeof(CurrentPromptRuntime).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    Check(current.IsResolved && current.Abi == PromptEnhancementAbi.ContextElevenArguments, "current context prompt ABI did not resolve");
    var unknown = PromptEnhancementHookCompatibility.Resolve(typeof(UnknownPromptRuntime).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    Check(!unknown.IsResolved && unknown.Status == "unsupported", "unknown prompt ABI was not reported as optional unsupported");
    Console.WriteLine("Optional prompt enhancement ABI matrix passed: legacy, context argument, and unknown signature.");
}

void CheckTurnAssociation()
{
    const string sameText = "同一句内容";
    var first = DisplayTurnIdentity.Create("npc:100211", "1712345678901");
    var duplicateCallback = DisplayTurnIdentity.Create("npc:100211", "1712345678901");
    var anotherEventWithSameText = DisplayTurnIdentity.Create("npc:100211", "1712345678902");
    var otherNpcSameTimestamp = DisplayTurnIdentity.Create("npc:100212", "1712345678901");
    Check(sameText == "同一句内容" && first == duplicateCallback, "same native event did not receive the same dedupe identity");
    Check(first != anotherEventWithSameText, "different NPC events with identical text collided");
    Check(first != otherNpcSameTimestamp, "parallel NPCs with identical native timestamps crossed identities");
    Check(!DisplayStyleDedupPolicy.ShouldUpgrade(false, false, true, false), "same-event callback without new style was not deduplicated");
    Console.WriteLine("Turn association regression passed: duplicate event stable; same text and parallel NPCs remain separate.");
}

void CheckCrashClassification()
{
    var windowsOom = TtsServiceCrashClassifier.Classify(unchecked((int)0xC0000017), "", "", "", "");
    Check(windowsOom.Category == "windows_resource_exhaustion" && windowsOom.Confidence == "high",
        "Windows STATUS_NO_MEMORY exit was not classified as resource exhaustion");

    var allocatorOom = TtsServiceCrashClassifier.Classify(1, "terminate called after throwing std::bad_alloc", "", "", "");
    Check(allocatorOom.Category == "probable_oom" && allocatorOom.Confidence == "high",
        "explicit native allocation failure was not classified as probable OOM");

    var accessViolation = TtsServiceCrashClassifier.Classify(1, "", "", "ExceptionCode=0xc0000005; Faulting module name=audio_backend.dll", "");
    Check(accessViolation.Category == "access_violation" && accessViolation.Confidence == "high" &&
          accessViolation.Summary.Contains("audio_backend.dll", StringComparison.Ordinal),
        "Windows Application Error exception code/module was not preferred over a generic exit code");

    var memoryPressure = TtsServiceCrashClassifier.Classify(1, "", "", "System resource event 2004 (matched PID 42): low virtual memory", "");
    Check(memoryPressure.Category == "system_memory_pressure" && memoryPressure.Confidence == "medium",
        "correlated low-memory event should be reported as pressure, not proven OOM termination");

    var unknown = TtsServiceCrashClassifier.Classify(1, "", "", "", "");
    Check(unknown.Category == "unknown_process_exit" && unknown.Confidence == "low",
        "unexplained nonzero process exit was assigned a cause");

    Console.WriteLine("TTS crash classification passed: Windows resource exhaustion, explicit OOM, access violation, correlated memory pressure, and unknown exit.");
}

void CheckDiagnostics()
{
    var root = Path.Combine(Path.GetTempPath(), "a1-tts-diagnostics-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var fingerprint = new TtsGameFingerprint("test-guid", "metadata-hash", "interop-hash", "plugin-hash", "capture-signature", "prompt-status");
        TtsDiagnostics.StartSession(root, "test compatibility", fingerprint, Console.WriteLine, Console.WriteLine);
        TtsDiagnostics.SetRequiredHook(1, "installed; waiting");
        TtsDiagnostics.RecordCapture(17, "npc:100211");
        TtsDiagnostics.RecordParse(true, 17, "npc:100211");
        TtsDiagnostics.Record("queue", "accepted", "NPC=npc:100211 promptTurnId=17");
        TtsDiagnostics.Record("service", "failed", "authorization=Bearer supersecret text=private dialogue",
            new HttpRequestException("request body: {\"api_key\":\"secret\",\"text\":\"private dialogue\"}"));

        var snapshot = TtsDiagnostics.Snapshot();
        Check(snapshot.RequiredHookInstalled == 1 && snapshot.RequiredHookFired == 1, "installed/fired coverage was not kept separately");
        Check(snapshot.Counts["capture.parsed"] == 1 && snapshot.Counts["queue.accepted"] == 1, "diagnostic phase counts were not recorded");
        var report = TtsDiagnostics.BuildReport();
        Check(!report.Contains("supersecret", StringComparison.Ordinal) &&
              !report.Contains("private dialogue", StringComparison.Ordinal) &&
              !report.Contains("\"secret\"", StringComparison.Ordinal), "diagnostic report retained credentials or dialogue text");

        for (var i = 0; i < 40; i++) TtsDiagnostics.Record("worker", "failed", "fixture " + i, new InvalidOperationException("worker-" + i));
        TtsDiagnostics.Record("worker", "failed", "duplicate fixture", new InvalidOperationException("same worker error"));
        TtsDiagnostics.Record("worker", "failed", "duplicate fixture", new InvalidOperationException("same worker error"));
        snapshot = TtsDiagnostics.Snapshot();
        Check(snapshot.RecentIssues.Count <= 24, "diagnostic issue ring exceeded its bound");
        Check(snapshot.RecentIssues.Last().Occurrences == 2, "repeated errors were not aggregated");

        var marker = Path.Combine(root, "A1IndexTTSMod", ".state", "tts-diagnostics", "run-marker.json");
        if (!File.Exists(marker))
        {
            var files = Directory.Exists(root) ? string.Join(";", Directory.GetFiles(root, "*", SearchOption.AllDirectories)) : "<root missing>";
            throw new InvalidOperationException("startup run marker was not persisted at " + marker + "; found=" + files);
        }
        Check(File.ReadAllText(marker).Contains("running", StringComparison.Ordinal), "startup run marker did not contain running state");
        TtsDiagnostics.MarkNormalExit();
        Check(File.ReadAllText(marker).Contains("normal", StringComparison.Ordinal), "normal-exit marker was not persisted");
        Console.WriteLine("Diagnostics regression passed: bounded ring, repeat aggregation, installed/fired counts, safe redaction, and run marker.");
    }
    finally
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}

void CheckInstalledInterop(string path)
{
    var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)))!;
    AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        foreach (var directory in new[] { Path.Combine(gameRoot, "BepInEx", "interop"), Path.Combine(gameRoot, "BepInEx", "core") })
        {
            var candidate = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(candidate))
            {
                try { return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate); } catch { }
            }
        }
        return null;
    };
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
    var npcType = assembly.GetType("Game.Model.NpcModel", throwOnError: true)!;
    var resolution = NpcReplyHookCompatibility.Resolve(npcType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    Check(resolution.IsResolved, "installed Game.dll does not expose a supported NPC reply hook: " + string.Join(";", resolution.ObservedSignatures));
    Console.WriteLine("Current installed Game.dll PASS: " + NpcReplyHookCompatibility.FormatSignature(resolution.Target!));
    var runtimeType = assembly.GetType("Game.Model.AinpcRuntime");
    if (runtimeType != null)
    {
        var prompt = PromptEnhancementHookCompatibility.Resolve(runtimeType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Console.WriteLine("Optional CompleteBudgeted ABI: " + prompt.Status + (prompt.Abi == null ? "" : " / " + prompt.Abi));
    }
}

CheckNpcCaptureAbi();
CheckPromptHookAbi();
CheckTurnAssociation();
CheckCrashClassification();
CheckDiagnostics();

var interopArgument = Array.IndexOf(args, "--game-interop");
if (interopArgument >= 0)
{
    Check(interopArgument + 1 < args.Length, "--game-interop requires a Game.dll path");
    CheckInstalledInterop(args[interopArgument + 1]);
}

Console.WriteLine("TTS compatibility and diagnostics checks passed.");

public sealed class LegacyNpcModel { public void AddNpcChatMessage(string messageText, string npcRawOutput) { } }
public sealed class UpdatedNpcModel { public void AddNpcChatMessage(string messageText, string npcRawOutput, int promptTurnId) { } }
public sealed class NoNpcModel { public void OtherMethod() { } }
public sealed class UnknownNpcModel { public void AddNpcChatMessage(string messageText, object npcRawOutput) { } }
public sealed class ExtraArgumentNpcModel { public void AddNpcChatMessage(string messageText, string npcRawOutput, int promptTurnId, bool includeHistory) { } }
public sealed class AmbiguousNpcModel
{
    public void AddNpcChatMessage(string messageText, string npcRawOutput) { }
    public void AddNpcChatMessage(string messageText, string npcRawOutput, int promptTurnId) { }
}
public sealed class KnownAndUnknownNpcModel
{
    public void AddNpcChatMessage(string messageText, string npcRawOutput, int promptTurnId) { }
    public void AddNpcChatMessage(string messageText, string npcRawOutput, int promptTurnId, bool includeHistory) { }
}
public sealed class WrongNamesNpcModel { public void AddNpcChatMessage(string text, string rawOutput, int requestId) { } }

public sealed class LegacyPromptRuntime
{
    public void CompleteBudgeted(Game.Model.NpcModel npc, JNGame.Ainpc.Prompt.ModeParams mode, string purpose, string prefix,
        string memory, Il2CppSystem.Collections.Generic.IReadOnlyList<JNGame.Ainpc.Llm.LlmMessage> history, string tail,
        string input, JNGame.Ainpc.Llm.LlmCallOptions options, JNGame.Ainpc.Prompt.PromptDocument doc) { }
}
public sealed class CurrentPromptRuntime
{
    public void CompleteBudgeted(Game.Model.NpcModel npc, JNGame.Ainpc.Prompt.ModeParams mode, string purpose, string prefix,
        string memory, string context, Il2CppSystem.Collections.Generic.IReadOnlyList<JNGame.Ainpc.Llm.LlmMessage> history,
        string tail, string input, JNGame.Ainpc.Llm.LlmCallOptions options, JNGame.Ainpc.Prompt.PromptDocument doc) { }
}
public sealed class UnknownPromptRuntime
{
    public void CompleteBudgeted(Game.Model.NpcModel npc, JNGame.Ainpc.Prompt.ModeParams mode, string purpose, string prefix,
        string memory, int newUnknownArgument, Il2CppSystem.Collections.Generic.IReadOnlyList<JNGame.Ainpc.Llm.LlmMessage> history,
        string tail, string input, JNGame.Ainpc.Llm.LlmCallOptions options, JNGame.Ainpc.Prompt.PromptDocument doc) { }
}

namespace Game.Model
{
    public sealed class NpcModel { }
}
namespace JNGame.Ainpc.Prompt
{
    public sealed class ModeParams { }
    public sealed class PromptDocument { }
}
namespace JNGame.Ainpc.Llm
{
    public sealed class LlmCallOptions { }
    public sealed class LlmMessage { }
}
namespace Il2CppSystem.Collections.Generic
{
    public interface IReadOnlyList<T> { }
}
