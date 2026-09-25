using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyPilot.Core.Matching;

/// <summary>Maps an ICAO type code (and optionally an airline) to a simulator model title.</summary>
public sealed record MatchingRule(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("airline")] string? Airline = null);

public sealed class ModelMatcher
{
    /// <summary>Model used when nothing else matches. Ships with every MSFS edition.</summary>
    public const string FallbackTitle = "Airbus A320 Neo Asobo";

    /// <summary>Stock aircraft that come with MSFS 2020/2024, used for types FSLTL does not cover.</summary>
    public static readonly IReadOnlyList<MatchingRule> DefaultRules =
    [
        new("A20N", "Airbus A320 Neo Asobo"),
        new("A320", "Airbus A320 Neo Asobo"),
        new("A321", "Airbus A320 Neo Asobo"),
        new("A319", "Airbus A320 Neo Asobo"),
        new("B78X", "Boeing 787-10 Asobo"),
        new("B789", "Boeing 787-10 Asobo"),
        new("B788", "Boeing 787-10 Asobo"),
        new("B748", "Boeing 747-8i Asobo"),
        new("B744", "Boeing 747-8i Asobo"),
        new("C172", "Cessna Skyhawk G1000 Asobo"),
        new("C152", "Cessna 152 Asobo"),
        new("C208", "Cessna 208B Grand Caravan EX"),
        new("C25C", "Cessna CJ4 Citation Asobo"),
        new("TBM9", "TBM 930 Asobo"),
        new("DA62", "DA62 Asobo"),
    ];

