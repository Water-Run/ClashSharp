/*
 * About Page
 * Displays application, author, open-source, GitHub, and mihomo information
 *
 * @author: WaterRun
 * @file: View/About.xaml.cs
 * @date: 2026-06-17
 */

using System;
using System.IO;
using System.Threading;
using ClashSharp.Service;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace ClashSharp.View;

/// <summary>Page for application identity, project links, and bundled mihomo core information.</summary>
/// <remarks>
/// Invariants: Static project metadata is available immediately after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Opens external links on button clicks and probes the bundled mihomo binary version.
/// </remarks>
public sealed partial class About : Page
{
    /// <summary>Clash# repository URL.</summary>
    private static readonly Uri GitHubUri = new("https://github.com/Water-Run/ClashSharp");

    /// <summary>mihomo upstream repository URL.</summary>
    private static readonly Uri MihomoUri = new("https://github.com/MetaCubeX/mihomo");

    /// <summary>Initializes the about page and starts mihomo version probing.</summary>
    public About()
    {
        InitializeComponent();
        RefreshLocalizedText();
        _ = RefreshMihomoStatusAsync();
    }

    /// <summary>Refreshes localized text owned by this page.</summary>
    private void RefreshLocalizedText()
    {
        LocalizationService localization = LocalizationService.Instance;
        PageTitleText.Text = localization.GetString("Nav.About");
        DescriptionText.Text = localization.GetString("Page.About.Description");
        AppDescriptionText.Text = localization.GetString("About.App.Description");
        AuthorTitleText.Text = localization.GetString("About.Author.Title");
        AuthorValueText.Text = localization.GetString("About.Author.Value");
        OpenSourceTitleText.Text = localization.GetString("About.OpenSource.Title");
        OpenSourceDescriptionText.Text = localization.GetString("About.OpenSource.Description");
        GitHubTitleText.Text = localization.GetString("About.GitHub.Title");
        GitHubDescriptionText.Text = localization.GetString("About.GitHub.Description");
        GitHubButtonText.Text = localization.GetString("About.OpenGitHub");
        MihomoTitleText.Text = localization.GetString("About.Mihomo.Title");
        MihomoDescriptionText.Text = localization.GetString("About.Mihomo.Description");
        MihomoButtonText.Text = localization.GetString("About.OpenMihomo");
        MihomoStatusText.Text = localization.GetString("About.Mihomo.Loading");
    }

    /// <summary>Loads bundled mihomo availability and version information.</summary>
    private async System.Threading.Tasks.Task RefreshMihomoStatusAsync()
    {
        try
        {
            string versionText = await MihomoCoreService.Instance.GetVersionTextAsync(CancellationToken.None);
            MihomoStatusText.Text = string.Format(
                LocalizationService.Instance.GetString("About.Mihomo.Available.Format"),
                versionText);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException or OperationCanceledException)
        {
            MihomoStatusText.Text = LocalizationService.Instance.GetString("About.Mihomo.Unavailable");
        }
    }

    /// <summary>Opens the project repository.</summary>
    private async void GitHubButton_Click(object sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(GitHubUri);
    }

    /// <summary>Opens the upstream mihomo repository.</summary>
    private async void MihomoButton_Click(object sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(MihomoUri);
    }
}
