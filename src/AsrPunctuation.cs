using System.Security.Cryptography;
using SherpaOnnx;

namespace A1IndexTTSMod;

internal interface IAsrPunctuationEngine : IDisposable
{
    string Add(string text);
}

/// <summary>Optional text-only model. Uses the already selected ASR native runtime.</summary>
internal sealed class AsrPunctuationEngine : IAsrPunctuationEngine
{
    internal const string ModelDirectory = "punctuation-ct-transformer-zh-en-int8";
    internal const string ModelSha256 = "65a3fb9f5ad7bfb96bf69e0dc4481df97f6ee60513c1d94ce981ba6effd524b1";
    private readonly OfflinePunctuation _engine;
    internal static string ModelPath(string root) => Path.Combine(root, "A1IndexTTSMod", "asr", ModelDirectory, "model.int8.onnx");
    internal AsrPunctuationEngine(string root)
    {
        var path = ModelPath(root);
        if (!File.Exists(path)) throw new FileNotFoundException("标点包未安装");
        using (var file = File.OpenRead(path))
        using (var sha = SHA256.Create())
            if (Convert.ToHexString(sha.ComputeHash(file)).ToLowerInvariant() != ModelSha256)
                throw new InvalidDataException("标点模型校验失败");
        // The quantized text model runs on one CPU thread, separately from GPU
        // acoustic decoding. Never adds a Python service or a second ASR model.
        _engine = new OfflinePunctuation(new OfflinePunctuationConfig
        {
            Model = new OfflinePunctuationModelConfig { CtTransformer = path, NumThreads = 1, Provider = "cpu" }
        });
        try { if (string.IsNullOrWhiteSpace(_engine.AddPunct("标点测试"))) throw new InvalidOperationException("标点初始化失败"); }
        catch { _engine.Dispose(); throw; }
    }
    public string Add(string text) => _engine.AddPunct(text);
    public void Dispose() => _engine.Dispose();
}

/// <summary>Only accepts inserted punctuation; lexical text is never rewritten.</summary>
internal sealed class AsrPunctuationFormatter
{
    internal const int Window = 128;
    private string _raw = "", _display = "";
    private static bool Mark(char c) => "，。？！、；：,.?!;:".Contains(c);
    internal static string Validate(string raw, string candidate, bool final)
    {
        candidate = candidate.Trim();
        if (new string(candidate.Where(c => !Mark(c)).ToArray()) != raw) return raw;
        return final ? candidate : candidate.TrimEnd('。', '？', '！', '.', '?', '!');
    }
    internal string Render(string raw, bool final, Func<string, string> add)
    {
        // Existing punctuation (including decimal points) belongs to the source.
        if (raw.Length == 0 || raw.Any(Mark)) return raw;
        var prefixLength = Math.Max(0, raw.Length - Window);
        if (prefixLength > 0 && char.IsLowSurrogate(raw[prefixLength])) prefixLength++;
        var prefix = raw[..prefixLength];
        if (prefixLength > 0 && _raw.StartsWith(prefix, StringComparison.Ordinal))
        {
            var consumed = 0; var end = 0;
            while (end < _display.Length && consumed < prefixLength) { if (!Mark(_display[end])) consumed++; end++; }
            if (consumed == prefixLength) prefix = _display[..end];
        }
        else if (prefixLength > 0)
        {
            // A large first result or a revision outside the active window:
            // rebuild bounded text chunks, never resubmit audio.
            var parts = new List<string>();
            for (var offset = 0; offset < prefixLength;)
            {
                var length = Math.Min(Window, prefixLength - offset);
                if (char.IsHighSurrogate(raw[offset + length - 1])) length--;
                if (length == 0) length = 2;
                var part = raw.Substring(offset, length);
                parts.Add(Validate(part, add(part), false)); offset += length;
            }
            prefix = string.Concat(parts);
        }
        var tail = raw[prefixLength..];
        _raw = raw; _display = prefix + Validate(tail, add(tail), final);
        return _display;
    }
}

/// <summary>Latest-only background text work; acoustic frames never wait for it.</summary>
internal sealed class AsrPunctuationService : IDisposable
{
    private sealed record Request(long Session, string Raw, bool Final, long Version, TaskCompletionSource<string>? Completion);
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Func<IAsrPunctuationEngine> _factory;
    private readonly Action<long, string, string> _publish;
    private readonly Action<string>? _log;
    private Request? _pending;
    private long _session, _version;
    private bool _disposed, _failed;
    private string _lastRaw = "";
    private string _status = "标点预热中";
    internal string Status => Volatile.Read(ref _status);
    internal AsrPunctuationService(Func<IAsrPunctuationEngine> factory, Action<long, string, string> publish, Action<string>? log = null)
    {
        _factory = factory; _publish = publish; _log = log;
        new Thread(Run) { IsBackground = true, Name = "A1 ASR punctuation" }.Start();
    }
    internal void Begin(long session)
    {
        lock (_gate) { _pending?.Completion?.TrySetResult(_pending.Raw); _pending = null; _session = session; _version++; _lastRaw = ""; }
    }
    internal void Submit(long session, string raw)
    {
        lock (_gate)
        {
            if (_disposed || _failed || session != _session || raw.Length == 0 || raw == _lastRaw) return;
            _lastRaw = raw; _pending = new(session, raw, false, ++_version, null); _wake.Set();
        }
    }
    internal async Task<string> Finish(long session, string raw, int timeoutMs = 500)
    {
        Task<string> task;
        lock (_gate)
        {
            if (_disposed || _failed || session != _session || raw.Length == 0) return raw;
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = new(session, raw, true, ++_version, completion); task = completion.Task; _wake.Set();
        }
        // Slow initialization/inference never prevents sending. Late results
        // have no authority to update a finalized or user-edited draft.
        return await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false) == task ? await task.ConfigureAwait(false) : raw;
    }
    private void Run()
    {
        IAsrPunctuationEngine? engine = null;
        try
        {
            engine = _factory(); Volatile.Write(ref _status, "自动标点 · 已就绪");
            long renderedSession = -1; var formatter = new AsrPunctuationFormatter(); var lastPartial = DateTime.MinValue;
            while (true)
            {
                _wake.WaitOne(50);
                Request? request;
                lock (_gate)
                {
                    if (_disposed) break;
                    request = _pending;
                    if (request == null || (!request.Final && DateTime.UtcNow - lastPartial < TimeSpan.FromMilliseconds(600))) continue;
                    _pending = null;
                }
                if (request.Session != renderedSession) { formatter = new(); renderedSession = request.Session; }
                var display = formatter.Render(request.Raw, request.Final, engine.Add);
                if (!request.Final) lastPartial = DateTime.UtcNow;
                lock (_gate)
                {
                    if (!_disposed && request.Session == _session && request.Version == _version)
                    {
                        if (!request.Final) _publish(request.Session, request.Raw, display);
                        request.Completion?.TrySetResult(display);
                    }
                    else request.Completion?.TrySetResult(request.Raw);
                }
            }
        }
        catch (Exception e)
        {
            lock (_gate) { _failed = true; _pending?.Completion?.TrySetResult(_pending.Raw); _pending = null; }
            Volatile.Write(ref _status, e is FileNotFoundException ? "自动标点 · 未安装（可继续输入）" : "自动标点 · 不可用（可继续输入）");
            _log?.Invoke($"ASR punctuation unavailable: type={e.GetType().Name}.");
        }
        finally { engine?.Dispose(); _wake.Dispose(); }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _pending?.Completion?.TrySetResult(_pending.Raw); _pending = null; if (!_failed) _wake.Set(); }
    }
}
