using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Gci.Core.Services;

public sealed record ParsedFeed(string? Title, IReadOnlyList<ParsedPost> Posts);

public sealed record ParsedPost(string Id, string Title, string? Link, string? Summary, DateTimeOffset? PublishedAt,
    IReadOnlyList<string> Categories, string? ImageUrl, string? ThumbnailUrl, string? Author);

/// <summary>Reads RSS 2.0 and Atom feeds into a common shape.</summary>
public static partial class FeedParser
{
    private const int SummaryLength = 320;
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";

    public static ParsedFeed Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new FormatException("Empty feed");

        if (root.Name.LocalName == "rss" || root.Element("channel") is not null)
        {
            var channel = root.Element("channel") ?? throw new FormatException("RSS feed without a channel");
            return new ParsedFeed(Text(channel.Element("title")), channel.Elements("item").Select(ParseRssItem).OfType<ParsedPost>().ToList());
        }
        if (root.Name == Atom + "feed")
            return new ParsedFeed(Text(root.Element(Atom + "title")), root.Elements(Atom + "entry").Select(ParseAtomEntry).OfType<ParsedPost>().ToList());

        throw new FormatException($"Not an RSS or Atom feed (root <{root.Name.LocalName}>)");
    }

    private static ParsedPost? ParseRssItem(XElement item)
    {
        var title = Text(item.Element("title"));
        var link = Text(item.Element("link"));
        var id = Text(item.Element("guid")) ?? link ?? title;
        if (id is null || title is null) return null;

        var image = item.Elements("enclosure")
                        .FirstOrDefault(e => ((string?)e.Attribute("type"))?.StartsWith("image/") != false)?.Attribute("url")?.Value
                    ?? item.Element(Media + "content")?.Attribute("url")?.Value
                    ?? item.Element(Media + "thumbnail")?.Attribute("url")?.Value
                    ?? FirstImage(Text(item.Element(Content + "encoded")) ?? Text(item.Element("description")));
        return new ParsedPost(
            id, Clean(title)!, link,
            Excerpt(Text(item.Element("description")) ?? Text(item.Element(Content + "encoded"))),
            Date(Text(item.Element("pubDate")) ?? Text(item.Element(Dc + "date"))),
            item.Elements("category").Select(c => Clean(c.Value)).OfType<string>().Distinct().ToList(),
            image, ThumbnailFor(image),
            Text(item.Element(Dc + "creator")) ?? Text(item.Element("author")));
    }

    private static ParsedPost? ParseAtomEntry(XElement entry)
    {
        var title = Text(entry.Element(Atom + "title"));
        var link = entry.Elements(Atom + "link")
            .FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value;
        var id = Text(entry.Element(Atom + "id")) ?? link ?? title;
        if (id is null || title is null) return null;

        var body = Text(entry.Element(Atom + "summary")) ?? Text(entry.Element(Atom + "content"));
        var image = entry.Element(Media + "thumbnail")?.Attribute("url")?.Value
                    ?? entry.Elements(Atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") == "enclosure"
                        && ((string?)l.Attribute("type"))?.StartsWith("image/") == true)?.Attribute("href")?.Value
                    ?? FirstImage(Text(entry.Element(Atom + "content")));
        return new ParsedPost(
            id, Clean(title)!, link, Excerpt(body),
            Date(Text(entry.Element(Atom + "published")) ?? Text(entry.Element(Atom + "updated"))),
            entry.Elements(Atom + "category").Select(c => (string?)c.Attribute("term")).OfType<string>().Distinct().ToList(),
            image, ThumbnailFor(image),
            Text(entry.Element(Atom + "author")?.Element(Atom + "name")));
    }

    /// <summary>Wix (The Peach Scout's host) resizes by path: …/v1/fit/w_1000,h_1000,… → w_320,h_320.</summary>
    internal static string? ThumbnailFor(string? image) =>
        image is not null && image.Contains("static.wixstatic.com", StringComparison.OrdinalIgnoreCase)
            ? WixSize().Replace(image, "w_320,h_320")
            : null;

    private static string? Text(XElement? e) => e is null || string.IsNullOrWhiteSpace(e.Value) ? null : e.Value.Trim();

    private static string? Clean(string? s) =>
        s is null ? null : Spaces().Replace(WebUtility.HtmlDecode(s), " ").Trim() is { Length: > 0 } t ? t : null;

    internal static string? Excerpt(string? html)
    {
        if (html is null) return null;
        var text = Clean(Tags().Replace(html, " "));
        if (text is null) return null;
        if (text.Length <= SummaryLength) return text;
        var cut = text.LastIndexOf(' ', SummaryLength);
        return text[..(cut > SummaryLength / 2 ? cut : SummaryLength)].TrimEnd(',', ';', ':', '.') + "…";
    }

    private static string? FirstImage(string? html) =>
        html is not null && ImgSrc().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    private static DateTimeOffset? Date(string? s)
    {
        if (s is null) return null;
        // RFC 822 dates sometimes use zone names .NET doesn't know; GMT/UT/Z are the common ones.
        var normalized = Regex.Replace(s, @"\s(UT|GMT|Z)$", " +0000");
        return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex("<img[^>]+src=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrc();

    [GeneratedRegex(@"w_\d+,h_\d+")]
    private static partial Regex WixSize();
}
