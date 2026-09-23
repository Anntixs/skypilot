namespace SkyPilot.Core.Model;

public enum FlightRules
{
    Ifr,
    Vfr,
}

public sealed record FlightPlan
{
    public FlightRules Rules { get; init; } = FlightRules.Ifr;
    public string AircraftType { get; init; } = "";
    public int TrueAirspeed { get; init; }
    public string Departure { get; init; } = "";
    public string Destination { get; init; } = "";
    public string Alternate { get; init; } = "";
    /// <summary>Planned departure time, UTC, "HHmm".</summary>
    public string DepartureTime { get; init; } = "";
    public string CruiseAltitude { get; init; } = "";
    public TimeSpan TimeEnroute { get; init; }
    public TimeSpan FuelOnBoard { get; init; }
    public string Route { get; init; } = "";
    public string Remarks { get; init; } = "";
}
