using System.Net;
using System.Text;
using Gci.Core.Providers;

namespace Gci.Core.Tests;

public class ProviderHttpTests
{
    private const string Url = "https://dutchie.com/graphql?operationName=FilteredProducts";

    // What Cloudflare returns to .NET for Dutchie and Jane.
    private const string BlockPage = """
        <!DOCTYPE html><html class="no-js" lang="en-US"><head><title>Attention Required! | Cloudflare</title></head>
        <body><div class="cf-error-details"><h1>Sorry, you have been blocked</h1></div></body></html>
        """;

    private sealed class FixedHandler(HttpStatusCode status, string body, string mediaType = "text/html") : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
        }
    }

    private sealed class FakeBrowser(Func<(int, string)> respond) : IBrowserTransport
    {
        public int Calls;

        public Task<(int Status, string Body)> SendAsync(HttpMethod method, string url, string? json,
            IDictionary<string, string>? headers, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond());
        }
    }

    [Fact]
    public async Task Blocked_host_is_read_through_the_browser_and_the_route_is_remembered()
    {
        var handler = new FixedHandler(HttpStatusCode.Forbidden, BlockPage);
        var browser = new FakeBrowser(() => (200, """{"data":{"ok":true}}"""));
        using var http = new ProviderHttp("test", handler, browser);

        var first = await http.GetJsonAsync(Url, CancellationToken.None);
        var second = await http.GetJsonAsync(Url, CancellationToken.None);

        Assert.True(first["data"]!["ok"]!.GetValue<bool>());
        Assert.NotNull(second);
        Assert.Equal(1, handler.Calls);  // later requests skip the route that was blocked
        Assert.Equal(2, browser.Calls);
        Assert.Equal(["dutchie.com"], http.BrowserHosts);
        Assert.Empty(http.CurlHosts);
    }

    [Fact]
    public async Task Blocked_everywhere_explains_the_likely_cause_without_retrying()
    {
        var handler = new FixedHandler(HttpStatusCode.Forbidden, BlockPage);
        var browser = new FakeBrowser(() => (403, BlockPage));
        using var http = new ProviderHttp("test", handler, browser);

        var ex = await Assert.ThrowsAsync<HostBlockedException>(() => http.GetJsonAsync(Url, CancellationToken.None));

        Assert.Contains("refusing connections from this PC", ex.Message);
        Assert.Contains("VPN", ex.Message);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, browser.Calls);
        Assert.Empty(http.BrowserHosts);
    }

    [Fact]
    public async Task Missing_webview2_runtime_says_how_to_fix_it()
    {
        var handler = new FixedHandler(HttpStatusCode.Forbidden, BlockPage);
        var browser = new FakeBrowser(() => throw new BrowserUnavailableException("the Microsoft Edge WebView2 Runtime isn't installed"));
        using var http = new ProviderHttp("test", handler, browser);

        var ex = await Assert.ThrowsAsync<HostBlockedException>(() => http.GetJsonAsync(Url, CancellationToken.None));

        Assert.Contains("WebView2 Runtime isn't installed", ex.Message);
        Assert.Contains(BrowserUnavailableException.DownloadUrl, ex.Message);
    }

    [Fact]
    public async Task Api_errors_are_not_mistaken_for_bot_blocks()
    {
        var handler = new FixedHandler(HttpStatusCode.Forbidden, """{"message":"Invalid Application-ID or API key","status":403}""",
            "application/json");
        var browser = new FakeBrowser(() => (200, "{}"));
        using var http = new ProviderHttp("test", handler, browser);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => http.GetJsonAsync(Url, CancellationToken.None));

        Assert.Contains("HTTP 403", ex.Message);
        Assert.Equal(0, browser.Calls);
    }

    [Fact]
    public async Task Without_a_browser_a_block_page_is_reported_as_before()
    {
        var handler = new FixedHandler(HttpStatusCode.Forbidden, BlockPage);
        using var http = new ProviderHttp("test", handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => http.GetJsonAsync(Url, CancellationToken.None));

        Assert.Contains("HTTP 403 from dutchie.com", ex.Message);
    }

    [Theory]
    [InlineData(403, BlockPage, true)]
    [InlineData(503, "<!DOCTYPE html><html><head><title>Just a moment...</title></head></html>", true)]
    [InlineData(403, """{"message":"Invalid key"}""", false)]
    [InlineData(404, "<html><body>Not found</body></html>", false)]
    [InlineData(200, "<html></html>", false)]
    public void Recognizes_cloudflare_block_and_challenge_pages(int status, string body, bool blocked) =>
        Assert.Equal(blocked, ProviderHttp.IsBlocked((status, body)));
}
