using System.Globalization;
using SkyPilot.Core.Model;

namespace SkyPilot.Core.Fsd;

/// <summary>A raw FSD line split into its command prefix and colon-separated fields.</summary>
public sealed record FsdPacket(string Command, string[] Fields)
{
    public string this[int index] => index < Fields.Length ? Fields[index] : "";

    public static FsdPacket? Parse(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        string command;
        string body;
        if (line[0] is '@' or '%')
        {
            command = line[..1];
            body = line[1..];
        }
        else if (line[0] is '#' or '$' && line.Length >= 3)
        {
            command = line[..3];
            body = line[3..];
        }
        else
        {
            return null;
        }
        return new FsdPacket(command, body.Split(':'));
    }

    public override string ToString() => Command + string.Join(':', Fields);
}

public sealed record PilotPosition(
    string Callsign,
    TransponderMode Mode,
    int Squawk,
    AircraftState State);

public sealed record AtcPosition(string Callsign, int FrequencyKhz, int Facility, int VisualRange, double Latitude, double Longitude);

/// <summary>Builds and parses the FSD packets a pilot client uses.</summary>
public static class Packets
{
    public const int ProtocolRevision = 100;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Strip characters that would break the colon-separated format.</summary>
    public static string Clean(string text) => text.Replace(':', ' ').Replace('\r', ' ').Replace('\n', ' ');

    public static string PilotLogin(string callsign, int cid, string password, string realName, int simType) =>
        $"#AP{callsign}:SERVER:{cid}:{Clean(password)}:1:{ProtocolRevision}:{simType}:{Clean(realName)}";

    public static string PilotLogoff(string callsign, int cid) => $"#DP{callsign}:{cid}";

    public static string Position(string callsign, TransponderMode mode, int squawk, AircraftState s, double pressureAltitude)
    {
        char modeChar = mode switch { TransponderMode.Standby => 'S', TransponderMode.Ident => 'Y', _ => 'N' };
        uint pbh = Pbh.Encode(s.PitchDegrees, s.BankDegrees, s.HeadingDegrees, s.OnGround);
        int pressureDelta = (int)Math.Round(pressureAltitude - s.AltitudeFeet);
        return string.Create(Inv,
            $"@{modeChar}:{callsign}:{squawk:0000}:1:{s.Latitude:0.000000}:{s.Longitude:0.000000}:" +
            $"{Math.Round(s.AltitudeFeet):0}:{Math.Round(s.GroundSpeedKnots):0}:{pbh}:{pressureDelta}");
    }

    public static PilotPosition? ParsePosition(FsdPacket p)
    {
        if (p.Command != "@" || p.Fields.Length < 9) return null;
        if (!double.TryParse(p[4], NumberStyles.Float, Inv, out var lat) ||
            !double.TryParse(p[5], NumberStyles.Float, Inv, out var lon) ||
            !double.TryParse(p[6], NumberStyles.Float, Inv, out var alt) ||
            !double.TryParse(p[7], NumberStyles.Float, Inv, out var gs) ||
            !uint.TryParse(p[8], NumberStyles.Integer, Inv, out var pbhWord))
            return null;
        int.TryParse(p[2], NumberStyles.Integer, Inv, out var squawk);
        var mode = p[0] switch { "S" => TransponderMode.Standby, "Y" => TransponderMode.Ident, _ => TransponderMode.ModeC };
        var pbh = Pbh.Decode(pbhWord);
        return new PilotPosition(p[1], mode, squawk,
            new AircraftState(lat, lon, alt, pbh.Pitch, pbh.Bank, pbh.Heading, gs, pbh.OnGround));
    }

