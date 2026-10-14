using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;

namespace A1IndexTTSMod;

// Tracks only an audio.cpp supervisor created by this plugin instance.
internal static class AudioCppLifecycle
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static string? _readyFile;
    private static string? _failedFile;
    private static string? _stopFile;
    private static string? _stdoutLogFile;
    private static string? _stderrLogFile;
    private static Process? _supervisor;
    private static Process? _serverProcess;
    private static CancellationTokenSource? _monitorCancellation;
    private static ManualLogSource? _log;
    private static bool _owned;
    private static bool _expectedStop;
    private static bool _failureReported;
    private static string _ownedHost = "127.0.0.1";
    private static int _ownedPort;
    private static string _statusMessage = "Not started";
    public static string StatusMessage { get { lock (Gate) return _statusMessage; } }

    public static async Task EnsureStartedAsync(ManualLogSource log, Uri endpoint, TtsBackend backend, string modelId, bool autoStart, string precision, string gpuBackend, int gpuDevice, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _log = log;
            if (_failureReported) await StopOwnedCoreAsync().ConfigureAwait(false);
            if (await IsListeningAsync(endpoint.Host, endpoint.Port, token).ConfigureAwait(false))
            {
                var stillOwnsThisEndpoint = _owned && _supervisor is { HasExited: false } && _ownedPort == endpoint.Port;
                if (!stillOwnsThisEndpoint)
                {
                    _owned = false;
                    _ownedPort = endpoint.Port;
                    _failureReported = false;
                    _statusMessage = "Connected to externally managed TTS service";
                    StartExternalHealthMonitor(endpoint);
                    log.LogInfo("Stage3 audio.cpp detected on configured loopback port; treating it as externally managed and will not stop it.");
                }
                else
                {
                    _statusMessage = "Connected to plugin-owned TTS service";
                    log.LogInfo("Stage3 audio.cpp is already ready and still belongs to this plugin supervisor.");
                }
                return;
            }
            if (!autoStart) throw new InvalidOperationException("TTS endpoint is unavailable and AutoStartAudioCpp is disabled.");
            var project = Path.Combine(Paths.GameRootPath, "A1IndexTTSMod");
            var adapters = GpuRouting.Adapters.Value;
            // Migrate the legacy CUDA default on AMD-only systems. Explicit
            // viable routes remain respected, including mixed GPU systems.
            if (gpuBackend.Equals("Auto", StringComparison.OrdinalIgnoreCase) ||
                (gpuBackend.Equals("Nvidia", StringComparison.OrdinalIgnoreCase) && !adapters.Any(g => g.Vendor == 0x10de) && adapters.Any(g => g.Vendor == 0x1002)))
            {
                var route = await GpuRouting.ResolveTtsAsync(project, token).ConfigureAwait(false);
                gpuBackend = route.Provider == "cuda" ? "Nvidia" : "Vulkan"; gpuDevice = route.Device;
                log.LogInfo($"TTS GPU selected: {gpuBackend}:{gpuDevice} {route.Name}");
            }
            if (!gpuBackend.Equals("Nvidia", StringComparison.OrdinalIgnoreCase) && !gpuBackend.Equals("Vulkan", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("GpuBackend must be Nvidia or Vulkan.");
            if (gpuDevice is < 0 or > 15) throw new InvalidOperationException("GpuDevice must be between 0 and 15.");
            if ((backend == TtsBackend.IndexTtsAudioCpp && (modelId != "indextts25" || precision is not ("q8_0" or "f16" or "orig"))) ||
                (backend == TtsBackend.CosyVoiceAudioCpp && modelId != "cosyvoice3") ||
                backend == TtsBackend.IndexTtsLegacyApi)
                throw new InvalidOperationException("Unsupported audio.cpp model ID or precision.");

            var script = Path.Combine(project, "scripts", "Run-AudioCppForGame.ps1");
            var server = Path.Combine(project, ".cache", "audiocpp", "runtime", gpuBackend.Equals("Vulkan", StringComparison.OrdinalIgnoreCase) ? "audiocpp_server-vulkan.exe" : "audiocpp_server.exe");
            var model = backend == TtsBackend.CosyVoiceAudioCpp
                ? Path.Combine(project, ".cache", "audiocpp", "models", "CosyVoice3-GGUF", "cosyvoice3-q8_0.gguf")
                : Path.Combine(project, ".cache", "audiocpp", "models", "IndexTTS2.5-GGUF", $"index-tts2_5-{precision}.gguf");
            var reference = Path.Combine(project, "references", "demo.wav");
            if (!File.Exists(script) || !File.Exists(server) || !File.Exists(model) || !File.Exists(reference))
                throw new FileNotFoundException("audio.cpp supervisor, executable, model, or reference WAV is missing.");

            var state = Path.Combine(project, ".state", "audiocpp");
            Directory.CreateDirectory(state);
            string instance = Guid.NewGuid().ToString("N");
            string prefix = $"game-{Environment.ProcessId}-{instance}";
            _readyFile = Path.Combine(state, prefix + ".ready");
            _failedFile = Path.Combine(state, prefix + ".failed.json");
            _stopFile = Path.Combine(state, prefix + ".stop");
            _stdoutLogFile = Path.Combine(state, prefix + ".stdout.log");
            _stderrLogFile = Path.Combine(state, prefix + ".stderr.log");
            File.Delete(_readyFile);
            File.Delete(_failedFile);
            File.Delete(_stopFile);
            File.Delete(_stdoutLogFile);
            File.Delete(_stderrLogFile);
            _failureReported = false;
            _expectedStop = false;
            var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -GamePid {Environment.ProcessId} -InstanceId {instance} -Backend {(backend == TtsBackend.CosyVoiceAudioCpp ? "cosyvoice3" : "index_tts2")} -Precision {precision} -GpuBackend {gpuBackend} -GpuDevice {gpuDevice} -Port {endpoint.Port}",
                WorkingDirectory = project,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("PowerShell supervisor did not start.");
            _supervisor = process;
            _ownedHost = endpoint.Host;
            _ownedPort = endpoint.Port;
            _owned = true;
            _statusMessage = "Starting audio.cpp and prewarming model";
            log.LogInfo($"Stage3 audio.cpp plugin-owned supervisor started pid={process.Id}, precision={precision}, GPU={gpuBackend}:{gpuDevice}.");
            try
            {
                while (!File.Exists(_readyFile))
                {
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(_failedFile)) throw new InvalidOperationException("audio.cpp failed to start: " + ReadFailureMessage(_failedFile));
                    if (process.HasExited)
                    {
                        for (var attempt = 0; attempt < 8 && !File.Exists(_failedFile) && !token.IsCancellationRequested; attempt++)
                            await Task.Delay(250, token).ConfigureAwait(false);
                        var details = File.Exists(_failedFile) ? ReadFailureMessage(_failedFile) : $"supervisor exit={process.ExitCode}; failure report unavailable";
                        throw new InvalidOperationException("audio.cpp supervisor exited before the service became ready: " + details);
                    }
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
                var serverPid = 0;
                for (var attempt = 0; attempt < 8 && serverPid == 0; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (File.Exists(_readyFile) && int.TryParse(File.ReadAllText(_readyFile).Trim(), out var parsedPid))
                            serverPid = parsedPid;
                    }
                    catch (IOException) { }
                    if (serverPid > 0) break;
                    if (File.Exists(_failedFile)) throw new InvalidOperationException("audio.cpp failed during readiness: " + ReadFailureMessage(_failedFile));
                    if (attempt < 7) await Task.Delay(100, token).ConfigureAwait(false);
                }
                if (serverPid <= 0) throw new InvalidDataException("audio.cpp supervisor readiness marker did not contain a valid service PID.");
                try { _serverProcess = Process.GetProcessById(serverPid); }
                catch (Exception exception)
                {
                    for (var attempt = 0; attempt < 8 && !File.Exists(_failedFile) && !token.IsCancellationRequested; attempt++)
                        await Task.Delay(250, token).ConfigureAwait(false);
                    var details = File.Exists(_failedFile) ? ReadFailureMessage(_failedFile) : $"service PID={serverPid} exited before it could be monitored";
                    throw new InvalidOperationException(details, exception);
                }
                _statusMessage = "audio.cpp ready (owned by this plugin)";
                _monitorCancellation = new CancellationTokenSource();
                var trackedServer = _serverProcess ?? throw new InvalidOperationException("TTS process monitor could not retain the service process handle.");
                var monitorToken = _monitorCancellation.Token;
                _ = Task.Run(() => MonitorOwnedServiceAsync(trackedServer, process, monitorToken));
            }
            catch
            {
                // A feature toggle may cancel startup while the server child is
                // still binding its port. Reap this exact supervisor before a
                // subsequent enable checks the endpoint, otherwise it can mistake
                // our half-started child for an external service.
                try { await StopOwnedCoreAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { log.LogWarning("Stage3 audio.cpp startup cleanup failed: " + cleanup.Message); }
                throw;
            }
        }
        finally { Gate.Release(); }
    }

    public static async Task StopOwnedAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopOwnedCoreAsync().ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static async Task StopOwnedCoreAsync()
    {
        var stop = _stopFile;
        var supervisor = _supervisor;
        if (!_owned && supervisor == null && _monitorCancellation == null)
        {
            _statusMessage = "No plugin-owned TTS service to stop";
            return;
        }
        _expectedStop = true;
        try { _monitorCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        if (_owned && stop != null) await File.WriteAllTextAsync(stop, "stop").ConfigureAwait(false);
        if (supervisor != null)
        {
            for (int i = 0; i < 120 && !supervisor.HasExited; i++) await Task.Delay(250).ConfigureAwait(false);
            if (!supervisor.HasExited) throw new TimeoutException("audio.cpp supervisor did not stop within 30 seconds.");
            supervisor.Dispose();
            var trackedServiceStillRunning = false;
            try { _serverProcess?.Refresh(); trackedServiceStillRunning = _serverProcess is { HasExited: false }; } catch { }
            if (trackedServiceStillRunning)
            {
                for (int i = 0; i < 40 && _ownedPort > 0 && await IsListeningAsync(_ownedHost, _ownedPort, CancellationToken.None).ConfigureAwait(false); i++)
                    await Task.Delay(250).ConfigureAwait(false);
                if (_ownedPort > 0 && await IsListeningAsync(_ownedHost, _ownedPort, CancellationToken.None).ConfigureAwait(false))
                    throw new TimeoutException("audio.cpp supervisor exited but its tracked service process is still listening.");
            }
            else if (_ownedPort > 0 && await IsListeningAsync(_ownedHost, _ownedPort, CancellationToken.None).ConfigureAwait(false))
                _log?.LogWarning("The tracked audio.cpp process exited, but the configured port remains in use by another/unidentified process; leaving it untouched.");
        }
        _serverProcess?.Dispose();
        _serverProcess = null;
        _monitorCancellation?.Dispose();
        _monitorCancellation = null;
        _owned = false;
        _supervisor = null;
        _ownedHost = "127.0.0.1";
        _ownedPort = 0;
        _expectedStop = false;
        _failureReported = false;
        _statusMessage = "TTS service monitor stopped";
    }

    private sealed record SupervisorFailureReport(string Kind, string Message, int? ProcessId,
        int? ExitCode, string? EventLogEvidence);

    private static void StartExternalHealthMonitor(Uri endpoint)
    {
        try { _monitorCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        _monitorCancellation?.Dispose();
        _monitorCancellation = new CancellationTokenSource();
        var token = _monitorCancellation.Token;
        _ = Task.Run(() => MonitorExternalServiceAsync(endpoint, token));
    }

    private static async Task MonitorExternalServiceAsync(Uri endpoint, CancellationToken token)
    {
        var failures = 0;
        var outageReported = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(5000, token).ConfigureAwait(false);
                if (await IsEndpointHealthyAsync(endpoint, token).ConfigureAwait(false))
                {
                    failures = 0;
                    if (outageReported)
                    {
                        outageReported = false;
                        _failureReported = false;
                        var recovered = "外部管理的 audio.cpp health endpoint 已恢复响应。";
                        TtsDiagnostics.Record("service_health", "recovered", $"endpoint={endpoint.Host}:{endpoint.Port}");
                        lock (Gate) _statusMessage = recovered;
                        SpeechMvp.MarkServiceRecovered(recovered);
                        _log?.LogInfo(recovered);
                    }
                    continue;
                }

                failures++;
                if (failures < 3 || outageReported) continue;
                outageReported = true;
                var summary = "外部管理的 audio.cpp health endpoint 连续 3 次无响应。插件无法取得该服务进程的退出码或日志，原因未确认。";
                var context = $"endpoint={endpoint.Host}:{endpoint.Port}; owner=external; process/log access=unavailable";
                TtsDiagnostics.RecordServiceHealthFailure(summary, context);
                _failureReported = true;
                lock (Gate) _statusMessage = summary;
                SpeechMvp.MarkServiceUnavailable(summary);
                _log?.LogWarning(summary);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { _log?.LogWarning("External TTS health monitor stopped: " + exception.GetType().Name); }
    }

    private static async Task<bool> IsEndpointHealthyAsync(Uri endpoint, CancellationToken token)
    {
        try
        {
            var health = new UriBuilder(endpoint.Scheme, endpoint.Host, endpoint.Port, "/health").Uri;
            using var response = await HealthClient.GetAsync(health, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task MonitorOwnedServiceAsync(Process server, Process supervisor, CancellationToken token)
    {
        var missedHealthChecks = 0;
        var endpointFailureReported = false;
        var nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(5);
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_expectedStop) return;
                var serviceExited = false;
                var supervisorExited = false;
                try { server.Refresh(); serviceExited = server.HasExited; } catch { serviceExited = true; }
                try { supervisor.Refresh(); supervisorExited = supervisor.HasExited; } catch { supervisorExited = true; }
                var failureFileExists = _failedFile != null && File.Exists(_failedFile);
                if (serviceExited || supervisorExited || failureFileExists)
                {
                    // Give the supervisor time to write its structured crash report and
                    // query Windows Error Reporting before falling back to local evidence.
                    for (var attempt = 0; attempt < 40 && _failedFile != null && !File.Exists(_failedFile); attempt++)
                    {
                        try { supervisor.Refresh(); supervisorExited = supervisor.HasExited; }
                        catch { supervisorExited = true; }
                        if (supervisorExited) break;
                        await Task.Delay(500, token).ConfigureAwait(false);
                    }
                    if (_expectedStop || token.IsCancellationRequested) return;
                    await ReportOwnedServiceFailureAsync(server, serviceExited, supervisorExited).ConfigureAwait(false);
                    return;
                }

                if (DateTimeOffset.UtcNow >= nextHealthCheck)
                {
                    nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(5);
                    var healthEndpoint = new UriBuilder(Uri.UriSchemeHttp, _ownedHost, _ownedPort, "/health").Uri;
                    if (await IsEndpointHealthyAsync(healthEndpoint, token).ConfigureAwait(false))
                    {
                        missedHealthChecks = 0;
                        if (endpointFailureReported)
                        {
                            endpointFailureReported = false;
                            _failureReported = false;
                            const string recovered = "插件管理的 audio.cpp 服务 health endpoint 已恢复响应。";
                            TtsDiagnostics.Record("service_health", "recovered", $"endpoint={_ownedHost}:{_ownedPort}; pid={SafeProcessId(server)}");
                            lock (Gate) _statusMessage = recovered;
                            SpeechMvp.MarkServiceRecovered(recovered);
                            _log?.LogInfo(recovered);
                        }
                    }
                    else missedHealthChecks++;
                    if (missedHealthChecks >= 3 && !endpointFailureReported)
                    {
                        endpointFailureReported = true;
                        var healthFailure = "audio.cpp 进程仍在运行，但 /health 连续 3 次无响应；进程可能卡住或监听器已停止。";
                        var stderr = ReadLogTail(_stderrLogFile);
                        var stdout = ReadLogTail(_stdoutLogFile);
                        var diagnosis = TtsServiceCrashClassifier.Classify(null, stderr, stdout, null, healthFailure);
                        var causeKnown = diagnosis.Category is "probable_oom" or "system_memory_pressure" or "windows_resource_exhaustion" or "gpu_or_driver_failure" or "model_load_failure";
                        var status = causeKnown
                            ? healthFailure + " 日志分类=" + diagnosis.Category + "；置信度=" + diagnosis.Confidence + "；" + diagnosis.Summary + "；证据=" + diagnosis.Evidence
                            : healthFailure + "；当前日志没有确认具体原因。";
                        var context = $"pid={SafeProcessId(server)}; endpoint={_ownedHost}:{_ownedPort}; logs={Path.GetFileName(_stderrLogFile ?? "")}";
                        TtsDiagnostics.RecordServiceHealthFailure(status, context);
                        lock (Gate) { _failureReported = true; _statusMessage = status; }
                        SpeechMvp.MarkServiceUnavailable(status);
                        _log?.LogError(status);
                    }
                }
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _log?.LogWarning("TTS service process monitor stopped: " + exception.GetType().Name);
        }
    }

    private static Task ReportOwnedServiceFailureAsync(Process server, bool serviceExited,
        bool supervisorExited)
    {
        if (_failureReported || _expectedStop) return Task.CompletedTask;
        var report = ReadFailureReport(_failedFile);
        int? exitCode = report?.ExitCode;
        int? processId = report?.ProcessId;
        string message = report?.Message ?? "audio.cpp 进程或 supervisor 在 TTS 服务就绪后意外退出。";
        if (!exitCode.HasValue && serviceExited)
        {
            try { server.Refresh(); if (server.HasExited) exitCode = server.ExitCode; } catch { }
        }
        processId ??= SafeProcessId(server);
        if (supervisorExited && !serviceExited && string.IsNullOrWhiteSpace(report?.Message))
            message = "audio.cpp supervisor 意外退出；服务进程仍可见，但后续生命周期不再受监控。";

        var stderr = ReadLogTail(_stderrLogFile);
        var stdout = ReadLogTail(_stdoutLogFile);
        var diagnosis = TtsServiceCrashClassifier.Classify(exitCode, stderr, stdout,
            report?.EventLogEvidence, message);
        var exitDisplay = exitCode.HasValue ? $"0x{unchecked((uint)exitCode.Value):X8} ({exitCode.Value})" : "不可用";
        var evidence = string.IsNullOrWhiteSpace(diagnosis.Evidence) ? "未找到明确错误行" : diagnosis.Evidence;
        var summary = $"原因分类={diagnosis.Category}；{diagnosis.Summary}；置信度={diagnosis.Confidence}；证据={evidence}";
        var context = $"pid={processId?.ToString() ?? "unknown"}; exit={exitDisplay}; kind={report?.Kind ?? "process_monitor"}; stderr={Path.GetFileName(_stderrLogFile ?? "")}; stdout={Path.GetFileName(_stdoutLogFile ?? "")}";
        TtsDiagnostics.RecordServiceProcessFailure(summary, context);
        var status = "audio.cpp 服务异常：" + diagnosis.Summary;
        lock (Gate) { _failureReported = true; _statusMessage = status; }
        SpeechMvp.MarkServiceUnavailable(status);
        _log?.LogError($"TTS service process failure pid={processId?.ToString() ?? "unknown"} exit={exitDisplay} category={diagnosis.Category} confidence={diagnosis.Confidence}: {diagnosis.Summary}; evidence={evidence}");
        return Task.CompletedTask;
    }

    private static SupervisorFailureReport? ReadFailureReport(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            string? GetString(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            int? pid = root.TryGetProperty("processId", out var pidValue) && pidValue.TryGetInt32(out var parsedPid) ? parsedPid : null;
            int? code = root.TryGetProperty("exitCode", out var codeValue) && codeValue.TryGetInt32(out var parsedCode) ? parsedCode : null;
            if (!code.HasValue && GetString("exitCodeHex") is { } hex)
            {
                var digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
                if (uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsedHex))
                    code = unchecked((int)parsedHex);
            }
            return new SupervisorFailureReport(GetString("kind") ?? "unknown", GetString("message") ?? "",
                pid, code, GetString("eventLogEvidence"));
        }
        catch { return null; }
    }

    private static string ReadFailureMessage(string path)
    {
        var report = ReadFailureReport(path);
        if (report == null) return TtsDiagnostics.Sanitize(File.ReadAllText(path), 600);
        var diagnosis = TtsServiceCrashClassifier.Classify(report.ExitCode, ReadLogTail(_stderrLogFile),
            ReadLogTail(_stdoutLogFile), report.EventLogEvidence, report.Message);
        var summary = "原因分类=" + diagnosis.Category + "；" + diagnosis.Summary + "；置信度=" + diagnosis.Confidence + "；证据=" + diagnosis.Evidence;
        var exitDisplay = report.ExitCode.HasValue ? $"0x{unchecked((uint)report.ExitCode.Value):X8} ({report.ExitCode.Value})" : "不可用";
        var context = $"pid={report.ProcessId?.ToString() ?? "unknown"}; exit={exitDisplay}; kind={report.Kind}; stderr={Path.GetFileName(_stderrLogFile ?? "")}; stdout={Path.GetFileName(_stdoutLogFile ?? "")}";
        TtsDiagnostics.RecordServiceProcessFailure(summary, context);
        return TtsDiagnostics.Sanitize(summary, 600);
    }

    internal static string DescribeRequestFailure(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: { } statusCode })
            return DescribeHttpFailure((int)statusCode);
        if (!_owned || string.IsNullOrWhiteSpace(_stderrLogFile))
            return "本地 TTS 请求连接失败；服务由外部管理，游戏无法读取它的进程退出码和日志，原因未确认。";

        var diagnosis = TtsServiceCrashClassifier.Classify(null, ReadLogTail(_stderrLogFile),
            ReadLogTail(_stdoutLogFile), null, "TTS endpoint connection failure");
        if (diagnosis.Category is "probable_oom" or "system_memory_pressure" or "windows_resource_exhaustion" or "gpu_or_driver_failure" or "model_load_failure")
            return TtsDiagnostics.Sanitize("TTS 请求连接失败；日志分类=" + diagnosis.Category + "；置信度=" + diagnosis.Confidence + "；" + diagnosis.Summary + "；证据=" + diagnosis.Evidence, 500);
        return "本地 TTS 请求连接失败；服务日志暂未确认具体原因。";
    }

    private static string DescribeHttpFailure(int statusCode)
    {
        var baseMessage = $"TTS 服务返回 HTTP {statusCode}。";
        if (!_owned || string.IsNullOrWhiteSpace(_stderrLogFile))
            return baseMessage + " 服务由外部管理，游戏无法读取其进程日志。";
        var diagnosis = TtsServiceCrashClassifier.Classify(null, ReadLogTail(_stderrLogFile),
            ReadLogTail(_stdoutLogFile), null, baseMessage);
        if (diagnosis.Category is not ("probable_oom" or "system_memory_pressure" or "windows_resource_exhaustion" or "gpu_or_driver_failure" or "model_load_failure"))
            return baseMessage + "；近期服务日志没有确认具体原因。";
        return TtsDiagnostics.Sanitize(baseMessage + "；日志分类=" + diagnosis.Category + "；置信度=" + diagnosis.Confidence + "；" + diagnosis.Summary + "；证据=" + diagnosis.Evidence, 500);
    }

    private static string ReadLogTail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - 16384);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join("\n", lines.TakeLast(24));
        }
        catch { return ""; }
    }

    private static int? SafeProcessId(Process process)
    {
        try { return process.Id; } catch { return null; }
    }

    private static async Task<bool> IsListeningAsync(string host, int port, CancellationToken token)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, token).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }
}
