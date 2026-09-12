using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Gci.Core.Services;

namespace Gci.Core.Tests;

public sealed class UpdateCheckerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gci-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeGitHub _github = new();
    private readonly UpdateChecker _checker;

    private static readonly byte[] Exe = [(byte)'M', (byte)'Z', .. Encoding.ASCII.GetBytes("pretend-this-is-gci")];
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    public UpdateCheckerTests()
    {
        _checker = new UpdateChecker("owner/gci", _github);
    }

    private static string ReleaseJson(string tag, bool draft = false, bool prerelease = false, bool withChecksum = true, long? size = null) => $$"""
        {
          "tag_name": "{{tag}}", "name": "GCI {{tag}}", "body": "Adds news feeds.", "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}}, "html_url": "https://github.com/owner/gci/releases/tag/{{tag}}",
          "published_at": "2026-09-12T01:00:00Z",
          "assets": [
            { "name": "gci.exe", "browser_download_url": "https://dl.test/gci.exe", "size": {{size ?? Exe.Length}} }
            {{(withChecksum ? """, { "name": "gci.exe.sha256", "browser_download_url": "https://dl.test/gci.exe.sha256", "size": 74 }""" : "")}}
          ]
        }
        """;

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.4", "1.4.0")]
    [InlineData("v2.0.1-beta", "2.0.1")]
    public void Parses_release_tags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateChecker.ParseVersion(tag));

    [Fact]
    public void Newer_ignores_assembly_revision()
    {
        Assert.True(UpdateChecker.IsNewer(new Version(1, 0, 1), new Version(1, 0, 0, 0)));
        Assert.False(UpdateChecker.IsNewer(new Version(1, 0, 0), new Version(1, 0, 0, 0)));
        Assert.False(UpdateChecker.IsNewer(new Version(0, 9, 9), new Version(1, 0, 0, 0)));
    }

    [Fact]
    public async Task Reads_latest_release_and_its_assets()
    {
        _github.Latest = ReleaseJson("v1.1.0");
        var release = (await _checker.GetLatestAsync())!;

        Assert.Equal(new Version(1, 1, 0), release.Version);
        Assert.Equal("https://dl.test/gci.exe", release.Exe!.DownloadUrl);
        Assert.NotNull(release.Checksum);
        Assert.Equal("https://api.github.com/repos/owner/gci/releases/latest", _github.Requested.First());
    }

    [Fact]
    public async Task No_releases_yet_is_not_an_error()
    {
        _github.Latest = null;
        Assert.Null(await _checker.GetLatestAsync());
    }

    [Fact]
    public void Drafts_and_prereleases_are_ignored()
    {
        Assert.Null(UpdateChecker.ParseRelease(JsonNode.Parse(ReleaseJson("v9.0.0", draft: true))));
        Assert.Null(UpdateChecker.ParseRelease(JsonNode.Parse(ReleaseJson("v9.0.0", prerelease: true))));
    }

    [Fact]
    public async Task Verified_download_is_kept()
    {
        _github.Files["https://dl.test/gci.exe"] = Exe;
        _github.Files["https://dl.test/gci.exe.sha256"] = Encoding.ASCII.GetBytes($"{Sha(Exe)}  gci.exe\n");
        var release = UpdateChecker.ParseRelease(JsonNode.Parse(ReleaseJson("v1.1.0")))!;

        var path = await _checker.DownloadAsync(release, _dir);

        Assert.Equal(Exe, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Tampered_download_is_rejected_and_deleted()
    {
        _github.Files["https://dl.test/gci.exe"] = [(byte)'M', (byte)'Z', .. Encoding.ASCII.GetBytes("tampered-payload!!!")];
        _github.Files["https://dl.test/gci.exe.sha256"] = Encoding.ASCII.GetBytes(Sha(Exe));
        var release = UpdateChecker.ParseRelease(JsonNode.Parse(ReleaseJson("v1.1.0")))!;

        await Assert.ThrowsAsync<InvalidDataException>(() => _checker.DownloadAsync(release, _dir));
        Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir) : []);
    }

    [Fact]
    public async Task Release_without_checksum_is_refused()
    {
        var release = UpdateChecker.ParseRelease(JsonNode.Parse(ReleaseJson("v1.1.0", withChecksum: false)))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _checker.DownloadAsync(release, _dir));
    }

    [Fact]
    public async Task Non_executable_download_is_rejected()
    {
        var html = Encoding.ASCII.GetBytes("<html>error page</html>");
        _github.Files["https://dl.test/gci.exe"] = html;
        _github.Files["https://dl.test/gci.exe.sha256"] = Encoding.ASCII.GetBytes(Sha(html));
        var release = UpdateChecker.ParseRelease(JsonNode.Parse(ReleaseJson("v1.1.0", size: html.Length)))!;

        await Assert.ThrowsAsync<InvalidDataException>(() => _checker.DownloadAsync(release, _dir));
    }

    public void Dispose()
    {
        _checker.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public string? Latest { get; set; }
        public Dictionary<string, byte[]> Files { get; } = new();
        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            if (url.Contains("/releases/latest"))
                return Task.FromResult(Latest is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Latest) });
            return Task.FromResult(Files.TryGetValue(url, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
