using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Osoko.UI.Converters;

/// <summary>Bool → Visibility with optional inversion.</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public bool UseHidden { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool boolean && boolean;
        if (Invert)
        {
            flag = !flag;
        }

        if (flag)
        {
            return Visibility.Visible;
        }

        return UseHidden ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is Visibility.Visible;
        return Invert ? !visible : visible;
    }
}

/// <summary>Null → Collapsed, non-null → Visible (optionally inverted).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null;
        if (Invert)
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Integer count → Visibility. Useful for "show when empty" hints.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public int Threshold { get; set; }

    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int number => number,
            double number => (int)number,
            _ => 0,
        };

        var visible = count > Threshold;
        if (Invert)
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
