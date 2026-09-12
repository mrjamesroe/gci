using System.Security.Cryptography;

namespace Gci.Core.Services;

/// <summary>Helpers for ntfy phone-push topics.</summary>
public static class NtfyTopic
{
    public const string DefaultServer = "https://ntfy.sh";

    // Lowercase letters and digits without look-alikes (l, o, 0, 1) so the name is easy to type on a phone.
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>
    /// A new unguessable topic URL. On a public ntfy server the topic name is the only secret, so it carries
    /// 18 random characters (~90 bits).
    /// </summary>
    public static string NewTopicUrl(string server = DefaultServer)
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return $"{server.TrimEnd('/')}/gci-{new string(chars)}";
    }

    /// <summary>The topic name from a topic URL ("https://ntfy.sh/gci-abc" → "gci-abc").</summary>
    public static string? TopicName(string? topicUrl) =>
        Uri.TryCreate(topicUrl, UriKind.Absolute, out var uri) && uri.AbsolutePath.Trim('/') is { Length: > 0 } name ? name : null;

    /// <summary>
    /// Link that opens the ntfy Android app subscribed to the topic (ntfy://host/topic). iOS has no equivalent,
    /// so iPhone users add the topic name by hand.
    /// </summary>
    public static string? SubscribeLink(string? topicUrl, string displayName = "GCI")
    {
        if (!Uri.TryCreate(topicUrl, UriKind.Absolute, out var uri) || TopicName(topicUrl) is not { } topic) return null;
        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var insecure = uri.Scheme == Uri.UriSchemeHttp ? "&secure=false" : "";
        return $"ntfy://{host}/{topic}?display={Uri.EscapeDataString(displayName)}{insecure}";
    }

    /// <summary>Whether the URL looks like a topic on the public ntfy.sh server (where anyone who guesses it can read it).</summary>
    public static bool IsPublicServer(string? topicUrl) =>
        Uri.TryCreate(topicUrl, UriKind.Absolute, out var uri) && uri.Host.Equals("ntfy.sh", StringComparison.OrdinalIgnoreCase);
}
