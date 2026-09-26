using System.Net;
using System.Text;
using SkyPilot.Core.Model;
using SkyPilot.Core.Web;

namespace SkyPilot.Core.Tests;

public class SimbriefClientTests
{
    private const string Ofp = """
        {
          "fetch": { "status": "Success" },
          "general": { "icao_airline": "AFL", "flight_number": "1234", "initial_altitude": "35000", "cruise_tas": "452",
                       "route": "GUBAR  R22 ARTOM   B141 LAPEK" },
          "atc": { "callsign": "AFL1234" },
          "origin": { "icao_code": "UUEE" },
          "destination": { "icao_code": "ULLI" },
          "alternate": [ { "icao_code": "ULLP" }, { "icao_code": "UUWW" } ],
          "aircraft": { "icao_code": "B738" },
          "times": { "est_time_enroute": "4980", "endurance": "9900", "sched_out": "1790251200" }
        }
        """;

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public void ReadsTheLatestOfp()
    {
        var (result, error) = SimbriefClient.Parse(Ofp);
        Assert.Null(error);
        var p = result!.Plan;
        Assert.Equal("AFL1234", result.Callsign);
        Assert.Equal(FlightRules.Ifr, p.Rules);
        Assert.Equal("B738", p.AircraftType);
        Assert.Equal(452, p.TrueAirspeed);
        Assert.Equal(("UUEE", "ULLI", "ULLP"), (p.Departure, p.Destination, p.Alternate));
        Assert.Equal("1200", p.DepartureTime);
        Assert.Equal("FL350", p.CruiseAltitude);
        Assert.Equal(TimeSpan.FromMinutes(83), p.TimeEnroute);
        Assert.Equal(TimeSpan.FromMinutes(165), p.FuelOnBoard);
        Assert.Equal("GUBAR R22 ARTOM B141 LAPEK", p.Route);
    }

    [Fact]
    public void AcceptsClockTimes_OneAlternate_LowLevels_AndNoAtcCallsign()
    {
        const string ofp = """
            {"fetch":{"status":"Success"},"general":{"icao_airline":"SBI","flight_number":"77","initial_altitude":"8000","cruise_tas":"280","route":"DCT"},
             "origin":{"icao_code":"uuee"},"destination":{"icao_code":"UUWW"},"alternate":{"icao_code":"UUDD"},
             "aircraft":{"icaocode":"at76"},"times":{"est_time_enroute":"01:23:00","endurance":"02:45","sched_out":"2026-09-24T08:05:00Z"}}
            """;
        var (result, error) = SimbriefClient.Parse(ofp);
        Assert.Null(error);
        var p = result!.Plan;
        Assert.Equal("SBI77", result.Callsign);
        Assert.Equal("AT76", p.AircraftType);
        Assert.Equal(("UUEE", "UUDD"), (p.Departure, p.Alternate));
        Assert.Equal("8000", p.CruiseAltitude);
        Assert.Equal("0805", p.DepartureTime);
        Assert.Equal(TimeSpan.FromMinutes(83), p.TimeEnroute);
        Assert.Equal(TimeSpan.FromMinutes(165), p.FuelOnBoard);
    }

    [Fact]
    public void ExplainsWhatWentWrong()
    {
        Assert.Contains("no such SimBrief user", SimbriefClient.Parse("""{"fetch":{"status":"Error: Unknown UserID"}}""").Error);
        Assert.Contains("cannot read", SimbriefClient.Parse("<html>Service unavailable</html>").Error);
        Assert.Contains("no departure or destination", SimbriefClient.Parse("""{"fetch":{"status":"Success"},"origin":{"icao_code":"UUEE"}}""").Error);
    }

    [Fact]
    public async Task AsksByUsernameOrPilotId()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Ofp);
        var client = new SimbriefClient(new HttpClient(handler));
        var (result, error) = await client.FetchAsync(" pilot one ");
        Assert.Null(error);
        Assert.Equal("ULLI", result!.Plan.Destination);
        Assert.Equal("https://www.simbrief.com/api/xml.fetcher.php?username=pilot%20one&json=v2", handler.LastUri!.AbsoluteUri);
        await client.FetchAsync("123456");
        Assert.Equal("https://www.simbrief.com/api/xml.fetcher.php?userid=123456&json=v2", handler.LastUri!.AbsoluteUri);
        Assert.Contains("Settings", (await client.FetchAsync("  ")).Error);
    }
}
