namespace SkyNetwork.Voice;

/// <summary>
/// Microphone to the air: collects samples into 20 ms frames, and while the push-to-talk is held
/// encodes them and sends them on the transmitting radios. Releasing sends a closing frame so
/// listeners hear the squelch close at once instead of waiting for a timeout.
/// </summary>
public sealed class Transmitter
{
    private readonly IAudioSender _sender;
    private readonly VoiceEncoder _encoder = new();
    private readonly float[] _frame = new float[AudioFormat.FrameSamples];
    private readonly object _lock = new();
    private int _filled;
    private uint _sequence;
    private bool _keyed;
    private byte[] _transmitterIds = [];

    public Transmitter(IAudioSender sender) => _sender = sender;

    /// <summary>Microphone gain (1 = as captured).</summary>
    public float Gain { get; set; } = 1;

    /// <summary>Peak level of the last frame (0..1), for a microphone meter.</summary>
    public float Level { get; private set; }

    public bool Transmitting
    {
        get { lock (_lock) return _keyed; }
    }

    /// <summary>Transceivers the transmission goes out on (those of radios with transmit on).</summary>
    public void SetTransmitters(IReadOnlyList<byte> ids)
    {
        lock (_lock) _transmitterIds = ids.ToArray();
    }

    /// <summary>Push-to-talk pressed (true) or released (false).</summary>
    public void Key(bool down)
    {
        lock (_lock)
        {
            if (down == _keyed) return;
            if (down)
            {
                _keyed = _transmitterIds.Length > 0 && _sender.IsConnected;
                if (!_keyed) return;
                _encoder.Reset();
                _filled = 0;
            }
            else
            {
                // Close the transmission with the samples we have (padded with silence).
                _frame.AsSpan(_filled).Clear();
                SendFrame(last: true);
                _keyed = false;
            }
        }
    }

    /// <summary>16-bit little-endian PCM from the microphone (48 kHz mono).</summary>
    public void AddPcm16(ReadOnlySpan<byte> pcm)
    {
        Span<float> samples = stackalloc float[Math.Min(pcm.Length / 2, 4096)];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(pcm[2 * i] | pcm[2 * i + 1] << 8) / 32768f;
        AddSamples(samples);
    }

    public void AddSamples(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            foreach (float s in samples)
            {
                _frame[_filled++] = Math.Clamp(s * Gain, -1f, 1f);
                if (_filled < _frame.Length) continue;
                float peak = 0;
                foreach (float x in _frame) peak = Math.Max(peak, Math.Abs(x));
                Level = peak;
                if (_keyed) SendFrame(last: false);
                _filled = 0;
            }
        }
    }

    private void SendFrame(bool last)
    {
        byte[] opus = _encoder.Encode(_frame);
        _sender.SendAudio(_sequence++, last, _transmitterIds, opus);
        _filled = 0;
    }
}
