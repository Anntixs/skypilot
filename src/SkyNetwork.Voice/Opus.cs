using Concentus;
using Concentus.Enums;

namespace SkyNetwork.Voice;

/// <summary>Audio format used on the air: 48 kHz mono, 20 ms Opus frames.</summary>
public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 960; // 20 ms
    public const int MaxOpusBytes = 400;
}

/// <summary>Opus encoder for one microphone, tuned for speech over a radio channel.</summary>
public sealed class VoiceEncoder
{
    private readonly IOpusEncoder _encoder;
    private readonly byte[] _buffer = new byte[AudioFormat.MaxOpusBytes];

    public VoiceEncoder(int bitrate = 24000)
    {
        _encoder = OpusCodecFactory.CreateEncoder(AudioFormat.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = bitrate;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 5;
        _encoder.Complexity = 6;
    }

    /// <summary>Encodes one 20 ms frame (960 samples, -1..1).</summary>
    public byte[] Encode(ReadOnlySpan<float> frame)
    {
        if (frame.Length != AudioFormat.FrameSamples) throw new ArgumentException("A frame is 960 samples", nameof(frame));
        int n = _encoder.Encode(frame, AudioFormat.FrameSamples, _buffer, _buffer.Length);
        return _buffer.AsSpan(0, n).ToArray();
    }

    public void Reset() => _encoder.ResetState();
}

/// <summary>Opus decoder for one incoming stream.</summary>
public sealed class VoiceDecoder
{
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(AudioFormat.SampleRate, 1);

    /// <summary>Decodes a frame into 960 samples; with no data (a lost frame) it conceals the gap.</summary>
    public void Decode(byte[]? opus, Span<float> output)
    {
        int n;
        try
        {
            n = opus is { Length: > 0 }
                ? _decoder.Decode(opus, output, AudioFormat.FrameSamples, false)
                : _decoder.Decode(ReadOnlySpan<byte>.Empty, output, AudioFormat.FrameSamples, false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OpusException)
        {
            n = 0; // a corrupt frame is dropped, not fatal
        }
        if (n < output.Length) output[Math.Max(n, 0)..].Clear();
    }
}
