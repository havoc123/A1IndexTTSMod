using System.Diagnostics;
using System.Net.Sockets;
using BepInEx;
using BepInEx.Logging;

namespace A1IndexTTSMod;

// Tracks only an audio.cpp supervisor created by this plugin instance.
internal static class AudioCppLifecycle
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _readyFile;
    private static string? _failedFile;
    private static string? _stopFile;
    private static Process? _supervisor;
    private static bool _owned;
    private static int _ownedPort;
    private static string _statusMessage = "Not started";
    public static string StatusMessage { get { lock (Gate) return _statusMessage; } }

    public static async Task EnsureStartedAsync(ManualLogSource log, Uri endpoint, TtsBackend backend, string modelId, bool autoStart, string precision, string gpuBackend, int gpuDevice, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (await IsListeningAsync(endpoint.Host, endpoint.Port, token).ConfigureAwait(false))
            {
                var stillOwnsThisEndpoint = _owned && _supervisor is { HasExited: false } && _ownedPort == endpoint.Port;
                if (!stillOwnsThisEndpoint)
                {
                    _owned = false;
                    _statusMessage = "Connected to externally managed TTS service";
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
            if (!gpuBackend.Equals("Nvidia", StringComparison.OrdinalIgnoreCase) && !gpuBackend.Equals("Vulkan", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("GpuBackend must be Nvidia or Vulkan.");
            if (gpuDevice is < 0 or > 15) throw new InvalidOperationException("GpuDevice must be between 0 and 15.");
            if ((backend == TtsBackend.IndexTtsAudioCpp && (modelId != "indextts25" || precision is not ("q8_0" or "f16" or "orig"))) ||
                (backend == TtsBackend.CosyVoiceAudioCpp && modelId != "cosyvoice3") ||
                backend == TtsBackend.IndexTtsLegacyApi)
                throw new InvalidOperationException("Unsupported audio.cpp model ID or precision.");

            var project = Path.Combine(Paths.GameRootPath, "A1IndexTTSMod");
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
            _failedFile = Path.Combine(state, prefix + ".failed.txt");
            _stopFile = Path.Combine(state, prefix + ".stop");
            File.Delete(_readyFile);
            File.Delete(_failedFile);
            File.Delete(_stopFile);
            var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -GamePid {Environment.ProcessId} -InstanceId {instance} -Backend {(backend == TtsBackend.CosyVoiceAudioCpp ? "cosyvoice3" : "index_tts2")} -Precision {precision} -GpuBackend {gpuBackend} -GpuDevice {gpuDevice} -Port {endpoint.Port}",
                WorkingDirectory = project,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("PowerShell supervisor did not start.");
            _supervisor = process;
            _ownedPort = endpoint.Port;
            _owned = true;
            _statusMessage = "Starting audio.cpp and prewarming model";
            log.LogInfo($"Stage3 audio.cpp plugin-owned supervisor started pid={process.Id}, precision={precision}, GPU={gpuBackend}:{gpuDevice}.");
            try
            {
                while (!File.Exists(_readyFile))
                {
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(_failedFile)) throw new InvalidOperationException("audio.cpp failed to start: " + File.ReadAllText(_failedFile));
                    if (process.HasExited) throw new InvalidOperationException($"audio.cpp supervisor exited before the service became ready (exit={process.ExitCode}; failure marker: {_failedFile}).");
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
                _statusMessage = "audio.cpp ready (owned by this plugin)";
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
        if (!_owned)
        {
            _statusMessage = "No plugin-owned TTS service to stop";
            return;
        }
        var stop = _stopFile;
        var supervisor = _supervisor;
        if (stop != null) await File.WriteAllTextAsync(stop, "stop").ConfigureAwait(false);
        if (supervisor != null)
        {
            for (int i = 0; i < 120 && !supervisor.HasExited; i++) await Task.Delay(250).ConfigureAwait(false);
            if (!supervisor.HasExited) throw new TimeoutException("audio.cpp supervisor did not stop within 30 seconds.");
            supervisor.Dispose();
            for (int i = 0; i < 40 && await IsListeningAsync("127.0.0.1", _ownedPort, CancellationToken.None).ConfigureAwait(false); i++)
                await Task.Delay(250).ConfigureAwait(false);
            if (await IsListeningAsync("127.0.0.1", _ownedPort, CancellationToken.None).ConfigureAwait(false))
                throw new TimeoutException("audio.cpp supervisor exited but its owned port is still listening; ownership is retained for retry.");
        }
        _owned = false;
        _supervisor = null;
        _statusMessage = "Plugin-owned audio.cpp stopped";
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
