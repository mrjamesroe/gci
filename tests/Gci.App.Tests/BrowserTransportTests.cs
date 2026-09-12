using System.Net.Http;
using System.Text.RegularExpressions;
using Gci.App.Services;
using Gci.App.ViewModels;

namespace Gci.App.Tests;

public class BrowserTransportTests
{
    [Fact]
    public void Fetch_script_sends_the_request_as_json_and_leaves_browser_owned_headers_alone()
    {
        var script = BrowserTransport.FetchScript("abc", HttpMethod.Post, "https://search.iheartjane.com/1/indexes/x/query?k=\"v\"",
            """{"filters":"store_id : 6118"}""",
            new Dictionary<string, string> { ["User-Agent"] = "GCI", ["apollo-require-preflight"] = "true" });

        Assert.Contains("\"method\":\"POST\"", script);
        Assert.Contains("\"apollo-require-preflight\":\"true\"", script);
        Assert.Contains("\"Content-Type\":\"application/json\"", script);
        Assert.DoesNotContain("User-Agent", script);
        Assert.Contains("\"credentials\":\"include\"", script);
        // The URL and body are embedded as JSON string literals, so quotes can't break out of them.
        Assert.Contains("\"https://search.iheartjane.com/1/indexes/x/query?k=\\u0022v\\u0022\"", script);
        Assert.Matches(new Regex("\"body\":\"\\{\\\\u0022filters\\\\u0022"), script);
    }

    [Theory]
    [InlineData("dutchie.com is refusing connections from this PC (Cloudflare bot protection), even through Microsoft Edge.", "cloudflare_blocked")]
    [InlineData("dutchie.com is blocking GCI (Cloudflare bot protection) and Windows' curl was blocked too. GCI reads such menus through Microsoft Edge WebView2, but the Microsoft Edge WebView2 Runtime isn't installed.", "webview2_missing")]
    [InlineData("Edge WebView2 couldn't reach dutchie.com: TypeError: Failed to fetch", "webview2_failed")]
    [InlineData("HTTP 403 from api.mosaic.green: (HTML error page)", "http_403")]
    public void Blocked_host_errors_get_their_own_telemetry_class(string error, string expected) =>
        Assert.Equal(expected, MainViewModel.ErrorClass(error));
}
