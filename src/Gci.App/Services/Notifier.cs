using System.Net.Http;
using System.Text;
using Gci.Core.Models;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Gci.App.Services;

/// <summary>Delivers watch matches as Windows toasts and, optionally, ntfy pushes to a phone.</summary>
public sealed class Notifier : IDisposable
{
    private const int MaxIndividualToasts = 4;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task NotifyAsync(IReadOnlyList<(ChangeEvent Event, WatchRule Rule)> matches, AppSettings settings)
    {
        if (matches.Count == 0) return;
        var distinct = matches.DistinctBy(m => (m.Event.ItemKey, m.Event.Kind)).ToList();

        if (settings.ToastNotifications)
        {
            foreach (var (e, rule) in distinct.Take(MaxIndividualToasts))
                ShowToast(Title(e), Body(e), e.StoreName, e.Url, rule.Name);
            if (distinct.Count > MaxIndividualToasts)
            {
                var rest = distinct.Skip(MaxIndividualToasts).ToList();
                ShowToast($"{rest.Count} more watched changes",
                    string.Join("\n", rest.Take(3).Select(m => $"{m.Event.Describe()}: {m.Event.Name}")),
                    "Open GCI → Changes for the full list", null, null);
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.NtfyTopicUrl))
        {
            foreach (var (e, _) in distinct.Take(8))
                await SendNtfyAsync(settings.NtfyTopicUrl!, Title(e), $"{Body(e)}\n{e.StoreName}", e.Url);
        }
    }

    /// <summary>New posts from followed feeds; posts mentioning a watch keyword say so in the title.</summary>
    public async Task NotifyPostsAsync(IReadOnlyList<(FeedPost Post, string Source, string? Watch)> posts, AppSettings settings)
    {
        if (posts.Count == 0) return;
        if (settings.ToastNotifications)
        {
            foreach (var (post, source, watch) in posts.Take(MaxIndividualToasts))
                ShowToast(watch is null ? $"New from {source}" : $"★ {source} mentions \"{watch}\"",
                    post.Title, source, post.Link, null, "Read post");
            if (posts.Count > MaxIndividualToasts)
                ShowToast($"{posts.Count - MaxIndividualToasts} more new posts", "Open GCI → News to read them.", null, null, null);
        }

        if (!string.IsNullOrWhiteSpace(settings.NtfyTopicUrl))
        {
            foreach (var (post, source, watch) in posts.Take(8))
                await SendNtfyAsync(settings.NtfyTopicUrl!, $"{(watch is null ? "" : "★ ")}{source}: {post.Title}", post.Summary ?? post.Title, post.Link);
        }
    }

    public void ShowToast(string title, string body, string? attribution, string? url, string? tag, string openLabel = "Open menu")
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument("action", "open")
                .AddText(title)
                .AddText(body);
            if (attribution is not null) builder.AddAttributionText(attribution);
            if (url is not null)
            {
                builder.AddArgument("url", url);
                builder.AddButton(new ToastButton().SetContent(openLabel).SetProtocolActivation(new Uri(url)));
            }
            builder.AddButton(new ToastButton().SetContent("Show GCI").AddArgument("action", "show"));
            builder.Show(toast =>
            {
                if (tag is not null) toast.Group = "gci";
            });
        }
        catch (Exception)
        {
            // Toasts can be disabled by policy or Focus Assist; the Changes tab still has everything.
        }
    }

    public async Task<string?> SendNtfyAsync(string topicUrl, string title, string body, string? clickUrl)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, topicUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            };
            // ntfy header values must be ASCII; non-ASCII titles go through RFC 2047 encoding.
            req.Headers.TryAddWithoutValidation("Title", IsAscii(title) ? title : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(title))}?=");
            req.Headers.TryAddWithoutValidation("Tags", "leaves");
            if (clickUrl is not null) req.Headers.TryAddWithoutValidation("Click", clickUrl);
            using var res = await _http.SendAsync(req);
            return res.IsSuccessStatusCode ? null : $"ntfy returned HTTP {(int)res.StatusCode}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string Title(ChangeEvent e) => e.Kind switch
    {
        ChangeKind.NewProduct => $"New: {e.Name}",
        ChangeKind.BackInStock => $"Back in stock: {e.Name}",
        ChangeKind.Restocked => $"Restocked: {e.Name}",
        ChangeKind.SoldOut => $"Sold out: {e.Name}",
        ChangeKind.PriceDrop => $"Price drop: {e.Name}",
        ChangeKind.QuantityChanged => $"Low stock: {e.Name}",
        _ => e.Name,
    };

    private static string Body(ChangeEvent e)
    {
        var parts = new List<string>();
        if (e.Size is not null) parts.Add(e.Size);
        if (e.Kind == ChangeKind.PriceDrop) parts.Add($"{e.OldPrice:C0} → {e.NewPrice:C0}");
        else if (e.NewPrice is { } p) parts.Add(p.ToString("C0"));
        if (e.Kind == ChangeKind.QuantityChanged) parts.Add($"only {e.NewQuantity} left");
        else if (e.Kind == ChangeKind.Restocked && e.OldQuantity is { } o && e.NewQuantity is { } n && n > o) parts.Add($"{o} → {n} units");
        else if (e.NewQuantity is { } q && e.Kind != ChangeKind.SoldOut) parts.Add($"{q} available");
        if (e.Detail is not null) parts.Add(e.Detail);
        return string.Join(" · ", parts);
    }

    private static bool IsAscii(string s) => s.All(c => c < 128);

    public void Dispose() => _http.Dispose();
}
