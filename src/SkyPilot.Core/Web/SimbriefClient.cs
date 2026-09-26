using System.Globalization;
using System.Text.Json;
using SkyPilot.Core.Model;

namespace SkyPilot.Core.Web;

/// <summary>A flight plan read from the pilot's latest SimBrief OFP, with the ATC callsign it was planned with.</summary>
public sealed record SimbriefPlan(FlightPlan Plan, string Callsign);

/// <summary>
/// Reads the pilot's latest SimBrief operational flight plan with the same API and rules as the SkyNetwork website's
/// SimBrief import: https://www.simbrief.com/api/xml.fetcher.php?username=… (or userid=… for a numeric Pilot ID), json=v2.
/// </summary>
public sealed class SimbriefClient(HttpClient http)
{
    public async Task<(SimbriefPlan? Plan, string? Error)> FetchAsync(string user, CancellationToken ct = default)
    {
        user = user.Trim();
        if (user.Length is 0 or > 64) return (null, "Enter your SimBrief username or Pilot ID in Settings.");
        string query = user.All(char.IsAsciiDigit) ? "userid=" : "username=";
        string url = $"https://www.simbrief.com/api/xml.fetcher.php?{query}{Uri.EscapeDataString(user)}&json=v2";
        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            // Unknown users and missing plans come back as 400 with a status text in the body.
            return Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, "SimBrief is not answering, try again later.");
        }
    }

    /// <summary>Reads an OFP in SimBrief's JSON format (also used by tests).</summary>
    public static (SimbriefPlan? Plan, string? Error) Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (null, "SimBrief sent an answer SkyPilot cannot read.");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, "SimBrief sent an answer SkyPilot cannot read.");

            string status = Str(root, "fetch", "status");
            if (!status.Equals("Success", StringComparison.OrdinalIgnoreCase))
                return (null, status.Contains("UserID", StringComparison.OrdinalIgnoreCase)
                    ? "There is no such SimBrief user: check the username or Pilot ID in Settings."
                    : status.Length > 0 ? "SimBrief: " + status : "SimBrief has no flight plan for this user.");

            string dep = Str(root, "origin", "icao_code"), dest = Str(root, "destination", "icao_code");
            if (dep.Length != 4 || dest.Length != 4) return (null, "The latest SimBrief plan has no departure or destination airport.");

            string callsign = Str(root, "atc", "callsign");
            if (callsign.Length == 0) callsign = Str(root, "general", "icao_airline") + Str(root, "general", "flight_number");
            string aircraft = Str(root, "aircraft", "icao_code");
            if (aircraft.Length == 0) aircraft = Str(root, "aircraft", "icaocode");
            int level = Int(Str(root, "general", "initial_altitude"));
            long outTime = Unix(Str(root, "times", "sched_out"));

            var plan = new FlightPlan
            {
                Rules = FlightRules.Ifr,
                AircraftType = aircraft.ToUpperInvariant(),
                TrueAirspeed = Int(Str(root, "general", "cruise_tas")),
                Departure = dep.ToUpperInvariant(),
                Destination = dest.ToUpperInvariant(),
                Alternate = Alternate(root).ToUpperInvariant(),
                DepartureTime = outTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(outTime).UtcDateTime.ToString("HHmm", CultureInfo.InvariantCulture) : "",
                CruiseAltitude = level >= 10000 ? $"FL{level / 100:000}" : level > 0 ? level.ToString(CultureInfo.InvariantCulture) : "",
                TimeEnroute = TimeSpan.FromMinutes(Math.Round(Seconds(Str(root, "times", "est_time_enroute")) / 60.0)),
                FuelOnBoard = TimeSpan.FromMinutes(Math.Round(Seconds(Str(root, "times", "endurance")) / 60.0)),
                Route = string.Join(' ', Str(root, "general", "route").Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant(),
            };
            return (new SimbriefPlan(plan, callsign.ToUpperInvariant()), null);
        }
    }

    // A single alternate is an object, several are an array: the first one is filed.
    private static string Alternate(JsonElement root)
    {
        if (!root.TryGetProperty("alternate", out var a)) return "";
        if (a.ValueKind == JsonValueKind.Array) a = a.GetArrayLength() > 0 ? a[0] : default;
        return a.ValueKind == JsonValueKind.Object ? Str(a, "icao_code") : "";
    }

    private static string Str(JsonElement e, params string[] path)
    {
        foreach (var name in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e)) return "";
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString()!.Trim(),
            JsonValueKind.Number => e.GetRawText(),
            _ => "",
        };
    }

    /// <summary>Times come as seconds ("13500") or, in some OFPs, as a clock ("03:45:00").</summary>
    private static int Seconds(string s)
    {
        if (!s.Contains(':')) return Int(s);
        var parts = s.Split(':');
        if (parts.Length is not (2 or 3) || !parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return 0;
        return int.Parse(parts[0], CultureInfo.InvariantCulture) * 3600 + int.Parse(parts[1], CultureInfo.InvariantCulture) * 60
               + (parts.Length == 3 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 0);
    }

    /// <summary>Moments come as unix seconds ("1790251200") or, in some OFPs, as ISO 8601 ("2026-09-24T12:00:00Z").</summary>
    private static long Unix(string s)
    {
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)) return unix;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
            ? when.ToUnixTimeSeconds() : 0;
    }

    private static double Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int Int(string s) => (int)Math.Round(Num(s));
}
