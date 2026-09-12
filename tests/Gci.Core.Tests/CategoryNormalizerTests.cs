using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.Core.Tests;

public class CategoryNormalizerTests
{
    [Theory]
    [InlineData("concentrates", "Riddler Crumble", ProductCategory.Concentrate)]
    [InlineData("vapes, disposables", "Illuminati Live Resin All In One", ProductCategory.Vape)]
    [InlineData("vape", "GMO Liquid Diamonds All In One", ProductCategory.Vape)]
    [InlineData("extract", "Blue Dream Live Rosin 1g", ProductCategory.Concentrate)]
    [InlineData("oral, rso-syringes, rso-syringes", "RSO Syringe", ProductCategory.Oral)]
    [InlineData("whole-flower", "Garlic Cookies Flower", ProductCategory.Flower)]
    [InlineData("edible", "Silent Flower - Sparkling THC Rosé 5mg", ProductCategory.Edible)]
    [InlineData("edible", "Five Flower - Hemp Derived - Blackberry 10mg", ProductCategory.Edible)]
    [InlineData("s2", "Blue Raspberry Soft Squares 1:4 400mg THC", ProductCategory.Edible)]
    [InlineData("Sublingual", "Watermelon Soft Gels", ProductCategory.Capsule)]
    [InlineData("Tincture", "Peppermint Tincture 1:1 1200mg THC", ProductCategory.Sublingual)]
    [InlineData("Vaporizer Devices (Inhalation)", "Blue Dream", ProductCategory.Vape)]
    [InlineData("gear", "Yocan - Vane 2 Dry Herb Vaporizer", ProductCategory.Accessory)]
    [InlineData("Apparel", "True Bliss Crewneck", ProductCategory.Apparel)]
    [InlineData(null, "Bubble Hash 1g", ProductCategory.Concentrate)]
    [InlineData("lozenge", "Cherry Lozenge 1:10 400mg THC", ProductCategory.Oral)]
    public void Maps_provider_labels_and_names(string? raw, string name, ProductCategory expected) =>
        Assert.Equal(expected, CategoryNormalizer.Normalize(raw, name));
}
