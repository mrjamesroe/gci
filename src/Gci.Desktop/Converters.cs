using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Gci.Desktop;

/// <summary>Pause/resume button label driven by the VM's IsPaused flag.</summary>
public sealed class PauseLabelConverter : IValueConverter
{
    public static readonly PauseLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "▶ Resume" : "⏸ Pause";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Decodes PNG bytes (the phone-setup QR the shared VM produces) into an Avalonia bitmap.</summary>
public sealed class BytesToBitmapConverter : IValueConverter
{
    public static readonly BytesToBitmapConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is byte[] { Length: > 0 } bytes ? new Bitmap(new MemoryStream(bytes)) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound value is not null (e.g. show the Install button only when an update exists).</summary>
public sealed class NotNullConverter : IValueConverter
{
    public static readonly NotNullConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound number is zero (empty-state messages).</summary>
public sealed class ZeroConverter : IValueConverter
{
    public static readonly ZeroConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is 0;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
