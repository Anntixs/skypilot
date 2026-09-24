using System.Net;
using System.Net.Sockets;

namespace SkyNetwork.Voice;

/// <summary>Something that can put audio frames on the air (the connection; a fake in tests).</summary>
public interface IAudioSender
{
    bool IsConnected { get; }
    void SendAudio(uint sequence, bool last, ReadOnlySpan<byte> transmitterIds, ReadOnlySpan<byte> opus);
}

public sealed class VoiceException(string message) : Exception(message);

/// <summary>
/// UDP session with the voice server: sign-in with CID and password, transceiver updates, audio
/// out and in, keepalives. Transceivers are re-sent periodically because UDP may drop them.
/// </summary>
public sealed class VoiceConnection : IAudioSender, IDisposable
{
    private static readonly TimeSpan KeepAliveEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TransceiversEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ServerSilentLimit = TimeSpan.FromSeconds(20);

    private readonly string _host;
    private readonly int _port;
    private readonly object _lock = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private uint _token;
    private IReadOnlyList<Transceiver> _transceivers = [];
    private DateTime _lastHeard;
    private int _closed;

    public VoiceConnection(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public bool IsConnected => _token != 0 && _closed == 0;

    /// <summary>Audio from other members (raised on a background thread).</summary>
    public event Action<AudioPacket>? AudioReceived;

    /// <summary>The session ended from the server side: kicked, or the server stopped answering.</summary>
    public event Action<string>? Closed;

    /// <summary>Signs in; throws <see cref="VoiceException"/> with the server's reason on refusal.</summary>
    public async Task ConnectAsync(uint cid, string callsign, string password, CancellationToken ct = default)
    {
        var addresses = await Dns.GetHostAddressesAsync(_host, ct);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                      ?? throw new VoiceException($"Cannot resolve {_host}");
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(address, _port);
        _udp = udp;
        byte[] auth = Protocol.Auth(cid, callsign, password);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            await udp.SendAsync(auth, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                while (true)
                {
                    var result = await udp.ReceiveAsync(timeout.Token);
                    var p = Protocol.Parse(result.Buffer);
                    if (p?.Type == PacketType.AuthOk)
                    {
                        _token = p.Token;
                        _lastHeard = DateTime.UtcNow;
                        _cts = new CancellationTokenSource();
                        _ = ReceiveLoop(udp, _cts.Token);
                        _ = KeepAliveLoop(_cts.Token);
                        return;
                    }
                    if (p?.Type == PacketType.AuthFail)
                    {
                        udp.Dispose();
                        throw new VoiceException(p.Reason);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // No answer: try again.
            }
            catch (SocketException)
            {
                // Port unreachable (ICMP): the server is not running there.
                break;
            }
        }
        udp.Dispose();
        throw new VoiceException("Voice server not responding");
    }

    /// <summary>Replaces the transceivers (sent now and repeated periodically).</summary>
    public void SetTransceivers(IReadOnlyList<Transceiver> transceivers)
    {
        _transceivers = transceivers.Take(Protocol.MaxTransceivers).ToList();
        SendTransceivers();
    }

    public void SendAudio(uint sequence, bool last, ReadOnlySpan<byte> transmitterIds, ReadOnlySpan<byte> opus)
    {
        if (!IsConnected || transmitterIds.Length == 0) return;
        Send(Protocol.Audio(_token, sequence, last, transmitterIds, opus));
    }

    private void SendTransceivers()
    {
        if (IsConnected) Send(Protocol.Transceivers(_token, _transceivers));
    }

    private void Send(byte[] datagram)
    {
        var udp = _udp;
        if (udp == null) return;
        try { udp.Send(datagram, datagram.Length); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // A lost datagram is normal for UDP; a dead socket shows up as silence from the server.
        }
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; } // ICMP unreachable while the server restarts
            var p = Protocol.Parse(result.Buffer);
            if (p == null) continue;
            _lastHeard = DateTime.UtcNow;
            switch (p.Type)
            {
                case PacketType.AudioRx when p.Audio != null:
                    AudioReceived?.Invoke(p.Audio);
                    break;
                case PacketType.Kick:
                    Close(p.Reason.Length > 0 ? p.Reason : "Disconnected by the server", sendBye: false);
                    return;
            }
        }
    }

    private async Task KeepAliveLoop(CancellationToken ct)
    {
        var lastTransceivers = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(KeepAliveEvery, ct); }
            catch (OperationCanceledException) { return; }
            Send(Protocol.KeepAlive(_token));
            if (DateTime.UtcNow - lastTransceivers >= TransceiversEvery)
            {
                SendTransceivers();
                lastTransceivers = DateTime.UtcNow;
            }
            if (DateTime.UtcNow - _lastHeard > ServerSilentLimit)
            {
                Close("Voice server not responding", sendBye: false);
                return;
            }
        }
    }

    private void Close(string reason, bool sendBye)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        if (sendBye && _token != 0) Send(Protocol.Bye(_token));
        _cts?.Cancel();
        lock (_lock)
        {
            _udp?.Dispose();
            _udp = null;
        }
        if (reason.Length > 0) Closed?.Invoke(reason);
    }

    public void Dispose() => Close("", sendBye: true);
}
