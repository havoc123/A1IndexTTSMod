using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace A1IndexTTSMod;

internal sealed record TtsGameFingerprint(
    string BuildGuid,
    string MetadataSha256,
    string InteropGameSha256,
    string PluginSha256,
    string CaptureSignature,
    string PromptEnhancementStatus);

internal sealed record TtsDiagnosticIssue(
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    long Occurrences,
    string Stage,
    string ExceptionType,
    string Summary,
    string? Context,
    string? StackTrace);

internal sealed record TtsDiagnosticsSnapshot(
    string SessionId,
    DateTimeOffset SessionStartedUtc,
    bool PluginLoaded,
    string CompatibilityStatus,
    TtsGameFingerprint? Fingerprint,
    int RequiredHookInstalled,
    long RequiredHookFired,
    string RequiredHookStatus,
    string OptionalPromptHookStatus,
    string TtsSettingsSummary,
    IReadOnlyDictionary<string, long> Counts,
    DateTimeOffset? LastEventUtc,
    string? LastEvent,
    string? LastEventOutcome,
    string? LastEventContext,
    DateTimeOffset? LastSuccessUtc,
    string? LastSuccessStage,
    DateTimeOffset? LastFailureUtc,
    TtsDiagnosticIssue? LastFailure,
    IReadOnlyList<TtsDiagnosticIssue> RecentIssues,
    bool PreviousRunEndedUnexpectedly,
    string? PreviousRunSummary);

