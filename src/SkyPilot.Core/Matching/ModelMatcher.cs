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

    /// <summary>Default rules for aircraft that come with MSFS 2020/2024.</summary>
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

    private readonly List<MatchingRule> _rules;

    public ModelMatcher(IEnumerable<MatchingRule>? rules = null)
    {
        _rules = (rules ?? DefaultRules).ToList();
    }

    public IReadOnlyList<MatchingRule> Rules => _rules;

    public static ModelMatcher Load(string path)
    {
        if (!File.Exists(path)) return new ModelMatcher();
        var custom = JsonSerializer.Deserialize<List<MatchingRule>>(File.ReadAllText(path)) ?? [];
        // User rules take priority, defaults remain as a fallback.
        return new ModelMatcher(custom.Concat(DefaultRules));
    }

    /// <summary>
    /// Pick the best model: type + airline, then type only, then an aircraft of the same family
    /// (first 3 letters of the type code), then <see cref="FallbackTitle"/>.
    /// </summary>
    public string Match(string equipment, string airline)
    {
        string type = NormalizeType(equipment);
        if (type.Length == 0) return FallbackTitle;
        return _rules.FirstOrDefault(r => Eq(r.Type, type) && r.Airline != null && Eq(r.Airline, airline))?.Title
               ?? _rules.FirstOrDefault(r => Eq(r.Type, type) && r.Airline == null)?.Title
               ?? _rules.FirstOrDefault(r => type.Length >= 3 && r.Type.StartsWith(type[..3], StringComparison.OrdinalIgnoreCase))?.Title
               ?? FallbackTitle;
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
