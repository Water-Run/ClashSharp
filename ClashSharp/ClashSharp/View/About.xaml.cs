using System;
using System.Globalization;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

    private readonly PageLoadSession _loadSession = new();

    private int _cardColumnCount = 1;

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
        Unloaded += OnUnloaded;
    }

    /// <summary>Reflows cards using the page's available width, including navigation and display scaling.</summary>
    private void AboutCards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        int columns = e.NewSize.Width >= 760 ? 2 : 1;
        if (columns == _cardColumnCount)
        {
            return;
        }

        _cardColumnCount = columns;
        AboutCards.ColumnDefinitions[1].Width = columns == 2
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        AboutCards.ColumnSpacing = columns == 2 ? 16 : 0;
        AboutCards.RowDefinitions.Clear();
        for (int row = 0; row < 4 / columns; row++)
        {
            AboutCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        Grid[] cards = [GitHubCard, UpdateCard, MihomoCard, ProxyInformationCard];
        for (int index = 0; index < cards.Length; index++)
        {
            Grid.SetRow(cards[index], index / columns);
            Grid.SetColumn(cards[index], index % columns);
        }
    }

    /// <summary>Starts mihomo status loading when the page enters the visual tree.</summary>
    /// <param name="sender">Loaded page instance. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _loadSession.RunAsync(_viewModel.LoadAsync);
    }

    /// <summary>Cancels update and core probes when the page leaves the visual tree.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loadSession.Cancel();
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
            Style = (Style)Application.Current.Resources["ClashSecondaryBodyTextBlockStyle"],
            IsTextSelectionEnabled = true,
        });
    }
}
