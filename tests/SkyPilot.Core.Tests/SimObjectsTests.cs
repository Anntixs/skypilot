using SkyPilot.Core.Matching;

namespace SkyPilot.Core.Tests;

public class SimObjectsTests
{
    private const string Aircraft = """
        [fltsim.0]
        title=Boeing 737-800 Aeroflot // default livery
        atc_parking_codes=AFL,SBI
        texture=AFL

        [fltsim.1]
        title=Boeing 737-800 House
        atc_parking_codes=

        [General]
        atc_type=BOEING
        atc_model=B738
        icao_type_designator=B738
        """;

    [Fact]
    public void Parse_TakesEveryLiveryWithTypeAndAirline()
    {
        var rules = SimObjectsLibrary.Parse(Aircraft).ToList();
        Assert.Equal([new MatchingRule("B738", "Boeing 737-800 Aeroflot", "AFL"), new MatchingRule("B738", "Boeing 737-800 House", null)], rules);
        // No ICAO type (atc_model is a name): nothing to match on.
        Assert.Empty(SimObjectsLibrary.Parse("[fltsim.0]\ntitle=X\n[General]\natc_model=737"));
    }

    [Fact]
    public void Library_ScansFoldersAndMatches()
    {
        var root = Path.Combine(Path.GetTempPath(), $"p3d-{Guid.NewGuid():N}");
        try
        {
            var dir = Path.Combine(root, "SimObjects", "Airplanes", "B738");
            Directory.CreateDirectory(Path.Combine(dir, "texture.AFL"));
            File.WriteAllText(Path.Combine(dir, "aircraft.cfg"), Aircraft);
            var a320 = Path.Combine(root, "SimObjects", "Airplanes", "A320");
            Directory.CreateDirectory(a320);
            File.WriteAllText(Path.Combine(a320, "sim.cfg"), "[fltsim.0]\ntitle=Airbus A320 Generic\n[General]\nicao_type_designator=A320\n");

            var lib = SimObjectsLibrary.Load([Path.Combine(root, "SimObjects", "Airplanes"), Path.Combine(root, "missing")]);
            Assert.Equal(3, lib.Rules.Count);
            var m = ModelMatcher.ForLibrary(lib);
            Assert.Equal("Airbus A320 Generic", m.Fallback);
            Assert.Equal("Boeing 737-800 Aeroflot", m.Match("B738", "AFL"));
            Assert.Equal("Boeing 737-800 House", m.Match("B738", "UTA"));
            Assert.Equal("Boeing 737-800 House", m.Match("B38M", ""));      // close substitute
            Assert.Equal("Airbus A320 Generic", m.Match("A21N", "SBI"));   // substitute A320
            Assert.Equal("Airbus A320 Generic", m.Match("C172", ""));      // nothing close: fallback
            Assert.Equal("Airbus A320 Neo Asobo", ModelMatcher.ForLibrary(SimObjectsLibrary.Empty).Fallback);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
