using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gci.Core.Providers;

/// <summary>
/// Shared HTTP plumbing: one client, browser-like headers, a single retry on transient failures.
/// Some hosts (Jane, Dutchie) sit behind Cloudflare bot rules that reject .NET's TLS handshake but
/// accept Windows' built-in curl.exe; when a host answers 403 with an HTML challenge, it is
/// remembered and later requests to it go through curl.exe instead.
/// </summary>
public sealed class ProviderHttp : IDisposable
{
    private static readonly string? CurlPath = FindCurl();
    private readonly HttpClient _client;
    private readonly string _userAgent;
    private readonly bool _allowCurl;
    private readonly ConcurrentDictionary<string, bool> _curlHosts = new(StringComparer.OrdinalIgnoreCase);

    public ProviderHttp(string userAgent, HttpMessageHandler? handler = null)
    {
        _userAgent = userAgent;
        // A test handler means requests must stay in-process.
        _allowCurl = handler is null && CurlPath is not null;
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
    }

    /// <summary>Hosts that rejected .NET and are being read through curl.exe.</summary>
    public IReadOnlyCollection<string> CurlHosts => _curlHosts.Keys.ToList();

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
            catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or ProviderException)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                continue;
            }

            if (IsTransient(status) && attempt < 2)
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
        if (_curlHosts.ContainsKey(host))
            return await SendViaCurlAsync(method, url, json, headers, ct);

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
        if (_allowCurl && res.StatusCode == HttpStatusCode.Forbidden && LooksLikeBotChallenge(body))
        {
            _curlHosts[host] = true;
            return await SendViaCurlAsync(method, url, json, headers, ct);
        }
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
        foreach (var arg in new[] { "-s", "-S", "--compressed", "--max-time", "30", "-X", method.Method, "-A", _userAgent,
                     "-H", "Accept-Language: en-US", "-w", "\n%{http_code}" })
            psi.ArgumentList.Add(arg);
        var hasAccept = headers?.Keys.Any(k => k.Equals("Accept", StringComparison.OrdinalIgnoreCase)) == true;
        if (!hasAccept) { psi.ArgumentList.Add("-H"); psi.ArgumentList.Add("Accept: application/json"); }
        var hasContentType = false;
        foreach (var (k, v) in headers ?? new Dictionary<string, string>())
        {
            hasContentType |= k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase);
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add($"{k}: {v}");
        }
        if (json is not null)
        {
            if (!hasContentType)
            {
                psi.ArgumentList.Add("-H");
                psi.ArgumentList.Add("Content-Type: application/json");
            }
            psi.ArgumentList.Add("--data-binary");
            psi.ArgumentList.Add("@-");
        }
        psi.ArgumentList.Add(url);

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
            throw new ProviderException($"curl.exe failed ({proc.ExitCode}) for {new Uri(url).Host}: {error.Trim()}");
        return (status, output[..split]);
    }

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
        if (!OperatingSystem.IsWindows()) return null;
        var path = Path.Combine(Environment.SystemDirectory, "curl.exe");
        return File.Exists(path) ? path : null;
    }

    public void Dispose() => _client.Dispose();
}

public sealed class ProviderException(string message) : Exception(message);
