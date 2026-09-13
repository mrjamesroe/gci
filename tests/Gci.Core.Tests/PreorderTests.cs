using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.Core.Tests;

public class PreorderTests
{
    private static readonly Uri TrulieveProduct =
        new("https://www.trulieve.com/product/modern-flower-la-kush-cake-3-whole-flower-126924?store=marietta&state=georgia");

    [Fact]
    public void Prefill_formats_details_the_ways_checkout_forms_expect()
    {
        var data = PrefillData.From(new PatientProfile
        {
            FirstName = " Pat ", LastName = "Example", BirthDate = new DateTime(1984, 7, 4),
            RegistryCardNumber = "GA-123456", CardExpires = new DateTime(2027, 3, 1),
            Phone = "+1 404.555.0123", Email = " pat@example.com ",
        });

        Assert.Equal("Pat", data.FirstName);
        Assert.Equal("Pat Example", data.FullName);
        Assert.Equal("1984-07-04", data.BirthDateIso);
        Assert.Equal("07/04/1984", data.BirthDateUs);
        Assert.Equal(("1984", "7", "4"), (data.BirthYear, data.BirthMonth, data.BirthDay));
        Assert.Equal("(404) 555-0123", data.Phone);
        Assert.Equal("4045550123", data.PhoneDigits);
        Assert.Equal("pat@example.com", data.Email);
        Assert.Equal("GA-123456", data.CardNumber);
        Assert.Equal("03/01/2027", data.CardExpiresUs);
        Assert.Equal(7, data.FieldCount);
    }

    [Fact]
    public void Prefill_leaves_out_anything_not_saved()
    {
        var data = PrefillData.From(new PatientProfile { FirstName = "Pat", Phone = "ext 12" });

        Assert.Null(data.LastName);
        Assert.Equal("Pat", data.FullName);
        Assert.Null(data.BirthDateIso);
        Assert.Equal("ext 12", data.Phone); // not a 10-digit number: kept as typed
        Assert.Null(data.Email);
        Assert.Equal(2, data.FieldCount);
        Assert.Equal(0, PrefillData.From(new PatientProfile()).FieldCount);
    }

    [Theory]
    [InlineData("https://www.trulieve.com/checkout", true)]
    [InlineData("https://shop.botanicalsciences.com/products/x?store=lotus-farmacy-suwanee", true)]
    [InlineData("https://dutchie.com/dispensary/true-bliss-peach/checkout", true)]
    [InlineData("https://www.iheartjane.com/cart", true)]
    [InlineData("https://treevanaremedy.com/shop/checkout", true)]
    [InlineData("http://www.trulieve.com/checkout", false)]            // never over plain HTTP
    [InlineData("https://trulieve.com.example.net/checkout", false)]   // look-alike host
    [InlineData("https://nottrulieve.com/checkout", false)]
    [InlineData("https://accounts.google.com/signin", false)]
    public void Details_only_go_to_store_sites_over_https(string page, bool allowed) =>
        Assert.Equal(allowed, PreorderSites.MayFill(new Uri(page), TrulieveProduct));

    [Fact]
    public void A_store_on_its_own_site_is_allowed_for_its_own_product()
    {
        var product = new Uri("https://shop.somedispensary.com/menu/item-1");
        Assert.True(PreorderSites.MayFill(new Uri("https://checkout.somedispensary.com/pay"), product));
        Assert.False(PreorderSites.MayFill(new Uri("https://shop.otherdispensary.com/"), product));
    }
}
