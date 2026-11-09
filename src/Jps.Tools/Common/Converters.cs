using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Jps.Tools.Common
{
    public sealed class Invert : IValueConverter
    {
        public static readonly Invert Instance = new Invert();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
    }

    /// <summary>A number → true when it is greater than <see cref="Limit"/>.</summary>
    public sealed class GreaterThan : IValueConverter
    {
        public int Limit { get; set; }
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int n && n > Limit;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>true → Visible, false → Collapsed; ConverterParameter=Invert flips it.</summary>
    public sealed class Visible : IValueConverter
    {
        public static readonly Visible Instance = new Visible();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var on = value is bool b ? b : value != null && !(value is string s && s.Length == 0);
            if ("Invert".Equals(parameter)) on = !on;
            return on ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
