using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gci.Core.Services;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

public sealed record ReleaseInfo(
    Version Version, string Tag, string Name, string? Notes, string PageUrl, DateTimeOffset? PublishedAt,
    ReleaseAsset? Exe, ReleaseAsset? Checksum);

/// <summary>
/// Looks up the latest GitHub release of GCI and downloads it safely: the exe must match the size GitHub reports,
/// start with a Windows executable header, and hash to the SHA-256 published alongside it (gci.exe.sha256).
/// </summary>
public sealed partial class UpdateChecker : IDisposable
{
    public const string DefaultRepository = "mrjamesroe/gci";
    public const string ExeAssetName = "gci.exe";
    public const string ChecksumAssetName = "gci.exe.sha256";

    private readonly HttpClient _http;
    private readonly string _repository;

    public UpdateChecker(string repository = DefaultRepository, HttpMessageHandler? handler = null, string? userAgent = null)
    {
        _repository = repository;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        // GitHub's API requires a User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? "GCI-Updater");
    }

    public string ReleasesPage => $"https://github.com/{_repository}/releases";

    /// <summary>The latest published (non-draft, non-prerelease) release, or null when there isn't one.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repository}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub returned HTTP {(int)res.StatusCode}");

        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return ParseRelease(json);
    }

    internal static ReleaseInfo? ParseRelease(JsonNode? json)
    {
        if (json is null || json["draft"]?.GetValue<bool>() == true || json["prerelease"]?.GetValue<bool>() == true) return null;
        var tag = json["tag_name"]?.GetValue<string>();
        if (tag is null || ParseVersion(tag) is not { } version) return null;

        var assets = (json["assets"] as JsonArray ?? new JsonArray())
            .OfType<JsonNode>()
            .Select(a => new ReleaseAsset(
                a["name"]?.GetValue<string>() ?? "",
                a["browser_download_url"]?.GetValue<string>() ?? "",
                a["size"]?.GetValue<long>() ?? 0))
            .ToList();

        return new ReleaseInfo(
            version, tag,
            json["name"]?.GetValue<string>() is { Length: > 0 } name ? name : tag,
            json["body"]?.GetValue<string>(),
            json["html_url"]?.GetValue<string>() ?? "",
            DateTimeOffset.TryParse(json["published_at"]?.GetValue<string>(), out var published) ? published : null,
            assets.FirstOrDefault(a => a.Name.Equals(ExeAssetName, StringComparison.OrdinalIgnoreCase)),
            assets.FirstOrDefault(a => a.Name.Equals(ChecksumAssetName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>"v1.2.3", "1.2", "v1.2.3-beta" → 1.2.3 / 1.2.0 / 1.2.3.</summary>
    public static Version? ParseVersion(string tag)
    {
        var m = VersionPattern().Match(tag.Trim());
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    /// <summary>Compares major.minor.build only (assembly versions carry a revision that releases don't).</summary>
    public static bool IsNewer(Version candidate, Version current) =>
        new Version(candidate.Major, candidate.Minor, Math.Max(0, candidate.Build)) >
        new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    /// <summary>
    /// Downloads the release's exe into <paramref name="directory"/> and verifies it. Throws when the release has no
    /// exe or checksum, or when the download doesn't match; a partial or bad file is deleted.
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, string directory, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (release.Exe is null) throw new InvalidOperationException($"Release {release.Tag} has no {ExeAssetName}.");
        if (release.Checksum is null) throw new InvalidOperationException($"Release {release.Tag} has no {ChecksumAssetName}; refusing to install an unverified download.");

        var expectedHash = ParseChecksum(await _http.GetStringAsync(release.Checksum.DownloadUrl, ct))
            ?? throw new InvalidOperationException($"{ChecksumAssetName} doesn't contain a SHA-256 hash.");

        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"gci-{release.Version}.exe");
        var partial = target + ".download";
        try
        {
            using (var res = await _http.GetAsync(release.Exe.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                res.EnsureSuccessStatusCode();
                var total = res.Content.Headers.ContentLength ?? release.Exe.Size;
                await using var input = await res.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(partial);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            var info = new FileInfo(partial);
            if (release.Exe.Size > 0 && info.Length != release.Exe.Size)
                throw new InvalidDataException($"Download was {info.Length:N0} bytes; GitHub lists {release.Exe.Size:N0}.");
            await using (var check = File.OpenRead(partial))
            {
                var header = new byte[2];
                if (await check.ReadAsync(header, ct) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                    throw new InvalidDataException("Download isn't a Windows executable.");
                check.Position = 0;
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).ToLowerInvariant();
                if (actual != expectedHash)
                    throw new InvalidDataException("Download doesn't match the published SHA-256 checksum.");
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>First 64-hex-digit token in a checksum file ("&lt;hash&gt;  gci.exe" or just the hash).</summary>
    public static string? ParseChecksum(string text) =>
        Sha256Pattern().Match(text) is { Success: true } m ? m.Value.ToLowerInvariant() : null;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"^v?(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]
    private static partial Regex Sha256Pattern();

    public void Dispose() => _http.Dispose();
}
