using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gci.Core.Providers;

/// <summary>
/// Shared HTTP plumbing: one client, browser-like headers, and up to two retries on transient failures/timeouts.
/// Some hosts (Jane, Dutchie) sit behind Cloudflare bot rules that fingerprint the TLS handshake and reject .NET's.
/// When a host answers with a Cloudflare block page, GCI escalates: first Windows' built-in curl.exe (accepted on most
/// PCs), then a real browser engine (<see cref="Browser"/>, Edge WebView2 in the app). The route that works is
/// remembered per host.
/// </summary>
public sealed class ProviderHttp : IDisposable
{
    public const string NoCurlVariable = "GCI_NO_CURL";

    private enum Route { Direct, Curl, Browser }

    private static readonly string? CurlPath = FindCurl();
    private readonly HttpClient _client;
    private readonly string _userAgent;
    private readonly bool _allowCurl;
    private readonly ConcurrentDictionary<string, Route> _routes = new(StringComparer.OrdinalIgnoreCase);

    public ProviderHttp(string userAgent, HttpMessageHandler? handler = null, IBrowserTransport? browser = null)
    {
        _userAgent = userAgent;
        Browser = browser;
        // A test handler means requests must stay in-process. GCI_NO_CURL=1 simulates a PC where curl is blocked.
        _allowCurl = handler is null && CurlPath is not null && Environment.GetEnvironmentVariable(NoCurlVariable) != "1";
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            // Generous: some menus (Trulieve's paged GraphQL) are slow under load, and a timeout fails the whole store.
            Timeout = TimeSpan.FromSeconds(45),
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
    }

    /// <summary>Last-resort route through a real browser engine; null where none is available (the CLI).</summary>
    public IBrowserTransport? Browser { get; }

    /// <summary>Hosts that rejected .NET and are being read through curl.exe.</summary>
    public IReadOnlyCollection<string> CurlHosts => HostsOn(Route.Curl);

    /// <summary>Hosts that rejected both .NET and curl and are being read through the browser engine.</summary>
    public IReadOnlyCollection<string> BrowserHosts => HostsOn(Route.Browser);

    private List<string> HostsOn(Route route) => _routes.Where(r => r.Value == route).Select(r => r.Key).ToList();

    public Task<JsonNode> GetJsonAsync(string url, CancellationToken ct, IDictionary<string, string>? headers = null) =>
        SendJsonAsync(HttpMethod.Get, url, null, headers, ct);

