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
