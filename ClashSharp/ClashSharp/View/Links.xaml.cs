/*
 * Links Page
 * Hosts subscription link management and delegates link state to its view model
 *
 * @author: WaterRun
 * @file: View/Links.xaml.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using ClashSharp.Service;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for managing subscription links used to update configuration profiles.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="LinksViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Creates input dialogs and delegates accepted input to the view model.
/// </remarks>
public sealed partial class Links : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly LinksViewModel _viewModel;

    /// <summary>Initializes the links page and its view model.</summary>
    public Links()
    {
        _viewModel = new(
            LocalizationService.Instance.GetString,
            ProfileCatalogService.Instance,
            LogStorageService.Instance);

        InitializeComponent();
        DataContext = _viewModel;
    }

    /// <summary>Shows the add-link dialog and delegates accepted input to the view model.</summary>
    /// <param name="sender">Command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void AddLinkButton_Click(object sender, RoutedEventArgs e)
    {
        TextBox nameBox = new()
        {
            Header = LocalizationService.Instance.GetString("Links.Dialog.Name"),
            Text = LocalizationService.Instance.GetString("Links.Dialog.DefaultName"),
        };
        TextBox uriBox = new()
        {
            Header = LocalizationService.Instance.GetString("Links.Dialog.Uri"),
            PlaceholderText = "https://example.com/subscription.yaml",
        };
        StackPanel content = new()
        {
            Spacing = 12,
        };
        content.Children.Add(nameBox);
        content.Children.Add(uriBox);

        ThemedContentDialog dialog = new()
        {
            Title = LocalizationService.Instance.GetString("Links.Dialog.AddTitle"),
            Content = content,
            PrimaryButtonText = LocalizationService.Instance.GetString("Command.Add"),
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            XamlRoot = XamlRoot,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _viewModel.AddLink(nameBox.Text, uriBox.Text);
        }
    }
}
