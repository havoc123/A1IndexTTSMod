using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;

namespace A1IndexTTSMod;

internal sealed record AsrSnapshot(long SessionId, string State, string Text, float Level, bool TestMode)
{
    internal bool IsActive => State is "准备中" or "录音中" or "收尾中";
}

/// <summary>Capture frames are generation-tagged; only the worker owns the decoder.</summary>
internal static class StreamingAsr
{
    private sealed record AudioFrame(long SessionId, float[] Samples);
    private sealed class CaptureSession
    {
        internal readonly object Gate = new();
        internal readonly long Id;
        internal readonly WasapiCapture Capture;
        internal readonly MMDevice? Device;
        internal readonly BufferedWaveProvider Buffer;
        internal readonly ISampleProvider Samples;
        internal long DeliveredBytes, QueuedSamples, ProcessedSamples;
        internal DateTime Started = DateTime.UtcNow;
        internal bool Stopping;
        internal readonly Stopwatch StopTimer = new();
        internal CaptureSession(long id, string deviceId)
        {
            Id = id;
            if (deviceId.Length == 0) Capture = new WasapiCapture();
            else { using var devices = new MMDeviceEnumerator(); Device = devices.GetDevice(deviceId); Capture = new WasapiCapture(Device); }
            Buffer = new BufferedWaveProvider(Capture.WaveFormat) { DiscardOnBufferOverflow = false, ReadFully = false };
            ISampleProvider samples = Buffer.ToSampleProvider();
            if (samples.WaveFormat.Channels == 2) samples = new StereoToMonoSampleProvider(samples);
            if (samples.WaveFormat.Channels != 1) throw new NotSupportedException("麦克风需要单声道或双声道格式。");
            if (samples.WaveFormat.SampleRate != 16000) samples = new WdlResamplingSampleProvider(samples, 16000);
            Samples = samples;
        }
    }

    private static readonly object Control = new();
    private static readonly BlockingCollection<Action> Work = new(new ConcurrentQueue<Action>());
    private static readonly BlockingCollection<AudioFrame> Audio = new(32);
    private static AsrSnapshot _snapshot = new(0, "未加载", "", 0, false);
    private static AsrDecoderSession? _decoder;
    private static CaptureSession? _capture;
    private static string _profileId = AsrModelProfiles.Lightweight.Id;
    private static string _loadedProfile = "";
    private static string _deviceId = "";
    private static long _session, _workerId;
    private static Action<string>? _log;
    private static string _root = AppContext.BaseDirectory;
    private static bool _enabled, _warmQueued;
    private static GpuRoute? _gpu;
    internal static bool Available { get; private set; }
    internal static bool ShowMicrophone => Available && _enabled;
    internal static string UnavailableReason { get; private set; } = "ASR 未安装";
    internal static string Provider => _gpu?.Provider ?? "cuda";
    private static string Root => _root;
    internal static string GameRoot => Root;
    internal static void ConfigureRoot(string root)
    {
        lock (Control)
        {
            if (IsActive || _decoder != null) throw new InvalidOperationException("Cannot change root while the ASR engine is loaded.");
            _root = Path.GetFullPath(root);
            try
            {
                _gpu = GpuRouting.SelectAsr(GpuRouting.Adapters.Value);
                if (_gpu?.Provider == "cuda" && !GpuRouting.RuntimeInstalled(_root, "cuda") &&
                    GpuRouting.RuntimeInstalled(_root, "directml") && AsrModelProfiles.Lightweight.IsInstalled(_root))
                {
                    var adapter = GpuRouting.Adapters.Value.First(g => g.Vendor == 0x10de);
                    _gpu = adapter.SupportsDirectML ? new GpuRoute("directml", adapter.Index, adapter.Name) : null;
                }
                Available = _gpu != null && GpuRouting.RuntimeInstalled(_root, _gpu.Provider)
                    && AsrModelProfiles.All.Any(p => GpuRouting.AllowsModel(_gpu.Provider, p) && p.IsInstalled(_root));
                UnavailableReason = _gpu == null ? "未发现支持的显卡" : Available ? "" : "ASR 可选包未完整安装";
                if (_gpu?.Provider == "directml") _profileId = AsrModelProfiles.Lightweight.Id;
            }
            catch (Exception e) { Available = false; UnavailableReason = "显卡检测失败：" + e.Message; }
        }
    }

