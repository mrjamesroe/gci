using System.Text.RegularExpressions;

namespace Gci.Core.Services;

/// <summary>
/// Groups each provider's strain label ("Indica", "Sativa-Hybrid", "Ratio", "1 to 1"…) into the four types patients
/// filter by. Same rules as strainOf() in the web app, so both filter the same way.
/// </summary>
public static partial class StrainType
{
    public const string Indica = "Indica", Sativa = "Sativa", Hybrid = "Hybrid", CbdRatio = "CBD / ratio";
    public static IReadOnlyList<string> All { get; } = [Indica, Sativa, Hybrid, CbdRatio];

    /// <summary>The type for a provider's strain label, or null when it doesn't say (blank, or just "THC").</summary>
    public static string? Of(string? strain)
    {
        if (string.IsNullOrWhiteSpace(strain)) return null;
        var s = strain.ToLowerInvariant();
        if (s.Contains("ratio") || s.Contains("cbd") || s.Contains("cbn") || s.Contains(':') || RatioWords().IsMatch(s)) return CbdRatio;
        if (s.Contains("hybrid")) return Hybrid;
        if (s.Contains("indica")) return Indica;
        if (s.Contains("sativa")) return Sativa;
        return null;
    }

    [GeneratedRegex(@"\d\s*to\s*\d")]
    private static partial Regex RatioWords();
}
