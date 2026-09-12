using System.Net.Http;
using Gci.App.Services;
using Gci.Core.Models;

namespace Gci.App.Tests;

public class NotifierTests
{
    private const string Topic = "https://ntfy.sh/gci-test";
    private const string Product = "https://www.trulieve.com/product/modern-flower-riddler-crumble-133001?store=marietta&state=georgia";

    private static string Header(HttpRequestMessage req, string name) => string.Join(",", req.Headers.GetValues(name));

    [Fact]
    public async Task Product_alert_has_order_button_tap_link_and_link_in_text()
    {
        using var req = Notifier.BuildNtfyRequest(Topic, "New: Rae Bae Crumble", "1g · $55 · 2 available\nTrulieve Marietta", Product, "Order now");

        Assert.Equal(Product, Header(req, "Click"));
        Assert.Equal($"action=view, label=Order now, url={Product}, clear=true", Header(req, "Actions"));
        var text = await req.Content!.ReadAsStringAsync();
        Assert.EndsWith($"\nOrder now: {Product}", text); // reachable in the iPhone app, which has no action buttons
        Assert.Equal("New: Rae Bae Crumble", Header(req, "Title"));
    }

    [Fact]
    public void Urls_with_separators_are_quoted_and_non_ascii_titles_encoded()
    {
        const string wix = "https://static.example.com/v1/fit/w_320,h_320/file.png";
        using var req = Notifier.BuildNtfyRequest(Topic, "★ The Peach Scout: Crumble “Riddler”", "Review", wix, "Read post");

        Assert.Contains($"url=\"{wix}\"", Header(req, "Actions"));
        Assert.StartsWith("=?UTF-8?B?", Header(req, "Title"));
    }

    [Fact]
    public void Alerts_without_links_have_no_actions()
    {
        using var req = Notifier.BuildNtfyRequest(Topic, "GCI test", "Phone notifications are working.", null, "Open");
        Assert.False(req.Headers.Contains("Actions"));
        Assert.False(req.Headers.Contains("Click"));
    }

    [Theory]
    [InlineData(ChangeKind.NewProduct, "Order now")]
    [InlineData(ChangeKind.BackInStock, "Order now")]
    [InlineData(ChangeKind.Restocked, "Order now")]
    [InlineData(ChangeKind.PriceDrop, "Order now")]
    [InlineData(ChangeKind.SoldOut, "View")]
    public void Button_says_order_now_unless_sold_out(ChangeKind kind, string expected)
    {
        var e = new ChangeEvent
        {
            At = DateTimeOffset.Now, Kind = kind, StoreKey = "s", StoreName = "Trulieve Marietta", Operator = "Trulieve",
            ItemKey = "s|1|default", Name = "Rae Bae Crumble",
        };
        Assert.Equal(expected, Notifier.ActionLabel(e));
    }
}
