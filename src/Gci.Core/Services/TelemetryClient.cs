using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gci.Core.Services;

/// <summary>Stable facts about this install that Aptabase records with every event.</summary>
public sealed record TelemetrySystemInfo(string OsVersion, string Locale, string AppVersion, string? AppBuildNumber, string DeviceModel, bool IsDebug);

/// <summary>
/// Anonymous usage telemetry sent to Aptabase (aptabase.com), in two tiers: a <see cref="BasicEnabled"/> install ping
/// that is on by default (version, OS and a random install id only), and <see cref="DetailedEnabled"/> opt-in usage for
/// everything else. Every value passes through <see cref="TelemetryScrubber"/>, events are batched (25 per request,
/// Aptabase's limit), and a daily cap keeps usage inside the free tier. A local log of everything sent is kept for the
/// user to inspect.
/// </summary>
public sealed class TelemetryClient : IDisposable
{
    public const string DefaultAppKey = "A-US-1722619423";
    public const string SdkVersion = "gci-telemetry@1.0";
    public const int DailyEventCap = 150;
    public const string LogFileName = "telemetry-log.jsonl";
    private const string StateFileName = "telemetry.json";
    private const int MaxBatch = 25;
    private const int MaxLogLines = 400;
    private static readonly TimeSpan MaxEventAge = TimeSpan.FromHours(23); // Aptabase rejects events older than a day
    private static readonly TimeSpan SessionIdle = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan SessionMaxAge = TimeSpan.FromDays(6);   // and sessions older than a week

    private readonly DataStore _data;
    private readonly HttpClient _http;
    private readonly string? _baseUrl;
    private readonly TelemetrySystemInfo _system;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly Timer _flushTimer;
    private readonly State _state;
    private string _sessionId;
    private DateTimeOffset _sessionStarted;
    private DateTimeOffset _lastTouched;
    private bool _basic;
    private bool _detailed;

