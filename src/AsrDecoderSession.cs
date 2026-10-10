using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Globalization;
using System.Diagnostics;
using SherpaOnnx;

namespace A1IndexTTSMod;

/// <summary>Shared by the game and WAV quality tool; one owner thread per instance.</summary>
internal sealed class AsrDecoderSession : IDisposable
{
    private static readonly object NativeGate = new();
    private static string? _runtimeDirectory;
    private static IntPtr _nativeHandle;
    private readonly OnlineRecognizer _recognizer;
    private readonly bool _usesTokenAliases;
    private OnlineStream? _stream;
    private readonly List<float[]> _reviewAudio = new();
    private readonly bool _supportsReview;
    private readonly bool _enableReplay;
    internal bool SupportsReview => _supportsReview;
    private readonly string[] _hotwords;
    private bool _canReview;
    internal const int MaximumReviewSamples = 16000 * 61;
    internal string StreamingFinal { get; private set; } = "";
    internal string ReviewFinal { get; private set; } = "";
    internal string FinalReviewSelection { get; private set; } = "not-run";
    internal bool FinalReviewApplied { get; private set; }
    internal bool FinalReviewChanged => FinalReviewApplied && StreamingFinal != Text;
    internal long FinalReviewSamples { get; private set; }
    internal double FinalReviewMilliseconds { get; private set; }
    internal string Text { get; private set; } = "";
    internal string[] Tokens { get; private set; } = Array.Empty<string>();
    internal int HotwordCount { get; }
    internal int SkippedHotwordCount { get; }
    internal long AcceptedSamples { get; private set; }
    internal long SessionSamples { get; private set; }
    internal const int TailSamples = 9600;
    internal string NativeRevision { get; private set; } = "upstream";

    internal AsrDecoderSession(string gameRoot, AsrModelProfile profile, bool useHotwords = true,
        string decodingMethod = "modified_beam_search", bool debug = false, float? hotwordScore = null,
        int maxActivePaths = 4, float? rareHotwordScore = null, string? runtimeOverride = null, string provider = "cuda", int device = 0, string? ortProfilePrefix = null, bool enableReplay = false)
    {
        if (provider is not ("cuda" or "directml") || device < 0 || device > 15) throw new ArgumentException("Unsupported ASR GPU provider/device.");
        if (!GpuRouting.AllowsModel(provider, profile)) throw new NotSupportedException("DirectML 仅允许 14M 流式模型。");
        if (!profile.IsInstalled(gameRoot)) throw new FileNotFoundException($"{profile.Label} 模型未完整安装。");
        var runtime = Path.GetFullPath(runtimeOverride ?? GpuRouting.RuntimeDirectory(gameRoot, provider));
        EnsureNative(runtime, gameRoot, provider);
        ResetGpuFunction? resetGpu = null; GpuCountFunction? gpuCount = null;
        var native = NativeLibrary.Load(Path.Combine(runtime, "sherpa-onnx-c-api.dll"));
        try
        {
            if (NativeLibrary.TryGetExport(native, "A1SherpaOnnxHotwordRevision", out var revision))
                NativeRevision = Marshal.PtrToStringAnsi(Marshal.GetDelegateForFunctionPointer<RevisionFunction>(revision)()) ?? "unknown";
            if (!NativeLibrary.TryGetExport(native, "A1SherpaOnnxResetGpuSessions", out var reset) ||
                !NativeLibrary.TryGetExport(native, "A1SherpaOnnxGpuSessions", out var count))
                throw new NotSupportedException("ASR 原生运行库需要升级到 v3，才能验证 GPU 初始化。");
            resetGpu = Marshal.GetDelegateForFunctionPointer<ResetGpuFunction>(reset);
            gpuCount = Marshal.GetDelegateForFunctionPointer<GpuCountFunction>(count);
        }
        finally { NativeLibrary.Free(native); }
        _supportsReview = NativeRevision == "a1-context-before-topk-finalize-v3" && decodingMethod == "modified_beam_search";
        _enableReplay = enableReplay;
        var directory = profile.ModelDirectory(gameRoot);
        var tokens = Path.Combine(directory, "tokens.txt");
        var hotwords = PrepareHotwords(gameRoot, profile, tokens, useHotwords && decodingMethod != "greedy_search", hotwordScore, rareHotwordScore,
            NativeRevision is "a1-context-before-topk-finalize-v1" or "a1-context-before-topk-finalize-v2" or "a1-context-before-topk-finalize-v3");
        HotwordCount = hotwords.Count; SkippedHotwordCount = hotwords.Skipped;
        _hotwords = hotwords.Words;
        _usesTokenAliases = hotwords.Tokens != tokens;
        var configuredProvider = provider;
        if (ortProfilePrefix != null)
        {
            var prefix = Path.GetFullPath(ortProfilePrefix);
            if (prefix.Contains('\n') || prefix.Contains('\r')) throw new ArgumentException("Invalid profiling path.");
            Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
            var providerFile = prefix + ".config";
            File.WriteAllText(providerFile, "ProfilingFilePrefix=" + prefix + "\n");
            configuredProvider += ":" + providerFile;
        }
        var config = new OnlineRecognizerConfig
        {
            FeatConfig = new FeatureConfig { SampleRate = 16000, FeatureDim = 80 },
            ModelConfig = new OnlineModelConfig
            {
                NumThreads = 1, Provider = configuredProvider, Tokens = hotwords.Tokens,
                ModelingUnit = "cjkchar", Debug = debug ? 1 : 0,
                Transducer = new OnlineTransducerModelConfig
                {
                    Encoder = Path.Combine(directory, profile.Encoder), Decoder = Path.Combine(directory, profile.Decoder),
                    Joiner = Path.Combine(directory, profile.Joiner)
                }
            },
            DecodingMethod = decodingMethod, MaxActivePaths = maxActivePaths, EnableEndpoint = 0,
            HotwordsFile = hotwords.Path, HotwordsScore = 1.5f
        };
        resetGpu!(device);
        _recognizer = new OnlineRecognizer(config);
        if (gpuCount!() < 3) { _recognizer.Dispose(); throw new InvalidOperationException($"{provider} GPU 初始化失败，未启用 CPU 回退。"); }
        // CUDA/cuDNN may spend seconds preparing kernels on its first Decode. Warm
        // them before microphone capture, so that cold work cannot overflow audio.
        try { Begin(); Accept(new float[16000]); Finish(); AcceptedSamples = 0; SessionSamples = 0; Text = ""; }
        catch { Dispose(); throw; }
    }

