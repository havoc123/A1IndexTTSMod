namespace A1IndexTTSMod;

internal enum AsrSendDecision { Wait, Send, Cancel }

/// <summary>One send intent tied to the exact recording and input field.</summary>
internal sealed class AsrSendGate
{
    private long _session;
    private int _input;
    internal bool Pending { get; private set; }
    internal bool Request(AsrSnapshot snapshot, int input)
    {
        if (Pending || !snapshot.IsActive || snapshot.TestMode || input == 0) return false;
        _session = snapshot.SessionId; _input = input; Pending = true;
        return true;
    }
    internal AsrSendDecision Poll(AsrSnapshot snapshot, int input, bool draftUnchanged)
    {
        if (!Pending) return AsrSendDecision.Wait;
        if (snapshot.SessionId == _session && snapshot.IsActive) return AsrSendDecision.Wait;
        Pending = false;
        return snapshot.SessionId == _session && snapshot.State == "就绪" && input == _input && draftUnchanged
            ? AsrSendDecision.Send : AsrSendDecision.Cancel;
    }
    internal void Cancel() => Pending = false;
}
