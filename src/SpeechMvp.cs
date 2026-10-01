using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace A1IndexTTSMod;

// Per-NPC reference WAVs with a default voice; completed WAVs play via WASAPI shared mode.
internal static class SpeechMvp
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndFileName = 0x00020000;
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly object Gate = new();
    private static ManualLogSource? _log;
    private static Uri? _endpoint;
    private static string _referenceId = "demo";
    private static string _audioCppModelId = "indextts25";
    private static bool _audioCpp;
    private static string _audioDir = "";
    private static string _voiceStageDir = "";
    private static string _emotionMapPath = "";
    private static int _timeoutSeconds = 180;
    private static bool _enabled;
    private static CancellationTokenSource? _pending;
    private static string? _playingFile;
    private static string? _lastReplyKey;
    private static DateTime _lastReplyUtc;
    private static long _generation;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    public static void Start(ManualLogSource log, ConfigEntry<bool> enabled, ConfigEntry<string> url,
        ConfigEntry<string> referenceId, ConfigEntry<string> audioCppModelId, ConfigEntry<int> timeoutSeconds,
        ConfigEntry<bool> autoStartAudioCpp, ConfigEntry<string> audioCppPrecision)
    {
        _log = log;
        _enabled = enabled.Value;
        _referenceId = referenceId.Value.Trim();
        _audioCppModelId = audioCppModelId.Value.Trim();
        _timeoutSeconds = Math.Clamp(timeoutSeconds.Value, 10, 600);
        if (!_enabled) { log.LogInfo("Stage3 MVP speech is disabled."); return; }
        if (!Uri.TryCreate(url.Value, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback ||
            endpoint.AbsolutePath is not ("/v1/tts" or "/v1/audio/speech") ||
            _referenceId.Length == 0 || _referenceId.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-')) ||
            _audioCppModelId.Length == 0 || _audioCppModelId.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
        {
            _enabled = false;
            log.LogWarning("Stage3 MVP disabled: TtsUrl must be a loopback HTTP /v1/tts or /v1/audio/speech URL; reference/model IDs must be simple names.");
            return;
        }
        _endpoint = endpoint;
        _audioCpp = endpoint.AbsolutePath == "/v1/audio/speech";
        _audioDir = Path.Combine(Paths.GameRootPath, "A1IndexTTSMod", ".state", "stage3-audio");
        _voiceStageDir = Path.Combine(Paths.GameRootPath, "A1IndexTTSMod", ".state", "stage3-voice");
        _emotionMapPath = Path.Combine(Paths.GameRootPath, "A1IndexTTSMod", "config", "emotions.json");
        Directory.CreateDirectory(_audioDir);
        log.LogInfo($"Stage3 MVP speech ready: {endpoint.Host}:{endpoint.Port}, backend={(_audioCpp ? "audio.cpp" : "IndexTTS API")}, reference={_referenceId}, model={_audioCppModelId}, timeout={_timeoutSeconds}s");
        if (_audioCpp) AudioCppLifecycle.Start(log, endpoint, _audioCppModelId, autoStartAudioCpp.Value, audioCppPrecision.Value.Trim().ToLowerInvariant());
    }

    public static void OnNpcReply(string npcKey, string displayText, string? emotion)
    {
        if (!_enabled || _endpoint == null) return;
        var text = displayText.Trim();
        if (text.Length == 0 || text.Length > 1000) return;
        var spokenText = SpeechTextFilter.RemoveParentheticals(text);
        if (spokenText.Length == 0) return;
        var key = npcKey + "\n" + text;
        CancellationTokenSource current;
        CancellationTokenSource? previous;
        long generation;
        lock (Gate)
        {
            // A repeated callback for the same display event must not synthesize twice.
            if (key == _lastReplyKey && DateTime.UtcNow - _lastReplyUtc < TimeSpan.FromMilliseconds(750)) return;
            _lastReplyKey = key;
            _lastReplyUtc = DateTime.UtcNow;
            current = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
            previous = _pending;
            _pending = current;
            generation = ++_generation;
        }
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        _log?.LogInfo($"Stage3 MVP TTS queued generation={generation} chars={spokenText.Length} omittedParentheticalChars={text.Length - spokenText.Length} emotion={emotion ?? "unknown"}");
        _ = Task.Run(() => SynthesizeAndPlayAsync(generation, npcKey, spokenText, emotion, current));
    }

    private static double[]? EmotionVector(string? emotion)
    {
        var tag = emotion?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(tag)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_emotionMapPath));
            if (document.RootElement.TryGetProperty(tag, out var values) && values.ValueKind == JsonValueKind.Array)
            {
                var vector = values.EnumerateArray().Select(value => value.GetDouble()).ToArray();
                if (vector.Length == 8 && vector.All(value => double.IsFinite(value) && value >= 0) && vector.Sum() <= 0.8 + 1e-9)
                    return vector;
                _log?.LogWarning($"Stage3 invalid emotion vector for {tag}; using built-in mapping.");
            }
        }
        catch (FileNotFoundException) { }
        catch (Exception e) { _log?.LogWarning($"Stage3 emotion map unreadable: {e.GetType().Name}; using built-in mapping."); }

        // IndexTTS order: happy, angry, sad, afraid, disgusted, melancholic, surprised, calm.
        return tag switch
        {
            "happy" => new[] { 0.8, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 },
            "angry" => new[] { 0.0, 0.7, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 },
            "doubt" => new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.35, 0.25 },
            "smile" => new[] { 0.35, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.4 },
            "think" => new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.6 },
            "sad" => new[] { 0.0, 0.0, 0.7, 0.0, 0.0, 0.0, 0.0, 0.0 },
            "surprise" => new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.7, 0.0 },
            "afraid" => new[] { 0.0, 0.0, 0.0, 0.7, 0.0, 0.0, 0.0, 0.0 },
            "normal" => new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.55 },
            _ => null
        };
    }

    private static async Task SynthesizeAndPlayAsync(long generation, string npcKey, string text, string? emotion, CancellationTokenSource source)
    {
        string? newFile = null;
        string? stagedVoice = null;
        try
        {
            var selection = _audioCpp ? NpcVoiceResolver.Resolve(npcKey) : new NpcVoiceResolver.Selection(null, "demo.wav", false);
            if (selection.Skip)
            {
                _log?.LogInfo($"Stage3 MVP TTS skipped generation={generation} npc={npcKey} reason={selection.Label}");
                return;
            }
            if (_audioCpp) await AudioCppLifecycle.WaitUntilReadyAsync(source.Token).ConfigureAwait(false);
            var vector = EmotionVector(emotion);
            _log?.LogInfo($"Stage3 MVP emotion generation={generation} tag={emotion ?? "unknown"} mode={(vector == null ? "qwen_text" : "vector")}");
            var npcVoice = selection.Path;
            var voicePath = npcVoice;
            if (voicePath != null && voicePath.Any(ch => ch > 127))
            {
                try
                {
                    Directory.CreateDirectory(_voiceStageDir);
                    stagedVoice = Path.Combine(_voiceStageDir, $"npc-{Environment.ProcessId}-{generation}.wav");
                    File.Copy(voicePath, stagedVoice, overwrite: true);
                    voicePath = stagedVoice;
                }
                catch (IOException e)
                {
                    DeleteIfPresent(stagedVoice);
                    stagedVoice = null;
                    npcVoice = null;
                    voicePath = null;
                    _log?.LogWarning($"Stage3 NPC reference unavailable; using demo.wav: {e.GetType().Name}");
                }
                catch (UnauthorizedAccessException e)
                {
                    DeleteIfPresent(stagedVoice);
                    stagedVoice = null;
                    npcVoice = null;
                    voicePath = null;
                    _log?.LogWarning($"Stage3 NPC reference unavailable; using demo.wav: {e.GetType().Name}");
                }
            }
            _log?.LogInfo($"Stage3 MVP voice generation={generation} npc={npcKey} reference={(npcVoice == null ? "demo.wav" : Path.GetFileName(npcVoice))}");
            var payload = _audioCpp
                ? AudioCppPayload(text, vector, voicePath)
                : JsonSerializer.Serialize(new
                {
                    text,
                    reference_id = _referenceId,
                    format = "wav",
                    lang = "ZH",
                    audiosr = false,
                    emo_vector = vector,
                    use_emo_text = vector == null
                });
            using var body = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await Client.PostAsync(_endpoint!, body, source.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var wav = await response.Content.ReadAsByteArrayAsync(source.Token).ConfigureAwait(false);
            if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
                throw new InvalidDataException("IndexTTS response is not a RIFF/WAVE file.");

            newFile = Path.Combine(_audioDir, $"reply-{generation:D6}.wav");
            await File.WriteAllBytesAsync(newFile, wav, source.Token).ConfigureAwait(false);
            string? oldFile;
            lock (Gate)
            {
                if (generation != _generation || source.IsCancellationRequested) return;
                PlaySound(null, IntPtr.Zero, 0); // Stop any earlier WinMM fallback before a new line.
                var wasapi = WasapiSpeechPlayer.TryPlay(newFile, _log);
                if (!wasapi && !PlaySound(newFile, IntPtr.Zero, SndAsync | SndNoDefault | SndFileName))
                    throw new InvalidOperationException($"WASAPI and WinMM playback failed: {Marshal.GetLastWin32Error()}");
                oldFile = _playingFile;
                _playingFile = newFile;
                _log?.LogInfo($"Stage3 MVP playback backend={(wasapi ? "wasapi_shared" : "winmm_fallback")} generation={generation}");
            }
            _log?.LogInfo($"Stage3 MVP WAV playing generation={generation} bytes={wav.Length}");
            newFile = null; // Keep the playing file until the next reply replaces it.
            DeleteIfPresent(oldFile);
        }
        catch (OperationCanceledException) { _log?.LogInfo($"Stage3 MVP TTS canceled generation={generation}"); }
        catch (Exception e) { _log?.LogWarning($"Stage3 MVP TTS failed generation={generation}: {e.GetType().Name}: {e.Message}"); }
        finally
        {
            DeleteIfPresent(newFile);
            DeleteIfPresent(stagedVoice);
            lock (Gate) { if (ReferenceEquals(_pending, source)) _pending = null; }
            source.Dispose();
        }
    }

    private static string AudioCppPayload(string text, double[]? vector, string? voicePath)
    {
        var request = new Dictionary<string, object>
        {
            ["model"] = _audioCppModelId,
            ["input"] = text,
            ["response_format"] = "wav",
            ["options"] = vector == null
                ? new { language = "zh", use_emotion_text = true }
                : (object)new { language = "zh", use_emotion_text = false, emotion_vector = vector }
        };
        if (voicePath != null) request["voice_ref"] = voicePath;
        return JsonSerializer.Serialize(request);
    }

    private static void DeleteIfPresent(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); } catch { /* Old WAV is disposable and ignored by Git. */ }
    }
}
