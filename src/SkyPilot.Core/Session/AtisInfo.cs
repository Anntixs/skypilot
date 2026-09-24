using System.Text.RegularExpressions;

namespace SkyPilot.Core.Session;

/// <summary>
/// ATIS (or controller information) text received from a station in reply to an ATIS request.
/// </summary>
/// <param name="Letter">ATIS letter found in the text ("INFORMATION B"), or null.</param>
public sealed partial record AtisInfo(string Station, IReadOnlyList<string> Lines, char? Letter, DateTime ReceivedAt)
{
    public string Text => string.Join('\n', Lines);

    private static readonly string[] Phonetic =
    [
        "ALFA", "ALPHA", "BRAVO", "CHARLIE", "DELTA", "ECHO", "FOXTROT", "GOLF", "HOTEL", "INDIA", "JULIETT", "JULIET",
        "KILO", "LIMA", "MIKE", "NOVEMBER", "OSCAR", "PAPA", "QUEBEC", "ROMEO", "SIERRA", "TANGO", "UNIFORM",
        "VICTOR", "WHISKEY", "WHISKY", "XRAY", "X-RAY", "YANKEE", "ZULU",
    ];

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:INFORMATION|INFO|ИНФОРМАЦИ[ЯЮИ]|ATIS|АТИС)(?=\s+([\p{L}-]+)(?![\p{L}\p{N}]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LetterRegex();

    /// <summary>
    /// Find the ATIS letter: "INFORMATION B", "ИНФОРМАЦИЯ B", "INFORMATION BRAVO", "ATIS K".
    /// Cyrillic letters that look like Latin ones (В, К, …) are accepted too.
    /// </summary>
    public static char? ExtractLetter(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            foreach (Match m in LetterRegex().Matches(line))
            {
                var word = m.Groups[1].Value.ToUpperInvariant();
                if (word.Length == 1 && ToLatin(word[0]) is { } c) return c;
                if (Array.IndexOf(Phonetic, word) >= 0) return word[0];
            }
        }
        return null;
    }

    private static char? ToLatin(char c) => c switch
    {
        >= 'A' and <= 'Z' => c,
        'А' => 'A', 'В' => 'B', 'С' => 'C', 'Е' => 'E', 'Н' => 'H', 'К' => 'K',
        'М' => 'M', 'О' => 'O', 'Р' => 'P', 'Т' => 'T', 'Х' => 'X', 'У' => 'Y',
        _ => null,
    };
}
