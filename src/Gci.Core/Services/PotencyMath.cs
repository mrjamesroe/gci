using System.Globalization;
using System.Text.RegularExpressions;

namespace Gci.Core.Services;

/// <summary>
/// Turns a product's potency label into a comparable milligrams-of-THC figure, so patients can compare products by the
/// cost of their dose. Georgia menus use two formats: a total like "THC 400 mg" (used as-is), and a percentage like
/// "THC 26%" (applied to the package size). Products that don't report THC — most vapes and some flower — return null
/// and are simply left out of value comparisons. CBD is ignored; this is the THC-dose value metric.
/// </summary>
public static partial class PotencyMath
{
    /// <summary>Total milligrams of THC in the package, or null when it can't be determined.</summary>
    public static double? ThcMilligrams(string? potency, string? size)
    {
        if (string.IsNullOrWhiteSpace(potency)) return null;
        var m = ThcPattern().Match(potency);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        if (m.Groups[2].Value.Equals("mg", StringComparison.OrdinalIgnoreCase)) return value; // already a total in mg
        // Percentage: THC is `value`% of the package weight/volume.
        return SizeMilligrams(size) is { } sizeMg ? sizeMg * value / 100.0 : null;
    }

    /// <summary>Price per milligram of THC (rounded to a tenth of a cent), or null when THC content is unknown/zero.</summary>
    public static decimal? CostPerThcMg(decimal? price, string? potency, string? size) =>
        price is { } p && p > 0 && ThcMilligrams(potency, size) is { } mg && mg > 0
            ? Math.Round(p / (decimal)mg, 3)
            : null;

    /// <summary>A size like "45g", "30ml", "300mg" or "3.5g" in milligrams (ml treated as 1 g/ml), or null.</summary>
    internal static double? SizeMilligrams(string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) return null;
        var m = SizePattern().Match(size);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "mg" => value,
            "g" or "ml" => value * 1000.0,
            _ => null,
        };
    }

    // "mg" is listed before "%" so a "THC 400 mg" total is read as milligrams, not a bare number.
    [GeneratedRegex(@"THC\s*([\d.]+)\s*(mg|%)", RegexOptions.IgnoreCase)]
    private static partial Regex ThcPattern();

    [GeneratedRegex(@"([\d.]+)\s*(mg|ml|g)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();
}
