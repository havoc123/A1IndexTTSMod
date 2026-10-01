using BepInEx.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace A1IndexTTSMod;

// Windows shared-mode output runs in the game process without Unity's disabled audio subsystem.
internal static class WasapiSpeechPlayer
{
    private static readonly object Gate = new();
    private static WasapiOut? _output;
    private static WaveFileReader? _reader;

    public static bool TryPlay(string wavPath, ManualLogSource? log)
    {
        lock (Gate)
        {
            StopCurrent();
            WaveFileReader? reader = null;
            WasapiOut? output = null;
            try
            {
                reader = new WaveFileReader(wavPath);
                output = new WasapiOut(AudioClientShareMode.Shared, true, 100);
                output.Init(reader);
                output.Play();
                if (output.PlaybackState != PlaybackState.Playing)
                    throw new InvalidOperationException("WASAPI did not enter the playing state.");
                _reader = reader;
                _output = output;
                return true;
            }
            catch (Exception e)
            {
                try { output?.Stop(); output?.Dispose(); reader?.Dispose(); } catch { }
                log?.LogWarning($"Stage3 WASAPI shared playback failed: {e.GetType().Name}: {e.Message}; falling back to WinMM.");
                return false;
            }
        }
    }

    private static void StopCurrent()
    {
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        _output = null;
        _reader = null;
    }
}
