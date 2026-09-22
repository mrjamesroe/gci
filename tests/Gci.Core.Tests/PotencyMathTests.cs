using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.Core.Tests;

public class PotencyMathTests
{
    [Theory]
    [InlineData("THC 400 mg · CBD 100 mg", "45g", 400)]   // total mg is used as-is; CBD ignored
    [InlineData("THC 1200 mg", "30ml", 1200)]
    [InlineData("THC 100 mg", null, 100)]                      // mg totals don't need the size
    [InlineData("THC 26%", "3.5g", 910)]                       // 3.5g = 3500mg; 26% = 910mg
    [InlineData("THC 1%", "300mg", 3)]
    public void ThcMilligrams_reads_totals_and_percentages(string potency, string? size, double expected)
    {
        Assert.Equal(expected, PotencyMath.ThcMilligrams(potency, size));
    }

    [Theory]
    [InlineData(null, "45g")]          // no potency
    [InlineData("THC 26%", null)]      // percentage but no size to apply it to
    [InlineData("some vape", "1g")]    // unparseable
    public void ThcMilligrams_is_null_when_it_cannot_be_determined(string? potency, string? size)
    {
        Assert.Null(PotencyMath.ThcMilligrams(potency, size));
    }

    [Fact]
    public void ThcMilligrams_zero_percent_is_zero()
    {
        Assert.Equal(0, PotencyMath.ThcMilligrams("THC 0%", "250mg"));   // 0% is a real 0, not "unknown"
    }

    [Fact]
    public void CostPerThcMg_divides_price_by_total_thc()
    {
        // $80 for 400mg THC = $0.20/mg.
        Assert.Equal(0.2m, PotencyMath.CostPerThcMg(80m, "THC 400 mg · CBD 100 mg", "45g"));
    }

    [Fact]
    public void CostPerThcMg_is_null_without_thc_or_price()
    {
        Assert.Null(PotencyMath.CostPerThcMg(80m, "THC 0%", "250mg"));
        Assert.Null(PotencyMath.CostPerThcMg(null, "THC 400 mg", "45g"));
    }

    [Fact]
    public void InventoryItem_exposes_the_dose_value()
    {
        var item = new InventoryItem
        {
            StoreKey = "s", ProductId = "p", VariantId = "v", Name = "Tincture",
            Price = 100m, Potency = "THC 1200 mg · CBD 120 mg", Size = "30ml", InStock = true,
        };
        Assert.Equal(1200, item.ThcMg);
        Assert.Equal(0.083m, item.CostPerThcMg);   // 100 / 1200
    }
}