    internal void Begin()
    {
        _stream?.Dispose(); _stream = _recognizer.CreateStream(); Text = ""; Tokens = Array.Empty<string>(); SessionSamples = 0;
        _reviewAudio.Clear(); _canReview = _supportsReview && _enableReplay;
        StreamingFinal = ""; ReviewFinal = ""; FinalReviewSelection = "not-run";
        FinalReviewApplied = false; FinalReviewSamples = 0; FinalReviewMilliseconds = 0;
    }

    internal string Accept(float[] samples)
    {
        if (_stream == null) throw new InvalidOperationException("ASR stream is not active.");
        _stream.AcceptWaveform(16000, samples); AcceptedSamples += samples.Length; SessionSamples += samples.Length;
        if (_canReview)
        {
            if (SessionSamples <= MaximumReviewSamples) _reviewAudio.Add(samples);
            else { _reviewAudio.Clear(); _canReview = false; }
        }
        DecodeReady(); return Text;
    }

    internal string Finish(bool padTail = true, bool verifyStableResult = false, bool review = false, Func<bool>? isCurrent = null)
    {
        if (_stream == null) throw new InvalidOperationException("ASR stream is not active.");
        try
        {
            if (padTail) _stream.AcceptWaveform(16000, new float[TailSamples]);
            _stream.InputFinished(); DecodeReady(finalResult: true);
            StreamingFinal = Text;
            var streamingTokens = Tokens;
            if (review && _canReview && SessionSamples > 0)
            {
                var timer = Stopwatch.StartNew();
                _stream.Dispose(); _stream = _recognizer.CreateStream();
                // Public stream option implemented by our pinned native v2. The
                // existing recognizer/model is reused; no second set of weights.
                _stream.SetOption("a1_final_paths", "8");
                foreach (var samples in _reviewAudio)
                {
                    if (isCurrent?.Invoke() == false) throw new OperationCanceledException("ASR review superseded");
                    _stream.AcceptWaveform(16000, samples); DecodeReady();
                    FinalReviewSamples += samples.Length;
                }
                if (padTail) _stream.AcceptWaveform(16000, new float[TailSamples]);
                _stream.InputFinished(); DecodeReady(finalResult: true);
                if (FinalReviewSamples != SessionSamples) throw new InvalidDataException("Final ASR review did not receive the complete recording.");
                FinalReviewApplied = true; FinalReviewMilliseconds = timer.Elapsed.TotalMilliseconds;
                ReviewFinal = Text;
            }
            if (verifyStableResult)
            {
                var repeated = _recognizer.GetResult(_stream);
                if (repeated.Text != Text || !repeated.Tokens.SequenceEqual(Tokens))
                    throw new InvalidDataException("Repeated final ASR result changed without new audio.");
            }
            if (FinalReviewApplied)
            {
                if (AsrFinalSelection.KeepStreaming(StreamingFinal, ReviewFinal, _hotwords))
                {
                    Text = StreamingFinal; Tokens = streamingTokens; FinalReviewSelection = "keep-complete-hotwords";
                }
                else FinalReviewSelection = "review";
            }
            return Text;
        }
        finally { End(); }
    }

