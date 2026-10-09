using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using A1IndexTTSMod;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

string? Option(string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
var root = Option("--game-root") ?? throw new ArgumentException("--game-root required");
var profile = AsrModelProfiles.Resolve(Option("--profile") ?? "lightweight14m");
if (args.Contains("--warm-only"))
{
    using var process = Process.GetCurrentProcess();
    void Resource(string stage, object? extra = null)
    {
        process.Refresh();
        Console.WriteLine(JsonSerializer.Serialize(new { stage, pid = process.Id, profile = profile.Id,
            workingSetMiB = process.WorkingSet64 / 1048576.0, privateMiB = process.PrivateMemorySize64 / 1048576.0,
            peakWorkingSetMiB = process.PeakWorkingSet64 / 1048576.0, cpuSeconds = process.TotalProcessorTime.TotalSeconds, extra }));
    }
    Resource("baseline");
    var watch = Stopwatch.StartNew();
    using var engine = new AsrDecoderSession(root, profile, runtimeOverride: Option("--runtime-dir"));
    Resource("ready", new { loadMs = watch.Elapsed.TotalMilliseconds, native = engine.NativeRevision, hotwords = engine.HotwordCount });
    await Task.Delay(int.Parse(Option("--hold-seconds") ?? "15") * 1000);
    Resource("idle");
    return;
}
if (args.Contains("--tokenize-only"))
{
    var tokenizer = new AsrBpeTokenizer(Path.Combine(profile.ModelDirectory(root), "bpe.model"), Path.Combine(profile.ModelDirectory(root), "tokens.txt"));
    var words = File.ReadLines(Path.Combine(root, "A1IndexTTSMod", "config", "asr-hotwords.zh-CN.txt"))
        .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).Distinct().Take(96);
    Console.WriteLine(JsonSerializer.Serialize(words.Select(word => new { word, tokens = tokenizer.Encode(word), middleTokens = tokenizer.Encode(word, false) })));
    return;
}
if (args.Contains("--capture"))
{
    StreamingAsr.ConfigureRoot(root); StreamingAsr.SetLog(Console.Error.WriteLine);
    if (!StreamingAsr.SetProfile(profile.Id)) throw new Exception("Capture model is not installed.");
    StreamingAsr.SetEnabled(true);
    async Task WaitFor(Func<AsrSnapshot, bool> predicate, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate(StreamingAsr.Snapshot))
        {
            if (StreamingAsr.State.StartsWith("错误")) throw new Exception(StreamingAsr.State);
            if (timer.ElapsedMilliseconds > milliseconds) throw new TimeoutException(StreamingAsr.State);
            await Task.Delay(20);
        }
    }
    StreamingAsr.Start(true); await WaitFor(s => s.State == "录音中", 90000);
    var initialId = StreamingAsr.Snapshot.SessionId;
    if (args.Contains("--cancel-restart"))
    {
        await Task.Delay(200); StreamingAsr.Cancel(); StreamingAsr.Start(true);
        await WaitFor(s => s.State == "录音中" && s.SessionId > initialId, 90000);
    }
    await Task.Delay(600); var expectedId = StreamingAsr.Snapshot.SessionId;
    StreamingAsr.Stop(); StreamingAsr.Stop(); await WaitFor(s => s.State == "就绪", 10000);
    if (StreamingAsr.Snapshot.SessionId != expectedId) throw new Exception("Final result belongs to the wrong session.");
    Console.WriteLine(JsonSerializer.Serialize(new { test = "capture-stop", profile = profile.Id, initialId, expectedId, state = StreamingAsr.State, passed = true }));
    StreamingAsr.Shutdown(); return;
}
var wav = Option("--wav");
var casesPath = Option("--cases");
var cases = casesPath != null ? JsonSerializer.Deserialize<QualityCase[]>(File.ReadAllText(casesPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
    : new[] { new QualityCase { Name = Path.GetFileName(wav), Wav = wav ?? throw new ArgumentException("--wav or --cases required, or use --capture"), Reference = Option("--reference") } };
var hotwords = !args.Contains("--no-hotwords"); var greedy = args.Contains("--greedy");
float? hotwordScore = Option("--hotword-score") is string score ? float.Parse(score, CultureInfo.InvariantCulture) : null;
float? rareHotwordScore = Option("--rare-hotword-score") is string rareScore ? float.Parse(rareScore, CultureInfo.InvariantCulture) : null;
var maxActivePaths = int.Parse(Option("--paths") ?? "4", CultureInfo.InvariantCulture);
if (maxActivePaths < 1 || maxActivePaths > 32) throw new ArgumentException("--paths must be between 1 and 32.");
if (hotwordScore.HasValue && (!float.IsFinite(hotwordScore.Value) || hotwordScore <= 0)) throw new ArgumentException("--hotword-score must be finite and positive.");
if (rareHotwordScore.HasValue && (!float.IsFinite(rareHotwordScore.Value) || rareHotwordScore <= 0)) throw new ArgumentException("--rare-hotword-score must be finite and positive.");
var timer = Stopwatch.StartNew();
using var decoder = new AsrDecoderSession(root, profile, hotwords, greedy ? "greedy_search" : "modified_beam_search", args.Contains("--debug"), hotwordScore, maxActivePaths, rareHotwordScore, Option("--runtime-dir"));
if (Option("--expected-hotwords") is string expectedHotwords && (decoder.HotwordCount != int.Parse(expectedHotwords, CultureInfo.InvariantCulture) || decoder.SkippedHotwordCount != 0))
    throw new Exception($"Hotword coverage mismatch: {decoder.HotwordCount} enabled, {decoder.SkippedHotwordCount} skipped.");
var loadMs = timer.Elapsed.TotalMilliseconds;
if (Option("--expected-native-revision") is string nativeRevision && decoder.NativeRevision != nativeRevision)
    throw new Exception($"Native runtime mismatch: expected {nativeRevision}, got {decoder.NativeRevision}.");
if (args.Contains("--cancel-review-test"))
{
    decoder.Begin(); decoder.Accept(new float[16000]); decoder.Accept(new float[16000]);
    var checks = 0;
    try
    {
        decoder.Finish(isCurrent: () => ++checks < 2);
        throw new Exception("Native final review ignored cancellation.");
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("PASS native review cancellation; following WAV cases reuse the resident model."); }
}
foreach (var testCase in cases)
{
    timer.Restart(); decoder.Begin();
    using var reader = new WaveFileReader(testCase.Wav);
    ISampleProvider input = reader.ToSampleProvider();
    if (input.WaveFormat.Channels == 2) input = new StereoToMonoSampleProvider(input);
    if (input.WaveFormat.SampleRate != 16000) input = new WdlResamplingSampleProvider(input, 16000);
    var buffer = new float[1600]; int count; long samples = 0; var partials = new List<string>(); string previous = "";
    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
    {
        samples += count; var text = decoder.Accept(buffer.AsSpan(0, count).ToArray());
        if (text != previous) { partials.Add(text); previous = text; }
    }
    var stopTimer = Stopwatch.StartNew(); var final = decoder.Finish(!args.Contains("--no-tail"), verifyStableResult: true, review: !args.Contains("--no-review"));
    var stopMs = stopTimer.Elapsed.TotalMilliseconds;
    if (samples != decoder.SessionSamples) throw new Exception("Input sample count mismatch.");
    var decodeMs = timer.Elapsed.TotalMilliseconds;
    double? cer = null;
    var reference = testCase.Reference;
    if (reference != null)
    {
        string Normalize(string text) => Regex.Replace(text, @"[\p{P}\p{Z}\s]", "");
        var expected = Normalize(reference); var actual = Normalize(final);
        var previousRow = Enumerable.Range(0, actual.Length + 1).ToArray();
        for (var i = 1; i <= expected.Length; i++)
        {
            var row = new int[actual.Length + 1]; row[0] = i;
            for (var j = 1; j <= actual.Length; j++) row[j] = Math.Min(Math.Min(row[j - 1] + 1, previousRow[j] + 1), previousRow[j - 1] + (expected[i - 1] == actual[j - 1] ? 0 : 1));
            previousRow = row;
        }
        cer = expected.Length == 0 ? null : (double)previousRow[actual.Length] / expected.Length;
    }
    Console.WriteLine(JsonSerializer.Serialize(new { name = testCase.Name, reference, expectedWords = testCase.ExpectedWords, group = testCase.Group,
        paths = maxActivePaths, hotwordScore, rareHotwordScore, nativeRevision = decoder.NativeRevision, profile = profile.Id, decoding = greedy ? "greedy_search" : "modified_beam_search", hotwords = decoder.HotwordCount,
        skippedHotwords = decoder.SkippedHotwordCount, loadMs, decodeMs, stopMs, samples,
        acceptedSamples = decoder.SessionSamples, rtf = decodeMs / 1000 / (samples / 16000.0),
        streamingFinal = decoder.StreamingFinal, review = decoder.FinalReviewApplied, reviewSamples = decoder.FinalReviewSamples,
        reviewFinal = decoder.ReviewFinal, reviewSelection = decoder.FinalReviewSelection,
        reviewChanged = decoder.FinalReviewChanged, reviewMs = decoder.FinalReviewMilliseconds,
        final, cer, partials, tokens = decoder.Tokens }));
    if (Option("--max-cer") is string maxCer && (!cer.HasValue || cer > double.Parse(maxCer, CultureInfo.InvariantCulture)))
        throw new Exception($"CER exceeds --max-cer: {cer}.");
}

internal sealed class QualityCase
{
    public string? Name { get; set; }
    public string Wav { get; set; } = "";
    public string? Reference { get; set; }
    public string[] ExpectedWords { get; set; } = Array.Empty<string>();
    public string Group { get; set; } = "";
}