    public TelemetryClient(DataStore data, TelemetrySystemInfo system, string? appKey = DefaultAppKey,
        HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        _data = data;
        _system = system;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _baseUrl = BaseUrlFor(appKey);
        _http = new HttpClient(handler ?? new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(20) };
        if (appKey is not null) _http.DefaultRequestHeaders.TryAddWithoutValidation("App-Key", appKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"GCI/{system.AppVersion}");
        _state = data.Load(StateFileName, () => new State());
        (_sessionId, _sessionStarted) = NewSession();
        _lastTouched = _clock();
        _flushTimer = new Timer(_ => _ = FlushAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
    }

    /// <summary>False when no valid app key is configured; telemetry then never sends.</summary>
    public bool IsConfigured => _baseUrl is not null;

    /// <summary>
    /// The anonymous install ping (<c>app_started</c> / <c>app_active</c>): the one tier that sends without opt-in.
    /// It carries only version, OS and a random install id — no behaviour, no personal data. On by default; turning it
    /// off drops any basic events still queued.
    /// </summary>
    public bool BasicEnabled
    {
        get => _basic && IsConfigured;
        set
        {
            lock (_lock)
            {
                _basic = value;
                if (!value) { _state.Queue.RemoveAll(e => e.Tier == EventTier.Basic); Save(); }
            }
        }
    }

    /// <summary>Detailed, opt-in usage (everything else). Turning it off drops anything detailed still queued.</summary>
    public bool DetailedEnabled
    {
        get => _detailed && IsConfigured;
        set
        {
            lock (_lock)
            {
                _detailed = value;
                if (!value)
                {
                    _state.Queue.RemoveAll(e => e.Tier == EventTier.Detailed);
                    _state.Counters.Clear();
                    _state.Once.Clear();
                    Save();
                }
            }
        }
    }

    /// <summary>True when any tier is on and configured, so the flusher should run.</summary>
    private bool AnySink => IsConfigured && (_basic || _detailed);

    public string LogPath => _data.PathFor(LogFileName);

    /// <summary>Queues a detailed (opt-in) event. Values may be strings, numbers or booleans; everything else is stringified.</summary>
    public void Track(string name, IReadOnlyDictionary<string, object?>? props = null)
    {
        if (DetailedEnabled) Enqueue(name, props, EventTier.Detailed);
    }

    /// <summary>Queues an anonymous basic-tier event (the install ping). Sent whenever <see cref="BasicEnabled"/> is on.</summary>
    public void TrackBasic(string name, IReadOnlyDictionary<string, object?>? props = null)
    {
        if (BasicEnabled) Enqueue(name, props, EventTier.Basic);
    }

    private void Enqueue(string name, IReadOnlyDictionary<string, object?>? props, EventTier tier)
    {
        var now = _clock();
        lock (_lock)
        {
            var today = Day(now);
            if (_state.CapDay != today)
            {
                _state.CapDay = today;
                _state.SentToday = 0;
            }
            if (_state.SentToday >= DailyEventCap)
            {
                Bump("dropped_events", 1);
                return;
            }
            _state.SentToday++;
            _state.Queue.Add(new QueuedEvent(now, TelemetryScrubber.EventName(name), SessionId(now), TelemetryScrubber.Props(props), null, tier));
            Save();
        }
    }

    /// <summary>Tracks the event only if <paramref name="key"/> hasn't been tracked within <paramref name="period"/>.</summary>
    public bool TrackOnce(string key, TimeSpan period, string name, IReadOnlyDictionary<string, object?>? props = null)
    {
        if (!DetailedEnabled) return false;
        var now = _clock();
        lock (_lock)
        {
            if (_state.Once.TryGetValue(key, out var last) && now - last < period) return false;
            _state.Once[key] = now;
            foreach (var stale in _state.Once.Where(kv => now - kv.Value > TimeSpan.FromDays(35)).Select(kv => kv.Key).ToList())
                _state.Once.Remove(stale);
        }
        Track(name, props);
        return true;
    }

    /// <summary>Tracks at most <paramref name="perDay"/> events named <paramref name="name"/> per day (the rest are counted).</summary>
    public bool TrackLimited(string name, int perDay, IReadOnlyDictionary<string, object?>? props = null)
    {
        if (!DetailedEnabled) return false;
        var key = $"limit:{name}:{Day(_clock())}";
        lock (_lock)
        {
            _state.Counters.TryGetValue(key, out var used);
            if (used >= perDay)
            {
                Bump($"over_limit_{name}", 1);
                return false;
            }
            _state.Counters[key] = used + 1;
        }
        Track(name, props);
        return true;
    }

    /// <summary>Adds to a counter reported (and reset) by the next daily summary.</summary>
    public void Increment(string counter, double by = 1)
    {
        if (!DetailedEnabled) return;
        lock (_lock) Bump(counter, by);
    }

    /// <summary>Returns and resets the daily-summary counters.</summary>
    public Dictionary<string, object?> TakeCounters()
    {
        lock (_lock)
        {
            var result = _state.Counters.Where(kv => !kv.Key.StartsWith("limit:"))
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            foreach (var key in result.Keys) _state.Counters.Remove(key);
            foreach (var stale in _state.Counters.Keys.Where(k => k.StartsWith("limit:") && !k.EndsWith(Day(_clock()))).ToList())
                _state.Counters.Remove(stale);
            Save();
            return result;
        }
    }

    /// <summary>Reports an exception to Aptabase's error endpoint with the message and stack trace scrubbed.</summary>
    public void TrackError(Exception ex, string kind, bool fatal = false)
    {
        if (!DetailedEnabled) return;
        var signature = $"{ex.GetType().Name}:{TelemetryScrubber.TopFrame(ex)}";
        if (!TrackOnceSilently($"error:{signature}", TimeSpan.FromDays(1))) return;
        var now = _clock();
        var error = new JsonObject
        {
            ["errorMessage"] = TelemetryScrubber.Text($"{(fatal ? "Fatal " : "")}{ex.GetType().Name}: {ex.Message}", 5000),
            ["errorType"] = ex.GetType().Name,
            ["stackTrace"] = TelemetryScrubber.Text(ex.StackTrace ?? "", 10000),
            ["timestamp"] = now.UtcDateTime.ToString("o"),
            ["platform"] = OsName(),
            ["osName"] = OsName(),
            ["osVersion"] = _system.OsVersion,
            ["appVersion"] = _system.AppVersion,
            ["sdkVersion"] = SdkVersion,
            ["severity"] = fatal ? "fatal" : "error",
            ["kind"] = kind,
            ["isDebug"] = _system.IsDebug,
        };
        lock (_lock)
        {
            error["sessionId"] = SessionId(now);
            _state.Queue.Add(new QueuedEvent(now, "$error", null, null, error.ToJsonString(), EventTier.Detailed));
            Save();
        }
        if (fatal) FlushAsync().Wait(TimeSpan.FromSeconds(3));
    }

    /// <summary>Sends queued events. Server errors and outages keep them for the next attempt; rejected batches are dropped.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (!AnySink) return;
        if (!await _flushGate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            while (true)
            {
                List<QueuedEvent> batch;
                lock (_lock)
                {
                    var cutoff = _clock() - MaxEventAge;
                    _state.Queue.RemoveAll(e => e.At < cutoff);
                    var errors = _state.Queue.Where(e => e.ErrorJson is not null).Take(1).ToList();
                    batch = errors.Count > 0 ? errors : _state.Queue.Where(e => e.ErrorJson is null).Take(MaxBatch).ToList();
                }
                if (batch.Count == 0) return;

                var isError = batch[0].ErrorJson is not null;
                var body = isError ? batch[0].ErrorJson! : BuildBatch(batch);
                HttpResponseMessage res;
                try
                {
                    res = await _http.PostAsync($"{_baseUrl}{(isError ? "/api/v0/error" : "/api/v0/events")}",
                        new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    return; // offline; retry later
                }

                using (res)
                {
                    var retry = (int)res.StatusCode >= 500 || res.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
                    if (retry) return;
                    lock (_lock)
                    {
                        foreach (var e in batch) _state.Queue.Remove(e);
                        Save();
                    }
                    AppendLog(batch, (int)res.StatusCode);
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private string BuildBatch(IEnumerable<QueuedEvent> events)
    {
        var array = new JsonArray();
        foreach (var e in events)
        {
            array.Add(new JsonObject
            {
                ["timestamp"] = e.At.UtcDateTime.ToString("o"),
                ["sessionId"] = e.SessionId,
                ["eventName"] = e.Name,
                ["systemProps"] = new JsonObject
                {
                    ["isDebug"] = _system.IsDebug,
                    ["osName"] = OsName(),
                    ["osVersion"] = _system.OsVersion,
                    ["locale"] = _system.Locale.Length <= 10 ? _system.Locale : _system.Locale[..10],
                    ["appVersion"] = _system.AppVersion,
                    ["appBuildNumber"] = _system.AppBuildNumber,
                    ["deviceModel"] = _system.DeviceModel,
                    ["sdkVersion"] = SdkVersion,
                },
                ["props"] = e.Props is null ? null : JsonNode.Parse(JsonSerializer.Serialize(e.Props)),
            });
        }
        return array.ToJsonString();
    }

    private void AppendLog(IEnumerable<QueuedEvent> sent, int status)
    {
        try
        {
            var lines = File.Exists(LogPath) ? File.ReadAllLines(LogPath).ToList() : new List<string>();
            foreach (var e in sent)
            {
                lines.Add(JsonSerializer.Serialize(new
                {
                    sent = _clock().UtcDateTime.ToString("o"),
                    status,
                    tier = e.Tier == EventTier.Basic ? "basic" : "detailed",
                    @event = e.Name,
                    at = e.At.UtcDateTime.ToString("o"),
                    props = e.Props,
                    error = e.ErrorJson is null ? null : JsonNode.Parse(e.ErrorJson),
                }));
            }
            if (lines.Count > MaxLogLines) lines.RemoveRange(0, lines.Count - MaxLogLines);
            File.WriteAllLines(LogPath, lines);
        }
        catch (IOException)
        {
            // The log is a convenience; never fail telemetry over it.
        }
    }

    private bool TrackOnceSilently(string key, TimeSpan period)
    {
        var now = _clock();
        lock (_lock)
        {
            if (_state.Once.TryGetValue(key, out var last) && now - last < period) return false;
            _state.Once[key] = now;
            return true;
        }
    }

    private string SessionId(DateTimeOffset now)
    {
        if (now - _lastTouched >= SessionIdle || now - _sessionStarted >= SessionMaxAge)
            (_sessionId, _sessionStarted) = NewSession();
        _lastTouched = now;
        return _sessionId;
    }

    /// <summary>Aptabase's numeric session format: start time in epoch seconds × 10⁸ plus a random suffix.</summary>
    private (string, DateTimeOffset) NewSession()
    {
        var now = _clock();
        return ((now.ToUnixTimeSeconds() * 100_000_000 + Random.Shared.NextInt64(0, 99_999_999)).ToString(CultureInfo.InvariantCulture), now);
    }

    private void Bump(string counter, double by)
    {
        var key = TelemetryScrubber.Key(counter);
        _state.Counters[key] = (_state.Counters.TryGetValue(key, out var v) ? v : 0) + by;
        Save();
    }

    private void Save() => _data.Save(StateFileName, _state);

    private static string Day(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The OS name Aptabase groups by, from the OS this process is actually running on (not the build target),
    /// so the WPF and Avalonia apps each report themselves correctly.</summary>
    internal static string OsName() =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsLinux() ? "Linux"
        : "Unknown";

    internal static string? BaseUrlFor(string? appKey)
    {
        var parts = appKey?.Split('-');
        if (parts is not { Length: 3 }) return null;
        return parts[1] switch
        {
            "US" => "https://us.aptabase.com",
            "EU" => "https://eu.aptabase.com",
            _ => null,
        };
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        try { FlushAsync().Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _http.Dispose();
    }

    internal enum EventTier { Detailed, Basic }  // Detailed = 0 so events queued before this version stay detailed

    internal sealed record QueuedEvent(DateTimeOffset At, string Name, string? SessionId, Dictionary<string, object>? Props, string? ErrorJson, EventTier Tier = EventTier.Detailed);

    internal sealed class State
    {
        public List<QueuedEvent> Queue { get; set; } = new();
        public Dictionary<string, DateTimeOffset> Once { get; set; } = new();
        public Dictionary<string, double> Counters { get; set; } = new();
        public string? CapDay { get; set; }
        public int SentToday { get; set; }
    }
}

/// <summary>Keeps anything identifying out of telemetry: usernames, profile paths, emails, long numbers, ntfy topics.</summary>
public static partial class TelemetryScrubber
{
    private const int MaxValue = 180;

    public static string EventName(string name)
    {
        var n = Key(name);
        return n.Length <= 60 ? n : n[..60];
    }

    /// <summary>Aptabase property keys: lowercase snake_case, at most 40 characters.</summary>
    public static string Key(string key)
    {
        var k = NonKey().Replace(key.Trim().ToLowerInvariant(), "_").Trim('_');
        if (k.Length == 0) k = "prop";
        return k.Length <= 40 ? k : k[..40];
    }

    public static Dictionary<string, object>? Props(IReadOnlyDictionary<string, object?>? props)
    {
        if (props is null || props.Count == 0) return null;
        var result = new Dictionary<string, object>();
        foreach (var (key, value) in props)
        {
            object? clean = value switch
            {
                null => null,
                bool b => b,
                string s => Text(s, MaxValue),
                Enum e => e.ToString(),
                IConvertible c when value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                    => Math.Round(c.ToDouble(CultureInfo.InvariantCulture), 3),
                _ => Text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", MaxValue),
            };
            if (clean is not null) result[Key(key)] = clean;
        }
        return result.Count == 0 ? null : result;
    }

    public static string Text(string s, int max)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) s = s.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        var user = Environment.UserName;
        if (user.Length >= 3) s = Regex.Replace(s, $@"\b{Regex.Escape(user)}\b", "<user>", RegexOptions.IgnoreCase);
        s = Email().Replace(s, "<email>");
        s = Ntfy().Replace(s, "<ntfy-topic>");
        s = LongNumber().Replace(s, "<number>");
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    /// <summary>First stack frame inside GCI's own code, for grouping errors.</summary>
    public static string TopFrame(Exception ex)
    {
        var frames = (ex.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var frame = frames.FirstOrDefault(f => f.Contains("Gci.")) ?? frames.FirstOrDefault() ?? "";
        var at = frame.IndexOf(" in ", StringComparison.Ordinal);
        return (at > 0 ? frame[..at] : frame).Replace("at ", "").Trim();
    }

    [GeneratedRegex("[^a-z0-9_]+")]
    private static partial Regex NonKey();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"https?://(www\.)?ntfy\.sh/\S+|https?://\S*ntfy\S*", RegexOptions.IgnoreCase)]
    private static partial Regex Ntfy();

    /// <summary>Runs of 7+ digits (card numbers, phone numbers), allowing spaces or dashes between digits.</summary>
    [GeneratedRegex(@"(?<!\w)\d(?:[\s-]?\d){6,}(?!\w)")]
    private static partial Regex LongNumber();
}

/// <summary>
/// Reduces watch keywords to a fixed vocabulary of product terms, so demand signals ("crumble", "live rosin")
/// are reported but free-form text never is.
/// </summary>
public static class KeywordVocabulary
{
    private static readonly HashSet<string> Terms = new(StringComparer.OrdinalIgnoreCase)
    {
        // concentrates
        "crumble", "badder", "batter", "budder", "shatter", "wax", "rosin", "live rosin", "live resin", "resin", "sauce",
        "diamonds", "liquid diamonds", "hash", "bubble hash", "hash rosin", "kief", "sugar", "distillate", "rso", "concentrate",
        "concentrates", "dab", "dabs", "extract", "solventless", "full spectrum",
        // forms
        "flower", "pre-roll", "preroll", "vape", "vapes", "cart", "carts", "cartridge", "disposable", "all in one", "aio", "pod",
        "gummy", "gummies", "chocolate", "edible", "edibles", "tincture", "capsule", "capsules", "softgel", "lozenge", "troche",
        "topical", "cream", "patch", "drink", "soft squares", "melts", "nasal spray", "syringe",
        // lineage / sizes
        "indica", "sativa", "hybrid", "cbd", "cbn", "cbg", "thc", "1g", "0.5g", "3.5g", "7g", "eighth",
        // operators & house brands (public names)
        "trulieve", "modern flower", "momenta", "roll one", "clutch", "botanical sciences", "state", "pairo", "viz", "px", "s2",
        "fine fettle", "aura", "comffy", "true bliss", "true", "treevana", "treevana remedy", "cookies", "wyld", "mellow fellow",
    };

    /// <summary>(recognized terms, number of keywords that weren't recognized).</summary>
    public static (IReadOnlyList<string> Recognized, int Custom) Classify(IEnumerable<string> keywords)
    {
        var recognized = new List<string>();
        var custom = 0;
        foreach (var raw in keywords)
        {
            var k = raw.Trim().ToLowerInvariant();
            if (k.Length == 0) continue;
            if (Terms.Contains(k)) recognized.Add(k);
            else custom++;
        }
        return (recognized.Distinct().ToList(), custom);
    }
}
