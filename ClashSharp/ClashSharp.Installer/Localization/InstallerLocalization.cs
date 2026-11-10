using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using ClashSharp.Installer.Presentation;

namespace ClashSharp.Installer.Localization;

/// <summary>Holds the one localized text instance that the window and the view model share.</summary>
internal static class InstallerLocalization
{
    /// <summary>Gets or sets the shared text; replaced once at startup before the window is created.</summary>
    internal static InstallerText Current { get; set; } = new();
}

/// <summary>
/// Markup for a fixed window text written in its Simplified Chinese source, such as
/// <c>Text="{l:T '重新检查'}"</c>. It binds to the shared text's revision, so the text follows a language change.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    /// <summary>Creates an empty extension for XAML.</summary>
    public TExtension()
    {
    }

    /// <summary>Creates the extension for one source text.</summary>
    /// <param name="source">The Simplified Chinese source text.</param>
    public TExtension(string source) => Source = source;

    /// <summary>Gets or sets the Simplified Chinese source text.</summary>
    [ConstructorArgument("source")]
    public string Source { get; set; } = string.Empty;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        Binding binding = new(nameof(InstallerText.Revision))
        {
            Source = InstallerLocalization.Current,
            Mode = BindingMode.OneWay,
            Converter = SourceTextConverter.Instance,
            ConverterParameter = Source,
        };
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class SourceTextConverter : IValueConverter
    {
        internal static readonly SourceTextConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            InstallerLocalization.Current[(string)parameter];

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

/// <summary>Translates a Simplified Chinese source text that arrives through a data binding.</summary>
public sealed class TranslateConverter : IValueConverter
{
    /// <summary>Gets the shared converter instance for <c>x:Static</c> use.</summary>
    public static TranslateConverter Instance { get; } = new();

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string source ? InstallerLocalization.Current[source] : value;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps the shared text's right-to-left flag to a WPF flow direction.</summary>
public sealed class RightToLeftConverter : IValueConverter
{
    /// <summary>Gets the shared converter instance for <c>x:Static</c> use.</summary>
    public static RightToLeftConverter Instance { get; } = new();

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
