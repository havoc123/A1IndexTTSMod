using System.Net.Http;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using LocalModManager.Abstractions;

namespace A1IndexTTSMod;

// Per-NPC reference WAVs with a default voice; completed WAVs play via WASAPI shared mode.
internal static class SpeechMvp
{
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly object Gate = new();
    private static readonly Queue<string> RecentDisplayOrder = new();
    private static readonly HashSet<string> RecentDisplayIds = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> RecentDisplayStylePresent = new(StringComparer.Ordinal);
    private static ManualLogSource? _log;
    private static Uri? _endpoint;
    private static string _referenceId = "demo";
    private static string _audioCppModelId = "cosyvoice3";
    private static bool _audioCpp;
    private static TtsBackend _backend;
    private static TtsSettings? _settings;
    private static string _audioDir = "";
    private static string _emotionMapPath = "";
    private static int _timeoutSeconds = 180;
    private static bool _enabled;
    private static bool _autoRead = true;
    private static int _volumePercent = 100;
    private static CancellationTokenSource? _pending;
    private static string? _pendingIdentity;
    private static bool _pendingPlaybackStarted;
    private static string? _playingFile;
    private static long _generation;
    private static long _transition;
    private static FeaturePluginState _state = FeaturePluginState.Stopped;
    private static string _statusMessage = "Disabled";
    private static bool _autoStartAudioCpp;
    private static string _precision = "q8_0";
    private static CancellationTokenSource? _lifecycleCancellation;
    private const long RecentAudioBudgetBytes = 50L * 1024 * 1024;

    public static FeaturePluginState State { get { lock (Gate) return _state; } }
    public static string StatusMessage { get { lock (Gate) return _statusMessage; } }
    public static bool IsFeatureEnabled { get { lock (Gate) return _enabled; } }

    public static void SetAutoRead(bool enabled)
    {
        CancellationTokenSource? pending = null;
        lock (Gate)
        {
            _autoRead = enabled;
            if (!enabled)
            {
                pending = _pending;
                _pending = null;
                _pendingIdentity = null;
                _pendingPlaybackStarted = false;
                ++_generation;
            }
        }
        if (pending != null)
        {
            try { pending.Cancel(); } catch (ObjectDisposedException) { }
            StopPlayback();
        }
    }

    public static void SetVolume(int volumePercent)
    {
        lock (Gate) _volumePercent = Math.Clamp(volumePercent, 0, 100);
        WasapiSpeechPlayer.SetVolume(_volumePercent);
    }

