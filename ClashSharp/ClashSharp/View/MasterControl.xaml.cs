/*
 * Master Control Page
 * Hosts the master control view and delegates runtime state to its view model
 *
 * @author: WaterRun
 * @file: View/MasterControl.xaml.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for the master control panel displaying proxy status overview and primary takeover state actions.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="MasterControlViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Creates singleton-backed service adapters for the view model and starts status loading on page load.
/// </remarks>
public sealed partial class MasterControl : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly MasterControlViewModel _viewModel;

    /// <summary>Initializes the master control page and its view model.</summary>
    public MasterControl()
    {
        _viewModel = new(
            new MasterControlLocalizationAdapter(LocalizationService.Instance),
            new MasterControlCoreAdapter(MihomoCoreService.Instance),
            new MasterControlWindowsProxyAdapter(WindowsProxyService.Instance),
            new MasterControlSettingsAdapter(AppSettingsService.Instance),
            new MasterControlTakeoverAdapter(NetworkTakeoverService.Instance),
            new MasterControlLogAdapter(LogStorageService.Instance),
            new MasterControlTrayStatusAdapter(TrayStatusService.Instance));

        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    /// <summary>Starts runtime status loading when the page is loaded.</summary>
    /// <param name="sender">Loaded page instance. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.LoadCommand.Execute(null);
    }

    /// <summary>Opens the latency-test dialog and runs a timed progress workflow.</summary>
    private async void OpenLatencyDialogButton_Click(object sender, RoutedEventArgs e)
    {
        using CancellationTokenSource cancellation = new();
        ProgressBar timeoutBar = new()
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
        };
        TextBlock progressText = new()
        {
            Text = LocalizationService.Instance.GetString("Master.LatencyDialog.Running"),
            TextWrapping = TextWrapping.Wrap,
        };
        StackPanel content = BuildLatencyDialogContent(progressText, timeoutBar);

        ContentDialog dialog = new()
        {
            Title = LocalizationService.Instance.GetString("Master.LatencyDialog.Title"),
            Content = content,
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            XamlRoot = XamlRoot,
        };

        dialog.Closing += (_, _) => cancellation.Cancel();
        dialog.Opened += async (_, _) =>
        {
            await RunLatencyTestWithProgressAsync(progressText, timeoutBar, cancellation.Token);
            dialog.Hide();
        };

        await dialog.ShowAsync();
    }

    /// <summary>Builds latency-test dialog content using the RunOnce-style progress row and timeout bar.</summary>
    private static StackPanel BuildLatencyDialogContent(TextBlock progressText, ProgressBar timeoutBar)
    {
        StackPanel content = new()
        {
            Spacing = 14,
            MinWidth = 360,
        };

        StackPanel progressRow = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        progressRow.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
        progressRow.Children.Add(progressText);
        content.Children.Add(progressRow);
        content.Children.Add(timeoutBar);
        return content;
    }

    /// <summary>Runs proxy latency tests while updating a timed progress bar.</summary>
    private async Task RunLatencyTestWithProgressAsync(TextBlock progressText, ProgressBar timeoutBar, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProxyNode> nodes = ProxyNodeCatalogService.Instance.GetNodes();
        TimeSpan estimatedDuration = TimeSpan.FromSeconds(Math.Clamp(nodes.Count * 3, 4, 60));
        DateTime startedAt = DateTime.UtcNow;
        DispatcherTimer timer = new()
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        timer.Tick += (_, _) =>
        {
            double progress = Math.Min(95, (DateTime.UtcNow - startedAt).TotalMilliseconds / estimatedDuration.TotalMilliseconds * 100);
            timeoutBar.Value = progress;
        };
        timer.Start();

        try
        {
            IReadOnlyList<ProxyNode> testedNodes = await ProxyLatencyService.Instance.TestNodesAsync(nodes, cancellationToken);
            progressText.Text = string.Format(
                LocalizationService.Instance.GetString("Master.LatencyDialog.Completed.Format"),
                testedNodes.Count);
            timeoutBar.Value = 100;
            _viewModel.LoadCommand.Execute(null);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            progressText.Text = LocalizationService.Instance.GetString("Master.LatencyDialog.Failed");
        }
        finally
        {
            timer.Stop();
        }
    }

    /// <summary>Opens a small editor that toggles which information tiles are visible.</summary>
    private async void EditInfoTilesButton_Click(object sender, RoutedEventArgs e)
    {
        StackPanel panel = new()
        {
            Spacing = 8,
            MinWidth = 280,
        };

        foreach (MasterControlInfoTileViewModel tile in _viewModel.InfoTiles)
        {
            panel.Children.Add(new CheckBox
            {
                Content = tile.Title,
                IsChecked = tile.IsVisible,
                Tag = tile,
            });
        }

        ContentDialog dialog = new()
        {
            Title = _viewModel.EditInfoTilesText,
            Content = panel,
            PrimaryButtonText = LocalizationService.Instance.GetString("Command.Save"),
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return;
        }

        foreach (object child in panel.Children)
        {
            if (child is CheckBox { Tag: MasterControlInfoTileViewModel tile } checkBox)
            {
                tile.IsVisible = checkBox.IsChecked == true;
            }
        }
    }
}
