/*
 * Links Page
 * Provides subscription link management entry points
 *
 * @author: WaterRun
 * @file: View/Links.xaml.cs
 * @date: 2026-06-15
 */

using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using ClashSharp.Model;
using ClashSharp.Service;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for managing subscription links used to update configuration profiles.</summary>
/// <remarks>
/// Invariants: Visible text and link rows are loaded during construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Reads localized strings and link preview metadata during construction.
/// </remarks>
public sealed partial class Links : Page
{
    /// <summary>Initializes the links page, applies localized text, and loads subscription links.</summary>
    public Links()
    {
        InitializeComponent();
        RefreshLocalizedText();
        RefreshLinks();
    }

    /// <summary>Refreshes localized text owned by this page.</summary>
    private void RefreshLocalizedText()
    {
        LocalizationService localization = LocalizationService.Instance;
        PageTitleText.Text = localization.GetString("Nav.Links");
        DescriptionText.Text = localization.GetString("Page.Links.Description");
        AddLinkButton.Label = localization.GetString("Command.Add");
        CheckLinksButton.Label = localization.GetString("Command.Check");
        UpdateLinksButton.Label = localization.GetString("Command.Update");
    }

    /// <summary>Refreshes the visible subscription link rows.</summary>
    private void RefreshLinks()
    {
        SubscriptionLinksList.ItemsSource = ProfileCatalogService.Instance.GetSubscriptionLinks();
    }

    /// <summary>Handles add-link command activation.</summary>
    /// <param name="sender">The command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void AddLinkButton_Click(object sender, RoutedEventArgs e)
    {
        TextBox nameBox = new()
        {
            Header = "名称",
            Text = "新订阅",
        };
        TextBox uriBox = new()
        {
            Header = "订阅链接",
            PlaceholderText = "https://example.com/subscription.yaml",
        };
        StackPanel content = new()
        {
            Spacing = 12,
        };
        content.Children.Add(nameBox);
        content.Children.Add(uriBox);

        ContentDialog dialog = new()
        {
            Title = "添加订阅链接",
            Content = content,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            ProfileSubscriptionLink link = ProfileCatalogService.Instance.AddSubscriptionLink(nameBox.Text, uriBox.Text);
            LogStorageService.Instance.AppendLog("Info", "Links", $"Subscription link added: {link.Name}.", link.Uri);
            RefreshLinks();
        }
        catch (ArgumentException exception)
        {
            LogStorageService.Instance.AppendLog("Warning", "Links", "Subscription link could not be added.", exception.Message);
        }
    }

    /// <summary>Handles link-check command activation.</summary>
    /// <param name="sender">The command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void CheckLinksButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedLink(out ProfileSubscriptionLink link))
        {
            return;
        }

        try
        {
            string status = await ProfileCatalogService.Instance.CheckSubscriptionLinkAsync(link, CancellationToken.None);
            LogStorageService.Instance.AppendLog("Info", "Links", $"Subscription link check completed: {status}.", link.Name);
        }
        catch (Exception exception) when (exception is ArgumentException or HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            LogStorageService.Instance.AppendLog("Warning", "Links", "Subscription link check failed.", exception.Message);
        }

        RefreshLinks();
    }

    /// <summary>Handles link-update command activation.</summary>
    /// <param name="sender">The command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void UpdateLinksButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedLink(out ProfileSubscriptionLink link))
        {
            return;
        }

        try
        {
            ProfileImportResult result = await ProfileCatalogService.Instance.ImportSubscriptionLinkAsync(link, CancellationToken.None);
            LogStorageService.Instance.AppendLog("Info", "Links", $"Subscription profile imported: {result.ProfileName}.", result.ConfigPath);
        }
        catch (Exception exception) when (exception is ArgumentException or HttpRequestException or OperationCanceledException or InvalidOperationException or IOException)
        {
            LogStorageService.Instance.AppendLog("Warning", "Links", "Subscription profile import failed.", exception.Message);
        }

        RefreshLinks();
    }

    /// <summary>Gets the selected subscription link when one is selected.</summary>
    /// <param name="link">Selected subscription link when available.</param>
    /// <returns>True when a link is selected; otherwise false.</returns>
    private bool TryGetSelectedLink(out ProfileSubscriptionLink link)
    {
        if (SubscriptionLinksList.SelectedItem is ProfileSubscriptionLink selectedLink)
        {
            link = selectedLink;
            return true;
        }

        link = default;
        LogStorageService.Instance.AppendLog("Info", "Links", "No subscription link selected.", null);
        return false;
    }
}