    public static void Start(ManualLogSource log, ConfigEntry<bool> enabled, ConfigEntry<string> url,
        ConfigEntry<string> referenceId, ConfigEntry<string> audioCppModelId, ConfigEntry<int> timeoutSeconds,
        ConfigEntry<bool> autoStartAudioCpp, ConfigEntry<string> audioCppPrecision, ConfigEntry<string> gpuBackend,
        ConfigEntry<int> gpuDevice, ConfigEntry<string> backend)
    {
        _log = log;
        _referenceId = referenceId.Value.Trim();
        _audioCppModelId = audioCppModelId.Value.Trim();
        _timeoutSeconds = Math.Clamp(timeoutSeconds.Value, 10, 600);
        _autoStartAudioCpp = autoStartAudioCpp.Value;
        _precision = audioCppPrecision.Value.Trim().ToLowerInvariant();
        var selectedGpuBackend = gpuBackend.Value.Trim();
        var backendName = backend.Value.Trim();
        if (!Enum.TryParse<TtsBackend>(backendName, ignoreCase: false, out _backend) ||
            !Uri.TryCreate(url.Value, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback ||
            endpoint.AbsolutePath is not ("/v1/tts" or "/v1/audio/speech") ||
            _referenceId.Length == 0 || _referenceId.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-')) ||
            _audioCppModelId.Length == 0 || _audioCppModelId.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')) ||
            !selectedGpuBackend.Equals("Nvidia", StringComparison.OrdinalIgnoreCase) &&
            !selectedGpuBackend.Equals("Vulkan", StringComparison.OrdinalIgnoreCase))
        {
            lock (Gate) { _enabled = false; _state = FeaturePluginState.Failed; _statusMessage = "Invalid TTS URL or model/reference ID"; }
            log.LogWarning("Stage3 MVP disabled: TtsUrl must be a loopback HTTP /v1/tts or /v1/audio/speech URL; reference/model IDs must be simple names.");
            return;
        }
        _audioCpp = _backend is TtsBackend.IndexTtsAudioCpp or TtsBackend.CosyVoiceAudioCpp;
        if ((_backend == TtsBackend.IndexTtsLegacyApi && endpoint.AbsolutePath != "/v1/tts") ||
            (_audioCpp && endpoint.AbsolutePath != "/v1/audio/speech"))
        {
            lock (Gate) { _enabled = false; _state = FeaturePluginState.Failed; _statusMessage = "TTS URL does not match selected backend"; }
            log.LogWarning("Stage3 MVP disabled: selected backend and TtsUrl path do not match.");
            return;
        }
        if ((_backend == TtsBackend.IndexTtsAudioCpp && _audioCppModelId != "indextts25") ||
            (_backend == TtsBackend.CosyVoiceAudioCpp && _audioCppModelId != "cosyvoice3"))
        {
            lock (Gate) { _enabled = false; _state = FeaturePluginState.Failed; _statusMessage = "Model ID does not match selected backend"; }
            log.LogWarning("Stage3 MVP disabled: IndexTtsAudioCpp requires model ID indextts25; CosyVoiceAudioCpp requires cosyvoice3.");
            return;
        }
        _endpoint = endpoint;
        _settings = new TtsSettings(_backend, endpoint, _referenceId, _audioCppModelId, _timeoutSeconds, _autoStartAudioCpp, _precision, selectedGpuBackend, gpuDevice.Value);
        _audioDir = Path.Combine(Paths.GameRootPath, "A1IndexTTSMod", ".state", "stage3-audio");
        _emotionMapPath = Path.Combine(Paths.GameRootPath, "A1IndexTTSMod", "config", "emotions.json");
        Directory.CreateDirectory(_audioDir);
        foreach (var stale in Directory.EnumerateFiles(_audioDir, "reply-*.wav"))
        {
            try { File.Delete(stale); } catch { }
        }
        log.LogInfo($"Stage3 MVP speech configured: {endpoint.Host}:{endpoint.Port}, backend={(_audioCpp ? "audio.cpp" : "IndexTTS API")}, reference={_referenceId}, model={_audioCppModelId}, timeout={_timeoutSeconds}s");
        SetEnabled(enabled.Value);
    }

    public static void SetEnabled(bool enabled)
    {
        CancellationTokenSource? oldPending;
        CancellationTokenSource? oldLifecycle;
        long transition;
        CancellationToken lifecycleToken;
        lock (Gate)
        {
            if (_enabled == enabled && ((enabled && (_state == FeaturePluginState.Starting || _state == FeaturePluginState.Running)) || (!enabled && _state == FeaturePluginState.Stopped))) return;
            _enabled = enabled;
            transition = ++_transition;
            ++_generation;
            oldPending = _pending;
            _pending = null;
            _pendingIdentity = null;
            _pendingPlaybackStarted = false;
            oldLifecycle = _lifecycleCancellation;
            _lifecycleCancellation = enabled ? new CancellationTokenSource() : null;
            lifecycleToken = _lifecycleCancellation?.Token ?? CancellationToken.None;
            _state = enabled ? FeaturePluginState.Starting : FeaturePluginState.Stopping;
            _statusMessage = enabled ? "Starting TTS service" : "Stopping speech and owned service";
        }
        try { oldPending?.Cancel(); } catch (ObjectDisposedException) { }
        try { oldLifecycle?.Cancel(); } catch (ObjectDisposedException) { }
        if (!enabled) StopPlayback();
        _ = Task.Run(() => TransitionAsync(transition, enabled, lifecycleToken));
    }

    private static async Task TransitionAsync(long transition, bool enabled, CancellationToken token)
    {
        try
        {
            if (enabled)
            {
                var settings = _settings ?? throw new InvalidOperationException("TTS settings snapshot is not configured.");
                if (_audioCpp)
                    await AudioCppLifecycle.EnsureStartedAsync(_log!, settings.Endpoint, settings.Backend, settings.ModelId, settings.AutoStart, settings.Precision, settings.GpuBackend, settings.GpuDevice, token).ConfigureAwait(false);
                SetTransitionState(transition, FeaturePluginState.Running, _audioCpp ? AudioCppLifecycle.StatusMessage : "TTS feature enabled");
                _log?.LogInfo("Stage3 MVP state=running: TTS endpoint ready or available.");
            }
            else
            {
                await AudioCppLifecycle.StopOwnedAsync().ConfigureAwait(false);
                SetTransitionState(transition, FeaturePluginState.Stopped, "Disabled");
                _log?.LogInfo("Stage3 MVP state=stopped: pending requests canceled and owned playback/service stopped.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            SetTransitionState(transition, FeaturePluginState.Failed, e.Message);
            _log?.LogWarning($"Stage3 MVP state=failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void SetTransitionState(long transition, FeaturePluginState state, string message)
    {
        lock (Gate)
        {
            if (transition != _transition) return;
            _state = state;
            _statusMessage = message;
        }
    }

    private static void StopPlayback()
    {
        lock (Gate)
        {
            WasapiSpeechPlayer.Stop();
            _playingFile = null;
        }
    }

    internal static bool Replay(SpeechPanelTurn turn)
    {
        if (!IsFeatureEnabled || string.IsNullOrWhiteSpace(turn.AudioPath) || !File.Exists(turn.AudioPath))
        {
            if (!string.IsNullOrWhiteSpace(turn.AudioPath)) SpeechPanelData.SetAudioMissing(turn.AudioPath);
            return false;
        }
        CancellationTokenSource? pending;
        lock (Gate)
        {
            pending = _pending;
            _pending = null;
            _pendingIdentity = null;
            _pendingPlaybackStarted = false;
            ++_generation;
            WasapiSpeechPlayer.Stop();
            var backend = WasapiSpeechPlayer.TryPlay(turn.AudioPath, _volumePercent, _log);
            if (backend == null) { turn.TtsStatus = "原音频播放失败"; return false; }
            _playingFile = turn.AudioPath;
            turn.TtsStatus = "重播中 · " + backend;
            turn.Error = null;
        }
        try { pending?.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    internal static bool PlayReference(string path)
    {
        if (!IsFeatureEnabled || !File.Exists(path)) return false;
        CancellationTokenSource? pending;
        lock (Gate)
        {
            pending = _pending;
            _pending = null;
            _pendingIdentity = null;
            _pendingPlaybackStarted = false;
            ++_generation;
            WasapiSpeechPlayer.Stop();
            var backend = WasapiSpeechPlayer.TryPlay(path, _volumePercent, _log);
            if (backend == null)
            {
                _statusMessage = "参考音播放失败";
                return false;
            }
            _playingFile = null;
            _statusMessage = "正在播放参考音 · " + backend;
        }
        try { pending?.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    internal static void StopFromPanel()
    {
        CancellationTokenSource? pending;
        lock (Gate)
        {
            pending = _pending;
            _pending = null;
            _pendingIdentity = null;
            _pendingPlaybackStarted = false;
            ++_generation;
        }
        try { pending?.Cancel(); } catch (ObjectDisposedException) { }
        StopPlayback();
    }

    internal static bool PreviewTurn(SpeechPanelTurn turn, string? referencePath) => StartManual(turn, referencePath, "试听");

    internal static bool Resynthesize(SpeechPanelTurn turn, string? referencePath) => StartManual(turn, referencePath, "重合成");

    private static bool StartManual(SpeechPanelTurn original, string? referencePath, string kind)
    {
        if (!IsFeatureEnabled || _endpoint == null || string.IsNullOrWhiteSpace(original.SpokenText)) return false;
        try
        {
            var turn = SpeechPanelData.CreateManualTurn(original, kind);
            var style = turn.VoiceStyleJson == null ? null : JsonSerializer.Deserialize<VoiceStyle>(turn.VoiceStyleJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            CancellationTokenSource current;
            CancellationTokenSource? previous;
            long generation;
            lock (Gate)
            {
                if (!_enabled || _endpoint == null) return false;
                current = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
                previous = _pending;
                _pending = current;
                _pendingIdentity = turn.Identity;
                _pendingPlaybackStarted = false;
                generation = ++_generation;
            }
            try { previous?.Cancel(); } catch (ObjectDisposedException) { }
            var reference = SnapshotReference(turn.NpcKey, referencePath);
            var request = new SpeechRequest(turn.NpcKey, turn.DisplayText, turn.SpokenText, turn.Emotion,
                style, generation, turn.Identity, referencePath, reference.Bytes, reference.Label, reference.Skip);
            _log?.LogInfo($"Stage3 manual {kind} generation={generation} source={original.Identity} npc={turn.NpcKey}");
            _ = Task.Run(() => SynthesizeAndPlayAsync(request, current));
            return true;
        }
        catch (Exception e)
        {
            _log?.LogWarning("Speech panel manual request could not start: " + e.GetType().Name);
            return false;
        }
    }

    public static void OnNpcReply(string npcKey, string displayText, string? emotion, VoiceStyle? voiceStyle, string displayIdentity)
    {
        if (!_enabled || _endpoint == null)
        {
            SpeechPanelData.UpdateTtsStatus(displayIdentity, "功能总开关关闭，未合成");
            return;
        }
        if (!_autoRead)
        {
            SpeechPanelData.UpdateTtsStatus(displayIdentity, "自动朗读已关闭，可从最近语音手动重合成");
            return;
        }
        // Keep the TTS request safe even if a caller receives an unmodified Harmony argument.
        var text = SpeechTextFilter.RemoveVoiceStyleEnvelope(displayText).Trim();
        if (text.Length == 0)
        {
            SpeechPanelData.UpdateTtsStatus(displayIdentity, "回复没有可朗读文本");
            return;
        }
        if (text.Length > 1000)
        {
            SpeechPanelData.UpdateTtsStatus(displayIdentity, "回复超过 1000 字，已跳过合成");
            return;
        }
        var spokenText = SpeechTextFilter.RemoveParentheticals(text);
        if (spokenText.Length == 0)
        {
            SpeechPanelData.UpdateTtsStatus(displayIdentity, "回复仅含括号动作描述，已跳过合成");
            return;
        }
        CancellationTokenSource current;
        CancellationTokenSource? previous;
        long generation;
        var isStyleUpgrade = false;
        lock (Gate)
        {
            if (!_enabled || !_autoRead || _endpoint == null) return;
            if (string.IsNullOrWhiteSpace(displayIdentity)) return;
            if (!RecentDisplayIds.Add(displayIdentity))
            {
                if (!RecentDisplayStylePresent.TryGetValue(displayIdentity, out var hasStyle) ||
                    !DisplayStyleDedupPolicy.ShouldUpgrade(hasStyle, voiceStyle != null,
                        _pendingIdentity == displayIdentity && _pending != null, _pendingPlaybackStarted))
                {
                    _log?.LogInfo($"Stage3 duplicate display callback suppressed identity={displayIdentity}; existing generation remains active.");
                    return;
                }
                RecentDisplayStylePresent[displayIdentity] = true;
                isStyleUpgrade = true;
            }
            else
            {
                RecentDisplayOrder.Enqueue(displayIdentity);
                RecentDisplayStylePresent[displayIdentity] = voiceStyle != null;
                while (RecentDisplayOrder.Count > 128)
                {
                    var expired = RecentDisplayOrder.Dequeue();
                    RecentDisplayIds.Remove(expired);
                    RecentDisplayStylePresent.Remove(expired);
                }
            }
            current = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
            previous = _pending;
            _pending = current;
            _pendingIdentity = displayIdentity;
            _pendingPlaybackStarted = false;
            generation = ++_generation;
        }
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        var reference = SnapshotReference(npcKey, null);
        var request = new SpeechRequest(npcKey, text, spokenText, emotion, voiceStyle, generation, displayIdentity,
            null, reference.Bytes, reference.Label, reference.Skip);
        _log?.LogInfo($"Stage3 MVP TTS {(isStyleUpgrade ? "upgraded" : "queued")} generation={generation} chars={spokenText.Length} omittedParentheticalChars={text.Length - spokenText.Length} emotion={emotion ?? "unknown"} voiceStyle={(voiceStyle == null ? "invalid_or_missing" : "present")} identity={displayIdentity}");
        _ = Task.Run(() => SynthesizeAndPlayAsync(request, current));
    }

    private static (byte[]? Bytes, string? Label, bool Skip) SnapshotReference(string npcKey, string? overridePath)
    {
        if (!_audioCpp) return (null, overridePath, false);
        var selection = overridePath == null ? NpcVoiceResolver.Resolve(npcKey) :
            new NpcVoiceResolver.Selection(overridePath, NpcVoiceResolver.GetReferenceLabel(overridePath), false);
        if (selection.Skip) return (null, selection.Label, true);
        if (selection.Path == null || !File.Exists(selection.Path)) return (null, selection.Label, false);
        try { return (File.ReadAllBytes(selection.Path), selection.Label, false); }
        catch (Exception e)
        {
            _log?.LogWarning("Stage3 reference audio snapshot failed: " + e.GetType().Name);
            return (null, selection.Label, false);
        }
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

    private static async Task SynthesizeAndPlayAsync(SpeechRequest request, CancellationTokenSource source)
    {
        var generation = request.Generation;
        var npcKey = request.NpcKey;
        var text = request.SpokenText;
        var emotion = request.LegacyEmotion;
        string? newFile = null;
        try
        {
            if (request.ReferenceSkip)
            {
                SpeechPanelData.UpdateTtsStatus(request.DisplayIdentity, "按本地 NPC 策略跳过语音", request.ReferenceLabelSnapshot);
                _log?.LogInfo($"Stage3 MVP TTS skipped generation={generation} npc={npcKey} reason={request.ReferenceLabelSnapshot}");
                return;
            }
            var vector = EmotionVector(emotion);
            _log?.LogInfo($"Stage3 MVP emotion generation={generation} tag={emotion ?? "unknown"} mode={(vector == null ? "qwen_text" : "vector")}");
            var voicePath = request.ReferencePathOverride;
            _log?.LogInfo($"Stage3 MVP voice generation={generation} npc={npcKey} reference={request.ReferenceLabelSnapshot ?? "demo.wav"}");
            string? voiceInstruction = null;
            var voiceReference = _audioCpp && request.ReferenceAudioSnapshot is { Length: > 0 } audio
                ? AudioCppVoiceReference.FromBytes(audio) : null;
            var payload = _audioCpp
                ? _backend == TtsBackend.CosyVoiceAudioCpp
                    ? CosyVoicePayload(text, request.VoiceStyle, voiceReference, out voiceInstruction)
                    : AudioCppPayload(text, vector, voiceReference)
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
            SpeechPanelData.UpdateTtsRequest(request.DisplayIdentity, _backend.ToString(), payload,
                request.ReferenceLabelSnapshot ?? _referenceId + " (服务默认)", voiceInstruction);
            Probe.RecordTtsInstruction(request.DisplayIdentity, _backend.ToString(), request.VoiceStyle, voiceInstruction);
            using var body = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await Client.PostAsync(_endpoint!, body, source.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var wav = await response.Content.ReadAsByteArrayAsync(source.Token).ConfigureAwait(false);
            if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
                throw new InvalidDataException("audio.cpp response is not a RIFF/WAVE file.");

            newFile = Path.Combine(_audioDir, $"reply-{generation:D6}.wav");
            await File.WriteAllBytesAsync(newFile, wav, source.Token).ConfigureAwait(false);
            string? playbackBackend;
            lock (Gate)
            {
                if (!_enabled || generation != _generation || source.IsCancellationRequested) return;
                playbackBackend = WasapiSpeechPlayer.TryPlay(newFile, _volumePercent, _log);
                if (playbackBackend == null) throw new InvalidOperationException("WASAPI and WinMM playback failed.");
                _playingFile = newFile;
                _pendingPlaybackStarted = true;
                _log?.LogInfo($"Stage3 MVP playback backend={playbackBackend} generation={generation}");
            }
            _log?.LogInfo($"Stage3 MVP WAV playing generation={generation} bytes={wav.Length}");
            SpeechPanelData.UpdateTtsStatus(request.DisplayIdentity, "播放中 · " + playbackBackend, null, newFile, wav.LongLength);
            newFile = null; // Keep the playing file until the next reply replaces it.
            PruneRecentAudio();
        }
        catch (OperationCanceledException)
        {
            SpeechPanelData.UpdateTtsStatus(request.DisplayIdentity, "已取消");
            _log?.LogInfo($"Stage3 MVP TTS canceled generation={generation}");
        }
        catch (Exception e)
        {
            SpeechPanelData.UpdateTtsStatus(request.DisplayIdentity, "合成或播放失败", e.Message);
            _log?.LogWarning($"Stage3 MVP TTS failed generation={generation}: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            DeleteIfPresent(newFile);
            lock (Gate)
            {
                if (ReferenceEquals(_pending, source))
                {
                    _pending = null;
                    _pendingIdentity = null;
                    _pendingPlaybackStarted = false;
                }
            }
            source.Dispose();
        }
    }

    private static string AudioCppPayload(string text, double[]? vector, object? voiceReference)
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
        if (voiceReference != null) request["voice_ref"] = voiceReference;
        return JsonSerializer.Serialize(request);
    }

    internal static string CosyVoicePayload(string text, VoiceStyle? voiceStyle, object? voiceReference, out string instruction)
    {
        instruction = CosyVoiceInstruction.Format(voiceStyle);
        return CosyVoiceInstruction.BuildPayload(text, voiceStyle, voiceReference, instruction);
    }

    private static void DeleteIfPresent(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); } catch { /* Old WAV is disposable and ignored by Git. */ }
    }

    private static void PruneRecentAudio()
    {
        var recent = SpeechPanelData.GetRecent();
        long total = recent.Sum(turn => File.Exists(turn.AudioPath) ? new FileInfo(turn.AudioPath!).Length : 0);
        foreach (var turn in recent.Reverse())
        {
            if (total <= RecentAudioBudgetBytes) break;
            var path = turn.AudioPath;
            if (string.IsNullOrEmpty(path) || string.Equals(path, _playingFile, StringComparison.OrdinalIgnoreCase)) continue;
            var bytes = 0L;
            try { if (File.Exists(path)) { bytes = new FileInfo(path).Length; File.Delete(path); } } catch { }
            total -= bytes;
            SpeechPanelData.SetAudioMissing(path);
        }
        foreach (var file in Directory.EnumerateFiles(_audioDir, "reply-*.wav"))
        {
            if (recent.Any(turn => string.Equals(turn.AudioPath, file, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(file, _playingFile, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch { }
        }
    }
}
