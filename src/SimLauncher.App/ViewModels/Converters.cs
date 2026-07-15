using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SimLauncher.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter BoolToVisible = new BoolToVisibilityConverter();
    public static readonly IValueConverter Invert = new InvertBoolConverter();

    private sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class InvertBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : value;
    }
}
