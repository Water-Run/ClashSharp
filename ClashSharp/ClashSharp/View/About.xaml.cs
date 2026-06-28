/*
 * About Page
 * Hosts the application information view and delegates state to its view model
 *
 * @author: WaterRun
 * @file: View/About.xaml.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using ClashSharp.Service;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClashSharp.View;

/// <summary>Page for application identity, project links, and bundled mihomo core information.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="AboutViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Creates singleton-backed service adapters and starts mihomo status loading when loaded.
/// </remarks>
public sealed partial class About : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly AboutViewModel _viewModel;

    /// <summary>Initializes the about page and its view model.</summary>
    public About()
    {
        _viewModel = new(
            new DisplayPageLocalizationAdapter(LocalizationService.Instance),
            new AboutCoreAdapter(MihomoCoreService.Instance),
            new WindowsUriLauncher());

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
            CloseButtonText = LocalizationService.Instance.GetString("Command.Close"),
            XamlRoot = XamlRoot,
        };

        await dialog.ShowAsync();
    }

    /// <summary>Builds the proxy information dialog content.</summary>
    private StackPanel BuildProxyInformationPanel()
    {
        SettingsProxyInformation information = SettingsProxyInformationAdapter.CreateSnapshot();
        int mixedPort = AppSettingsService.Instance.MixedPort;
        string coreBinaryText = information.IsCoreBinaryAvailable
            ? information.CoreBinaryPath
            : LocalizationService.Instance.GetString("Settings.ProxyInformation.CoreBinary.Missing");

        StackPanel panel = new()
        {
            Spacing = 10,
            MinWidth = 360,
            MaxWidth = 640,
        };

        AddInformationText(panel, string.Format(
            LocalizationService.Instance.GetString("Settings.ProxyInformation.LocalEntry.Format"),
            mixedPort));
        AddInformationText(panel, string.Format(
            LocalizationService.Instance.GetString("Settings.ProxyInformation.CoreConfig.Format"),
            information.ConfigPath));
        AddInformationText(panel, string.Format(
            LocalizationService.Instance.GetString("Settings.ProxyInformation.CoreBinary.Format"),
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
