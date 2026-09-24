using System.Xml.Linq;

namespace SkyPilot.Core.Matching;

/// <summary>
/// Reads the installed FSLTL traffic packages (fsltl-traffic-base and friends) from the MSFS
/// Community folder and turns them into matching rules. Only liveries that are actually
/// installed become rules, so every title handed to the simulator exists.
/// </summary>
public sealed class FsltlLibrary
{
    public const string GenericAirline = "ZZZZ";

    public IReadOnlyList<MatchingRule> Rules { get; }
    public IReadOnlySet<string> Titles { get; }
    public string? PackagePath { get; }

    private FsltlLibrary(List<MatchingRule> rules, HashSet<string> titles, string? packagePath)
    {
        Rules = rules;
        Titles = titles;
        PackagePath = packagePath;
    }

    public static FsltlLibrary Empty { get; } = new([], new(StringComparer.OrdinalIgnoreCase), null);

    public bool IsInstalled => Titles.Count > 0;

    /// <summary>Scan every Community folder given and merge what is found.</summary>
    public static FsltlLibrary Load(IEnumerable<string> communityFolders)
    {
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var liveries = new List<Livery>();
        var vmr = new List<MatchingRule>();
        string? first = null;

        foreach (var community in communityFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> packages;
            try { packages = Directory.EnumerateDirectories(community, "fsltl*").ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var package in packages)
            {
                var airplanes = Path.Combine(package, "SimObjects", "Airplanes");
                if (!Directory.Exists(airplanes)) continue;
                first ??= package;
                foreach (var cfg in SafeFiles(airplanes, "aircraft.cfg", SearchOption.AllDirectories))
                {
                    foreach (var l in ReadAircraftCfg(cfg))
                        if (titles.Add(l.Title)) liveries.Add(l);
                }
                foreach (var file in SafeFiles(package, "*.vmr", SearchOption.TopDirectoryOnly))
                    vmr.AddRange(ReadVmr(file));
            }
        }

        // Rules shipped with the package first (they know which livery suits which operator),
        // then everything derived from the installed liveries.
        var rules = vmr.Where(r => titles.Contains(r.Title)).ToList();
        rules.AddRange(liveries.Where(l => l.Airline != null).Select(l => new MatchingRule(l.Type, l.Title, l.Airline)));
        rules.AddRange(liveries.Where(l => l.Airline == null).Select(l => new MatchingRule(l.Type, l.Title)));
        return new FsltlLibrary(rules, titles, first);
    }

    public static FsltlLibrary Load(string communityFolder) => Load([communityFolder]);

    /// <summary>Generic (unbranded) model of a type, or any livery of it when there is none.</summary>
    public string? AnyTitleFor(string type) =>
        Rules.FirstOrDefault(r => r.Airline == null && Eq(r.Type, type))?.Title
        ?? Rules.FirstOrDefault(r => Eq(r.Type, type))?.Title;

    // ---- aircraft.cfg ---------------------------------------------------------------

    internal sealed record Livery(string Type, string Title, string? Airline);

    internal static IEnumerable<Livery> ReadAircraftCfg(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        return ParseAircraftCfg(lines, Path.GetFileName(Path.GetDirectoryName(path)) ?? "");
    }

    internal static List<Livery> ParseAircraftCfg(IEnumerable<string> lines, string folderName)
    {
        string? generalType = null;
        var sections = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        bool inGeneral = false;

        foreach (var raw in lines)
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                var name = line.Trim('[', ']').Trim();
                inGeneral = name.Equals("GENERAL", StringComparison.OrdinalIgnoreCase);
                current = name.StartsWith("FLTSIM.", StringComparison.OrdinalIgnoreCase) ? new(StringComparer.OrdinalIgnoreCase) : null;
                if (current != null) sections.Add(current);
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"').Trim();
            if (current != null) current[key] = value;
            else if (inGeneral && key.Equals("icao_type_designator", StringComparison.OrdinalIgnoreCase)) generalType = value;
        }