    private void DecodeReady(bool finalResult = false)
    {
        while (_recognizer.IsReady(_stream!)) _recognizer.Decode(_stream!);
        var result = _recognizer.GetResult(_stream!);
        // A byte-fallback character may span several streaming steps. Hide its
        // incomplete UTF-8 placeholder in drafts; final text remains unmodified.
        Text = finalResult ? result.Text : result.Text.Replace("\ufffd", ""); Tokens = result.Tokens;
        if (_usesTokenAliases && Tokens.Any(token => token.Any(c => c >= 0xe000 && c < 0xe000 + 2000)))
            throw new InvalidDataException("ASR 原生词表别名未保留原始输出，请核对 sherpa-onnx 1.13.8 运行库。");
    }

    internal void End() { _stream?.Dispose(); _stream = null; _reviewAudio.Clear(); }
    public void Dispose() { End(); _recognizer.Dispose(); }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr RevisionFunction();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ResetGpuFunction(int device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GpuCountFunction();

    private static (string Path, int Count, int Skipped, string Tokens, string[] Words) PrepareHotwords(string root, AsrModelProfile profile,
        string tokensPath, bool enabled, float? hotwordScore, float? rareHotwordScore, bool contextualPruning)
    {
        var source = Path.Combine(root, "A1IndexTTSMod", "config", "asr-hotwords.zh-CN.txt");
        var target = Path.Combine(root, "A1IndexTTSMod", ".state", "asr-hotwords", profile.Id);
        Directory.CreateDirectory(target);
        AsrBpeTokenizer? bpe = profile.UsesBpe ? new AsrBpeTokenizer(Path.Combine(profile.ModelDirectory(root), "bpe.model"), tokensPath) : null;
        var vocab = bpe == null ? "" : Path.Combine(target, "bpe.vocab");
        if (bpe != null) bpe.ExportVocabulary(vocab);
        if (!enabled || !File.Exists(source)) return ("", 0, 0, tokensPath, Array.Empty<string>());
        var nativeTokens = bpe == null ? tokensPath : Path.Combine(target, "native-tokens.txt");
        if (bpe != null) bpe.ExportNativeTokens(tokensPath, nativeTokens);
        var symbols = File.ReadLines(tokensPath).Select(line => line.Split(' ')[0]).ToHashSet(StringComparer.Ordinal);
        var valid = new List<string>(); var skipped = new List<string>(); var encoded = new List<string>(); var paths = new List<string>();
        var words = new List<string>();
        foreach (var word in File.ReadLines(source).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).Distinct().Take(96))
        {
            string[] pieces;
            try { pieces = bpe?.Encode(word) ?? word.EnumerateRunes().Select(rune => rune.ToString()).ToArray(); }
            catch (ArgumentException) { skipped.Add(word); continue; }
            var middle = bpe?.Encode(word, false) ?? pieces;
            if (pieces.Concat(middle).Any(piece => !symbols.Contains(piece) || piece == "<unk>")) { skipped.Add(word); continue; }
            // The byte sequences for rare Chinese spelling have a much lower acoustic
            // prior. This profile-specific compensation was checked with positive and
            // ordinary-speech controls; it does not replace text after recognition.
            var isRare = pieces.Concat(middle).Any(piece => piece.StartsWith("<0x", StringComparison.Ordinal));
            var wordScore = isRare && rareHotwordScore.HasValue ? rareHotwordScore.Value : hotwordScore ??
                (bpe == null ? (contextualPruning ? 2.5f : 1.5f) : isRare ? (contextualPruning ? 5f : 6f) : 3f);
            // Byte fallback takes more tokens. Keep a word's total boost proportional to
            // its character count so that rare spelling does not multiply its score.
            var score = wordScore * word.EnumerateRunes().Count() / pieces.Length;
            valid.Add(word + " :" + score.ToString("R", CultureInfo.InvariantCulture));
            words.Add(word);
            encoded.Add(word + "\t" + string.Join(" ", pieces) + "\t" + score.ToString("R", CultureInfo.InvariantCulture));
            if (bpe == null) paths.Add(valid[^1]);
            else
            {
                paths.Add(bpe.NativePath(pieces) + " :" + score.ToString("R", CultureInfo.InvariantCulture));
                // Re-encode without the boundary: it may be fused with a Chinese
                // piece, so simply dropping the first token would lose characters.
                var middleScore = wordScore * word.EnumerateRunes().Count() / middle.Length;
                paths.Add(bpe.NativePath(middle) + " :" + middleScore.ToString("R", CultureInfo.InvariantCulture));
                encoded.Add(word + " [句中]\t" + string.Join(" ", middle) + "\t" + middleScore.ToString("R", CultureInfo.InvariantCulture));
            }
        }
        File.WriteAllLines(Path.Combine(target, "skipped.txt"), skipped, new UTF8Encoding(false));
        File.WriteAllLines(Path.Combine(target, "encoded.tsv"), encoded, new UTF8Encoding(false));
        File.WriteAllLines(Path.Combine(target, "validated.txt"), valid, new UTF8Encoding(false));
        var path = Path.Combine(target, "native-paths.txt"); File.WriteAllLines(path, paths, new UTF8Encoding(false));
        return (valid.Count == 0 ? "" : path, valid.Count, skipped.Count, nativeTokens, words.ToArray());
    }

