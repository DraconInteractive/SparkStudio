using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SparkStudio.Infrastructure;

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
