using System.Text.Json.Serialization;

namespace Gci.Core.Models;

/// <summary>An RSS/Atom feed GCI follows (The Peach Scout by default).</summary>
public sealed class FeedSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Send a notification for every new post (watch-keyword matches are flagged either way).</summary>
    public bool Notify { get; set; } = true;
}

public sealed record FeedPost
{
    public required string FeedId { get; init; }
    /// <summary>The feed's guid/id for the post, falling back to its link.</summary>
    public required string PostId { get; init; }
    public required string Title { get; init; }
    public string? Link { get; init; }
    /// <summary>Plain-text excerpt (HTML stripped).</summary>
    public string? Summary { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public List<string> Categories { get; init; } = new();
    public string? ImageUrl { get; init; }
    /// <summary>A smaller rendition of <see cref="ImageUrl"/> when the host can resize.</summary>
    public string? ThumbnailUrl { get; init; }
    public string? Author { get; init; }
    public DateTimeOffset FirstSeenAt { get; init; }

    [JsonIgnore]
    public string Key => $"{FeedId}|{PostId}";

    [JsonIgnore]
    public DateTimeOffset SortDate => PublishedAt ?? FirstSeenAt;
}
