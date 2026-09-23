using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SkyPilot.Core.Model;
using SkyPilot.Core.Simulation;

namespace SkyPilot.Core.Tests;

public sealed class FakeSimulator : ISimulator
{
    public string Name => "Fake";
    public bool IsConnected { get; private set; }
    public ConcurrentDictionary<string, (string Title, AircraftState State)> Aircraft { get; } = new();
    public List<(int Radio, int Khz)> ComChanges { get; } = [];
    public int? Squawk { get; private set; }

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<OwnAircraftData>? OwnAircraftUpdated;
    public event EventHandler<AircraftCreateFailedEventArgs>? AircraftCreateFailed;

    public bool Connect()
    {
        IsConnected = true;
        ConnectionChanged?.Invoke(this, true);
        return true;
    }

    public void Disconnect()
    {
        IsConnected = false;
        ConnectionChanged?.Invoke(this, false);
    }

    public void Push(OwnAircraftData data) => OwnAircraftUpdated?.Invoke(this, data);
    public void FailCreate(string callsign, string title) => AircraftCreateFailed?.Invoke(this, new(callsign, title));

    public void AddAircraft(string callsign, string modelTitle, AircraftState state) => Aircraft[callsign] = (modelTitle, state);

    public void UpdateAircraft(string callsign, AircraftState state)
    {
        if (Aircraft.TryGetValue(callsign, out var a)) Aircraft[callsign] = (a.Title, state);
    }

    public void RemoveAircraft(string callsign) => Aircraft.TryRemove(callsign, out _);
    public void RemoveAllAircraft() => Aircraft.Clear();
    public void SetComFrequency(int radio, int khz) => ComChanges.Add((radio, khz));
    public void SetTransponderCode(int code) => Squawk = code;
    public void Dispose() { }

    public static OwnAircraftData Own(double lat = 55.97, double lon = 37.41, int com1 = 118100, int com2 = 121500) =>
        new(new AircraftState(lat, lon, 3000, 2, 0, 250, 180, false), 3000, com1, com2, 2000, true);
}

/// <summary>Minimal in-process FSD server: accepts one client, records lines, lets tests inject packets.</summary>
public sealed class FakeFsdServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly BlockingCollection<string> _received = new();
    private TcpClient? _client;
    private StreamWriter? _writer;
    private readonly Task _accept;

    public FakeFsdServer(string? loginReply = "#TMSERVER:{0}:Welcome")
    {
        _listener.Start();
        _accept = Task.Run(async () =>
        {
            _client = await _listener.AcceptTcpClientAsync();
            var stream = _client.GetStream();
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
            var reader = new StreamReader(stream);
            bool first = true;
            while (await reader.ReadLineAsync() is { } line)
            {
                if (first && loginReply != null)
                {
                    first = false;
                    var callsign = line[3..line.IndexOf(':')];
                    await _writer.WriteLineAsync(string.Format(loginReply, callsign));
                }
                _received.Add(line);
            }
        });
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Expect(string prefix, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (_received.TryTake(out var line, Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds)))
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
        throw new TimeoutException("No packet starting with " + prefix);
    }

    public Task SendAsync(string line) => _writer!.WriteLineAsync(line);

    public void DropClient() => _client?.Close();

    public async ValueTask DisposeAsync()
    {
        _client?.Close();
        _listener.Stop();
        try { await _accept.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }
}
