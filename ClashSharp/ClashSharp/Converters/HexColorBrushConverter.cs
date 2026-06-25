/*
 * Hex Color Brush Converter
 * Converts persisted ARGB accent color text into a WinUI brush for preview swatches
 *
 * @author: WaterRun
 * @file: Converters/HexColorBrushConverter.cs
 * @date: 2026-06-25
 */

using System;
using ClashSharp.Service;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace ClashSharp.Converters;

/// <summary>Converts persisted accent color text into a brush suitable for XAML previews.</summary>
public sealed class HexColorBrushConverter : IValueConverter
{
    /// <summary>Converts a hexadecimal accent color value into a <see cref="SolidColorBrush"/>.</summary>
    /// <param name="value">Expected color text in #RRGGBB or #AARRGGBB format.</param>
    /// <param name="targetType">Target binding type.</param>
    /// <param name="parameter">Unused converter parameter.</param>
    /// <param name="language">Current binding language.</param>
    /// <returns>A solid brush using the parsed color, or the default accent color when invalid.</returns>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string colorValue = value as string ?? string.Empty;
        return new SolidColorBrush(AppThemeService.ParseAccentColorOrDefault(colorValue));
    }

    /// <summary>Back conversion is intentionally unsupported because the picker owns persistence.</summary>
    /// <param name="value">Ignored.</param>
    /// <param name="targetType">Ignored.</param>
    /// <param name="parameter">Ignored.</param>
    /// <param name="language">Ignored.</param>
    /// <returns>Always throws.</returns>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException("Accent color preview is one-way.");
    }
}
