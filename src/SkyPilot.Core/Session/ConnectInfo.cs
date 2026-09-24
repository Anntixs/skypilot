namespace SkyPilot.Core.Session;

public sealed record ConnectInfo(
    string Host,
    int Port,
    int Cid,
    string Password,
    string Callsign,
    string TypeCode,
    string RealName);

public sealed record AtcStation(string Callsign, int FrequencyKhz, int Facility, DateTime LastSeen)
{
    /// <summary>ATIS stations connect as separate clients named like "UUEE_ATIS".</summary>
    public bool IsAtis => IsAtisCallsign(Callsign);

    /// <summary>"ATIS", "TWR", "OBS", …</summary>
    public string FacilityText => FacilityName(Callsign, Facility);

    public static bool IsAtisCallsign(string callsign) => callsign.EndsWith("_ATIS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Like <see cref="FacilityName(int)"/>, but ATIS stations are "ATIS" whatever facility they report.</summary>
    public static string FacilityName(string callsign, int facility) =>
        IsAtisCallsign(callsign) ? "ATIS" : FacilityName(facility);

    public static string FacilityName(int facility) => facility switch
    {
        1 => "FSS",
        2 => "DEL",
        3 => "GND",
        4 => "TWR",
        5 => "APP",
        6 => "CTR",
        _ => "OBS",
    };
}
