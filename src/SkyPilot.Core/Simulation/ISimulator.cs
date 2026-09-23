using SkyPilot.Core.Model;

namespace SkyPilot.Core.Simulation;

public sealed class AircraftCreateFailedEventArgs(string callsign, string modelTitle) : EventArgs
{
    public string Callsign { get; } = callsign;
    public string ModelTitle { get; } = modelTitle;
}

/// <summary>
/// The flight simulator as seen by SkyPilot: reads the user's aircraft and draws network traffic.
/// Implementations raise events on a background thread.
/// </summary>
public interface ISimulator : IDisposable
{
    /// <summary>Human readable simulator name, e.g. "Microsoft Flight Simulator".</summary>
    string Name { get; }
    bool IsConnected { get; }

    event EventHandler<bool>? ConnectionChanged;
    event EventHandler<OwnAircraftData>? OwnAircraftUpdated;
    event EventHandler<AircraftCreateFailedEventArgs>? AircraftCreateFailed;

    /// <summary>Try to attach to a running simulator. Returns false if none is running.</summary>
    bool Connect();
    void Disconnect();

    void AddAircraft(string callsign, string modelTitle, AircraftState state);
    void UpdateAircraft(string callsign, AircraftState state);
    void RemoveAircraft(string callsign);
    void RemoveAllAircraft();

    /// <summary>Tune COM1 (radio = 1) or COM2 (radio = 2).</summary>
    void SetComFrequency(int radio, int khz);
    void SetTransponderCode(int code);
}
