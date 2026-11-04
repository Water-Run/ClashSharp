using System;
using System.Globalization;
using Microsoft.UI.Xaml.Data;

namespace ClashSharp.Converters;

/// <summary>Scales a 0 to 1 view-model fraction to a device-independent length for chart marks.</summary>
/// <remarks>
/// The converter parameter is the full-scale length in DIPs. View models therefore stay free of pixel sizes,
/// and out-of-range or non-numeric inputs collapse to a zero-length mark instead of overflowing the plot.
/// </remarks>
public sealed class FractionToLengthConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        double fraction = value is double number && double.IsFinite(number) ? Math.Clamp(number, 0, 1) : 0;
        double fullScale = parameter switch
        {
            double length => length,
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => 0,
        };

        return double.IsFinite(fullScale) && fullScale > 0 ? fraction * fullScale : 0d;
    }

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