    public Task<JsonNode> PostJsonAsync(string url, object body, CancellationToken ct, IDictionary<string, string>? headers = null) =>
        SendJsonAsync(HttpMethod.Post, url, body is string s ? s : JsonSerializer.Serialize(body), headers, ct);

    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        var (status, text) = await SendAsync(HttpMethod.Get, url, null,
            new Dictionary<string, string> { ["Accept"] = "text/html,application/xhtml+xml" }, ct);
        if (status is < 200 or >= 300)
            throw new ProviderException($"HTTP {status} from {new Uri(url).Host}");
        return text;
    }

    private async Task<JsonNode> SendJsonAsync(HttpMethod method, string url, string? json,
        IDictionary<string, string>? headers, CancellationToken ct)
    {
        var host = new Uri(url).Host;
        for (var attempt = 1; ; attempt++)
        {
            int status;
            string text;
            try
            {
                (status, text) = await SendAsync(method, url, json, headers, ct);
            }
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested && ex is not HostBlockedException
                                       && ex is HttpRequestException or TaskCanceledException or ProviderException)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                continue;
            }

            if (IsTransient(status) && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                continue;
            }
            if (status is < 200 or >= 300)
                throw new ProviderException($"HTTP {status} from {host}: {Summarize(text)}");
            try
            {
                return JsonNode.Parse(text) ?? throw new ProviderException($"Empty response from {host}");
            }
            catch (JsonException)
            {
                throw new ProviderException($"Unexpected non-JSON response from {host}: {Summarize(text)}");
            }
        }
    }

    private async Task<(int Status, string Body)> SendAsync(HttpMethod method, string url, string? json,
        IDictionary<string, string>? headers, CancellationToken ct)
    {
        var host = new Uri(url).Host;
        var route = _routes.GetValueOrDefault(host, Route.Direct);

        if (route == Route.Direct)
        {
            try
            {
                var direct = await SendDirectAsync(method, url, json, headers, ct);
                if (!IsBlocked(direct) || (!_allowCurl && Browser is null)) return direct;
            }
            catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException && (_allowCurl || Browser is not null))
            {
                // Some bot rules drop the TLS handshake instead of answering; treat it like a block page.
            }
            route = Route.Curl;
        }

        var curlProblem = "was blocked earlier";
        if (route == Route.Curl)
        {
            if (_allowCurl)
            {
                try
                {
                    var viaCurl = await SendViaCurlAsync(method, url, json, headers, ct);
                    if (!IsBlocked(viaCurl))
                    {
                        _routes[host] = Route.Curl;
                        return viaCurl;
                    }
                    curlProblem = "was turned away too";
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is ProviderException or System.ComponentModel.Win32Exception)
                {
                    curlProblem = $"failed ({ex.Message})";
                }
            }
            else
            {
                curlProblem = CurlPath is null ? "isn't available" : "is turned off";
            }
        }

        if (Browser is null)
            throw new HostBlockedException($"{host}'s bot protection (Cloudflare) turns away GCI's built-in connection, and the " +
                                           $"fallback, Windows' curl, {curlProblem}. The GCI app can read it through Microsoft Edge WebView2 instead.");
        try
        {
            var viaBrowser = await Browser.SendAsync(method, url, json, headers, ct);
            if (IsBlocked(viaBrowser))
                throw new HostBlockedException($"{host} is refusing connections from this PC (Cloudflare bot protection), " +
                                               "even through Microsoft Edge. A VPN, proxy or network filter is the usual cause; " +
                                               $"try opening https://{host} in Edge on this PC.");
            _routes[host] = Route.Browser;
            return viaBrowser;
        }
        catch (BrowserUnavailableException ex)
        {
            throw new HostBlockedException($"{host}'s bot protection (Cloudflare) turns away GCI's built-in connection, and the " +
                                           $"fallback, Windows' curl, {curlProblem}. The next fallback is Microsoft Edge WebView2, but {ex.Message}. " +
                                           $"Install the WebView2 Runtime from {BrowserUnavailableException.DownloadUrl}");
        }
    }

    private async Task<(int Status, string Body)> SendDirectAsync(HttpMethod method, string url, string? json,
        IDictionary<string, string>? headers, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (json is not null)
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue; // set by the content itself
                if (k.Equals("Accept", StringComparison.OrdinalIgnoreCase)) req.Headers.Accept.Clear();
                req.Headers.TryAddWithoutValidation(k, v);
            }
        }

        using var res = await _client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        return ((int)res.StatusCode, body);
    }

    private async Task<(int Status, string Body)> SendViaCurlAsync(HttpMethod method, string url, string? json,
        IDictionary<string, string>? headers, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(CurlPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = json is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in CurlArguments(method, url, json is not null, headers, _userAgent, CurlCompression.Value))
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi) ?? throw new ProviderException("Could not start curl.exe");
        using var reg = ct.Register(() => { try { proc.Kill(); } catch { /* already exited */ } });
        if (json is not null)
        {
            await proc.StandardInput.WriteAsync(json);
            proc.StandardInput.Close();
        }
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var output = await stdoutTask;
        var error = await stderrTask;

        var split = output.LastIndexOf('\n');
        if (proc.ExitCode != 0 || split < 0 || !int.TryParse(output[(split + 1)..].Trim(), out var status))
        {
            var firstLine = error.Trim().Split('\n', 2)[0].Trim();
            throw new ProviderException($"curl.exe failed ({proc.ExitCode}) for {new Uri(url).Host}: {firstLine}");
        }
        return (status, output[..split]);
    }

    internal static List<string> CurlArguments(HttpMethod method, string url, bool hasBody,
        IDictionary<string, string>? headers, string userAgent, bool compressed)
    {
        var args = new List<string> { "-s", "-S", "--max-time", "30", "-X", method.Method, "-A", userAgent,
            "-H", "Accept-Language: en-US", "-w", "\n%{http_code}" };
        // Older Windows builds of curl have no zlib and exit with code 2 if asked to decompress.
        if (compressed) args.Insert(2, "--compressed");
        var hasAccept = headers?.Keys.Any(k => k.Equals("Accept", StringComparison.OrdinalIgnoreCase)) == true;
        if (!hasAccept) args.AddRange(["-H", "Accept: application/json"]);
        var hasContentType = false;
        foreach (var (k, v) in headers ?? new Dictionary<string, string>())
        {
            hasContentType |= k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase);
            args.AddRange(["-H", $"{k}: {v}"]);
        }
        if (hasBody)
        {
            if (!hasContentType) args.AddRange(["-H", "Content-Type: application/json"]);
            args.AddRange(["--data-binary", "@-"]);
        }
        args.Add(url);
        return args;
    }

    /// <summary>Whether a curl build (from its <c>curl -V</c> output) can decompress responses.</summary>
    internal static bool SupportsCompression(string versionOutput) =>
        versionOutput.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("Features:", StringComparison.OrdinalIgnoreCase)) is { } features
        && features.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(f => f.Equals("libz", StringComparison.OrdinalIgnoreCase) || f.Equals("brotli", StringComparison.OrdinalIgnoreCase)
                      || f.Equals("zstd", StringComparison.OrdinalIgnoreCase));

    private static readonly Lazy<bool> CurlCompression = new(() =>
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(CurlPath!, "-V")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return false;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return SupportsCompression(output);
        }
        catch (Exception)
        {
            return false; // uncompressed always works
        }
    });

    /// <summary>A Cloudflare block or challenge page (as opposed to the API's own error response).</summary>
    public static bool IsBlocked((int Status, string Body) response) =>
        response.Status is 403 or 429 or 503 && LooksLikeBotChallenge(response.Body);

    private static bool LooksLikeBotChallenge(string body) =>
        body.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("cloudflare", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(int code) => code is 429 or 500 or 502 or 503 or 504;

    private static string Summarize(string s)
    {
        if (s.Contains("<html", StringComparison.OrdinalIgnoreCase)) return "(HTML error page)";
        return s.Length <= 200 ? s : s[..200] + "…";
    }

    private static string? FindCurl()
    {
        if (OperatingSystem.IsWindows())
        {
            var win = Path.Combine(Environment.SystemDirectory, "curl.exe");
            return File.Exists(win) ? win : null;
        }
        // macOS ships curl at /usr/bin/curl; also check common Homebrew and Linux locations.
        foreach (var path in new[] { "/usr/bin/curl", "/usr/local/bin/curl", "/opt/homebrew/bin/curl", "/bin/curl" })
            if (File.Exists(path)) return path;
        return null;
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Sends requests through a real browser engine, for hosts whose bot rules reject every other client.</summary>
public interface IBrowserTransport
{
    Task<(int Status, string Body)> SendAsync(HttpMethod method, string url, string? json,
        IDictionary<string, string>? headers, CancellationToken ct);
}

/// <summary>The browser engine can't be used on this PC (for example, the WebView2 Runtime isn't installed).</summary>
public sealed class BrowserUnavailableException(string message) : Exception(message)
{
    public const string DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
}

public class ProviderException(string message) : Exception(message);

/// <summary>The host's bot protection rejected every route GCI has; retrying right away won't help.</summary>
public sealed class HostBlockedException(string message) : ProviderException(message);
