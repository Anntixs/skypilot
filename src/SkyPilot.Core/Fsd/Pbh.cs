namespace SkyPilot.Core.Fsd;

/// <summary>
/// Classic FSD packed pitch/bank/heading word: 10 bits each for pitch, bank and heading
/// (a full circle is 1024 units) plus an on-ground bit.
///   bits 22-31 pitch, 12-21 bank, 2-11 heading, bit 1 on ground.
/// Pitch and bank are sent in the flight-simulator convention (positive = nose down / left
/// wing down), i.e. negated relative to <see cref="Model.AircraftState"/>.
/// </summary>
public static class Pbh
{
    private const double UnitsPerDegree = 1024.0 / 360.0;

    public static uint Encode(double pitch, double bank, double heading, bool onGround)
    {
        uint p = ToUnits(-pitch);
        uint b = ToUnits(-bank);
        uint h = ToUnits(heading);
        return (p << 22) | (b << 12) | (h << 2) | (onGround ? 2u : 0u);
    }

    public static (double Pitch, double Bank, double Heading, bool OnGround) Decode(uint value)
    {
        double pitch = -ToSignedDegrees((value >> 22) & 0x3FF);
        double bank = -ToSignedDegrees((value >> 12) & 0x3FF);
        double heading = ((value >> 2) & 0x3FF) / UnitsPerDegree;
        return (pitch, bank, heading, (value & 2) != 0);
    }

    private static uint ToUnits(double degrees)
    {
        double normalized = degrees % 360.0;
        if (normalized < 0) normalized += 360.0;
        return (uint)Math.Round(normalized * UnitsPerDegree) & 0x3FF;
    }

    private static double ToSignedDegrees(uint units)
    {
        double deg = units / UnitsPerDegree;
        return deg > 180 ? deg - 360 : deg;
    }
}