    /// <summary>
    /// Close substitutes tried when a type has no model of its own: a neo for a ceo,
    /// a MAX for an NG and so on. Order matters: the most similar airframe first.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Substitutes =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["A19N"] = ["A319", "A20N", "A320"],
            ["A319"] = ["A19N", "A320", "A20N"],
            ["A318"] = ["A319", "A320", "A20N"],
            ["A20N"] = ["A320", "A21N", "A321"],
            ["A320"] = ["A20N", "A321", "A21N"],
            ["A21N"] = ["A321", "A20N", "A320"],
            ["A321"] = ["A21N", "A320", "A20N"],
            ["A332"] = ["A333", "A339", "A359"],
            ["A333"] = ["A332", "A339", "A359"],
            ["A338"] = ["A339", "A332", "A333"],
            ["A339"] = ["A333", "A332", "A359"],
            ["A359"] = ["A35K", "A339", "A333"],
            ["A35K"] = ["A359", "A339"],
            ["A388"] = ["B748", "B744"],
            ["BCS1"] = ["BCS3", "A20N"],
            ["BCS3"] = ["BCS1", "A20N"],
            ["B736"] = ["B737", "B738"],
            ["B737"] = ["B738", "B38M"],
            ["B738"] = ["B38M", "B739", "B737"],
            ["B739"] = ["B39M", "B738", "B38M"],
            ["B37M"] = ["B737", "B38M", "B738"],
            ["B38M"] = ["B738", "B39M", "B739"],
            ["B39M"] = ["B739", "B38M", "B738"],
            ["B3XM"] = ["B39M", "B739", "B38M"],
            ["B752"] = ["B753", "B739"],
            ["B753"] = ["B752", "B739"],
            ["B762"] = ["B763", "B764"],
            ["B763"] = ["B764", "B762", "B788"],
            ["B764"] = ["B763", "B762"],
            ["B772"] = ["B77L", "B77W", "B773"],
            ["B77L"] = ["B772", "B77W"],
            ["B773"] = ["B77W", "B772"],
            ["B77W"] = ["B773", "B772", "B77L"],
            ["B778"] = ["B779", "B77W"],
            ["B779"] = ["B778", "B77W"],
            ["B744"] = ["B748", "B742"],
            ["B748"] = ["B744"],
            ["B788"] = ["B789", "B78X"],
            ["B789"] = ["B788", "B78X"],
            ["B78X"] = ["B789", "B788"],
            ["E170"] = ["E175", "E190"],
            ["E175"] = ["E170", "E190"],
            ["E190"] = ["E195", "E290", "E175"],
            ["E195"] = ["E190", "E295"],
            ["E290"] = ["E190", "E295"],
            ["E295"] = ["E195", "E290"],
            ["CRJ7"] = ["CRJ9", "CRJ2"],
            ["CRJ9"] = ["CRJ7", "CRJX"],
            ["CRJX"] = ["CRJ9", "CRJ7"],
            ["CRJ2"] = ["CRJ7"],
            ["AT72"] = ["AT76", "AT75"],
            ["AT75"] = ["AT76", "AT72"],
            ["AT76"] = ["AT75", "AT72"],
            ["DH8D"] = ["AT76"],
            ["SU95"] = ["E190", "A20N"],
            ["MD82"] = ["MD83", "MD88"],
            ["MD83"] = ["MD82", "MD88"],
            ["MD88"] = ["MD83", "MD82"],
        };

    private readonly List<MatchingRule> _rules;

    /// <param name="rules">User rules (checked first). Defaults to <see cref="DefaultRules"/> when null.</param>
    /// <param name="fsltl">Installed FSLTL traffic models, preferred over the stock aircraft.</param>
    public ModelMatcher(IEnumerable<MatchingRule>? rules = null, FsltlLibrary? fsltl = null)
    {
        Fsltl = fsltl ?? FsltlLibrary.Empty;
        _rules = (rules ?? DefaultRules).ToList();
        if (Fsltl.IsInstalled)
        {
            // User rules, then FSLTL, then the stock aircraft for types FSLTL doesn't have.
            var user = _rules.Except(DefaultRules).ToList();
            _rules = [.. user, .. Fsltl.Rules, .. DefaultRules];
            Fallback = Fsltl.AnyTitleFor("A320") ?? Fsltl.AnyTitleFor("A20N") ?? Fsltl.AnyTitleFor("B738") ?? FallbackTitle;
        }
    }

    private ModelMatcher(List<MatchingRule> rules, string fallback)
    {
        Fsltl = FsltlLibrary.Empty;
        _rules = rules;
        Fallback = fallback;
    }

    /// <summary>
    /// For a simulator with its own installed aircraft (Prepar3D): the user's rules, then the scanned liveries. When
    /// nothing is installed for a type, an A320 / 737 of the library stands in (and the simulator itself falls back
    /// to the user's own aircraft).
    /// </summary>
    public static ModelMatcher ForLibrary(SimObjectsLibrary library, IEnumerable<MatchingRule>? userRules = null)
    {
        var rules = (userRules ?? []).Concat(library.Rules).ToList();
        string fallback = new[] { "A320", "A20N", "B738", "A321", "B737" }
            .Select(t => library.Rules.FirstOrDefault(r => Eq(r.Type, t))?.Title)
            .FirstOrDefault(t => t != null) ?? library.Rules.FirstOrDefault()?.Title ?? FallbackTitle;
        return new ModelMatcher(rules, fallback);
    }

    /// <summary>The user's own rules from model-matching.json (empty when the file is missing or broken).</summary>
    public static List<MatchingRule> LoadUserRules(string path)
    {
        try
        {
            if (File.Exists(path))
                return (JsonSerializer.Deserialize<List<MatchingRule>>(File.ReadAllText(path)) ?? [])
                    .Where(r => !string.IsNullOrWhiteSpace(r.Type) && !string.IsNullOrWhiteSpace(r.Title)).ToList();
        }
        catch (JsonException)
        {
        }
        return [];
    }

    public IReadOnlyList<MatchingRule> Rules => _rules;

    public FsltlLibrary Fsltl { get; }

    /// <summary>Model shown when nothing matches: an FSLTL A320 if installed, else <see cref="FallbackTitle"/>.</summary>
    public string Fallback { get; } = FallbackTitle;

    /// <summary>Load user rules from <paramref name="path"/> (optional) and the FSLTL models found in the Community folders.</summary>
    public static ModelMatcher Load(string path, FsltlLibrary? fsltl = null)
    {
        List<MatchingRule> custom = [];
        try
        {
            if (File.Exists(path))
                custom = JsonSerializer.Deserialize<List<MatchingRule>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException)
        {
            // A broken file must not stop traffic from being shown.
        }
        // User rules take priority, defaults remain as a fallback.
        return new ModelMatcher(custom.Where(r => !string.IsNullOrWhiteSpace(r.Type) && !string.IsNullOrWhiteSpace(r.Title)).Concat(DefaultRules), fsltl);
    }

    /// <summary>
    /// Pick the best model. For the type itself and then its close substitutes: a livery of the
    /// same airline first, then a generic one. After that an aircraft of the same family
    /// (first 3 letters of the type code), then <see cref="Fallback"/>.
    /// </summary>
    public string Match(string equipment, string airline)
    {
        string type = NormalizeType(equipment);
        if (type.Length == 0) return Fallback;
        airline = airline.Trim();

        string[] types = [type, .. Substitutes.GetValueOrDefault(type, [])];
        if (airline.Length > 0)
        {
            // The exact type in the right livery, then a close substitute in the right livery.
            foreach (var t in types)
            {
                var hit = _rules.FirstOrDefault(r => Eq(r.Type, t) && r.Airline != null && Eq(r.Airline, airline));
                if (hit != null) return hit.Title;
            }
        }
        foreach (var t in types)
        {
            var hit = _rules.FirstOrDefault(r => Eq(r.Type, t) && r.Airline == null)
                      ?? (Fsltl.IsInstalled ? _rules.FirstOrDefault(r => Eq(r.Type, t) && Fsltl.Titles.Contains(r.Title)) : null);
            if (hit != null) return hit.Title;
        }
        return _rules.FirstOrDefault(r => type.Length >= 3 && r.Airline == null && r.Type.StartsWith(type[..3], StringComparison.OrdinalIgnoreCase))?.Title
               ?? Fallback;
    }

    /// <summary>"H/B744/L" or "B738/M-SDE2E3FGHIRWY/LB1" -> "B744" / "B738".</summary>
    public static string NormalizeType(string equipment)
    {
        var parts = equipment.Trim().ToUpperInvariant().Split('/');
        string type = parts.Length >= 2 && parts[0].Length == 1 ? parts[1] : parts[0];
        int dash = type.IndexOf('-');
        return dash >= 0 ? type[..dash] : type;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
