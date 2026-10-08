using BepInEx.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace A1IndexTTSMod;

// Windows shared-mode output runs in the game process without Unity's disabled audio subsystem.
internal static class WasapiSpeechPlayer
{
    private static readonly object Gate = new();
    private static WasapiOut? _output;
    private static WaveOutEvent? _fallback;
    private static WaveFileReader? _reader;
    private static PcmVolumeWaveProvider? _volume;

    public static string? TryPlay(string wavPath, int volumePercent, ManualLogSource? log)
    {
        lock (Gate)
        {
            StopCurrent();
            WaveFileReader? reader = null;
            WasapiOut? output = null;
            try
            {
                reader = new WaveFileReader(wavPath);
                var volume = new PcmVolumeWaveProvider(reader, volumePercent);
                output = new WasapiOut(AudioClientShareMode.Shared, true, 100);
                output.Init(volume);
                output.Play();
                if (output.PlaybackState != PlaybackState.Playing)
                    throw new InvalidOperationException("WASAPI did not enter the playing state.");
                _reader = reader;
                _output = output;
                _volume = volume;
                return "WASAPI";
            }
            catch (Exception e)
            {
                try { output?.Stop(); output?.Dispose(); reader?.Dispose(); } catch { }
                log?.LogWarning($"Stage3 WASAPI shared playback failed: {e.GetType().Name}: {e.Message}; falling back to WinMM.");
                try
                {
                    reader = new WaveFileReader(wavPath);
                    var volume = new PcmVolumeWaveProvider(reader, volumePercent);
                    var fallback = new WaveOutEvent();
                    fallback.Init(volume);
                    fallback.Play();
                    if (fallback.PlaybackState != PlaybackState.Playing) throw new InvalidOperationException("WinMM did not enter the playing state.");
                    _reader = reader;
                    _fallback = fallback;
                    _volume = volume;
                    return "WinMM";
                }
                catch (Exception fallbackError)
                {
                    try { reader?.Dispose(); } catch { }
                    log?.LogWarning($"Stage3 WinMM fallback failed: {fallbackError.GetType().Name}: {fallbackError.Message}");
                    return null;
                }
            }
        }
    }

    public static void SetVolume(int volumePercent)
    {
        lock (Gate) if (_volume != null) _volume.VolumePercent = Math.Clamp(volumePercent, 0, 100);
    }

    public static void Stop()
    {
        lock (Gate) StopCurrent();
    }

    private static void StopCurrent()
    {
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _fallback?.Stop(); } catch { }
        try { _fallback?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        _volume = null;
        _output = null;
        _fallback = null;
        _reader = null;
    }
}