        var result = new List<Livery>();
        foreach (var s in sections)
        {
            if (!s.TryGetValue("title", out var title) || title.Length == 0) continue;
            var (titleType, titleAirline) = SplitFsltlName(title);
            var type = FirstNonEmpty(s.GetValueOrDefault("icao_type_designator"), generalType, titleType, SplitFsltlName(folderName).Type);
            if (type == null) continue;
            var airline = FirstNonEmpty(s.GetValueOrDefault("icao_airline"), titleAirline)?.ToUpperInvariant();
            if (airline is null or GenericAirline || airline.Length is < 2 or > 3) airline = null;
            result.Add(new Livery(type.ToUpperInvariant(), title, airline));
        }
        return result;
    }

    /// <summary>"FSLTL_A20N_AFL-Aeroflot" -> ("A20N", "AFL"); "FSLTL_B738_ZZZZ" -> ("B738", "ZZZZ").</summary>
    internal static (string? Type, string? Airline) SplitFsltlName(string name)
    {
        var parts = name.Split('_', 3);
        if (parts.Length < 2 || !parts[0].Equals("FSLTL", StringComparison.OrdinalIgnoreCase)) return (null, null);
        string? type = parts[1].Length is >= 2 and <= 4 ? parts[1] : null;
        string? airline = null;
        if (parts.Length == 3)
        {
            var a = parts[2];
            int cut = a.IndexOfAny(['-', '_', ' ']);
            airline = cut >= 0 ? a[..cut] : a;
        }
        return (type, airline);
    }

    // ---- .vmr rule files ---------------------------------------------------------------

    internal static IEnumerable<MatchingRule> ReadVmr(string path)
    {
        try { return ParseVmr(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return []; }
    }

    internal static List<MatchingRule> ParseVmr(string xml)
    {
        var rules = new List<MatchingRule>();
        foreach (var el in XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "ModelMatchRule"))
        {
            var type = (string?)el.Attribute("TypeCode");
            var models = (string?)el.Attribute("ModelName");
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(models)) continue;
            var prefix = ((string?)el.Attribute("CallsignPrefix"))?.Trim().ToUpperInvariant();
            if (prefix is { Length: > 0 } && prefix.Length != 3) continue; // callsign-specific rules
            string? airline = string.IsNullOrEmpty(prefix) ? null : prefix;
            foreach (var model in models.Split("//", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                rules.Add(new MatchingRule(type.Trim().ToUpperInvariant(), model, airline));
        }
        return rules;
    }

    // ---- helpers ---------------------------------------------------------------------

    private static IEnumerable<string> SafeFiles(string dir, string pattern, SearchOption option)
    {
        try { return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { RecurseSubdirectories = option == SearchOption.AllDirectories, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive }).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string StripComment(string line)
    {
        int c = line.IndexOf(';');
        return c >= 0 ? line[..c] : line;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Finds the MSFS 2020/2024 Community folders on this machine.</summary>
public static class CommunityFolders
{
    public static IReadOnlyList<string> Find(string? overridePath = null)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(overridePath)) result.Add(overridePath.Trim());

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] userCfgs =
        [
            Path.Combine(local, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"),
            Path.Combine(roaming, "Microsoft Flight Simulator", "UserCfg.opt"),
            Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"),
            Path.Combine(roaming, "Microsoft Flight Simulator 2024", "UserCfg.opt"),
        ];
        foreach (var cfg in userCfgs)
        {
            try
            {
                if (!File.Exists(cfg)) continue;
                var packages = InstalledPackagesPath(File.ReadAllLines(cfg));
                if (packages != null) result.Add(Path.Combine(packages, "Community"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    /// <summary>Value of the <c>InstalledPackagesPath "D:\MSFS"</c> line of UserCfg.opt.</summary>
    public static string? InstalledPackagesPath(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line["InstalledPackagesPath".Length..].Trim().Trim('"');
            return value.Length > 0 ? value : null;
        }
        return null;
    }
}
