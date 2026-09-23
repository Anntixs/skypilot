using System.Net;
using System.Text;
using SkyPilot.Core.Model;
using SkyPilot.Core.Web;

namespace SkyPilot.Core.Tests;

public class WebsiteClientTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task ParsesFiledPlan()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            { "rules": "IFR", "aircraft": "A20N", "cruiseSpeed": 450, "departure": "UUEE", "destination": "ULLI",
              "alternate": "ULLO", "departureTime": "1200", "cruiseAltitude": "FL350", "enrouteMinutes": 70,
              "fuelMinutes": 180, "route": "DCT", "remarks": "/V/" }
            """);
        var client = new WebsiteClient(new HttpClient(handler), new Uri("https://example.test/"));
        var plan = await client.GetLatestFlightPlanAsync(1000001);

        Assert.Equal("https://example.test/api/flightplans/latest?cid=1000001", handler.LastUri!.ToString());
        Assert.NotNull(plan);
        Assert.Equal(("UUEE", "ULLI", "A20N", FlightRules.Ifr), (plan!.Departure, plan.Destination, plan.AircraftType, plan.Rules));
        Assert.Equal(new TimeSpan(1, 10, 0), plan.TimeEnroute);
        Assert.Equal(TimeSpan.FromHours(3), plan.FuelOnBoard);
    }

    [Fact]
    public async Task NoPlan_ReturnsNull()
    {
        var client = new WebsiteClient(new HttpClient(new StubHandler(HttpStatusCode.NotFound, "")), new Uri("https://example.test/"));
        Assert.Null(await client.GetLatestFlightPlanAsync(1));
    }

    [Theory]
    [InlineData("skynetwork.example", "https://skynetwork.example/")]
    [InlineData("http://127.0.0.1:8000", "http://127.0.0.1:8000/")]
    [InlineData("https://site.example/sky/", "https://site.example/sky/")]
    public void TryParseSite(string text, string expected)
    {
        Assert.True(WebsiteClient.TryParseSite(text, out var site));
        Assert.Equal(expected, site.ToString());
    }

    [Fact]
    public void FlightPlanPage_IncludesCallsign()
    {
        var client = new WebsiteClient(new HttpClient(), new Uri("https://site.example/sky/"));
        Assert.Equal("https://site.example/sky/flightplan?callsign=AFL123", client.FlightPlanPage("AFL123").ToString());
    }
}
