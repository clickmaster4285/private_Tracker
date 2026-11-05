using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace client.Converters;

/// <summary>
/// Converts a 0–100 percentage into a star-sized GridLength for a progress bar.
/// With ConverterParameter "remainder" it returns the complementary segment,
/// so a Grid can be split into [fill | track] purely from the percentage.
/// </summary>
public class PercentToGridLengthConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var pct = value is double d ? Math.Clamp(d, 0, 100) : 0d;
        var remainder = string.Equals(parameter as string, "remainder", StringComparison.OrdinalIgnoreCase);
        var stars = remainder ? 100 - pct : pct;
        // Keep a tiny floor so a 0-star column never collapses and mis-sizes neighbors.
        return new GridLength(Math.Max(stars, 0.001), GridUnitType.Star);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
