using A1IndexTTSMod;

StreamingAsr.ConfigureRoot(Path.Combine(Path.GetTempPath(), "a1-asr-absent-" + Guid.NewGuid().ToString("N")));
StreamingAsr.SetEnabled(true);
await Task.Delay(150);
if (StreamingAsr.Available || StreamingAsr.ShowMicrophone || AsrDecoderSession.Created != 0) throw new Exception("Missing ASR package started decoder warm-up");
StreamingAsr.SetEnabled(false);
await Task.Delay(100);
StreamingAsr.ConfigureRoot(@"E:\Program Files (x86)\Steam\steamapps\common\A1");
StreamingAsr.SetLog(Console.WriteLine);
async Task Until(Func<bool> ready)
{
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (!ready()) { if (DateTime.UtcNow > deadline) throw new Exception("ASR state timeout: " + StreamingAsr.State); await Task.Delay(10); }
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
var amd = new GpuAdapter(2, 0x1002, "AMD Radeon RX 7900 XTX", 24UL << 30);
var intel = new GpuAdapter(0, 0x8086, "Intel UHD", 0);
Assert(GpuRouting.SelectAsr(new[] { intel, amd }) is { Provider: "directml", Device: 2 }, "DirectML picked Intel device zero instead of AMD");
Assert(GpuRouting.SelectAsr(new[] { amd, new GpuAdapter(1, 0x10de, "NVIDIA RTX", 16UL << 30) })?.Provider == "cuda", "Mixed GPU system lost NVIDIA preference");
Assert(GpuRouting.SelectAsr(new[] { amd with { SupportsDirectML = false } }) == null, "Unsupported DirectX GPU was treated as DirectML-capable");
Assert(!GpuRouting.AllowsModel("directml", AsrModelProfiles.Accurate), "DirectML allowed 160M");
Assert(GpuRouting.AllowsModel("directml", AsrModelProfiles.Lightweight), "DirectML rejected 14M");
Assert(GpuRouting.ParseTtsDevices("vulkan:0 \"Intel UHD\" [GPU]\nvulkan:1 \"AMD Radeon RX 7900 XTX\" [IGPU]\n", "vulkan", amd).Device == 1, "Vulkan reused DXGI index");
Console.WriteLine("PASS absent package prevents warm-up; AMD/mixed GPU policy; DirectML 14M limit; Vulkan actual backend index");
var ttsRoute = await GpuRouting.ResolveTtsAsync(Path.Combine(StreamingAsr.GameRoot, "A1IndexTTSMod"), CancellationToken.None);
Assert(ttsRoute.Provider == "cuda" && ttsRoute.Device >= 0, "Auto TTS did not resolve the actual NVIDIA backend device");
Console.WriteLine("PASS TTS Auto runs the real device enumeration command and selects NVIDIA CUDA");
var words = new[] { "测试甲", "测试门", "测试乙" };
Assert(AsrFinalSelection.KeepStreaming("测试甲准备出发吧", "策式假拟在这里吗", words), "Review discarded a completed name");
Assert(AsrFinalSelection.KeepStreaming("测试甲去测试门", "测试甲去策式门", words), "Review discarded one of multiple contextual words");
Assert(AsrFinalSelection.KeepStreaming("测试甲请查看清单测试甲", "测试甲请查看清单", words), "Review discarded a repeated contextual word");
Assert(!AsrFinalSelection.KeepStreaming("请把兰色方块移到右边", "请把蓝色方块移到右边", words), "Ordinary speech correction was blocked");
Assert(!AsrFinalSelection.KeepStreaming("安排请尽测试丙和策式以帮忙", "安排请进测试丙和测试乙帮忙", words), "Review could not add an acoustically decoded name");
Assert(!AsrFinalSelection.KeepStreaming("策式假准备出发吧", "册式假准备出发吧", words), "Selection invented a dictionary match for two wrong spellings");
Console.WriteLine("PASS final hypothesis selection preserves completed hotwords and permits whole-audio corrections");
if (!StreamingAsr.SetProfile("accurate160m")) throw new Exception("Installed test profile unavailable");
await Task.Delay(100);
Assert(AsrDecoderSession.Created == 0, "Disabled ASR loaded a model during configuration");
Assert(StreamingAsr.Available && !StreamingAsr.ShowMicrophone, "Installed disabled package lost settings or kept its microphone");
AsrDecoderSession.WarmGate.Reset();
StreamingAsr.SetEnabled(true);
await Until(() => AsrDecoderSession.Created == 1);
StreamingAsr.SetEnabled(false); StreamingAsr.SetEnabled(true);
AsrDecoderSession.WarmGate.Set();
await Until(() => AsrDecoderSession.Disposed >= 1);
await Until(() => StreamingAsr.State == "就绪" && AsrDecoderSession.Live == 1);
Console.WriteLine("PASS re-enable during cold warm-up survives the queued release and preloads automatically");
Assert(!StreamingAsr.IsActive, "Startup warm-up opened a recording session");
Assert(StreamingAsr.Available && StreamingAsr.ShowMicrophone, "Ready enabled ASR hid its microphone");
Assert(AsrDecoderSession.FirstProfile == "accurate160m", "Startup loaded the default instead of the configured model");
Console.WriteLine("PASS startup ASR readiness without opening microphone");
var created = AsrDecoderSession.Created; var disposed = AsrDecoderSession.Disposed;
StreamingAsr.SetForeground(false);
await Task.Delay(31000);
Assert(StreamingAsr.State == "就绪" && AsrDecoderSession.Created == created && AsrDecoderSession.Disposed == disposed,
    "Background/idle released the resident model");
StreamingAsr.SetForeground(true);
await Task.Delay(100);
Assert(AsrDecoderSession.Created == created && StreamingAsr.State == "就绪", "Focus return cold-loaded the recognizer");
StreamingAsr.Cancel(); await Until(() => StreamingAsr.State == "就绪");
Assert(AsrDecoderSession.Created == created, "Dialogue/capture cancel reloaded the model");
Console.WriteLine("PASS model residency through 31 seconds idle, focus loss/return and capture cancel");
StreamingAsr.SetEnabled(false); await Until(() => AsrDecoderSession.Disposed == disposed + 1);
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
        internal static readonly ManualResetEventSlim WarmGate = new(true);
        internal static int Live => Created - Disposed;
        internal AsrDecoderSession(string root, AsrModelProfile profile, string provider = "cuda", int device = 0)
        { if (Interlocked.Increment(ref Created) == 1) FirstProfile = profile.Id; WarmGate.Wait(); }
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
