using System.Diagnostics;
using BepInEx;
using BepInEx.Logging;

namespace A1IndexTTSMod;

// A visible supervisor console hosts audio.cpp and stops it when this game exits.
internal static class AudioCppLifecycle
{
    private static string? _readyFile;
    private static string? _failedFile;

    public static void Start(ManualLogSource log, Uri endpoint, string modelId, bool autoStart, string precision)
    {
        if (!autoStart || endpoint.AbsolutePath != "/v1/audio/speech") return;
        if (modelId != "indextts25" || precision is not ("q8_0" or "f16" or "orig"))
        {
            log.LogWarning("Stage3 audio.cpp auto-start skipped: unsupported model ID or precision.");
            return;
        }

        var project = Path.Combine(Paths.GameRootPath, "A1IndexTTSMod");
        var script = Path.Combine(project, "scripts", "Run-AudioCppForGame.ps1");
        var server = Path.Combine(project, ".cache", "audiocpp", "runtime", "audiocpp_server.exe");
        var model = Path.Combine(project, ".cache", "audiocpp", "models", "IndexTTS2.5-GGUF", $"index-tts2_5-{precision}.gguf");
        var reference = Path.Combine(project, "references", "demo.wav");
        if (!File.Exists(script) || !File.Exists(server) || !File.Exists(model) || !File.Exists(reference))
        {
            log.LogWarning("Stage3 audio.cpp auto-start skipped: supervisor, executable, model, or reference WAV is missing.");
            return;
        }

        var state = Path.Combine(project, ".state", "audiocpp");
        Directory.CreateDirectory(state);
        _readyFile = Path.Combine(state, $"game-{Environment.ProcessId}.ready");
        _failedFile = Path.Combine(state, $"game-{Environment.ProcessId}.failed.txt");
        File.Delete(_readyFile);
        File.Delete(_failedFile);
        try
        {
            using var helper = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -GamePid {Environment.ProcessId} -Precision {precision} -Port {endpoint.Port}",
                WorkingDirectory = project,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            });
            if (helper == null) throw new InvalidOperationException("PowerShell supervisor did not start.");
            log.LogInfo($"Stage3 audio.cpp visible console started pid={helper.Id}, precision={precision}.");
        }
        catch (Exception e)
        {
            _readyFile = null;
            _failedFile = null;
            log.LogWarning($"Stage3 audio.cpp auto-start failed: {e.GetType().Name}: {e.Message}");
        }
    }

    public static async Task WaitUntilReadyAsync(CancellationToken token)
    {
        var ready = _readyFile;
        var failed = _failedFile;
        if (ready == null || failed == null) return;
        while (!File.Exists(ready))
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(failed)) throw new InvalidOperationException("audio.cpp failed to start: " + File.ReadAllText(failed));
            await Task.Delay(250, token).ConfigureAwait(false);
        }
    }
}
