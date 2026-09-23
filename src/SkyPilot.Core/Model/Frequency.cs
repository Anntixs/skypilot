using System.Globalization;

namespace SkyPilot.Core.Model;

/// <summary>Helpers for VHF frequencies, kept internally as integer kHz (118.100 MHz = 118100).</summary>
public static class Frequency
{
    public const int Unicom = 122800;

    public static bool TryParse(string text, out int khz)
    {
        khz = 0;
        if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
            return false;
        var value = (int)Math.Round(mhz * 1000);
        if (value < 118000 || value > 136990)
            return false;
        khz = value;
        return true;
    }

    public static string Format(int khz) =>
        (khz / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>FSD radio address: 118.100 -> "@18100".</summary>
    public static string ToFsdAddress(int khz) => "@" + (khz - 100000).ToString(CultureInfo.InvariantCulture);

    public static bool TryParseFsdAddress(string address, out int khz)
    {
        khz = 0;
        if (address.Length < 2 || address[0] != '@' ||
            !int.TryParse(address.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return false;
        khz = value + 100000;
        return true;
    }

    /// <summary>
    /// True if two frequencies are the same channel. Sims round 8.33 kHz channels differently
    /// (118.005 vs 118.000), so allow a small tolerance.
    /// </summary>
    public static bool SameChannel(int a, int b) => Math.Abs(a - b) <= 5;
}
