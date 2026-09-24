using SkyPilot.Core.Matching;

namespace SkyPilot.Core.Tests;

public sealed class FsltlTests : IDisposable
{
    private readonly string _community = Path.Combine(Path.GetTempPath(), "skypilot-fsltl-" + Guid.NewGuid().ToString("N"));

    public FsltlTests()
    {
        var planes = Path.Combine(_community, "fsltl-traffic-base", "SimObjects", "Airplanes");
        Write(Path.Combine(planes, "FSLTL_A20N", "aircraft.cfg"), """
            [VERSION]
            major = 1
            [GENERAL]
            icao_type_designator = "A20N"
            [FLTSIM.0]
            title = "FSLTL_A20N_ZZZZ" ; generic white
            icao_airline = "ZZZZ"
            [FLTSIM.1]
            title = "FSLTL_A20N_AFL-Aeroflot"
            icao_airline = "AFL"
            [FLTSIM.2]
            title = "FSLTL_A20N_SBI-S7"
            """);
        Write(Path.Combine(planes, "FSLTL_A320", "aircraft.cfg"), """
            [FLTSIM.0]
            title = "FSLTL_A320_ZZZZ"
            [FLTSIM.1]
            title = "FSLTL_A320_SDM-Rossiya"
            icao_airline = "SDM"
            """);
        Write(Path.Combine(planes, "FSLTL_B738", "aircraft.cfg"), """
            [FLTSIM.0]
            title = "FSLTL_B738_ZZZZ"
            [FLTSIM.1]
            title = "FSLTL_B738_UTA-UTair"
            """);
        Write(Path.Combine(_community, "fsltl-traffic-base", "FSLTL_Rules.vmr"), """
            <?xml version="1.0" encoding="utf-8"?>
            <ModelMatchRuleSet>
              <ModelMatchRule CallsignPrefix="SDM" TypeCode="A20N" ModelName="FSLTL_A320_SDM-Rossiya" />
              <ModelMatchRule CallsignPrefix="XXX" TypeCode="A20N" ModelName="FSLTL_A20N_XXX-NotInstalled" />
              <ModelMatchRule TypeCode="A19N" ModelName="FSLTL_A19N_ZZZZ//FSLTL_A20N_ZZZZ" />
            </ModelMatchRuleSet>
            """);
        // Not an FSLTL package: ignored.
        Write(Path.Combine(_community, "other-livery", "SimObjects", "Airplanes", "X", "aircraft.cfg"), "[FLTSIM.0]\ntitle = Other");
    }

    public void Dispose()
    {
        try { Directory.Delete(_community, true); } catch (IOException) { }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Library_ReadsInstalledLiveries()
    {
        var lib = FsltlLibrary.Load(_community);
        Assert.True(lib.IsInstalled);
        Assert.Equal(7, lib.Titles.Count);
        Assert.DoesNotContain("Other", lib.Titles);
        Assert.Contains(lib.Rules, r => r is { Type: "A20N", Airline: "SBI", Title: "FSLTL_A20N_SBI-S7" });
        Assert.Contains(lib.Rules, r => r is { Type: "A20N", Airline: null, Title: "FSLTL_A20N_ZZZZ" });
        // Rules that point at liveries which are not installed are dropped.
        Assert.DoesNotContain(lib.Rules, r => r.Title.Contains("NotInstalled"));
    }

    [Theory]
    [InlineData("A20N", "AFL", "FSLTL_A20N_AFL-Aeroflot")]
    [InlineData("H/A20N/L", "SBI", "FSLTL_A20N_SBI-S7")]
    [InlineData("A20N", "SDM", "FSLTL_A320_SDM-Rossiya")] // package rule
    [InlineData("A20N", "DLH", "FSLTL_A20N_ZZZZ")]         // unknown airline: generic livery
    [InlineData("A320", "AFL", "FSLTL_A20N_AFL-Aeroflot")] // no A320 in that livery: the neo is closest
    [InlineData("A19N", "", "FSLTL_A20N_ZZZZ")]            // package rule, second alternative
    [InlineData("B38M", "UTA", "FSLTL_B738_UTA-UTair")]
    [InlineData("B738", "", "FSLTL_B738_ZZZZ")]
    [InlineData("C172", "", "Cessna Skyhawk G1000 Asobo")] // not in FSLTL: stock model
    [InlineData("ZZZZ", "", "FSLTL_A320_ZZZZ")]            // unknown type: FSLTL fallback
    public void Matcher_PrefersFsltl(string equipment, string airline, string title)
    {
        var m = new ModelMatcher(fsltl: FsltlLibrary.Load(_community));
        Assert.Equal(title, m.Match(equipment, airline));
        Assert.Equal("FSLTL_A320_ZZZZ", m.Fallback);
    }

    [Fact]
    public void Matcher_UserRulesWin()
    {
        var path = Path.Combine(_community, "model-matching.json");
        File.WriteAllText(path, """[{ "type": "A20N", "airline": "AFL", "title": "My A320 AFL" }]""");
        var m = ModelMatcher.Load(path, FsltlLibrary.Load(_community));
        Assert.Equal("My A320 AFL", m.Match("A20N", "AFL"));
        Assert.Equal("FSLTL_A20N_SBI-S7", m.Match("A20N", "SBI"));
    }

    [Fact]
    public void Matcher_BrokenUserFileIsIgnored()
    {
        var path = Path.Combine(_community, "broken.json");
        File.WriteAllText(path, "{ not json");
        Assert.Equal("Airbus A320 Neo Asobo", ModelMatcher.Load(path).Match("A20N", ""));
    }

    [Fact]
    public void WithoutFsltl_StockModelsAndSubstitutes()
    {
        var m = new ModelMatcher();
        Assert.Equal(ModelMatcher.FallbackTitle, m.Fallback);
        Assert.Equal("Boeing 787-10 Asobo", m.Match("B78X", ""));
        Assert.Equal("Boeing 747-8i Asobo", m.Match("A388", ""));
    }

    [Theory]
    [InlineData("FSLTL_A20N_AFL-Aeroflot", "A20N", "AFL")]
    [InlineData("FSLTL_B738_ZZZZ", "B738", "ZZZZ")]
    [InlineData("FSLTL_A320", "A320", null)]
    [InlineData("Airbus A320 Neo Asobo", null, null)]
    public void SplitName(string name, string? type, string? airline) =>
        Assert.Equal((type, airline), FsltlLibrary.SplitFsltlName(name));

    [Fact]
    public void UserCfg_InstalledPackagesPath()
    {
        string[] lines = ["{Version", "}", "InstalledPackagesPath \"D:\\MSFS Packages\"", ""];
        Assert.Equal("D:\\MSFS Packages", CommunityFolders.InstalledPackagesPath(lines));
        Assert.Null(CommunityFolders.InstalledPackagesPath(["Other 1"]));
    }
}
