using System.Globalization;
using Gci.Core.Models;

namespace Gci.Core.Services;

/// <summary>What the Preorder window opens: one product at one store.</summary>
public sealed record PreorderRequest
{
    public required string StoreKey { get; init; }
    public required string StoreName { get; init; }
    public required string Operator { get; init; }
    public required ProviderKind Provider { get; init; }
    public required string ProductName { get; init; }
    public required string ProductUrl { get; init; }
    /// <summary>Where the request came from, for usage stats: inventory, changes or toast.</summary>
    public string Source { get; init; } = "inventory";
}

/// <summary>
/// The patient details GCI fills into a store's checkout form, already formatted the ways forms expect.
/// Only fields the patient saved are present.
/// </summary>
public sealed record PrefillData
{
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? FullName { get; init; }
    /// <summary>yyyy-MM-dd, for date inputs.</summary>
    public string? BirthDateIso { get; init; }
    /// <summary>MM/dd/yyyy, for text inputs.</summary>
    public string? BirthDateUs { get; init; }
    public string? BirthYear { get; init; }
    public string? BirthMonth { get; init; }
    public string? BirthDay { get; init; }
    public string? Phone { get; init; }
    public string? PhoneDigits { get; init; }
    public string? Email { get; init; }
    public string? CardNumber { get; init; }
    public string? CardExpiresIso { get; init; }
    public string? CardExpiresUs { get; init; }

    public int FieldCount =>
        new[] { FirstName, LastName, BirthDateIso, Phone, Email, CardNumber, CardExpiresIso }.Count(v => v is not null);

    public static PrefillData From(PatientProfile p)
    {
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var first = Clean(p.FirstName);
        var last = Clean(p.LastName);
        var digits = p.Phone is null ? null : new string(p.Phone.Where(char.IsDigit).ToArray());
        if (digits is { Length: 11 } && digits[0] == '1') digits = digits[1..];
        var inv = CultureInfo.InvariantCulture;
        return new PrefillData
        {
            FirstName = first,
            LastName = last,
            FullName = first is null && last is null ? null : $"{first} {last}".Trim(),
            BirthDateIso = p.BirthDate?.ToString("yyyy-MM-dd", inv),
            BirthDateUs = p.BirthDate?.ToString("MM/dd/yyyy", inv),
            BirthYear = p.BirthDate?.ToString("yyyy", inv),
            BirthMonth = p.BirthDate?.Month.ToString(inv),
            BirthDay = p.BirthDate?.Day.ToString(inv),
            Phone = digits is { Length: 10 } ? $"({digits[..3]}) {digits[3..6]}-{digits[6..]}" : Clean(p.Phone),
            PhoneDigits = string.IsNullOrEmpty(digits) ? null : digits,
            Email = Clean(p.Email),
            CardNumber = Clean(p.RegistryCardNumber),
            CardExpiresIso = p.CardExpires?.ToString("yyyy-MM-dd", inv),
            CardExpiresUs = p.CardExpires?.ToString("MM/dd/yyyy", inv),
        };
    }
}

/// <summary>Which pages GCI may fill patient details into: the store sites it reads menus from, over HTTPS.</summary>
public static class PreorderSites
{
    private static readonly string[] KnownSites =
        ["trulieve.com", "botanicalsciences.com", "iheartjane.com", "dutchie.com", "treevanaremedy.com"];

    /// <summary>True for HTTPS pages on a known store site or on the same site as the product being ordered.</summary>
    public static bool MayFill(Uri page, Uri product)
    {
        if (page.Scheme != Uri.UriSchemeHttps) return false;
        var site = Site(page.Host);
        return KnownSites.Contains(site, StringComparer.OrdinalIgnoreCase)
               || (product.Scheme == Uri.UriSchemeHttps && site.Equals(Site(product.Host), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The registrable part of a host name (last two labels), e.g. shop.botanicalsciences.com → botanicalsciences.com.</summary>
    internal static string Site(string host)
    {
        var labels = host.TrimEnd('.').Split('.');
        return labels.Length <= 2 ? host.TrimEnd('.') : string.Join('.', labels[^2..]);
    }
}
