using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SkyPilot.Core.Model;

namespace SkyPilot.Core.Simulation;

/// <summary>
/// X-Plane 11 / 12 through the SkyPilot plugin (Resources/plugins/SkyPilot). The plugin draws other aircraft with
/// CSL models (it picks the model from the ICAO type and airline) and talks to SkyPilot over UDP on this computer:
/// one ASCII message per datagram, fields separated by spaces.
/// <list type="bullet">
/// <item>SkyPilot → plugin: HELLO 1 (every second), ADD cs type airline livery, POS cs lat lon altFt pitch bank hdg gs onGround,
/// DEL cs, CLEAR, COM 1|2 kHz, XPDR code, BYE.</item>
/// <item>Plugin → SkyPilot: HELLO 1 xplaneVersion pluginVersion, OWN lat lon altFt pitch bank hdg gs onGround pressAltFt com1 com2 squawk xpdrOn
/// (about 10 times a second), LOG text.</item>
/// </list>
/// </summary>
public sealed class XPlaneSimulator : ISimulator
{
    public const int DefaultPluginPort = 51730;
    public const int Protocol = 1;
    /// <summary>The plugin is considered gone when nothing arrived for this long.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly IPEndPoint _plugin;
    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();
    private UdpClient? _udp;
    private Thread? _reader;
    private Timer? _hello;
    private DateTime _lastHeard;
    private volatile bool _connected;

    public XPlaneSimulator(int pluginPort = DefaultPluginPort, Func<DateTime>? clock = null)
    {
        _plugin = new IPEndPoint(IPAddress.Loopback, pluginPort);
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public string Name { get; private set; } = "X-Plane";
    public bool IsConnected => _connected;
    public bool MatchesModels => true;
    public string? LastError => null;

    /// <summary>Messages from the plugin (missing CSL models and the like).</summary>
    public event EventHandler<string>? PluginLog;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<OwnAircraftData>? OwnAircraftUpdated;
    /// <summary>Never raised: the plugin falls back to another CSL model by itself.</summary>
    public event EventHandler<AircraftCreateFailedEventArgs>? AircraftCreateFailed
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Starts listening and greets the plugin. True once the plugin has answered: the first call only asks,
    /// a later call (the window retries every few seconds) sees the answer.
    /// </summary>
    public bool Connect()
    {
        lock (_lock)
        {
            if (_udp == null)
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var udp = _udp;
                _reader = new Thread(() => ReadLoop(udp)) { IsBackground = true, Name = "X-Plane" };
                _reader.Start();
                _hello = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            }
        }
        return _connected;
    }

    public void Disconnect()
    {
        UdpClient? udp;
        bool was;
        lock (_lock)
        {
            if (_udp != null) Send("BYE");
            udp = _udp;
            _udp = null;
            _hello?.Dispose();
            _hello = null;
            was = _connected;
            _connected = false;
        }
        udp?.Dispose();
        if (was) ConnectionChanged?.Invoke(this, false);
    }

    private void Tick()
    {
        bool lost;
        lock (_lock)
        {
            if (_udp == null) return;
            Send($"HELLO {Protocol}");
            lost = _connected && _clock() - _lastHeard > Timeout;
            if (lost) _connected = false;
        }
        if (lost) ConnectionChanged?.Invoke(this, false);
    }

    private void ReadLoop(UdpClient udp)
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            byte[] data;
            try
            {
                data = udp.Receive(ref any);
            }
            catch (SocketException)
            {
                // A "port unreachable" from a HELLO nobody listened to: keep waiting for the plugin.
                if (!ReferenceEquals(udp, _udp)) return;
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            OnMessage(Encoding.UTF8.GetString(data));
        }
    }

    internal void OnMessage(string message)
    {
        var f = message.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length == 0) return;
        bool nowConnected = false;
        lock (_lock)
        {
            _lastHeard = _clock();
            if (!_connected && f[0] is "HELLO" or "OWN")
            {
                _connected = true;
                nowConnected = true;
            }
        }
        switch (f[0])
        {
            case "HELLO" when f.Length >= 3:
                Name = f[2].Length > 0 && char.IsDigit(f[2][0]) ? $"X-Plane {f[2][..Math.Min(2, f[2].Length)]}" : "X-Plane";
                break;
            case "LOG":
                PluginLog?.Invoke(this, message.Trim()[3..].Trim());
                break;
        }
        if (nowConnected) ConnectionChanged?.Invoke(this, true);
        if (f[0] == "OWN" && ParseOwn(f) is { } own) OwnAircraftUpdated?.Invoke(this, own);
    }

    /// <summary>"OWN lat lon altFt pitch bank hdg gs onGround pressAltFt com1 com2 squawk xpdrOn".</summary>
    internal static OwnAircraftData? ParseOwn(string[] f)
    {
        if (f.Length < 14) return null;
        static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        static int I(string s) => (int)Math.Round(D(s));
        try
        {
            var state = new AircraftState(D(f[1]), D(f[2]), D(f[3]), D(f[4]), D(f[5]), D(f[6]), D(f[7]), f[8] == "1");
            return new OwnAircraftData(state, D(f[9]), I(f[10]), I(f[11]), I(f[12]), f[13] == "1");
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private void Send(string message)
    {
        var udp = _udp;
        if (udp == null) return;
        var bytes = Encoding.UTF8.GetBytes(message);
        try
        {
            udp.Send(bytes, bytes.Length, _plugin);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // The plugin is not there (yet): the next HELLO tries again.
        }
    }

    private static string F(double v) => v.ToString("0.#######", CultureInfo.InvariantCulture);
    private static string Word(string s) => string.IsNullOrWhiteSpace(s) ? "-" : s.Trim().Replace(' ', '_');

    public void AddAircraft(string callsign, AircraftModel model, AircraftState state)
    {
        Send($"ADD {Word(callsign)} {Word(model.IcaoType)} {Word(model.Airline)} -");
        UpdateAircraft(callsign, state);
    }

    public void UpdateAircraft(string callsign, AircraftState s) =>
        Send($"POS {Word(callsign)} {F(s.Latitude)} {F(s.Longitude)} {F(s.AltitudeFeet)} {F(s.PitchDegrees)} {F(s.BankDegrees)} " +
             $"{F(s.HeadingDegrees)} {F(s.GroundSpeedKnots)} {(s.OnGround ? 1 : 0)}");

    public void RemoveAircraft(string callsign) => Send($"DEL {Word(callsign)}");
    public void RemoveAllAircraft() => Send("CLEAR");
    public void SetComFrequency(int radio, int khz) => Send($"COM {(radio == 2 ? 2 : 1)} {khz}");
    public void SetTransponderCode(int code) => Send($"XPDR {code:0000}");

    public void Dispose() => Disconnect();
}
