using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SkyPilot.Core.Fsd;
using SkyPilot.Core.Matching;
using SkyPilot.Core.Model;
using SkyPilot.Core.Simulation;

namespace SkyPilot.Core.Session;

/// <summary>
/// A pilot's session on SkyNetwork: sends the user's position, draws other pilots in the
/// simulator, and routes text messages. Events are raised on background threads.
/// </summary>
public sealed partial class NetworkSession : IAsyncDisposable
{
    public static readonly TimeSpan PositionInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan ModelInfoWait = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan IdentDuration = TimeSpan.FromSeconds(18);
    /// <summary>ATIS text is complete when no line arrived for this long, even without the end marker.</summary>
    public static readonly TimeSpan AtisLineWait = TimeSpan.FromSeconds(2);
    /// <summary>Give up waiting for a station that did not answer an ATIS request at all.</summary>
    public static readonly TimeSpan AtisReplyTimeout = TimeSpan.FromSeconds(6);

    private readonly ISimulator _sim;
    private ModelMatcher _matcher;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, RemoteAircraft> _traffic = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AtcStation> _atc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AtisInfo> _atis = new(StringComparer.OrdinalIgnoreCase);
    // ATIS replies being assembled, by station (guarded by _gate).
    private readonly Dictionary<string, PendingAtis> _atisPending = new(StringComparer.OrdinalIgnoreCase);

    private FsdClient? _fsd;
    private ConnectInfo? _info;
    private Timer? _positionTimer;
    private Timer? _renderTimer;
    private OwnAircraftData? _own;
    private DateTime _identUntil;

    public NetworkSession(ISimulator sim, ModelMatcher matcher, Func<DateTime>? clock = null)
    {
        _sim = sim;
        _matcher = matcher;
        _clock = clock ?? (() => DateTime.UtcNow);
        _sim.OwnAircraftUpdated += (_, data) => _own = data;
        _sim.AircraftCreateFailed += OnAircraftCreateFailed;
        _sim.ConnectionChanged += OnSimConnectionChanged;
    }

    /// <summary>Picks model titles; the window replaces it when another simulator (with other models) connects.</summary>
    public ModelMatcher Matcher
    {
        get => _matcher;
        set
        {
            lock (_gate) _matcher = value;
        }
    }

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler? ControllersChanged;
    public event EventHandler? TrafficChanged;

    /// <summary>A complete ATIS / controller information text arrived (also sent as a MessageKind.Atis message).</summary>
    public event EventHandler<AtisInfo>? AtisReceived;

    public bool IsConnected => _fsd?.IsConnected == true;
    public string Callsign => _info?.Callsign ?? "";
    public OwnAircraftData? OwnAircraft => _own;
    public bool ModeC { get; set; }

    /// <summary>Receive text on COM1 / COM2 (the RX buttons).</summary>
    public bool Com1Receive { get; set; } = true;
    public bool Com2Receive { get; set; } = true;

    /// <summary>Radio used for transmitting text: 1 or 2 (the TX buttons).</summary>
    public int TransmitRadio { get; set; } = 1;
    public bool IsIdenting => _clock() < _identUntil;

    public TransponderMode TransponderMode =>
        IsIdenting ? TransponderMode.Ident : ModeC ? TransponderMode.ModeC : TransponderMode.Standby;

    public IReadOnlyList<AtcStation> Controllers =>
        _atc.Values.OrderBy(a => a.FrequencyKhz).ThenBy(a => a.Callsign).ToList();

    /// <summary>The last ATIS / controller information received from each station.</summary>
    public IReadOnlyDictionary<string, AtisInfo> Atis => _atis;

    public IReadOnlyList<RemoteAircraft> Traffic
    {
        get { lock (_gate) return _traffic.Values.OrderBy(t => t.Callsign).ToList(); }
    }

    [GeneratedRegex("^[A-Z0-9_-]{2,12}$")]
    private static partial Regex CallsignRegex();

    public static bool IsValidCallsign(string callsign) => CallsignRegex().IsMatch(callsign);

