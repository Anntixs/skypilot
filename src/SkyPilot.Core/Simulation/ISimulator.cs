using SkyPilot.Core.Model;

namespace SkyPilot.Core.Simulation;

public sealed class AircraftCreateFailedEventArgs(string callsign, string modelTitle) : EventArgs
{
    public string Callsign { get; } = callsign;
    public string ModelTitle { get; } = modelTitle;
}

/// <summary>
/// What to draw for another aircraft: the model title for simulators that load models by name (MSFS, Prepar3D),
/// and the ICAO type and airline for simulators that pick a model themselves (X-Plane with CSL models).
/// </summary>
public sealed record AircraftModel(string Title, string IcaoType, string Airline);

/// <summary>
/// The flight simulator as seen by SkyPilot: reads the user's aircraft and draws network traffic.
/// Implementations raise events on a background thread.
/// </summary>
public interface ISimulator : IDisposable
{
    /// <summary>Human readable simulator name, e.g. "Microsoft Flight Simulator".</summary>
    string Name { get; }
    /// <summary>True when the simulator chooses models from the ICAO type and airline itself; the title is then unused.</summary>
    bool MatchesModels => false;
    /// <summary>Why the last <see cref="Connect"/> failed (a missing DLL or plugin), or null.</summary>
    string? LastError => null;
    bool IsConnected { get; }

    event EventHandler<bool>? ConnectionChanged;
    event EventHandler<OwnAircraftData>? OwnAircraftUpdated;
    event EventHandler<AircraftCreateFailedEventArgs>? AircraftCreateFailed;

    /// <summary>Try to attach to a running simulator. Returns false if none is running.</summary>
    bool Connect();
    void Disconnect();

    void AddAircraft(string callsign, AircraftModel model, AircraftState state);
    void UpdateAircraft(string callsign, AircraftState state);
    void RemoveAircraft(string callsign);
    void RemoveAllAircraft();

    /// <summary>Tune COM1 (radio = 1) or COM2 (radio = 2).</summary>
    void SetComFrequency(int radio, int khz);
    void SetTransponderCode(int code);
}