    static StreamingAsr() => new Thread(WorkerLoop) { IsBackground = true, Name = "A1IndexTTS-ASR" }.Start();
    internal static AsrSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal static string State => Snapshot.State;
    internal static string Text => Snapshot.Text;
    internal static float Level => Snapshot.Level;
    internal static bool IsActive => Snapshot.IsActive;
    internal static bool TestMode => Snapshot.TestMode;
    internal static AsrModelProfile Profile => AsrModelProfiles.Resolve(_profileId);
    internal static string EngineDescription => $"{Profile.Label} · {Profile.Precision} · {Provider.ToUpperInvariant()} GPU {_gpu?.Device ?? 0} · 流式 4 路{(_decoder?.SupportsReview == true ? " / 终稿 8 路" : "")} · 热词 {_decoder?.HotwordCount ?? 0} 个";
    internal static void SetLog(Action<string> log) => _log = log;
    internal static void SetDevice(string id) { lock (Control) _deviceId = id ?? ""; }
    internal static void SetEnabled(bool enabled)
    {
        lock (Control) _enabled = enabled;
        if (enabled) WarmUp(); else Cancel(true);
    }
    internal static void SetForeground(bool focused)
    {
        // Focus affects capture, never model residency.
        if (focused) WarmUp();
    }
    internal static void WarmUp()
    {
        lock (Control)
        {
            if (!Available) { Volatile.Write(ref _snapshot, Snapshot with { State = UnavailableReason }); return; }
            if (!_enabled || Snapshot.IsActive || _warmQueued || (Snapshot.State == "就绪" && _loadedProfile == _profileId)) return;
            var profile = Profile;
            _warmQueued = true;
            Volatile.Write(ref _snapshot, Snapshot with { State = "预热中", TestMode = false });
            Work.Add(() =>
            {
                try
                {
                    if (!_enabled || profile.Id != _profileId) return;
                    _workerId = Interlocked.Read(ref _session);
                    EnsureDecoder(profile);
                    lock (Control)
                        if (!Snapshot.IsActive && _enabled && profile.Id == _profileId)
                            Volatile.Write(ref _snapshot, Snapshot with { State = "就绪", Level = 0, TestMode = false });
                }
                finally { lock (Control) _warmQueued = false; }
            });
        }
    }
    internal static bool SetProfile(string id)
    {
        lock (Control)
        {
            if (Snapshot.IsActive) return false;
            var profile = AsrModelProfiles.Resolve(id);
            if (!GpuRouting.AllowsModel(Provider, profile) || !profile.IsInstalled(Root)) return false;
            _profileId = profile.Id;
            Work.Add(() => { if (_loadedProfile != profile.Id && _capture == null) DisposeDecoder(); WarmUp(); });
            WarmUp();
            return true;
        }
    }

    internal static void Start(bool testMode = false)
    {
        lock (Control)
        {
            if (!_enabled || !Available || Snapshot.IsActive) return;
            var id = Interlocked.Increment(ref _session);
            Volatile.Write(ref _snapshot, new(id, "准备中", "", 0, testMode));
            var profile = Profile; var device = _deviceId;
            Work.Add(() => StartOnWorker(id, profile, device));
        }
    }

    internal static void Stop()
    {
        lock (Control)
        {
            var snapshot = Snapshot;
            if (!snapshot.IsActive || snapshot.State == "收尾中") return;
            Volatile.Write(ref _snapshot, snapshot with { State = "收尾中" });
            Work.Add(() => BeginStop(snapshot.SessionId));
        }
    }

    internal static void Cancel(bool releaseRecognizer = false)
    {
        lock (Control)
        {
            var id = Interlocked.Increment(ref _session);
            Volatile.Write(ref _snapshot, Snapshot with { SessionId = id, State = "已取消", Level = 0 });
            Work.Add(() =>
            {
                AbortCapture(); _decoder?.End(); if (releaseRecognizer) DisposeDecoder();
                // An earlier disable may still be queued behind a cold load when
                // the user enables ASR again. Reconcile with the current switch
                // after releasing, rather than leaving a ready state with no model.
                if (releaseRecognizer && _enabled) WarmUp();
                if (!releaseRecognizer && _enabled && _decoder != null) Publish(id, "就绪", Snapshot.Text, 0);
            });
        }
    }
    internal static void Shutdown() { lock (Control) _enabled = false; Cancel(true); }

    private static void WorkerLoop()
    {
        while (true)
        {
            try
            {
                if (Work.TryTake(out var action, 20)) action();
                for (var i = 0; i < 4 && Audio.TryTake(out var frame); i++) Process(frame);
                var capture = _capture;
                if (capture != null && !capture.Stopping && DateTime.UtcNow - capture.Started >= TimeSpan.FromSeconds(60)) Stop();
                if (capture?.Stopping == true && capture.StopTimer.Elapsed > TimeSpan.FromSeconds(3))
                    Fail(capture.Id, new TimeoutException("麦克风停止超时，已保留最后草稿。"));
            }
            catch (Exception e) { Fail(_workerId, e); }
        }
    }

    private static void StartOnWorker(long id, AsrModelProfile profile, string device)
    {
        if (id != Interlocked.Read(ref _session)) return;
        _workerId = id;
        AbortCapture();
        EnsureDecoder(profile);
        if (id != Interlocked.Read(ref _session)) return;
        _decoder!.Begin();
        var capture = new CaptureSession(id, device); _capture = capture;
        capture.Capture.DataAvailable += (_, e) => OnData(capture, e);
        capture.Capture.RecordingStopped += (_, e) => Work.Add(() => FinishStoppedCapture(capture, e.Exception));
        capture.Capture.StartRecording();
        if (Snapshot.State == "收尾中") BeginStop(id);
        else Publish(id, "录音中", "");
    }

