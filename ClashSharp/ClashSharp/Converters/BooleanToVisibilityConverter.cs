/*
 * Boolean To Visibility Converter
 * Converts ViewModel boolean state into WinUI visibility values
 *
 * @author: WaterRun
 * @file: Converters/BooleanToVisibilityConverter.cs
 * @date: 2026-06-25
 */

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace ClashSharp.Converters;

/// <summary>Converts a boolean value into <see cref="Visibility"/> without leaking UI types into view models.</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>Converts true to visible and false to collapsed.</summary>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Converts visible back to true and all other values to false.</summary>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return value is Visibility.Visible;
    }
}
