using System;
using System.Globalization;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClashSharp.View;

/// <summary>Page for application identity, project links, and bundled mihomo core information.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="AboutViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Starts mihomo status loading when loaded and presents platform dialogs.
/// </remarks>
public sealed partial class About : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly AboutViewModel _viewModel;

    private readonly Func<string, string> _getString;

    private readonly Func<AboutPageComposition.ProxyInformation> _readProxyInformation;

    /// <summary>Initializes the about page and its view model.</summary>
    public About()
        : this(AboutPageComposition.Create())
    {
    }

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal About(AboutPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        _getString = dependencies.GetString;
        _readProxyInformation = dependencies.ReadProxyInformation;
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    /// <summary>Starts mihomo status loading when the page enters the visual tree.</summary>
    /// <param name="sender">Loaded page instance. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.LoadCommand.Execute(null);
    }

    /// <summary>Opens local proxy and core path information in a dialog.</summary>
    private async void OpenProxyInformationButton_Click(object sender, RoutedEventArgs e)
    {
        ThemedContentDialog dialog = new()
        {
            Title = _viewModel.ProxyInformationTitleText,
            Content = BuildProxyInformationPanel(),
            CloseButtonText = _getString("Command.Close"),
            XamlRoot = XamlRoot,
        };

        await dialog.ShowManagedAsync();
    }

    /// <summary>Builds the proxy information dialog content.</summary>
    private StackPanel BuildProxyInformationPanel()
    {
        AboutPageComposition.ProxyInformation information = _readProxyInformation();
        string coreBinaryText = information.IsCoreBinaryAvailable
            ? information.CoreBinaryPath
            : _getString("Settings.ProxyInformation.CoreBinary.Missing");

        StackPanel panel = new()
        {
            Spacing = 10,
            MinWidth = 360,
            MaxWidth = 640,
        };

        AddInformationText(panel, string.Format(
            CultureInfo.CurrentCulture,
            _getString("Settings.ProxyInformation.LocalEntry.Format"),
            information.MixedPort));
        AddInformationText(panel, string.Format(
            CultureInfo.CurrentCulture,
            _getString("Settings.ProxyInformation.CoreConfig.Format"),
            information.ConfigPath));
        AddInformationText(panel, string.Format(
            CultureInfo.CurrentCulture,
            _getString("Settings.ProxyInformation.CoreBinary.Format"),
            coreBinaryText));

        return panel;
    }

    /// <summary>Adds one wrapped information line to a panel.</summary>
    private static void AddInformationText(StackPanel panel, string text)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
    }
}
