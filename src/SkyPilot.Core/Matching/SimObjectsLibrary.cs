using System.Text.RegularExpressions;

namespace SkyPilot.Core.Matching;

/// <summary>
/// The aircraft installed in a SimConnect simulator that keeps them in SimObjects folders (Prepar3D): every
/// livery of every aircraft.cfg / sim.cfg with its ICAO type and airline, as matching rules.
/// </summary>
public sealed partial class SimObjectsLibrary
{
    private SimObjectsLibrary(List<MatchingRule> rules) => Rules = rules;

    /// <summary>One rule per livery: type from icao_type_designator or atc_model, airline from the parking codes.</summary>
    public IReadOnlyList<MatchingRule> Rules { get; }
    public bool IsEmpty => Rules.Count == 0;

    public static SimObjectsLibrary Empty { get; } = new([]);

    /// <summary>Reads the aircraft under the given SimObjects / Airplanes folders (or any folder holding them).</summary>
    public static SimObjectsLibrary Load(IEnumerable<string> folders)
    {
        var rules = new List<MatchingRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var cfg in FindConfigs(folder, 6))
            {
                string text;
                try { text = File.ReadAllText(cfg); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var rule in Parse(text))
                    if (seen.Add(rule.Title)) rules.Add(rule);
            }
        return new SimObjectsLibrary(rules);
    }

    private static IEnumerable<string> FindConfigs(string root, int depth)
    {
        var stack = new Stack<(string Dir, int Level)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, level) = stack.Pop();
            string[] files = [], dirs = [];
            try
            {
                files = Directory.GetFiles(dir, "*.cfg");
                if (level < depth) dirs = Directory.GetDirectories(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (name.Equals("aircraft.cfg", StringComparison.OrdinalIgnoreCase) || name.Equals("sim.cfg", StringComparison.OrdinalIgnoreCase))
                    yield return f;
            }
            foreach (var d in dirs)
            {
                var n = Path.GetFileName(d);
                if (n.StartsWith("texture", StringComparison.OrdinalIgnoreCase) || n.StartsWith("model", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("panel", StringComparison.OrdinalIgnoreCase) || n.Equals("sound", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("Scenery", StringComparison.OrdinalIgnoreCase) || n.Equals("Effects", StringComparison.OrdinalIgnoreCase))
                    continue;
                stack.Push((d, level + 1));
            }
        }
    }

    /// <summary>The liveries of one aircraft.cfg: [fltsim.N] title=, atc_parking_codes=; [General] icao_type_designator= / atc_model=.</summary>
    public static IEnumerable<MatchingRule> Parse(string cfg)
    {
        string section = "";
        string? type = null;
        var liveries = new List<(string Title, string? Airline)>();
        string? title = null, airline = null;

        void FlushLivery()
        {
            if (title != null) liveries.Add((title, airline));
            title = null;
            airline = null;
        }

        foreach (var raw in cfg.Split('\n'))
        {
            var line = raw.Trim();
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment].Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[')
            {
                if (section.StartsWith("fltsim", StringComparison.OrdinalIgnoreCase)) FlushLivery();
                section = line.Trim('[', ']').Trim();
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim().Trim('"').Trim();
            if (section.StartsWith("fltsim", StringComparison.OrdinalIgnoreCase))
            {
                if (key == "title") title = value;
                else if (key is "atc_parking_codes" or "icao_airline" && airline == null)
                {
                    var code = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                    if (code != null && AirlineCode().IsMatch(code)) airline = code.ToUpperInvariant();
                }
            }
            else if (section.Equals("General", StringComparison.OrdinalIgnoreCase))
            {
                if (key == "icao_type_designator" && TypeCode().IsMatch(value)) type = value.ToUpperInvariant();
                else if (key == "atc_model" && type == null && TypeCode().IsMatch(value)) type = value.ToUpperInvariant();
            }
        }
        if (section.StartsWith("fltsim", StringComparison.OrdinalIgnoreCase)) FlushLivery();
        if (type == null) yield break;
        foreach (var (t, a) in liveries) yield return new MatchingRule(type, t, a);
    }

    /// <summary>Folders to scan for a Prepar3D installation: its SimObjects and the add-on packages it knows.</summary>
    public static IEnumerable<string> Prepar3DFolders(IEnumerable<string> installFolders)
    {
        foreach (var root in installFolders)
            yield return Path.Combine(root, "SimObjects", "Airplanes");
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        foreach (var version in new[] { "v6", "v5", "v4" })
        {
            // add-ons.cfg lists the packages installed outside the simulator folder.
            foreach (var root in roots)
            {
                var cfg = Path.Combine(root, "Lockheed Martin", $"Prepar3D {version}", "add-ons.cfg");
                if (!File.Exists(cfg)) continue;
                string[] lines;
                try { lines = File.ReadAllLines(cfg); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var l in lines)
                {
                    var t = l.Trim();
                    if (t.StartsWith("PATH=", StringComparison.OrdinalIgnoreCase)) yield return t[5..].Trim();
                }
            }
            // Packages in Documents\Prepar3D vX Add-ons are found without add-ons.cfg.
            var docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"Prepar3D {version} Add-ons");
            if (Directory.Exists(docs)) yield return docs;
        }
    }

    [GeneratedRegex("^(?=.*[A-Z])[A-Z0-9]{2,4}$", RegexOptions.IgnoreCase)]
    private static partial Regex TypeCode();

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.IgnoreCase)]
    private static partial Regex AirlineCode();
}
