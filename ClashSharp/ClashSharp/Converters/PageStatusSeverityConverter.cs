using System;
using ClashSharp.ApplicationModel.Presentation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace ClashSharp.Converters;

/// <summary>Uses native InfoBar colors, icons and accessibility for page feedback.</summary>
public sealed class PageStatusSeverityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        PageStatusSeverity.Success => InfoBarSeverity.Success,
        PageStatusSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
