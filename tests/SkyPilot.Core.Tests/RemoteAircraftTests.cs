using SkyPilot.Core.Model;

namespace SkyPilot.Core.Tests;

public class RemoteAircraftTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Extrapolates_AlongLastVelocity()
    {
        var a = new RemoteAircraft("AFL1");
        a.OnPositionReport(new AircraftState(55.0, 37.0, 3000, 0, 0, 90, 250, false), T0);
        a.OnPositionReport(new AircraftState(55.0, 37.1, 3500, 0, 0, 90, 250, false), T0.AddSeconds(5));

        var p = a.Predict(T0.AddSeconds(7.5));
        Assert.Equal(37.15, p.Longitude, 6);
        Assert.Equal(3750, p.AltitudeFeet, 3);
    }

    [Fact]
    public void Extrapolation_IsCapped()
    {
        var a = new RemoteAircraft("AFL1");
        a.OnPositionReport(new AircraftState(55.0, 37.0, 3000, 0, 0, 90, 250, false), T0);
        a.OnPositionReport(new AircraftState(55.0, 37.1, 3000, 0, 0, 90, 250, false), T0.AddSeconds(5));
        var far = a.Predict(T0.AddSeconds(500));
        Assert.True(far.Longitude < 37.3);
    }

    [Fact]
    public void ParkedAircraft_DoesNotMove()
    {
        var a = new RemoteAircraft("AFL1");
        a.OnPositionReport(new AircraftState(55.0, 37.0, 600, 0, 0, 90, 0, true), T0);
        a.OnPositionReport(new AircraftState(55.0, 37.0001, 600, 0, 0, 90, 0, true), T0.AddSeconds(5));
        Assert.Equal(37.0001, a.Predict(T0.AddSeconds(10)).Longitude, 7);
    }

    [Fact]
    public void Render_BlendsWithoutJumps_AndWrapsHeading()
    {
        var a = new RemoteAircraft("AFL1");
        a.OnPositionReport(new AircraftState(55.0, 37.0, 3000, 0, 0, 350, 250, false), T0);
        var first = a.Render(T0);
        Assert.Equal(350, first.HeadingDegrees, 3);

        a.OnPositionReport(new AircraftState(55.0, 37.0, 3000, 0, 0, 10, 250, false), T0.AddSeconds(5));
        var step = a.Render(T0.AddSeconds(5.1));
        // Turned through north (350 -> 10), never the long way round through 180.
        Assert.True(step.HeadingDegrees >= 350 || step.HeadingDegrees <= 11, $"heading {step.HeadingDegrees}");
    }
}
