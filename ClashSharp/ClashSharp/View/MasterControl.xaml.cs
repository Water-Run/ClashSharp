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
using System.Linq;
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
    private const double RootScrollBarGutter = 18;
    private const double InfoTileEditorMinWidth = 420;
    private const double InfoTileEditorPreferredWidth = 620;
    private const double InfoTileEditorHorizontalChrome = 96;
    private const double InfoTileEditorMinListHeight = 260;
    private const double InfoTileEditorVerticalChrome = 260;

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
            new MasterControlTrayStatusAdapter(TrayStatusService.Instance),
            new MasterControlRuntimeAdapter(),
            ApplicationActionService.Instance,
            OnModeAppliedAsync);

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

    private static async Task OnModeAppliedAsync(ClashSharpMode mode)
    {
        await ApplicationActionService.Instance.PublishProxyModeAppliedAsync(mode, CancellationToken.None);
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
            case MasterControlTileAction.ExportConfiguration:
                Frame.Navigate(typeof(Settings));
                break;
            case MasterControlTileAction.ImportConfiguration:
                Frame.Navigate(typeof(Settings));
                break;
        }
    }

    /// <summary>Stops listening to view model events when the page leaves the visual tree.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.TileActionRequested -= OnTileActionRequested;
        Unloaded -= OnUnloaded;
    }

    /// <summary>Returns the window-level XAML root so dialogs center in the visible window.</summary>
    /// <returns>Window root when available; otherwise the page root.</returns>
    private XamlRoot GetDialogXamlRoot()
    {
        return App.MainWindow?.Content is FrameworkElement root && root.XamlRoot is not null
            ? root.XamlRoot
            : XamlRoot;
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
            XamlRoot = GetDialogXamlRoot(),
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
        XamlRoot xamlRoot = GetDialogXamlRoot();
        StartupGuideDialog dialog = new();
        await dialog.ShowCenteredAsync(xamlRoot);
    }

    /// <summary>Runs startup conflict detection and shows the shared result dialog.</summary>
    private async Task ShowStartupConflictDialogAsync()
    {
        IReadOnlyList<StartupConflictIssue> issues = StartupConflictDetectionService.Instance.CheckConflicts(AppSettingsService.Instance.MixedPort);
        await StartupConflictDialogPresenter.ShowAsync(GetDialogXamlRoot(), issues);
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
        XamlRoot dialogRoot = GetDialogXamlRoot();
        double editorWidth = CalculateInfoTilesEditorWidth(dialogRoot);
        SearchableOptionList optionList = new()
        {
            SearchPlaceholder = _viewModel.SearchInfoTilesPlaceholderText,
            AllowMultiple = true,
            MaxListHeight = CalculateInfoTilesEditorListHeight(dialogRoot),
            Width = editorWidth,
        };
        optionList.SetOptions(_viewModel.InfoTiles.Select(tile => new SearchableOptionItem(
            tile.Id,
            tile.Title,
            tile.TypeText,
            tile.Description,
            tile.Glyph,
            tile,
            tile.IsVisible)));

        StackPanel panel = new()
        {
            Spacing = 10,
            Width = editorWidth,
        };
        panel.Children.Add(optionList);

        ContentDialog dialog = new()
        {
            Title = _viewModel.EditInfoTilesText,
            Content = panel,
            PrimaryButtonText = LocalizationService.Instance.GetString("Command.Save"),
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = dialogRoot,
        };

        if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return;
        }

        foreach (SearchableOptionItem option in optionList.Options)
        {
            if (option.Payload is MasterControlInfoTileViewModel tile)
            {
                tile.IsVisible = option.IsChecked;
            }
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

    private static double CalculateInfoTilesEditorWidth(XamlRoot dialogRoot)
    {
        double availableWidth = dialogRoot.Size.Width > 0
            ? dialogRoot.Size.Width - InfoTileEditorHorizontalChrome
            : InfoTileEditorPreferredWidth;
        return Math.Clamp(availableWidth, InfoTileEditorMinWidth, InfoTileEditorPreferredWidth);
    }

    private static double CalculateInfoTilesEditorListHeight(XamlRoot dialogRoot)
    {
        double availableHeight = dialogRoot.Size.Height > 0
            ? dialogRoot.Size.Height - InfoTileEditorVerticalChrome
            : InfoTileEditorMinListHeight;
        return Math.Max(InfoTileEditorMinListHeight, availableHeight);
    }

    private void RootScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double horizontalPadding = RootScrollViewer.Padding.Left + RootScrollViewer.Padding.Right;
        ContentHost.Width = Math.Max(320, e.NewSize.Width - horizontalPadding - RootScrollBarGutter);
    }
}