/// <summary>
/// Small, in-process, bounded TTS diagnostics store. All public entrypoints are best effort:
/// a logging or persistence failure must never escape into a game or Harmony callback.
/// </summary>
internal static class TtsDiagnostics
{
    private const int MaximumIssues = 24;
    private const int MaximumSummaryLength = 500;
    private const int MaximumStackLength = 6000;
    private static readonly object Gate = new();
    private static readonly Queue<TtsDiagnosticIssue> Issues = new();
    private static readonly Dictionary<string, long> Counts = new(StringComparer.Ordinal)
    {
        ["capture.hook_installed"] = 0,
        ["capture.received"] = 0,
        ["capture.parsed"] = 0,
        ["capture.filtered"] = 0,
        ["reply.received"] = 0,
        ["reply.tts_disabled"] = 0,
        ["reply.auto_read_off"] = 0,
        ["reply.filtered"] = 0,
        ["queue.accepted"] = 0,
        ["queue.duplicate_suppressed"] = 0,
        ["synthesis.started"] = 0,
        ["synthesis.succeeded"] = 0,
        ["synthesis.failed"] = 0,
        ["playback.started"] = 0,
        ["playback.device_selected"] = 0,
        ["playback.succeeded"] = 0,
        ["playback.failed"] = 0,
        ["voice.selected"] = 0,
        ["voice.fallback"] = 0,
        ["service.ready"] = 0,
        ["service.failed"] = 0,
        ["service_process.failed"] = 0,
        ["service_health.failed"] = 0,
        ["service_health.recovered"] = 0,
        ["cancellation.completed"] = 0,
        ["failures"] = 0,
        ["exceptions"] = 0
    };
    private static readonly Regex CredentialField = new(
        "(?i)([\\\"']?(?:authorization|api[_-]?key|access[_-]?token|token|secret|cookie|password|voice_ref|audio|messages|prompt|content|input|text|npcrawoutput)[\\\"']?\\s*[:=]\\s*)(?:\\\"(?:\\\\.|[^\\\"])*\\\"|'[^']*'|[^,\\s}\\]]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UrlCredential = new(
        "(?i)([?&](?:api[_-]?key|access[_-]?token|token|secret|key|password)=)[^&#\\s]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AuthorizationHeader = new(
        "(?i)(authorization\\s*[:=]\\s*)(?:bearer\\s+)?[^\\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BodyTail = new(
        "(?is)(response body|request body|body)\\s*[:=]\\s*.*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string _sessionId = "uninitialized";
    private static DateTimeOffset _sessionStartedUtc;
    private static bool _started;
    private static bool _pluginLoaded;
    private static string _compatibilityStatus = "尚未检查";
    private static TtsGameFingerprint? _fingerprint;
    private static int _requiredHookInstalled;
    private static long _requiredHookFired;
    private static string _requiredHookStatus = "尚未检查";
    private static string _optionalPromptHookStatus = "尚未检查";
    private static string _ttsSettingsSummary = "尚未配置";
    private static DateTimeOffset? _lastSuccessUtc;
    private static string? _lastSuccessStage;
    private static DateTimeOffset? _lastEventUtc;
    private static string? _lastEvent;
    private static string? _lastEventOutcome;
    private static string? _lastEventContext;
    private static DateTimeOffset? _lastFailureUtc;
    private static TtsDiagnosticIssue? _lastFailure;
    private static string? _storageDirectory;
    private static bool _previousRunEndedUnexpectedly;
    private static string? _previousRunSummary;

    internal static void StartSession(string gameRoot, string compatibilityStatus,
        TtsGameFingerprint fingerprint, Action<string>? logInfo = null, Action<string>? logWarning = null)
    {
        try
        {
            lock (Gate)
            {
                if (_started) return;
                _started = true;
                _sessionId = Guid.NewGuid().ToString("N");
                _sessionStartedUtc = DateTimeOffset.UtcNow;
                _pluginLoaded = true;
                _compatibilityStatus = SafeValue(compatibilityStatus, 500);
                _fingerprint = fingerprint;
                _storageDirectory = Path.Combine(gameRoot, "A1IndexTTSMod", ".state", "tts-diagnostics");
                ReadPreviousStateUnsafe(logWarning);
                WriteRunMarkerUnsafe("running", null);
            }
            Record("plugin", "loaded", "插件初始化完成");
            if (logInfo != null) SafeLog(() => logInfo($"TTS diagnostics session={_sessionId} compatibility={_compatibilityStatus}; fingerprints recorded, dialogue text capture disabled."));
            if (_previousRunEndedUnexpectedly && logWarning != null)
                SafeLog(() => logWarning("上次 TTS 会话没有正常结束；现有记录不足以确定退出原因，不能据此认定为 TTS 导致。"));
        }
        catch
        {
            // Diagnostics initialization is deliberately non-fatal.
        }
    }

    internal static void SetCompatibility(string status, TtsGameFingerprint fingerprint)
    {
        try
        {
            lock (Gate)
            {
                _compatibilityStatus = SafeValue(status, 500);
                _fingerprint = fingerprint;
            }
        }
        catch { }
    }

    internal static void SetRequiredHook(int installed, string status)
    {
        try
        {
            lock (Gate)
            {
                _requiredHookInstalled = Math.Max(0, installed);
                _requiredHookStatus = SafeValue(status, 300);
            }
            if (installed > 0) Increment("capture.hook_installed");
        }
        catch { }
    }

    internal static void SetOptionalPromptHook(string status)
    {
        try { lock (Gate) _optionalPromptHookStatus = SafeValue(status, 300); }
        catch { }
    }

    internal static void SetTtsSettingsSummary(string summary)
    {
        try { lock (Gate) _ttsSettingsSummary = SafeValue(summary, 300); }
        catch { }
    }

    internal static bool RequiredHookAvailable
    {
        get { try { lock (Gate) return _requiredHookInstalled > 0; } catch { return false; } }
    }

    internal static string RequiredHookStatus
    {
        get { try { lock (Gate) return _requiredHookStatus; } catch { return "状态读取失败"; } }
    }

    internal static void RecordCapture(int promptTurnId, string? npcKey)
    {
        try
        {
            lock (Gate) _requiredHookFired++;
            Record("capture", "received", $"NPC={SafeValue(npcKey, 40)} promptTurnId={promptTurnId}");
        }
        catch { }
    }

    internal static void RecordServiceProcessFailure(string summary, string context)
    {
        try
        {
            Record("service_process", "failed", context,
                new InvalidOperationException(Sanitize(summary, MaximumSummaryLength)));
        }
        catch { }
    }

    internal static void RecordServiceHealthFailure(string summary, string context)
    {
        try { Record("service_health", "failed", context, new InvalidOperationException(Sanitize(summary, MaximumSummaryLength))); }
        catch { }
    }

    internal static void RecordParse(bool parsed, int promptTurnId, string? npcKey, string? reason = null)
    {
        try
        {
            var context = $"NPC={SafeValue(npcKey, 40)} promptTurnId={promptTurnId}";
            if (parsed) Record("capture", "parsed", context);
            else Record("capture", "filtered", context + "; reason=" + SafeValue(reason, 100));
        }
        catch { }
    }

    internal static void Record(string stage, string outcome, string? context = null, Exception? exception = null)
    {
        try
        {
            var key = stage + "." + outcome;
            TtsDiagnosticIssue? persist = null;
            lock (Gate)
            {
                if (Counts.ContainsKey(key)) Counts[key]++;
                _lastEventUtc = DateTimeOffset.UtcNow;
                _lastEvent = SafeValue(stage, 80);
                _lastEventOutcome = SafeValue(outcome, 80);
                _lastEventContext = SafeValue(context, 250);
                if (exception == null && IsSuccessOutcome(key))
                {
                    _lastSuccessUtc = _lastEventUtc;
                    _lastSuccessStage = key;
                }
                if (exception != null || outcome == "failed" || outcome == "error")
                {
                    var now = DateTimeOffset.UtcNow;
                    var error = MakeIssue(now, stage, exception, context);
                    _lastFailureUtc = now;
                    if (Counts.ContainsKey("failures")) Counts["failures"]++;
                    if (exception != null && Counts.ContainsKey("exceptions")) Counts["exceptions"]++;
                    if (AggregateIssueUnsafe(error, out var aggregate))
                        persist = aggregate;
                    _lastFailure = aggregate;
                }
            }
            if (persist != null) _ = Task.Run(() => PersistLastFailure(persist));
        }
        catch { }
    }

    internal static void MarkNormalExit()
    {
        try
        {
            lock (Gate)
            {
                if (!_started || string.IsNullOrWhiteSpace(_storageDirectory)) return;
                WriteRunMarkerUnsafe("normal", DateTimeOffset.UtcNow);
            }
        }
        catch { }
    }

    internal static TtsDiagnosticsSnapshot Snapshot()
    {
        try
        {
            lock (Gate)
                return new TtsDiagnosticsSnapshot(_sessionId, _sessionStartedUtc, _pluginLoaded,
                    _compatibilityStatus, _fingerprint, _requiredHookInstalled, _requiredHookFired,
                    _requiredHookStatus, _optionalPromptHookStatus, _ttsSettingsSummary,
                    new Dictionary<string, long>(Counts, StringComparer.Ordinal), _lastEventUtc,
                    _lastEvent, _lastEventOutcome, _lastEventContext, _lastSuccessUtc,
                    _lastSuccessStage, _lastFailureUtc, _lastFailure, Issues.ToArray(),
                    _previousRunEndedUnexpectedly, _previousRunSummary);
        }
        catch
        {
            return new TtsDiagnosticsSnapshot(_sessionId, _sessionStartedUtc, _pluginLoaded,
                "诊断暂不可用", _fingerprint, _requiredHookInstalled, _requiredHookFired,
                _requiredHookStatus, _optionalPromptHookStatus, _ttsSettingsSummary, new Dictionary<string, long>(),
                _lastEventUtc, _lastEvent, _lastEventOutcome, _lastEventContext,
                _lastSuccessUtc, _lastSuccessStage, _lastFailureUtc, _lastFailure,
                Array.Empty<TtsDiagnosticIssue>(), _previousRunEndedUnexpectedly, _previousRunSummary);
        }
    }

    internal static string BuildReport()
    {
        try
        {
            return JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return "{\"diagnostics\":\"report serialization failed\"}";
        }
    }

    internal static string? ExportReport()
    {
        try
        {
            string directory;
            lock (Gate) directory = _storageDirectory ?? throw new InvalidOperationException("诊断存储尚未初始化。");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "report-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + ".json");
            File.WriteAllText(path, BuildReport(), new UTF8Encoding(false));
            foreach (var stale in Directory.GetFiles(directory, "report-*.json")
                         .Select(file => new FileInfo(file)).OrderByDescending(file => file.CreationTimeUtc).Skip(5))
            {
                try { File.Delete(stale.FullName); } catch { }
            }
            return path;
        }
        catch { return null; }
    }

    internal static string Sanitize(string? value, int maxLength = MaximumSummaryLength)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var safe = BodyTail.Replace(value, "$1=[已隐藏]");
            safe = AuthorizationHeader.Replace(safe, "$1[已隐藏]");
            safe = CredentialField.Replace(safe, "$1[已隐藏]");
            safe = UrlCredential.Replace(safe, "$1[已隐藏]");
            safe = safe.Replace("\r", " ").Replace("\n", " ").Trim();
            return safe.Length <= maxLength ? safe : safe[..maxLength] + "…[截断]";
        }
        catch { return "[摘要不可用]"; }
    }

    private static bool IsSuccessOutcome(string key) => key is
        "plugin.loaded" or "compatibility.ready" or "service.ready" or "service_health.recovered" or "synthesis.succeeded" or "playback.succeeded";

    private static void Increment(string key)
    {
        try { lock (Gate) if (Counts.ContainsKey(key)) Counts[key]++; }
        catch { }
    }

    private static TtsDiagnosticIssue MakeIssue(DateTimeOffset now, string stage, Exception? exception, string? context)
    {
        var type = exception?.GetType().FullName ?? "Failure";
        var summary = exception == null ? "操作失败" : Sanitize(exception.Message);
        string? stack = null;
        try
        {
            if (exception?.StackTrace is { Length: > 0 } rawStack)
            {
                stack = UrlCredential.Replace(rawStack, "$1[已隐藏]");
                stack = stack.Replace("\r", "");
                if (stack.Length > MaximumStackLength) stack = stack[..MaximumStackLength] + "\n…[截断]";
            }
        }
        catch { }
        return new TtsDiagnosticIssue(now, now, 1, SafeValue(stage, 80), SafeValue(type, 200),
            summary, Sanitize(context, 200), stack);
    }

    private static bool AggregateIssueUnsafe(TtsDiagnosticIssue issue, out TtsDiagnosticIssue aggregate)
    {
        var prior = Issues.LastOrDefault(candidate => candidate.Stage == issue.Stage &&
            candidate.ExceptionType == issue.ExceptionType && candidate.Summary == issue.Summary);
        if (prior != null)
        {
            aggregate = prior with { LastSeenUtc = issue.LastSeenUtc, Occurrences = prior.Occurrences + 1 };
            var kept = Issues.Where(candidate => !ReferenceEquals(candidate, prior)).ToArray();
            Issues.Clear();
            foreach (var candidate in kept) Issues.Enqueue(candidate);
            Issues.Enqueue(aggregate);
            return false;
        }
        Issues.Enqueue(issue);
        while (Issues.Count > MaximumIssues) Issues.Dequeue();
        aggregate = issue;
        return true;
    }

    private static void ReadPreviousStateUnsafe(Action<string>? logWarning)
    {
        if (string.IsNullOrWhiteSpace(_storageDirectory)) return;
        try
        {
            Directory.CreateDirectory(_storageDirectory);
            var markerPath = Path.Combine(_storageDirectory, "run-marker.json");
            if (File.Exists(markerPath))
            {
                using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
                var root = marker.RootElement;
                var state = root.TryGetProperty("state", out var value) ? value.GetString() : null;
                if (string.Equals(state, "running", StringComparison.Ordinal))
                {
                    _previousRunEndedUnexpectedly = true;
                    var started = root.TryGetProperty("startedUtc", out var startedValue) ? startedValue.GetString() : null;
                    _previousRunSummary = "上次会话标记为运行中后退出；原因未确认" + (started == null ? "" : "，开始于 " + started);
                    logWarning?.Invoke("检测到上次运行标记仍为 running；这只说明没有记录到正常退出，不证明 TTS 导致进程退出。");
                }
            }
            var failurePath = Path.Combine(_storageDirectory, "last-error.json");
            if (File.Exists(failurePath))
            {
                var saved = JsonSerializer.Deserialize<TtsDiagnosticIssue>(File.ReadAllText(failurePath));
                if (saved != null)
                {
                    _lastFailure = saved with
                    {
                        Summary = Sanitize(saved.Summary),
                        Context = Sanitize(saved.Context, 200),
                        StackTrace = saved.StackTrace == null ? null : UrlCredential.Replace(saved.StackTrace, "$1[已隐藏]")
                    };
                    _lastFailureUtc = saved.LastSeenUtc;
                    if (_previousRunEndedUnexpectedly) _previousRunSummary += $"；最近已记录阶段 {saved.Stage}（{saved.ExceptionType}），不等同于崩溃原因";
                }
            }
        }
        catch
        {
            _previousRunEndedUnexpectedly = true;
            _previousRunSummary = "上次会话记录不可读";
        }
    }

    private static void WriteRunMarkerUnsafe(string state, DateTimeOffset? endedUtc)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_storageDirectory)) return;
            Directory.CreateDirectory(_storageDirectory);
            var path = Path.Combine(_storageDirectory, "run-marker.json");
            var marker = new
            {
                sessionId = _sessionId,
                state,
                startedUtc = _sessionStartedUtc,
                endedUtc,
                pluginLoaded = _pluginLoaded,
                compatibility = _compatibilityStatus,
                fingerprint = _fingerprint
            };
            File.WriteAllText(path, JsonSerializer.Serialize(marker), new UTF8Encoding(false));
        }
        catch { }
    }

    private static void PersistLastFailure(TtsDiagnosticIssue issue)
    {
        try
        {
            string? directory;
            lock (Gate) directory = _storageDirectory;
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "last-error.json");
            File.WriteAllText(path, JsonSerializer.Serialize(issue), new UTF8Encoding(false));
        }
        catch { }
    }

    private static string SafeValue(string? value, int maximum) => Sanitize(value, maximum);

    private static void SafeLog(Action action)
    {
        try { action(); } catch { }
    }
}