    private static void EnsureDecoder(AsrModelProfile profile)
    {
        if (_decoder == null || _loadedProfile != profile.Id)
        {
            DisposeDecoder();
            _decoder = new AsrDecoderSession(Root, profile, provider: Provider, device: _gpu?.Device ?? 0); _loadedProfile = profile.Id;
            _log?.Invoke($"ASR engine initialized: profile={profile.Id}, precision={profile.Precision}, requestedProvider={Provider}, beam=4, hotwords={_decoder.HotwordCount}, skipped={_decoder.SkippedHotwordCount}, native={_decoder.NativeRevision}.");
        }
    }

    private static void OnData(CaptureSession capture, WaveInEventArgs e)
    {
        if (capture.Id != Interlocked.Read(ref _session)) return;
        try
        {
            lock (capture.Gate)
            {
                capture.DeliveredBytes += e.BytesRecorded;
                capture.Buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                DrainSamples(capture);
            }
        }
        catch (Exception exception) { Work.Add(() => Fail(capture.Id, exception)); }
    }

    private static void DrainSamples(CaptureSession capture)
    {
        var buffer = new float[1600]; int count;
        while ((count = capture.Samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            var samples = buffer.AsSpan(0, count).ToArray();
            if (!Audio.TryAdd(new(capture.Id, samples))) throw new InvalidOperationException("识别队列过载，已保留最后草稿。");
            capture.QueuedSamples += count;
            var level = MathF.Sqrt(samples.Sum(value => value * value) / count);
            lock (Control)
                if (capture.Id == _session) Volatile.Write(ref _snapshot, Snapshot with { Level = level });
        }
    }

    private static void Process(AudioFrame frame)
    {
        if (frame.SessionId != Interlocked.Read(ref _session) || frame.SessionId != _workerId || _decoder == null) return;
        var text = _decoder.Accept(frame.Samples);
        if (_capture?.Id == frame.SessionId) _capture.ProcessedSamples += frame.Samples.Length;
        lock (Control)
            if (frame.SessionId == _session) Volatile.Write(ref _snapshot, Snapshot with { Text = text });
    }

    private static void BeginStop(long id)
    {
        var capture = _capture;
        if (id != _session || capture?.Id != id || capture.Stopping) return;
        capture.Stopping = true; capture.StopTimer.Start();
        capture.Capture.StopRecording(); // RecordingStopped queues finalization; worker keeps draining frames.
    }

    private static void FinishStoppedCapture(CaptureSession capture, Exception? error)
    {
        if (!ReferenceEquals(_capture, capture) || capture.Id != _session) return;
        if (error != null) { Fail(capture.Id, error); return; }
        if (!capture.Stopping) { Fail(capture.Id, new IOException("麦克风意外停止。")); return; }
        // RecordingStopped follows the final DataAvailable callback. Flush the resampler's
        // short right context after all real bytes have been delivered.
        lock (capture.Gate)
        {
            var format = capture.Capture.WaveFormat;
            var padding = new byte[Math.Max(format.BlockAlign, format.AverageBytesPerSecond / 25)];
            capture.Buffer.AddSamples(padding, 0, padding.Length); DrainSamples(capture);
        }
        while (Audio.TryTake(out var frame)) Process(frame);
        if (capture.ProcessedSamples != capture.QueuedSamples) throw new InvalidOperationException("音频收尾样本不完整。");
        var final = _decoder!.Finish(isCurrent: () => capture.Id == Interlocked.Read(ref _session));
        _log?.Invoke($"ASR finalized: session={capture.Id}, deliveredBytes={capture.DeliveredBytes}, queuedSamples={capture.QueuedSamples}, processedSamples={capture.ProcessedSamples}, tailSamples={AsrDecoderSession.TailSamples}, review={_decoder.FinalReviewApplied}, reviewSelection={_decoder.FinalReviewSelection}, reviewChanged={_decoder.FinalReviewChanged}, reviewSamples={_decoder.FinalReviewSamples}, reviewMs={_decoder.FinalReviewMilliseconds:0}, stopMs={capture.StopTimer.ElapsedMilliseconds}.");
        DisposeCapture(capture); _capture = null;
        Publish(capture.Id, "就绪", final, 0);
    }

    private static void Publish(long id, string state, string text, float? level = null)
    {
        lock (Control)
            if (id == _session) Volatile.Write(ref _snapshot, new(id, state, text, level ?? Snapshot.Level, Snapshot.TestMode));
    }
    private static void Fail(long id, Exception error)
    {
        if (id != Interlocked.Read(ref _session)) return;
        _log?.Invoke($"ASR failed: session={id}, type={error.GetType().Name}, message={error.Message}");
        AbortCapture(); _decoder?.End();
        Publish(id, "错误：" + error.Message, Snapshot.Text, 0);
    }
    private static void AbortCapture()
    {
        var capture = _capture; _capture = null;
        if (capture != null) DisposeCapture(capture);
        while (Audio.TryTake(out _)) { }
    }
    private static void DisposeCapture(CaptureSession capture)
    {
        try { capture.Capture.StopRecording(); } catch { }
        capture.Capture.Dispose(); capture.Device?.Dispose();
    }
    private static void DisposeDecoder() { _decoder?.Dispose(); _decoder = null; _loadedProfile = ""; }
}
