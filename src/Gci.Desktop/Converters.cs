using System.Globalization;
using Avalonia.Data.Converters;

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
