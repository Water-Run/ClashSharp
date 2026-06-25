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
using ClashSharp.Components;
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
    private const double MinInfoTileWidth = 220;
    private const double PreferredInfoTileWidth = 280;
    private const double InfoTileHorizontalMargin = 10;
    private const int MaxInfoTileColumns = 4;

    /// <summary>Bindable view model for this page.</summary>
    private readonly MasterControlViewModel _viewModel;

    private double _infoTileItemWidth = PreferredInfoTileWidth;

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
        _viewModel.TileActionRequested += OnTileActionRequested;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
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
        await ShowLatencyDialogAsync();
    }

    /// <summary>Handles functional information-tile actions requested by the view model.</summary>
    private async void OnTileActionRequested(object? sender, MasterControlTileAction action)
    {
        switch (action)
        {
            case MasterControlTileAction.ShowStartupPrompt:
                await ShowStartupPromptDialogAsync();
                break;
            case MasterControlTileAction.CheckStartupConflicts:
                await ShowStartupConflictDialogAsync();
                break;
            case MasterControlTileAction.RunLatencyTest:
                await ShowLatencyDialogAsync();
                break;
        }
    }

    /// <summary>Stops listening to view model events when the page leaves the visual tree.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.TileActionRequested -= OnTileActionRequested;
        Unloaded -= OnUnloaded;
    }

    /// <summary>Opens the latency-test dialog and runs a timed progress workflow.</summary>
    private async Task ShowLatencyDialogAsync()
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

    /// <summary>Shows the startup prompt dialog from a functional tile.</summary>
    private async Task ShowStartupPromptDialogAsync()
    {
        StartupGuideDialog dialog = new()
        {
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    /// <summary>Runs startup conflict detection and shows the shared result dialog.</summary>
    private async Task ShowStartupConflictDialogAsync()
    {
        IReadOnlyList<StartupConflictIssue> issues = StartupConflictDetectionService.Instance.CheckConflicts(AppSettingsService.Instance.MixedPort);
        await StartupConflictDialogPresenter.ShowAsync(XamlRoot, issues);
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
        await ShowInfoTilesEditorAsync();
    }

    /// <summary>Opens a small editor that toggles which information tiles are visible.</summary>
    private async Task ShowInfoTilesEditorAsync()
    {
        TextBox searchBox = new()
        {
            Name = "InfoTileSearchBox",
            PlaceholderText = _viewModel.SearchInfoTilesPlaceholderText,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        StackPanel listPanel = new()
        {
            Spacing = 8,
        };
        searchBox.TextChanged += (_, _) => FilterInfoTileEditorRows(searchBox.Text, listPanel);

        StackPanel panel = new()
        {
            Spacing = 10,
            MinWidth = 420,
            MaxWidth = 620,
        };
        panel.Children.Add(searchBox);

        foreach (MasterControlInfoTileViewModel tile in _viewModel.InfoTiles)
        {
            listPanel.Children.Add(BuildInfoTileEditorRow(tile));
        }
        panel.Children.Add(new ScrollViewer
        {
            Content = listPanel,
            MaxHeight = Math.Max(260, XamlRoot.Size.Height - 260),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

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

        foreach (object child in listPanel.Children)
        {
            if (child is DialogOptionRow { Tag: MasterControlInfoTileViewModel tile } optionRow)
            {
                tile.IsVisible = optionRow.IsChecked == true;
            }
        }
    }

    /// <summary>Builds one tile-visibility editor row with icon and localized tile name.</summary>
    private static DialogOptionRow BuildInfoTileEditorRow(MasterControlInfoTileViewModel tile)
    {
        return new DialogOptionRow
        {
            Title = tile.Title,
            Metadata = tile.TypeText,
            Description = tile.Description,
            Glyph = tile.Glyph,
            IsChecked = tile.IsVisible,
            Tag = tile,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    private static void FilterInfoTileEditorRows(string query, StackPanel listPanel)
    {
        string normalizedQuery = query.Trim();
        foreach (object child in listPanel.Children)
        {
            if (child is not DialogOptionRow { Tag: MasterControlInfoTileViewModel tile } optionRow)
            {
                continue;
            }

            bool isVisible = normalizedQuery.Length == 0
                || tile.Title.Contains(normalizedQuery, StringComparison.CurrentCultureIgnoreCase)
                || tile.TypeText.Contains(normalizedQuery, StringComparison.CurrentCultureIgnoreCase)
                || tile.Description.Contains(normalizedQuery, StringComparison.CurrentCultureIgnoreCase);
            optionRow.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void InfoTileGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateInfoTileWidths(e.NewSize.Width);
    }

    private void InfoTileGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        ApplyInfoTileContainerWidth(args.ItemContainer);
    }

    private void UpdateInfoTileWidths(double availableWidth)
    {
        if (availableWidth <= 0)
        {
            return;
        }

        int columns = Math.Clamp((int)Math.Round(availableWidth / PreferredInfoTileWidth), 1, MaxInfoTileColumns);
        while (columns > 1 && CalculateInfoTileWidth(availableWidth, columns) < MinInfoTileWidth)
        {
            columns--;
        }

        _infoTileItemWidth = Math.Max(MinInfoTileWidth, CalculateInfoTileWidth(availableWidth, columns));
        foreach (MasterControlInfoTileViewModel tile in _viewModel.InfoTiles)
        {
            if (InfoTileGrid.ContainerFromItem(tile) is FrameworkElement item)
            {
                ApplyInfoTileContainerWidth(item);
            }
        }
    }

    private void ApplyInfoTileContainerWidth(FrameworkElement item)
    {
        item.Width = _infoTileItemWidth;
    }

    private static double CalculateInfoTileWidth(double availableWidth, int columns)
    {
        return Math.Floor((availableWidth / columns) - InfoTileHorizontalMargin);
    }
}
