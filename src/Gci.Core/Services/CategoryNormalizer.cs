using Gci.Core.Models;

namespace Gci.Core.Services;

/// <summary>
/// Maps each provider's category labels and product names onto <see cref="ProductCategory"/>.
/// The provider's own category wins when it's recognizable (so a drink from a brand called
/// "Silent Flower" stays an Edible); product-name keywords are the fallback. Within each pass,
/// vape hardware is checked before concentrate words so "Live Resin All In One" lands in Vape
/// while "Riddler Crumble" lands in Concentrate.
/// </summary>
public static class CategoryNormalizer
{
    private static readonly string[] VapeWords =
        { "cart", "cartridge", "vape", "vaporizer", "all in one", "all-in-one", " aio", "disposable", " pod", "510" };

    private static readonly string[] ConcentrateWords =
        { "concentrate", "extract", "crumble", "shatter", "badder", "budder", "batter", "wax", "rosin",
          "sauce", "diamonds", "live resin", "hash", "kief", "dab", "sift", "resin" };

    private static readonly string[] FlowerWords = { "flower", "pre-roll", "preroll", "popcorn" };
    private static readonly string[] TopicalWords = { "topical", "cream", "lotion", "balm", "transdermal", "patch", "salve" };
    private static readonly string[] CapsuleWords = { "capsule", "caps", "softgel", "soft gel", "tablet" };
    private static readonly string[] SublingualWords = { "tincture", "sublingual" };
    private static readonly string[] EdibleWords =
        { "edible", "gumm", "chocolate", "soft square", " s2 ", "melts", "chew", "candy", "brownie", "beverage", "drink", "soft drop" };
    private static readonly string[] OralWords = { "lozenge", "troche", "nasal", " oral", "powder", "spray" };

    public static ProductCategory Normalize(string? rawCategory, string? name, string? subtype = null)
    {
        var raw = $" {rawCategory} {subtype} ".ToLowerInvariant();
        var text = $" {rawCategory} {subtype} {name} ".ToLowerInvariant();
        var nameOnly = $" {name} ".ToLowerInvariant();

        if (ContainsAny(raw, "accessor", "gear", "hardware", "battery", "batteries")) return ProductCategory.Accessory;
        if (ContainsAny(raw, "apparel", "merch", "clothing")) return ProductCategory.Apparel;
        if (ContainsAny(text, " rso", "syringe")) return ProductCategory.Oral;

        // Pass 1: the provider's category label.
        if (ContainsAny(raw, FlowerWords)) return ProductCategory.Flower;
        if (ContainsAny(raw, VapeWords)) return ProductCategory.Vape;
        if (ContainsAny(raw, ConcentrateWords)) return ProductCategory.Concentrate;
        if (ContainsAny(raw, TopicalWords)) return ProductCategory.Topical;
        if (ContainsAny(raw, CapsuleWords)) return ProductCategory.Capsule;
        if (ContainsAny(raw, EdibleWords)) return ProductCategory.Edible;
        if (ContainsAny(raw, SublingualWords))
            return ContainsAny(nameOnly, CapsuleWords) ? ProductCategory.Capsule : ProductCategory.Sublingual;
        if (ContainsAny(raw, OralWords)) return ProductCategory.Oral;

        // Pass 2: keywords anywhere, including the product name.
        if (ContainsAny(text, VapeWords)) return ProductCategory.Vape;
        if (ContainsAny(text, ConcentrateWords)) return ProductCategory.Concentrate;
        if (ContainsAny(text, FlowerWords)) return ProductCategory.Flower;
        if (ContainsAny(text, TopicalWords)) return ProductCategory.Topical;
        if (ContainsAny(text, CapsuleWords)) return ProductCategory.Capsule;
        if (ContainsAny(text, SublingualWords)) return ProductCategory.Sublingual;
        if (ContainsAny(text, EdibleWords)) return ProductCategory.Edible;
        if (ContainsAny(text, OralWords)) return ProductCategory.Oral;
        return ProductCategory.Other;
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var n in needles)
            if (haystack.Contains(n, StringComparison.Ordinal)) return true;
        return false;
    }
}
