using System.Net.Sockets;
using System.Text;

namespace SkyPilot.Core.Fsd;

public sealed class FsdErrorEventArgs(int code, string parameter, string message) : EventArgs
{
    public int Code { get; } = code;
    public string Parameter { get; } = parameter;
    public string Message { get; } = message;
}

public sealed class FsdLoginException(string message) : Exception(message);

/// <summary>
/// Low-level FSD connection: a TCP socket exchanging CRLF-terminated text packets.
/// Events are raised on a background thread.
/// </summary>
public sealed class FsdClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpClient? _tcp;
    private Stream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    public event EventHandler<FsdPacket>? PacketReceived;
    public event EventHandler<string>? Disconnected;

    public bool IsConnected => _tcp?.Connected == true && _readLoop is { IsCompleted: false };

    /// <summary>
    /// Connect and send the login packet. Completes once the server has accepted the login
    /// (first reply is not an error) or throws <see cref="FsdLoginException"/>.
    /// </summary>
    public async Task ConnectAsync(string host, int port, string loginPacket, CancellationToken ct = default)
    {
        if (_tcp != null) throw new InvalidOperationException("Already connected");
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8);
            await WriteAsync(stream, loginPacket, timeout.Token).ConfigureAwait(false);

            var first = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                        ?? throw new FsdLoginException("Сервер закрыл соединение");
            var packet = FsdPacket.Parse(first);
            if (packet?.Command == "$ER")
                throw new FsdLoginException(packet[4].Length > 0 ? packet[4] : "Ошибка входа " + packet[2]);

            _tcp = tcp;
            _stream = stream;
            _cts = new CancellationTokenSource();
            if (packet != null) PacketReceived?.Invoke(this, packet);
            _readLoop = Task.Run(() => ReadLoopAsync(reader, _cts.Token));
        }
        catch (Exception e) when (e is not FsdLoginException)
        {
            tcp.Dispose();
            throw new FsdLoginException(e is OperationCanceledException ? "Сервер не отвечает" : e.Message);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public async Task SendAsync(string packet)
    {
        var stream = _stream;
        if (stream == null) return;
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await WriteAsync(stream, packet, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            // The read loop notices the broken connection and raises Disconnected.
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static Task WriteAsync(Stream stream, string packet, CancellationToken ct) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(packet + "\r\n"), ct).AsTask();

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        string reason = "Соединение закрыто сервером";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                var packet = FsdPacket.Parse(line);
                if (packet != null) PacketReceived?.Invoke(this, packet);
            }
            if (ct.IsCancellationRequested) reason = "Отключено";
        }
        catch (OperationCanceledException)
        {
            reason = "Отключено";
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            reason = "Потеряно соединение с сервером";
        }
        Close();
        Disconnected?.Invoke(this, reason);
    }

    private void Close()
    {
        _cts?.Cancel();
        _stream = null;
        _tcp?.Dispose();
        _tcp = null;
    }

    public async Task DisconnectAsync(string? logoffPacket = null)
    {
        if (logoffPacket != null) await SendAsync(logoffPacket).ConfigureAwait(false);
        _cts?.Cancel();
        try { _tcp?.Client.Shutdown(SocketShutdown.Both); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        if (_readLoop != null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { /* already reported */ }
        }
        _readLoop = null;
        Close();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}
