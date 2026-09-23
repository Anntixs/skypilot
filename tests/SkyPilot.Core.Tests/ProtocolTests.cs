using SkyPilot.Core.Fsd;
using SkyPilot.Core.Matching;
using SkyPilot.Core.Model;
using SkyPilot.Core.Session;

namespace SkyPilot.Core.Tests;

public class ProtocolTests
{
    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(5, -10, 270, true)]
    [InlineData(-3.5, 25, 359, false)]
    [InlineData(12, 45, 90.5, true)]
    public void Pbh_RoundTrips(double pitch, double bank, double heading, bool onGround)
    {
        var d = Pbh.Decode(Pbh.Encode(pitch, bank, heading, onGround));
        Assert.Equal(pitch, d.Pitch, 0.5);
        Assert.Equal(bank, d.Bank, 0.5);
        Assert.Equal(heading, d.Heading, 0.5);
        Assert.Equal(onGround, d.OnGround);
    }

    [Fact]
    public void Position_RoundTrips()
    {
        var state = new AircraftState(55.972778, 37.414722, 3500, 2.5, -15, 250, 180, false);
        string line = Packets.Position("AFL123", TransponderMode.ModeC, 4521, state, 3480);
        Assert.StartsWith("@N:AFL123:4521:1:55.972778:37.414722:3500:180:", line);
        Assert.EndsWith(":-20", line);

        var pos = Packets.ParsePosition(FsdPacket.Parse(line)!)!;
        Assert.Equal("AFL123", pos.Callsign);
        Assert.Equal(TransponderMode.ModeC, pos.Mode);
        Assert.Equal(4521, pos.Squawk);
        Assert.Equal(state.Latitude, pos.State.Latitude, 5);
        Assert.Equal(state.HeadingDegrees, pos.State.HeadingDegrees, 0.5);
        Assert.Equal(state.BankDegrees, pos.State.BankDegrees, 0.5);
    }

    [Fact]
    public void Position_UsesInvariantCulture()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
        try
        {
            string line = Packets.Position("AFL1", TransponderMode.Standby, 2000, new AircraftState(1.5, 2.5, 0, 0, 0, 0, 0, true), 0);
            Assert.Contains(":1.500000:2.500000:", line);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = old;
        }
    }

    [Fact]
    public void FlightPlan_HasAllFields()
    {
        var fp = new FlightPlan
        {
            AircraftType = "a20n", TrueAirspeed = 450, Departure = "uuee", Destination = "ulli", Alternate = "ullo",
            DepartureTime = "1200", CruiseAltitude = "FL350", TimeEnroute = new TimeSpan(1, 10, 0),
            FuelOnBoard = new TimeSpan(3, 0, 0), Route = "dct", Remarks = "/V/ test: colon",
        };
        var p = FsdPacket.Parse(Packets.FlightPlan("AFL123", fp))!;
        Assert.Equal("$FP", p.Command);
        Assert.Equal(17, p.Fields.Length);
        Assert.Equal(["AFL123", "*A", "I", "A20N", "450", "UUEE", "1200", "0", "FL350", "ULLI", "1", "10", "3", "0", "ULLO", "/V/ test  colon", "DCT"], p.Fields);
    }

    [Fact]
    public void PlaneInfo_ParsesEquipmentAndAirline()
    {
        var p = FsdPacket.Parse(Packets.PlaneInfoResponse("AFL1", "SBI2", "B738", "AFL"))!;
        Assert.Equal(("B738", "AFL"), Packets.ParsePlaneInfo(p));
    }

    [Theory]
    [InlineData("AFL123", "AFL")]
    [InlineData("RA12345", "")]
    [InlineData("N172SP", "")]
    [InlineData("SBI", "")]
    public void AirlineFromCallsign(string callsign, string airline) =>
        Assert.Equal(airline, Packets.AirlineFromCallsign(callsign));

    [Fact]
    public void Frequency_Conversions()
    {
        Assert.True(Frequency.TryParse("118.1", out var khz));
        Assert.Equal(118100, khz);
        Assert.True(Frequency.TryParse("121,500", out khz));
        Assert.Equal(121500, khz);
        Assert.False(Frequency.TryParse("108.0", out _));
        Assert.Equal("@18100", Frequency.ToFsdAddress(118100));
        Assert.True(Frequency.TryParseFsdAddress("@24850", out khz));
        Assert.Equal(124850, khz);
        Assert.Equal("118.100", Frequency.Format(118100));
        Assert.True(Frequency.SameChannel(118005, 118000));
        Assert.False(Frequency.SameChannel(118025, 118000));
    }

    [Theory]
    [InlineData("A20N", "", "Airbus A320 Neo Asobo")]
    [InlineData("H/B748/L", "", "Boeing 747-8i Asobo")]
    [InlineData("C172/G", "", "Cessna Skyhawk G1000 Asobo")]
    [InlineData("B789", "", "Boeing 787-10 Asobo")]
    [InlineData("ZZZZ", "", ModelMatcher.FallbackTitle)]
    [InlineData("", "", ModelMatcher.FallbackTitle)]
    public void ModelMatcher_Defaults(string equipment, string airline, string title) =>
        Assert.Equal(title, new ModelMatcher().Match(equipment, airline));

    [Fact]
    public void ModelMatcher_PrefersAirlineRule()
    {
        var m = new ModelMatcher([new("A20N", "A320 Aeroflot", "AFL"), new("A20N", "A320 Generic")]);
        Assert.Equal("A320 Aeroflot", m.Match("A20N", "AFL"));
        Assert.Equal("A320 Generic", m.Match("A20N", "SBI"));
    }

    [Theory]
    [InlineData("7000", true)]
    [InlineData("2000", true)]
    [InlineData("7800", false)]
    [InlineData("123", false)]
    public void Squawk_Validation(string text, bool valid) =>
        Assert.Equal(valid, CommandProcessor.TryParseSquawk(text, out _));

    [Theory]
    [InlineData("AFL123", true)]
    [InlineData("UUEE_TWR", true)]
    [InlineData("A", false)]
    [InlineData("AFL 123", false)]
    [InlineData("afl123", false)]
    public void Callsign_Validation(string cs, bool valid) => Assert.Equal(valid, NetworkSession.IsValidCallsign(cs));
}
