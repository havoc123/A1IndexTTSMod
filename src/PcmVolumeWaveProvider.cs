using NAudio.Wave;

namespace A1IndexTTSMod;

/// <summary>Applies volume while keeping the source WAV encoding and sample rate unchanged.</summary>
internal sealed class PcmVolumeWaveProvider : IWaveProvider
{
    private readonly IWaveProvider _source;
    private int _volumePercent;

    public PcmVolumeWaveProvider(IWaveProvider source, int volumePercent)
    {
        _source = source;
        VolumePercent = volumePercent;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int VolumePercent
    {
        get => Volatile.Read(ref _volumePercent);
        set => Volatile.Write(ref _volumePercent, Math.Clamp(value, 0, 100));
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _source.Read(buffer, offset, count);

        // ★把 PCM 分一路推给手机（NPC 通话窗口的声音下行）。
        //   ★位置说明：这里在音量缩放**之前**分一路，是有意为之——
        //     音量是 PC 端的听感控制，手机端不应受它影响（否则玩家调PC 音量会把
        //     手机也一起调小声）。若要"完全一致"可挪到 return 之前，但那样
        //     玩家一调音量，手机音量就跟着变，体验反而更差。
        //     整段用 try 包住：转发任何异常都绝不能影响 PC 本地播放。
        if (bytesRead > 0)
        {
            try { AudioForwarder.OnPcmAvailable(buffer, offset, bytesRead, WaveFormat); }
            catch { /* 转发失败绝不能影响 PC 本地播放 */ }
        }

        var volume = VolumePercent / 100f;
        if (bytesRead <= 0 || volume == 1f) return bytesRead;

        var format = WaveFormat;
        if (format.Encoding == WaveFormatEncoding.Pcm)
        {
            ScalePcm(buffer, offset, bytesRead, format.BitsPerSample, volume);
            return bytesRead;
        }
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            ScaleFloat(buffer, offset, bytesRead, format.BitsPerSample, volume);
            return bytesRead;
        }

        throw new NotSupportedException($"Volume adjustment does not support WAV encoding {format.Encoding}.");
    }

    private static void ScalePcm(byte[] buffer, int offset, int count, int bitsPerSample, float volume)
    {
        switch (bitsPerSample)
        {
            case 8:
                for (var i = offset; i < offset + count; i++)
                {
                    var centered = buffer[i] - 128;
                    buffer[i] = (byte)(Math.Clamp((int)Math.Round(centered * (double)volume), -128, 127) + 128);
                }
                break;
            case 16:
                for (var i = offset; i + 1 < offset + count; i += 2)
                {
                    var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                    var scaled = Math.Clamp((int)Math.Round(sample * (double)volume), short.MinValue, short.MaxValue);
                    buffer[i] = (byte)scaled;
                    buffer[i + 1] = (byte)(scaled >> 8);
                }
                break;
            case 24:
                for (var i = offset; i + 2 < offset + count; i += 3)
                {
                    var sample = buffer[i] | (buffer[i + 1] << 8) | (buffer[i + 2] << 16);
                    if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
                    var scaled = Math.Clamp((long)Math.Round(sample * (double)volume), -8388608L, 8388607L);
                    buffer[i] = (byte)scaled;
                    buffer[i + 1] = (byte)(scaled >> 8);
                    buffer[i + 2] = (byte)(scaled >> 16);
                }
                break;
            case 32:
                for (var i = offset; i + 3 < offset + count; i += 4)
                {
                    var sample = BitConverter.ToInt32(buffer, i);
                    var scaled = Math.Clamp(Math.Round(sample * (double)volume), int.MinValue, int.MaxValue);
                    BitConverter.TryWriteBytes(buffer.AsSpan(i, 4), (int)scaled);
                }
                break;
            default:
                throw new NotSupportedException($"PCM volume adjustment does not support {bitsPerSample}-bit samples.");
        }
    }

    private static void ScaleFloat(byte[] buffer, int offset, int count, int bitsPerSample, float volume)
    {
        switch (bitsPerSample)
        {
            case 32:
                for (var i = offset; i + 3 < offset + count; i += 4)
                    BitConverter.TryWriteBytes(buffer.AsSpan(i, 4), BitConverter.ToSingle(buffer, i) * volume);
                break;
            case 64:
                for (var i = offset; i + 7 < offset + count; i += 8)
                    BitConverter.TryWriteBytes(buffer.AsSpan(i, 8), BitConverter.ToDouble(buffer, i) * volume);
                break;
            default:
                throw new NotSupportedException($"IEEE float volume adjustment does not support {bitsPerSample}-bit samples.");
        }
    }
}