    public static AtcPosition? ParseAtcPosition(FsdPacket p)
    {
        if (p.Command != "%" || p.Fields.Length < 7) return null;
        if (!int.TryParse(p[1], NumberStyles.Integer, Inv, out var freq) ||
            !int.TryParse(p[2], NumberStyles.Integer, Inv, out var facility) ||
            !int.TryParse(p[3], NumberStyles.Integer, Inv, out var range) ||
            !double.TryParse(p[5], NumberStyles.Float, Inv, out var lat) ||
            !double.TryParse(p[6], NumberStyles.Float, Inv, out var lon))
            return null;
        return new AtcPosition(p[0], freq + 100000, facility, range, lat, lon);
    }

    public static string TextMessage(string from, string to, string text) => $"#TM{from}:{to}:{Clean(text)}";

    /// <summary>
    /// $FP&lt;cs&gt;:*A:&lt;rules&gt;:&lt;type&gt;:&lt;tas&gt;:&lt;dep&gt;:&lt;deptime&gt;:&lt;actdeptime&gt;:&lt;alt&gt;:&lt;dest&gt;:
    /// &lt;hrs enroute&gt;:&lt;min enroute&gt;:&lt;hrs fuel&gt;:&lt;min fuel&gt;:&lt;altn&gt;:&lt;remarks&gt;:&lt;route&gt;
    /// </summary>
    public static string FlightPlan(string callsign, FlightPlan fp)
    {
        string rules = fp.Rules == FlightRules.Vfr ? "V" : "I";
        string[] fields =
        [
            "*A", rules, Clean(fp.AircraftType).ToUpperInvariant(), fp.TrueAirspeed.ToString(Inv),
            Clean(fp.Departure).ToUpperInvariant(), Clean(fp.DepartureTime), "0", Clean(fp.CruiseAltitude).ToUpperInvariant(),
            Clean(fp.Destination).ToUpperInvariant(),
            ((int)fp.TimeEnroute.TotalHours).ToString(Inv), fp.TimeEnroute.Minutes.ToString(Inv),
            ((int)fp.FuelOnBoard.TotalHours).ToString(Inv), fp.FuelOnBoard.Minutes.ToString(Inv),
            Clean(fp.Alternate).ToUpperInvariant(), Clean(fp.Remarks), Clean(fp.Route).ToUpperInvariant(),
        ];
        return $"$FP{callsign}:" + string.Join(':', fields);
    }

    /// <summary>Ask an ATIS station (or a controller) for its ATIS / controller information.</summary>
    public static string AtisRequest(string from, string station) => $"$CQ{from}:{station}:ATIS";

    /// <summary>Plane information request: ask another pilot what aircraft they fly.</summary>
    public static string PlaneInfoRequest(string from, string to) => $"#SB{from}:{to}:PIR";

    public static string PlaneInfoResponse(string from, string to, string equipment, string airline)
    {
        var s = $"#SB{from}:{to}:PI:GEN:EQUIPMENT={Clean(equipment)}";
        return airline.Length > 0 ? s + $":AIRLINE={Clean(airline)}" : s;
    }

    /// <summary>Parse "#SB&lt;from&gt;:&lt;to&gt;:PI:GEN:EQUIPMENT=B738:AIRLINE=AFL".</summary>
    public static (string Equipment, string Airline)? ParsePlaneInfo(FsdPacket p)
    {
        if (p.Command != "#SB" || p[2] != "PI" || p[3] != "GEN") return null;
        string equipment = "", airline = "";
        foreach (var f in p.Fields.Skip(4))
        {
            if (f.StartsWith("EQUIPMENT=", StringComparison.Ordinal)) equipment = f[10..];
            else if (f.StartsWith("AIRLINE=", StringComparison.Ordinal)) airline = f[8..];
        }
        return (equipment, airline);
    }

    /// <summary>Airline designator guessed from a callsign: "AFL123" -> "AFL", "RA12345" -> "".</summary>
    public static string AirlineFromCallsign(string callsign) =>
        callsign.Length > 3 && callsign.Take(3).All(char.IsLetter) && char.IsDigit(callsign[3]) ? callsign[..3] : "";
}