    public async Task ConnectAsync(ConnectInfo info, CancellationToken ct = default)
    {
        if (IsConnected) throw new InvalidOperationException("Already connected");
        if (!_sim.IsConnected || _own == null)
            throw new FsdLoginException("Simulator not connected. Start MSFS and load into an aircraft.");
        info = info with { Callsign = info.Callsign.Trim().ToUpperInvariant(), TypeCode = info.TypeCode.Trim().ToUpperInvariant() };
        if (!IsValidCallsign(info.Callsign)) throw new FsdLoginException("Invalid callsign");
        if (info.TypeCode.Length is < 2 or > 4) throw new FsdLoginException("Enter the ICAO aircraft type code (e.g. A20N)");

        var fsd = new FsdClient();
        fsd.PacketReceived += OnPacket;
        fsd.Disconnected += OnFsdDisconnected;
        _fsd = fsd;
        _info = info;
        try
        {
            await fsd.ConnectAsync(info.Host, info.Port,
                Packets.PilotLogin(info.Callsign, info.Cid, info.Password, info.RealName, simType: 1), ct).ConfigureAwait(false);
        }
        catch
        {
            _fsd = null;
            _info = null;
            await fsd.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _positionTimer = new Timer(_ => _ = SendPositionAsync(), null, TimeSpan.Zero, PositionInterval);
        _renderTimer = new Timer(_ => RenderTick(), null, RenderInterval, RenderInterval);
        ConnectionChanged?.Invoke(this, true);
        Info($"Connected to {info.Host} as {info.Callsign}");
    }

    public async Task DisconnectAsync()
    {
        var fsd = _fsd;
        if (fsd == null) return;
        await fsd.DisconnectAsync(Packets.PilotLogoff(Callsign, _info!.Cid)).ConfigureAwait(false);
        // OnFsdDisconnected does the cleanup.
    }

    private void OnFsdDisconnected(object? sender, string reason)
    {
        if (!ReferenceEquals(sender, _fsd)) return;
        _positionTimer?.Dispose();
        _renderTimer?.Dispose();
        _positionTimer = _renderTimer = null;
        _fsd = null;
        lock (_gate)
        {
            _traffic.Clear();
            _atisPending.Clear();
            if (_sim.IsConnected) _sim.RemoveAllAircraft();
        }
        _atc.Clear();
        _atis.Clear();
        ControllersChanged?.Invoke(this, EventArgs.Empty);
        TrafficChanged?.Invoke(this, EventArgs.Empty);
        Info(reason);
        ConnectionChanged?.Invoke(this, false);
    }

    private void OnSimConnectionChanged(object? sender, bool connected)
    {
        if (connected)
        {
            // Traffic must be re-created after the simulator reconnects.
            lock (_gate)
                foreach (var t in _traffic.Values) t.InSimulator = false;
            return;
        }
        _own = null;
        if (IsConnected)
        {
            Error("Lost connection to the simulator — disconnecting from the network");
            _ = DisconnectAsync();
        }
    }

    // ---- outgoing -------------------------------------------------------------

    internal async Task SendPositionAsync()
    {
        var fsd = _fsd;
        var own = _own;
        if (fsd == null || own == null) return;
        await fsd.SendAsync(Packets.Position(Callsign, TransponderMode, own.TransponderCode, own.State, own.PressureAltitudeFeet))
            .ConfigureAwait(false);
    }

    public async Task SendRadioAsync(string text)
    {
        var fsd = RequireConnection();
        var own = _own ?? throw new InvalidOperationException("No data from the simulator");
        int khz = TransmitRadio == 2 ? own.Com2Khz : own.Com1Khz;
        if (khz is < 118000 or > 136990) throw new InvalidOperationException($"COM{TransmitRadio} not tuned");
        await fsd.SendAsync(Packets.TextMessage(Callsign, Frequency.ToFsdAddress(khz), text)).ConfigureAwait(false);
        Raise(new ChatMessage(MessageKind.Radio, Callsign, text, _clock(), FrequencyKhz: khz, Outgoing: true));
    }

    public async Task SendPrivateAsync(string to, string text)
    {
        var fsd = RequireConnection();
        to = to.Trim().ToUpperInvariant();
        if (!IsValidCallsign(to)) throw new InvalidOperationException("Invalid recipient callsign");
        await fsd.SendAsync(Packets.TextMessage(Callsign, to, text)).ConfigureAwait(false);
        Raise(new ChatMessage(MessageKind.Private, Callsign, text, _clock(), Peer: to, Outgoing: true));
    }

    public async Task SendFlightPlanAsync(FlightPlan plan)
    {
        var fsd = RequireConnection();
        if (plan.Departure.Length == 0 || plan.Destination.Length == 0)
            throw new InvalidOperationException("Enter departure and destination airports");
        await fsd.SendAsync(Packets.FlightPlan(Callsign, plan)).ConfigureAwait(false);
        Info($"Flight plan {plan.Departure} → {plan.Destination} sent");
    }

    /// <summary>
    /// Ask a station for its ATIS; a controller answers with its controller information.
    /// The reply arrives as <see cref="AtisReceived"/> and a MessageKind.Atis message.
    /// </summary>
    public async Task RequestAtisAsync(string station)
    {
        var fsd = RequireConnection();
        station = station.Trim().ToUpperInvariant();
        if (!IsValidCallsign(station)) throw new InvalidOperationException("Invalid station callsign");
        lock (_gate) _atisPending[station] = new PendingAtis(_clock());
        await fsd.SendAsync(Packets.AtisRequest(Callsign, station)).ConfigureAwait(false);
    }

    public void Ident()
    {
        _identUntil = _clock() + IdentDuration;
        _ = SendPositionAsync();
    }

    private FsdClient RequireConnection() => _fsd ?? throw new InvalidOperationException("Not connected to the network");

    // ---- incoming ---------------------------------------------------------------

    internal void OnPacket(object? sender, FsdPacket p)
    {
        switch (p.Command)
        {
            case "@":
                if (Packets.ParsePosition(p) is { } pos && !pos.Callsign.Equals(Callsign, StringComparison.OrdinalIgnoreCase))
                    OnPilotPosition(pos);
                break;
            case "%":
                if (Packets.ParseAtcPosition(p) is { } atc)
                {
                    bool isNew = !_atc.ContainsKey(atc.Callsign);
                    _atc[atc.Callsign] = new AtcStation(atc.Callsign, atc.FrequencyKhz, atc.Facility, _clock());
                    if (isNew) ControllersChanged?.Invoke(this, EventArgs.Empty);
                }
                break;
            case "#DP":
                RemoveTraffic(p[0]);
                break;
            case "#DA":
                _atis.TryRemove(p[0], out _);
                if (_atc.TryRemove(p[0], out _)) ControllersChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "#TM":
                OnTextMessage(p[0], p[1], string.Join(':', p.Fields.Skip(2)));
                break;
            case "#SB":
                OnSquawkBox(p);
                break;
            case "$PI":
                if (IsToMe(p[1])) _ = _fsd?.SendAsync($"$PO{Callsign}:{p[0]}:{p[2]}");
                break;
            case "$CQ":
                OnClientQuery(p);
                break;
            case "$CR":
                if (p[2] == "ATIS" && IsToMe(p[1])) OnAtisReply(p);
                break;
            case "$ER":
                Error($"Server: {p[4]} {p[3]}".Trim());
                break;
        }
    }

    private bool IsToMe(string to) => to.Equals(Callsign, StringComparison.OrdinalIgnoreCase);

    /// <summary>".wallop": a request for help to every supervisor online ("*S").</summary>
    public async Task SendSupervisorRequestAsync(string text)
    {
        var fsd = RequireConnection();
        text = text.Trim();
        if (text.Length == 0) throw new InvalidOperationException("Describe what happened");
        await fsd.SendAsync(Packets.TextMessage(Callsign, "*S", text)).ConfigureAwait(false);
        Raise(new ChatMessage(MessageKind.Broadcast, Callsign, "[to supervisors] " + text, _clock(), Outgoing: true));
    }

    private void OnTextMessage(string from, string to, string text)
    {
        var now = _clock();
        if (from.Equals("SERVER", StringComparison.OrdinalIgnoreCase))
            Raise(new ChatMessage(MessageKind.Server, from, text, now));
        else if (IsToMe(to))
            Raise(new ChatMessage(MessageKind.Private, from, text, now, Peer: from.ToUpperInvariant()));
        else if (to == "*")
            Raise(new ChatMessage(MessageKind.Broadcast, from, text, now));
        else if (to == "*S")
            Raise(new ChatMessage(MessageKind.Broadcast, from, "[WALLOP] " + text, now));
        else if (Frequency.TryParseFsdAddress(to, out var khz) && _own is { } own &&
                 (Com1Receive && Frequency.SameChannel(khz, own.Com1Khz) || Com2Receive && Frequency.SameChannel(khz, own.Com2Khz)))
            Raise(new ChatMessage(MessageKind.Radio, from, text, now, FrequencyKhz: khz));
    }

    private void OnSquawkBox(FsdPacket p)
    {
        if (!IsToMe(p[1])) return;
        if (p[2] == "PIR" && _info != null)
        {
            _ = _fsd?.SendAsync(Packets.PlaneInfoResponse(Callsign, p[0], _info.TypeCode, Packets.AirlineFromCallsign(Callsign)));
        }
        else if (Packets.ParsePlaneInfo(p) is { } info)
        {
            lock (_gate)
            {
                if (!_traffic.TryGetValue(p[0], out var t)) return;
                bool changed = !string.Equals(t.Equipment, info.Equipment, StringComparison.OrdinalIgnoreCase);
                t.Equipment = info.Equipment;
                t.Airline = info.Airline.Length > 0 ? info.Airline : Packets.AirlineFromCallsign(t.Callsign);
                // The aircraft was already drawn with a guessed model: redraw with the right one.
                if (changed && t.InSimulator && (_sim.MatchesModels || _matcher.Match(t.Equipment, t.Airline) != t.ModelTitle))
                {
                    _sim.RemoveAircraft(t.Callsign);
                    t.InSimulator = false;
                }
            }
            TrafficChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnClientQuery(FsdPacket p)
    {
        if (!IsToMe(p[1]) || _fsd == null || _info == null) return;
        switch (p[2])
        {
            case "RN":
                _ = _fsd.SendAsync($"$CR{Callsign}:{p[0]}:RN:{Packets.Clean(_info.RealName)}::1");
                break;
            case "CAPS":
                _ = _fsd.SendAsync($"$CR{Callsign}:{p[0]}:CAPS");
                break;
            case "INF":
                _ = _fsd.SendAsync(Packets.TextMessage(Callsign, p[0],
                    $"SkyPilot {typeof(NetworkSession).Assembly.GetName().Version} / {_sim.Name}"));
                break;
        }
    }

    /// <summary>
    /// $CR&lt;station&gt;:&lt;me&gt;:ATIS:T:&lt;line&gt; for each text line, then ...:ATIS:E:&lt;count&gt;.
    /// Other kinds (V = voice URL, Z = zulu time, …) are ignored.
    /// </summary>
    private void OnAtisReply(FsdPacket p)
    {
        string station = p[0];
        AtisInfo? done = null;
        lock (_gate)
        {
            switch (p[3])
            {
                case "T":
                    if (!_atisPending.TryGetValue(station, out var pending))
                        _atisPending[station] = pending = new PendingAtis(_clock());
                    pending.Lines.Add(string.Join(':', p.Fields.Skip(4)).Trim());
                    pending.LastLine = _clock();
                    break;
                case "E":
                    if (_atisPending.Remove(station, out var ended))
                        done = Complete(station, ended);
                    break;
            }
        }
        if (done != null) PublishAtis(done);
    }

    private AtisInfo Complete(string station, PendingAtis pending)
    {
        var lines = pending.Lines.Where(l => l.Length > 0).ToList();
        var atis = new AtisInfo(station.ToUpperInvariant(), lines, AtisInfo.ExtractLetter(lines), _clock());
        _atis[atis.Station] = atis;
        return atis;
    }

    private void PublishAtis(AtisInfo atis)
    {
        AtisReceived?.Invoke(this, atis);
        Raise(new ChatMessage(MessageKind.Atis, atis.Station,
            atis.Lines.Count > 0 ? atis.Text : "Station sent no information", atis.ReceivedAt));
    }

    private void OnPilotPosition(PilotPosition pos)
    {
        bool added = false;
        lock (_gate)
        {
            if (!_traffic.TryGetValue(pos.Callsign, out var t))
            {
                t = new RemoteAircraft(pos.Callsign) { Airline = Packets.AirlineFromCallsign(pos.Callsign) };
                _traffic[pos.Callsign] = t;
                added = true;
            }
            t.OnPositionReport(pos.State, _clock());
        }
        if (added)
        {
            _ = _fsd?.SendAsync(Packets.PlaneInfoRequest(Callsign, pos.Callsign));
            TrafficChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RemoveTraffic(string callsign)
    {
        lock (_gate)
        {
            if (!_traffic.Remove(callsign, out var t)) return;
            if (t.InSimulator) _sim.RemoveAircraft(t.Callsign);
        }
        TrafficChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnAircraftCreateFailed(object? sender, AircraftCreateFailedEventArgs e)
    {
        lock (_gate)
        {
            if (!_traffic.TryGetValue(e.Callsign, out var t)) return;
            if (e.ModelTitle == ModelMatcher.FallbackTitle)
            {
                t.InSimulator = true; // give up; don't retry every frame
                Error($"Cannot display {e.Callsign}: model \"{e.ModelTitle}\" not found");
                return;
            }
            // Matched model missing: try the network fallback, then the stock A320neo.
            t.ModelTitle = e.ModelTitle == _matcher.Fallback ? ModelMatcher.FallbackTitle : _matcher.Fallback;
            _sim.AddAircraft(t.Callsign, ModelOf(t), t.Render(_clock()));
        }
    }

    // ---- traffic rendering -----------------------------------------------------------

    private static AircraftModel ModelOf(RemoteAircraft t) =>
        new(t.ModelTitle ?? "", ModelMatcher.NormalizeType(t.Equipment), t.Airline);

    internal void RenderTick()
    {
        var now = _clock();
        bool changed = false;
        lock (_gate)
        {
            foreach (var t in _traffic.Values.ToList())
            {
                if (now - t.LastUpdate > StaleAfter)
                {
                    _traffic.Remove(t.Callsign);
                    if (t.InSimulator) _sim.RemoveAircraft(t.Callsign);
                    changed = true;
                    continue;
                }
                if (!_sim.IsConnected) continue;
                if (!t.InSimulator)
                {
                    if (t.Equipment.Length == 0 && now - t.FirstSeen < ModelInfoWait) continue;
                    t.ModelTitle = _sim.MatchesModels ? "" : _matcher.Match(t.Equipment, t.Airline);
                    _sim.AddAircraft(t.Callsign, ModelOf(t), t.Render(now));
                    t.InSimulator = true;
                }
                else
                {
                    _sim.UpdateAircraft(t.Callsign, t.Render(now));
                }
            }
        }
        CheckPendingAtis(now);
        foreach (var a in _atc.Values)
        {
            if (now - a.LastSeen > TimeSpan.FromSeconds(60) && _atc.TryRemove(a.Callsign, out _))
                ControllersChanged?.Invoke(this, EventArgs.Empty);
        }
        if (changed) TrafficChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Finish ATIS replies that never got an end marker, and give up on stations that never answered.</summary>
    private void CheckPendingAtis(DateTime now)
    {
        List<AtisInfo>? done = null;
        List<string>? unanswered = null;
        lock (_gate)
        {
            foreach (var (station, pending) in _atisPending.ToList())
            {
                if (pending.Lines.Count > 0 && now - pending.LastLine >= AtisLineWait)
                {
                    _atisPending.Remove(station);
                    (done ??= []).Add(Complete(station, pending));
                }
                else if (pending.Lines.Count == 0 && now - pending.RequestedAt >= AtisReplyTimeout)
                {
                    _atisPending.Remove(station);
                    (unanswered ??= []).Add(station);
                }
            }
        }
        foreach (var atis in done ?? []) PublishAtis(atis);
        foreach (var station in unanswered ?? []) Info($"{station}: no reply to ATIS request");
    }

    private sealed class PendingAtis(DateTime requestedAt)
    {
        public DateTime RequestedAt { get; } = requestedAt;
        public DateTime LastLine { get; set; } = requestedAt;
        public List<string> Lines { get; } = [];
    }

    // ---- helpers ------------------------------------------------------------------------

    private void Raise(ChatMessage m) => MessageReceived?.Invoke(this, m);
    internal void Info(string text) => Raise(new ChatMessage(MessageKind.Info, "SkyPilot", text, _clock()));
    internal void Error(string text) => Raise(new ChatMessage(MessageKind.Error, "SkyPilot", text, _clock()));

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _positionTimer?.Dispose();
        _renderTimer?.Dispose();
    }
}
