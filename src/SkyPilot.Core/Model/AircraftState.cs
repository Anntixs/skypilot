namespace SkyPilot.Core.Model;

/// <summary>
/// Position and attitude of an aircraft. Angles use the aviation convention:
/// pitch is positive nose up, bank is positive right wing down, heading is true degrees.
/// </summary>
public readonly record struct AircraftState(
    double Latitude,
    double Longitude,
    double AltitudeFeet,
    double PitchDegrees,
    double BankDegrees,
    double HeadingDegrees,
    double GroundSpeedKnots,
    bool OnGround);

public enum TransponderMode
{
    Standby,
    ModeC,
    Ident,
}

/// <summary>Everything SkyPilot reads from the user's own aircraft.</summary>
public sealed record OwnAircraftData(
    AircraftState State,
    double PressureAltitudeFeet,
    int Com1Khz,
    int Com2Khz,
    int TransponderCode,
    bool TransponderOn);
