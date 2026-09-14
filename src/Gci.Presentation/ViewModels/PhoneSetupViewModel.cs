using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gci.App.Services;
using Gci.Core.Services;
using QRCoder;

namespace Gci.App.ViewModels;

/// <summary>
/// Walks the user through phone alerts: creates a private ntfy topic, shows a QR code that subscribes the Android
/// app in one scan (iPhone users type the topic name), and sends a test push.
/// </summary>
public sealed partial class PhoneSetupViewModel : ObservableObject
{
    public const string AppStoreUrl = "https://apps.apple.com/us/app/ntfy/id1625396347";
    public const string PlayStoreUrl = "https://play.google.com/store/apps/details?id=io.heckel.ntfy";

    private readonly INotifier _notifier;
    private readonly IClipboard _clipboard;
    private readonly Action<string?> _saveTopic;
    private readonly TelemetryClient _telemetry;

    public PhoneSetupViewModel(string? currentTopicUrl, INotifier notifier, IClipboard clipboard, Action<string?> saveTopic,
        TelemetryClient telemetry)
    {
        _notifier = notifier;
        _clipboard = clipboard;
        _saveTopic = saveTopic;
        _telemetry = telemetry;
        _telemetry.Track("phone_setup_opened", new Dictionary<string, object?> { ["had_topic"] = !string.IsNullOrWhiteSpace(currentTopicUrl) });
        UseTopic(string.IsNullOrWhiteSpace(currentTopicUrl) ? NtfyTopic.NewTopicUrl() : currentTopicUrl!);
    }

    [ObservableProperty] private string _topicUrl = "";
    [ObservableProperty] private string _topicName = "";
    [ObservableProperty] private string _server = "";
    [ObservableProperty] private byte[]? _qrCodePng;
    [ObservableProperty] private bool _isPublicServer;
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _testSucceeded;
    [ObservableProperty] private bool _isSending;

    public bool Configured { get; private set; } = true;

    private void UseTopic(string url)
    {
        TopicUrl = url;
        TopicName = NtfyTopic.TopicName(url) ?? url;
        Server = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
        IsPublicServer = NtfyTopic.IsPublicServer(url);
        QrCodePng = NtfyTopic.SubscribeLink(url) is { } link ? RenderQrPng(link) : null;
        TestResult = "";
        TestSucceeded = false;
        _saveTopic(url);
        Configured = true;
    }

    [RelayCommand]
    private async Task SendTestAsync()
    {
        IsSending = true;
        var error = await _notifier.SendNtfyAsync(TopicUrl, "GCI is connected",
            "Watch alerts will arrive here. Tap Order now on an alert to open the product page.",
            "https://github.com/mrjamesroe/gci", "About GCI");
        IsSending = false;
        TestSucceeded = error is null;
        TestResult = error is null
            ? "Sent. If it didn't show up on your phone, check the topic name matches and that notifications are allowed for ntfy."
            : $"Couldn't send: {error}";
        _telemetry.Track("phone_setup_test", new Dictionary<string, object?> { ["ok"] = error is null, ["public_server"] = IsPublicServer });
    }

    [RelayCommand]
    private void NewTopic()
    {
        UseTopic(NtfyTopic.NewTopicUrl());
        _telemetry.Track("phone_setup_new_topic");
    }

    [RelayCommand]
    private void CopyTopic()
    {
        try
        {
            _clipboard.SetText(TopicName);
            TestResult = "Topic name copied.";
        }
        catch (Exception)
        {
            TestResult = "Couldn't reach the clipboard; type the topic name instead.";
        }
    }

    [RelayCommand]
    private void TurnOff()
    {
        _saveTopic(null);
        Configured = false;
        _telemetry.Track("phone_setup_turned_off");
    }

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser registered; the link text is visible to copy by hand.
        }
    }

    /// <summary>The subscribe QR as PNG bytes; the view decodes it to its own bitmap type.</summary>
    private static byte[] RenderQrPng(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(10);
    }
}
