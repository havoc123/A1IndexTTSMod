using A1IndexTTSMod;
using System.Collections.Concurrent;

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; }
async Task Wait(Func<bool> ready) {
    using var timeout = new CancellationTokenSource(3000);
    while (!ready()) await Task.Delay(10, timeout.Token);
}
Check(AsrPunctuationFormatter.Validate("你好吗", "你好吗？", false) == "你好吗", "partial hides terminal punctuation");
Check(AsrPunctuationFormatter.Validate("你好吗", "你好吗？", true) == "你好吗？", "final keeps question mark");
Check(AsrPunctuationFormatter.Validate("你好谢谢", "你好，谢谢。", true) == "你好，谢谢。", "insert comma and period");
Check(AsrPunctuationFormatter.Validate("你好", "您好。", true) == "你好", "reject lexical rewrite");
Check(AsrPunctuationFormatter.Validate("你好", "", true) == "你好", "reject empty result");
Check(AsrPunctuationFormatter.Validate("Hello world", "hello world.", true) == "Hello world", "preserve case");
Check(AsrPunctuationFormatter.Validate("你好世界", "你好 世界。", true) == "你好世界", "preserve whitespace");
var formatter = new AsrPunctuationFormatter();
Check(formatter.Render("价格3.14元", true, _ => throw new Exception()) == "价格3.14元", "existing decimal untouched");
Check(formatter.Render("你好，朋友", true, _ => throw new Exception()) == "你好，朋友", "existing punctuation untouched");
var longText = string.Concat(Enumerable.Repeat("今天晴天", 120));
var lengths = new List<int>();
string Add(string text) { lengths.Add(text.Length); return text + "。"; }
Check(formatter.Render(longText, true, Add).Replace("。", "") == longText && lengths.All(n => n <= 128), "bounded long text preserves characters");
lengths.Clear(); formatter.Render(longText + "明天呢", true, Add);
Check(lengths.Count == 1, "cached prefix only reprocesses tail");
lengths.Clear(); var revised = "明天" + longText[2..];
Check(formatter.Render(revised, true, Add).Replace("。", "") == revised && lengths.Count > 1, "earlier ASR revision rebuilds text prefix");
var unicode = string.Concat(Enumerable.Repeat("你😀好", 75));
Check(formatter.Render(unicode, true, text => {
    Check(!char.IsLowSurrogate(text[0]) && !char.IsHighSurrogate(text[^1]), "surrogate boundary intact");
    return text + "。";
}).Replace("。", "") == unicode, "unicode preserved");

var published = new ConcurrentQueue<(long Session, string Raw, string Display)>();
var initialization = new ManualResetEventSlim();
using (var service = new AsrPunctuationService(() => { initialization.Wait(); return new Fake(text => text + "。"); }, (s,r,d) => published.Enqueue((s,r,d)))) {
    service.Begin(1); service.Submit(1, "旧文字"); service.Submit(1, "新文字"); initialization.Set();
    await Wait(() => !published.IsEmpty);
    Check(published.Count == 1 && published.TryPeek(out var p) && p.Raw == "新文字", "latest pending partial wins");
    Check(await service.Finish(1, "你好") == "你好。", "final text punctuation");
    service.Begin(2);
    Check(await service.Finish(1, "旧文字") == "旧文字", "old session final ignored");
}
published.Clear();
var started = new ManualResetEventSlim(); var release = new ManualResetEventSlim(); var exited = new ManualResetEventSlim();
using (var service = new AsrPunctuationService(() => new Fake(text => { started.Set(); release.Wait(); exited.Set(); return text + "。"; }), (s,r,d) => published.Enqueue((s,r,d)))) {
    service.Begin(10); service.Submit(10, "原文字"); await Wait(() => started.IsSet);
    // Manual editing/cancel advances the session before old text finishes.
    service.Begin(11); release.Set(); await Wait(() => exited.IsSet); await Task.Delay(70);
    Check(published.IsEmpty, "cancelled inference cannot overwrite edited draft");
}
started.Reset(); release.Reset(); exited.Reset();
using (var service = new AsrPunctuationService(() => new Fake(text => { started.Set(); release.Wait(); exited.Set(); return text + "。"; }), (s,r,d) => published.Enqueue((s,r,d)))) {
    service.Begin(20);
    var result = service.Finish(20, "慢结果", 50); await Wait(() => started.IsSet);
    Check(await result == "慢结果", "timeout returns raw draft for send");
    release.Set(); await Wait(() => exited.IsSet); await Task.Delay(70);
    Check(published.IsEmpty, "late final result never publishes partial");
}
using (var service = new AsrPunctuationService(() => throw new FileNotFoundException(), (_,_,_) => throw new Exception())) {
    await Wait(() => service.Status.Contains("未安装")); service.Begin(30);
    Check(await service.Finish(30, "仍可发送") == "仍可发送", "missing optional package does not block send");
}
using (var service = new AsrPunctuationService(() => new Fake(_ => throw new InvalidOperationException()), (_,_,_) => throw new Exception())) {
    service.Begin(40); service.Submit(40, "失败文字"); await Wait(() => service.Status.Contains("不可用"));
    Check(await service.Finish(40, "仍可发送") == "仍可发送", "inference fault does not block send");
}
var gate = new AsrSendGate(); var snapshot = new AsrSnapshot(50, "录音中", "你好", 0, false);
Check(gate.Request(snapshot, 100), "send requests finish");
Check(gate.Poll(snapshot with { State="收尾中" }, 100, true) == AsrSendDecision.Wait, "send waits for tail and punctuation");
Check(gate.Poll(snapshot with { State="就绪", Text="你好。" }, 100, true) == AsrSendDecision.Send, "send completed punctuated draft");
Check(gate.Poll(snapshot, 100, true) == AsrSendDecision.Wait, "send once");
gate.Request(snapshot, 100);
Check(gate.Poll(snapshot with { SessionId=51, State="就绪" }, 100, true) == AsrSendDecision.Cancel, "cancel generation cancels send");
gate.Request(snapshot, 100);
Check(gate.Poll(snapshot with { State="就绪" }, 100, false) == AsrSendDecision.Cancel, "manual edit cancels send");
Console.WriteLine($"PASS {passed} functional checks; no acoustic model, microphone or private samples.");

sealed class Fake : IAsrPunctuationEngine {
    private readonly Func<string,string> _add;
    internal Fake(Func<string,string> add) => _add = add;
    public string Add(string text) => _add(text);
    public void Dispose() { }
}
namespace A1IndexTTSMod {
    internal sealed record AsrSnapshot(long SessionId, string State, string Text, float Level, bool TestMode) {
        internal bool IsActive => State is "准备中" or "录音中" or "收尾中";
    }
}
