using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Jps.Shell.Views
{
    /// <summary>true → Visible. Pass ConverterParameter="Invert" for the opposite.</summary>
    public sealed class BoolToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var visible = value is bool b && b;
            if ("Invert".Equals(parameter))
                visible = !visible;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Non-empty string or non-null object → Visible.</summary>
    public sealed class HasValueToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value == null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Text → UPPER CASE (WPF has no text-transform).</summary>
    public sealed class ToUpper : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string)?.ToUpper(culture);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Compares an enum value with ConverterParameter; equal → Visible.</summary>
    public sealed class EnumToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value != null && value.ToString() == (string)parameter ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
