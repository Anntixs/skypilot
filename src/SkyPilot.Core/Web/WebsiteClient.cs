using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SkyPilot.Core.Model;

namespace SkyPilot.Core.Web;

/// <summary>
/// Flight plans are filed on the SkyNetwork website; SkyPilot only reads them.
/// API contract (see docs/website-api.md):
///   GET {site}/api/flightplans/latest?cid={cid}  -> 200 JSON plan, or 404 if none is filed
///   page to file a plan: {site}/flightplan?callsign={callsign}
/// </summary>
public sealed class WebsiteClient(HttpClient http, Uri site)
{
    public Uri Site { get; } = site;

    public Uri FlightPlanPage(string callsign) =>
        new(Site, "flightplan" + (callsign.Length > 0 ? "?callsign=" + Uri.EscapeDataString(callsign) : ""));

    /// <summary>The member's most recent filed flight plan, or null if none is filed.</summary>
    public async Task<FlightPlan?> GetLatestFlightPlanAsync(int cid, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(new Uri(Site, $"api/flightplans/latest?cid={cid}"), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<FlightPlanDto>(ct).ConfigureAwait(false);
        return dto?.ToFlightPlan();
    }

    /// <summary>Normalizes a user-entered site address: adds https:// and a trailing slash.</summary>
    public static bool TryParseSite(string text, out Uri site)
    {
        site = null!;
        text = text.Trim();
        if (text.Length == 0) return false;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!text.EndsWith('/')) text += "/";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        site = uri;
        return true;
    }

    internal sealed class FlightPlanDto
    {
        [JsonPropertyName("rules")] public string? Rules { get; set; }
        [JsonPropertyName("aircraft")] public string? Aircraft { get; set; }
        [JsonPropertyName("cruiseSpeed")] public int CruiseSpeed { get; set; }
        [JsonPropertyName("departure")] public string? Departure { get; set; }
        [JsonPropertyName("destination")] public string? Destination { get; set; }
        [JsonPropertyName("alternate")] public string? Alternate { get; set; }
        [JsonPropertyName("departureTime")] public string? DepartureTime { get; set; }
        [JsonPropertyName("cruiseAltitude")] public string? CruiseAltitude { get; set; }
        [JsonPropertyName("enrouteMinutes")] public int EnrouteMinutes { get; set; }
        [JsonPropertyName("fuelMinutes")] public int FuelMinutes { get; set; }
        [JsonPropertyName("route")] public string? Route { get; set; }
        [JsonPropertyName("remarks")] public string? Remarks { get; set; }

        public FlightPlan ToFlightPlan() => new()
        {
            Rules = string.Equals(Rules, "VFR", StringComparison.OrdinalIgnoreCase) || string.Equals(Rules, "V", StringComparison.OrdinalIgnoreCase)
                ? FlightRules.Vfr : FlightRules.Ifr,
            AircraftType = Aircraft ?? "",
            TrueAirspeed = CruiseSpeed,
            Departure = Departure ?? "",
            Destination = Destination ?? "",
            Alternate = Alternate ?? "",
            DepartureTime = DepartureTime ?? "",
            CruiseAltitude = CruiseAltitude ?? "",
            TimeEnroute = TimeSpan.FromMinutes(EnrouteMinutes),
            FuelOnBoard = TimeSpan.FromMinutes(FuelMinutes),
            Route = Route ?? "",
            Remarks = Remarks ?? "",
        };
    }
}
