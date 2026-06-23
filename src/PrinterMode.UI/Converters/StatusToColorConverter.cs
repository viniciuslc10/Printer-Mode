using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PrinterMode.Core.Enums;

namespace PrinterMode.UI.Converters;

public class PrinterStatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == DependencyProperty.UnsetValue || value == null) return Brushes.Gray;
        return value is PrinterStatus status
            ? status switch
            {
                PrinterStatus.Connected => new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                PrinterStatus.Disconnected => new SolidColorBrush(Color.FromRgb(112, 112, 128)),
                PrinterStatus.Error => new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                _ => new SolidColorBrush(Color.FromRgb(255, 193, 7))
            }
            : Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public class DriverStatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == DependencyProperty.UnsetValue || value == null) return Brushes.Gray;
        return value is DriverStatus status
            ? status switch
            {
                DriverStatus.Installed => new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                DriverStatus.NotInstalled => new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                DriverStatus.Installing => new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                DriverStatus.Error => new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                _ => new SolidColorBrush(Color.FromRgb(112, 112, 128))
            }
            : Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public class ConnectionTypeToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == DependencyProperty.UnsetValue || value == null) return "❓";
        return value is ConnectionType type
            ? type switch
            {
                ConnectionType.USB => "🔌",
                ConnectionType.Serial => "📡",
                ConnectionType.Network => "🌐",
                ConnectionType.Shared => "🖨",
                _ => "❓"
            }
            : "❓";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var boolValue = value is bool b && b;
        var invert = parameter?.ToString() == "invert";
        var visible = invert ? !boolValue : boolValue;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