    internal static void EnsureNative(string runtime, string root, string provider)
    {
        lock (NativeGate)
        {
            if (_runtimeDirectory != null)
            {
                if (_runtimeDirectory != runtime) throw new InvalidOperationException("Cannot mix ASR native runtime directories in one process.");
                return;
            }
            if (!File.Exists(Path.Combine(runtime, "sherpa-onnx-c-api.dll")) || !File.Exists(Path.Combine(runtime, "onnxruntime.dll")))
                throw new FileNotFoundException("ASR 原生运行库未安装。");
            var directories = new[] { runtime, root }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
            var dependencies = provider == "directml" ? new[] { "DirectML.dll" } : new[] { "cudnn64_9.dll", "cublas64_12.dll", "cublasLt64_12.dll", "cudart64_12.dll" };
            var missing = dependencies
                .Where(name => !directories.Any(dir => File.Exists(Path.Combine(dir, name)))).ToArray();
            if (missing.Length > 0) throw new DllNotFoundException(provider + " 依赖缺失：" + string.Join(", ", missing));
            Environment.SetEnvironmentVariable("PATH", runtime + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
            _nativeHandle = NativeLibrary.Load(Path.Combine(runtime, "sherpa-onnx-c-api.dll"));
            NativeLibrary.SetDllImportResolver(typeof(OnlineRecognizer).Assembly, ResolveNative);
            _runtimeDirectory = runtime;
        }
    }

    private static IntPtr ResolveNative(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == "sherpa-onnx-c-api" ? NativeLibrary.Load(Path.Combine(_runtimeDirectory!, "sherpa-onnx-c-api.dll")) : IntPtr.Zero;
}
