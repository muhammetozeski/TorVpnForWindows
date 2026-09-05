using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Ui;

/// <summary>Tints log lines by where they came from, so the three streams stay readable together.</summary>
public sealed class LogSourceToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush AppBrush = Freeze("#FFD7DCE8");
    private static readonly SolidColorBrush TorBrush = Freeze("#FFB794F6");
    private static readonly SolidColorBrush SingBoxBrush = Freeze("#FF6FBF8B");
    private static readonly SolidColorBrush FallbackBrush = Freeze("#FF8D96AC");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            LogSource.App => AppBrush,
            LogSource.Tor => TorBrush,
            LogSource.SingBox => SingBoxBrush,
            _ => FallbackBrush
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Log colouring is display only.");

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
