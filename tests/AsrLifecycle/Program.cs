using A1IndexTTSMod;

StreamingAsr.ConfigureRoot(@"E:\Program Files (x86)\Steam\steamapps\common\A1");
StreamingAsr.SetLog(Console.WriteLine);
async Task Until(Func<bool> ready)
{
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (!ready()) { if (DateTime.UtcNow > deadline) throw new Exception("ASR state timeout: " + StreamingAsr.State); await Task.Delay(10); }
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
var words = new[] { "苏倾盏", "焚天宗", "江楚弦" };
Assert(AsrFinalSelection.KeepStreaming("苏倾盏你在这里吗", "苏青产拟在这里吗", words), "Review discarded a completed name");
Assert(AsrFinalSelection.KeepStreaming("苏倾盏去焚天宗", "苏倾盏去风天宗", words), "Review discarded one of multiple contextual words");
Assert(AsrFinalSelection.KeepStreaming("苏倾盏你听到了吗苏倾盏", "苏倾盏你听到了吗", words), "Review discarded a repeated contextual word");
Assert(!AsrFinalSelection.KeepStreaming("延展这条路向前走", "沿着这条路向前走", words), "Ordinary speech correction was blocked");
Assert(!AsrFinalSelection.KeepStreaming("共享请尽小蝶和姜楚贤帮忙", "共享请进小蝶和江楚弦帮忙", words), "Review could not add an acoustically decoded name");
Assert(!AsrFinalSelection.KeepStreaming("苏青展你在这里吗", "苏清展你在这里吗", words), "Selection invented a dictionary match for two wrong spellings");
Console.WriteLine("PASS final hypothesis selection preserves completed hotwords and permits whole-audio corrections");
if (!StreamingAsr.SetProfile("accurate160m")) throw new Exception("Installed test profile unavailable");
await Task.Delay(100);
Assert(AsrDecoderSession.Created == 0, "Disabled ASR loaded a model during configuration");
StreamingAsr.SetEnabled(true);
await Until(() => StreamingAsr.State == "就绪");
Assert(!StreamingAsr.IsActive, "Startup warm-up opened a recording session");
Assert(AsrDecoderSession.FirstProfile == "accurate160m", "Startup loaded the default instead of the configured model");
Console.WriteLine("PASS startup ASR readiness without opening microphone");
var created = AsrDecoderSession.Created;
StreamingAsr.SetForeground(false);
await Task.Delay(31000);
Assert(StreamingAsr.State == "就绪" && AsrDecoderSession.Created == created && AsrDecoderSession.Disposed == 0,
    "Background/idle released the resident model");
StreamingAsr.SetForeground(true);
await Task.Delay(100);
Assert(AsrDecoderSession.Created == created && StreamingAsr.State == "就绪", "Focus return cold-loaded the recognizer");
StreamingAsr.Cancel(); await Until(() => StreamingAsr.State == "就绪");
Assert(AsrDecoderSession.Created == created, "Dialogue/capture cancel reloaded the model");
Console.WriteLine("PASS model residency through 31 seconds idle, focus loss/return and capture cancel");
StreamingAsr.SetEnabled(false); await Until(() => AsrDecoderSession.Disposed == 1);
StreamingAsr.SetEnabled(true); await Until(() => StreamingAsr.State == "就绪");
Assert(AsrDecoderSession.Created == created + 1, "Re-enable did not preload again");
Assert(StreamingAsr.SetProfile("accurate160m") && StreamingAsr.SetProfile("lightweight14m"), "Profile switch unavailable");
await Until(() => StreamingAsr.State == "就绪");
Assert(AsrDecoderSession.Live == 1, "Multiple model weights are resident after profile switch");
Console.WriteLine("PASS disabled ASR releases weights; enabled ASR warms; rapid profile switches leave one model");

var gate = new AsrSendGate();
var recording = new AsrSnapshot(10, "录音中", "有误草稿", 0, false);
Assert(gate.Request(recording, 15), "Send during recording was rejected");
Assert(!gate.Request(recording, 15), "Repeated send intent was accepted");
Assert(gate.Poll(recording with { State = "收尾中" }, 15, true) == AsrSendDecision.Wait, "Partial draft sent before finalization");
Assert(gate.Poll(recording with { State = "就绪", Text = "校验终稿" }, 15, true) == AsrSendDecision.Send, "Completed final draft was not sent");
Assert(gate.Poll(recording with { State = "就绪" }, 15, true) == AsrSendDecision.Wait, "One click sent more than once");
foreach (var final in new[] { recording with { State = "错误：设备" }, recording with { State = "已取消" }, recording with { SessionId = 11, State = "就绪" } })
{ Assert(gate.Request(recording, 15), "Send intent setup failed"); Assert(gate.Poll(final, 15, true) == AsrSendDecision.Cancel, "Failure/cancel/stale generation auto-sent"); }
Assert(gate.Request(recording, 15), "Send intent setup failed");
Assert(gate.Poll(recording with { State = "就绪" }, 99, true) == AsrSendDecision.Cancel, "Different NPC input was sent");
Assert(gate.Request(recording, 15), "Send intent setup failed");
Assert(gate.Poll(recording with { State = "就绪" }, 15, false) == AsrSendDecision.Cancel, "Manual edit was overwritten and sent");
Assert(!gate.Request(recording with { TestMode = true }, 15), "Settings microphone test sent to game");
Console.WriteLine("PASS recording send: wait final, send once, cancel errors/edits/stale/input change/test mode");
StreamingAsr.Shutdown();

namespace A1IndexTTSMod
{
    // Replace only the CUDA decoder in this control-thread test. Production
    // StreamingAsr and its real queue/state transitions run unchanged.
    internal sealed class AsrDecoderSession : IDisposable
    {
        internal static int Created, Disposed;
        internal static string FirstProfile = "";
        internal static int Live => Created - Disposed;
        internal AsrDecoderSession(string root, AsrModelProfile profile)
        { if (Interlocked.Increment(ref Created) == 1) FirstProfile = profile.Id; }
        internal int HotwordCount => 94;
        internal int SkippedHotwordCount => 0;
        internal string NativeRevision => "test";
        internal const int TailSamples = 9600;
        internal void Begin() { }
        internal string Accept(float[] samples) => "草稿";
        internal bool FinalReviewApplied => true;
        internal bool SupportsReview => true;
        internal string FinalReviewSelection => "review";
        internal bool FinalReviewChanged => true;
        internal long FinalReviewSamples => 1;
        internal double FinalReviewMilliseconds => 1;
        internal string Finish(Func<bool>? isCurrent = null) => "终稿";
        internal void End() { }
        public void Dispose() { Interlocked.Increment(ref Disposed); }
    }
}
