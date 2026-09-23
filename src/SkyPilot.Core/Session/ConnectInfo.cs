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
