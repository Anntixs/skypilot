using SkyPilot.Core.Model;

namespace SkyPilot.Core.Simulation;

/// <summary>Which simulator SkyPilot looks for ("auto" tries all of them).</summary>
public static class SimulatorKind
{
    public const string Auto = "auto";
    public const string Msfs = "msfs";
    public const string Prepar3D = "p3d";
    public const string XPlane = "xplane";

    public static IReadOnlyList<(string Id, string Title)> All { get; } =
    [
        (Auto, "Automatic"),
        (Msfs, "Microsoft Flight Simulator 2020 / 2024"),
        (Prepar3D, "Prepar3D v4 / v5 / v6"),
        (XPlane, "X-Plane 11 / 12"),
    ];
}

/// <summary>
/// All the simulators SkyPilot supports behind one <see cref="ISimulator"/>: <see cref="Connect"/> attaches to the
/// first one that is running (or only to the chosen one), and everything else goes to that simulator until it
/// disconnects. Events of the others are ignored.
/// </summary>
public sealed class SimulatorHub : ISimulator
{
    private readonly IReadOnlyList<(string Kind, ISimulator Sim)> _sims;
    private readonly object _lock = new();
    private ISimulator? _active;

    public SimulatorHub(IEnumerable<(string Kind, ISimulator Sim)> simulators)
    {
        _sims = simulators.ToList();
        foreach (var (_, sim) in _sims)
        {
            var s = sim;
            s.ConnectionChanged += (_, connected) => OnConnectionChanged(s, connected);
            s.OwnAircraftUpdated += (_, data) => { if (IsActive(s)) OwnAircraftUpdated?.Invoke(this, data); };
            s.AircraftCreateFailed += (_, e) => { if (IsActive(s)) AircraftCreateFailed?.Invoke(this, e); };
        }
    }

    /// <summary>The simulator to use: one of <see cref="SimulatorKind"/>.</summary>
    public string Preferred { get; set; } = SimulatorKind.Auto;

    /// <summary>The connected simulator, or null.</summary>
    public ISimulator? Active
    {
        get { lock (_lock) return _active; }
    }

    /// <summary>The kind of the connected simulator, or null.</summary>
    public string? ActiveKind
    {
        get
        {
            var active = Active;
            return active == null ? null : _sims.First(x => ReferenceEquals(x.Sim, active)).Kind;
        }
    }

    public string Name => Active?.Name ?? "Simulator";
    public bool IsConnected => Active?.IsConnected == true;
    public bool MatchesModels => Active?.MatchesModels == true;
    public string? LastError { get; private set; }

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<OwnAircraftData>? OwnAircraftUpdated;
    public event EventHandler<AircraftCreateFailedEventArgs>? AircraftCreateFailed;

    private bool IsActive(ISimulator sim)
    {
        lock (_lock) return ReferenceEquals(_active, sim);
    }

    private IEnumerable<(string Kind, ISimulator Sim)> Candidates() =>
        Preferred == SimulatorKind.Auto ? _sims : _sims.Where(s => s.Kind == Preferred);

    public bool Connect()
    {
        lock (_lock)
        {
            if (_active != null) return true;
        }
        string? error = null;
        foreach (var (_, sim) in Candidates())
        {
            if (sim.Connect())
            {
                lock (_lock) _active = sim;
                LastError = null;
                // Some simulators report the connection at once (before _active was set): pass it on now.
                if (sim.IsConnected) ConnectionChanged?.Invoke(this, true);
                return true;
            }
            error ??= sim.LastError;
        }
        LastError = error;
        return false;
    }

    private void OnConnectionChanged(ISimulator sim, bool connected)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_active, sim)) return;
            if (!connected) _active = null;
        }
        ConnectionChanged?.Invoke(this, connected);
    }

    public void Disconnect()
    {
        ISimulator? active;
        lock (_lock) active = _active;
        active?.Disconnect();
        lock (_lock) _active = null;
    }

    public void AddAircraft(string callsign, AircraftModel model, AircraftState state) => Active?.AddAircraft(callsign, model, state);
    public void UpdateAircraft(string callsign, AircraftState state) => Active?.UpdateAircraft(callsign, state);
    public void RemoveAircraft(string callsign) => Active?.RemoveAircraft(callsign);
    public void RemoveAllAircraft() => Active?.RemoveAllAircraft();
    public void SetComFrequency(int radio, int khz) => Active?.SetComFrequency(radio, khz);
    public void SetTransponderCode(int code) => Active?.SetTransponderCode(code);

    public void Dispose()
    {
        foreach (var (_, sim) in _sims) sim.Dispose();
    }
}
