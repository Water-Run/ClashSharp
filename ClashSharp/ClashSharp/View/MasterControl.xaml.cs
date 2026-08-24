using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Components;
using ClashSharp.Model;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Presentation.Layout;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;

namespace ClashSharp.View;

/// <summary>Page for the master control panel displaying proxy status overview and primary takeover state actions.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="MasterControlViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Creates singleton-backed service adapters for the view model and starts status loading on page load.
/// </remarks>
public sealed partial class MasterControl : Page
{
    private const int HeroStatusColumnCount = 2;
    private const double HeroStatusHorizontalMargin = 10;
    private const double MinHeroStatusItemWidth = 120;
    private const double PreferredHeroStatusItemWidth = 220;
    private const double HeroStatusFlyoutMinListHeight = 180;
    private const double HeroStatusFlyoutMaxListHeight = 420;
    private const double HeroStatusFlyoutVerticalChrome = 220;
    private const double MinInfoTileWidth = 220;
    private const double PreferredInfoTileWidth = 280;
    private const double InfoTileHorizontalMargin = 10;
    private const int MaxInfoTileColumns = 4;
    private const double InfoTileEditorMinWidth = 420;
    private const double InfoTileEditorPreferredWidth = 620;
    private const double InfoTileEditorHorizontalChrome = 96;
    private const double InfoTileEditorMinListHeight = 260;
    private const double InfoTileEditorVerticalChrome = 260;

    /// <summary>Bindable view model for this page.</summary>
    private readonly MasterControlViewModel _viewModel;
    private readonly Func<string, string> _getString;
    private readonly IApplicationErrorSink _errorSink;
    private readonly IStartupGuidePresenter _startupGuide;
    private readonly Func<XamlRoot, CancellationToken, Task> _showStartupConflicts;
    private readonly Func<IReadOnlyList<ProxyNode>> _getProxyNodes;
    private readonly Func<IReadOnlyList<ProxyNode>, CancellationToken, Task<IReadOnlyList<ProxyNode>>> _testProxyLatencyAsync;
    private readonly Action _openSettings;
    private readonly MasterHeroStatusSelectionGate _heroStatusSelection = new();

    /// <summary>Owns cancellable work for the current visit to this page.</summary>
    private CancellationTokenSource? _pageLifetime;

    /// <summary>Tracks the latest load so unload and a later reload can observe its completion.</summary>
    private Task _loadTask = Task.CompletedTask;

    private double _heroStatusItemWidth = PreferredHeroStatusItemWidth;
    private double _infoTileItemWidth = PreferredInfoTileWidth;

    internal MasterControl(MasterControlPageDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel
            ?? throw new ArgumentException("A master-control view model is required.", nameof(dependencies));
        _getString = dependencies.GetString
            ?? throw new ArgumentException("A localization function is required.", nameof(dependencies));
        _errorSink = dependencies.ErrorSink
            ?? throw new ArgumentException("An application error sink is required.", nameof(dependencies));
        _startupGuide = dependencies.StartupGuide
            ?? throw new ArgumentException("A startup-guide presenter is required.", nameof(dependencies));
        _showStartupConflicts = dependencies.ShowStartupConflicts
            ?? throw new ArgumentException("A startup-conflict presenter is required.", nameof(dependencies));
        _getProxyNodes = dependencies.GetProxyNodes
            ?? throw new ArgumentException("A proxy-node function is required.", nameof(dependencies));
        _testProxyLatencyAsync = dependencies.TestProxyLatencyAsync
            ?? throw new ArgumentException("A latency-test function is required.", nameof(dependencies));
        _openSettings = dependencies.OpenSettings
            ?? throw new ArgumentException("A settings navigation function is required.", nameof(dependencies));
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.TileActionRequested += OnTileActionRequested;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Starts runtime status loading when the page is loaded.</summary>
    /// <param name="sender">Loaded page instance. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_pageLifetime is not null)
        {
            return;
        }

        CancellationTokenSource lifetime = new();
        _pageLifetime = lifetime;
        await LoadForCurrentPageAsync();
    }

    private async Task LoadForCurrentPageAsync()
    {
        CancellationTokenSource? lifetime = _pageLifetime;
        if (lifetime is null)
        {
            return;
        }

        Task previousLoad = _loadTask;
        await previousLoad;
        if (!ReferenceEquals(_pageLifetime, lifetime))
        {
            return;
        }

        if (_viewModel.LoadCommand.IsRunning)
        {
            await _loadTask;
            return;
        }

        _loadTask = _viewModel.LoadCommand.ExecuteObservedAsync(null, lifetime.Token);
        await _loadTask;
    }

    /// <summary>Opens the latency-test dialog and runs a timed progress workflow.</summary>
    private async void OpenLatencyDialogButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowLatencyDialogAsync();
    }

    private void SetHeroStatusDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor)
        {
            return;
        }

        Flyout flyout = new()
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
        };
        flyout.Content = BuildHeroStatusFlyoutContent(flyout);
        flyout.ShowAt(anchor);
    }

    private void HeroStatusSlotComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: int slotIndex, SelectedValue: MasterHeroStatusItemKind kind })
        {
            return;
        }

        _heroStatusSelection.TryApplySelection(
            slotIndex,
            kind,
            _viewModel.SetHeroStatusSlot);
    }

    private StackPanel BuildHeroStatusFlyoutContent(Flyout flyout)
    {
        StackPanel root = new()
        {
            Width = 320,
            Spacing = 10,
        };

        StackPanel slotHost = new()
        {
            Spacing = 8,
        };
        foreach (MasterHeroStatusSlotViewModel slot in _viewModel.HeroStatusSlots)
        {
            Grid row = new()
            {
                ColumnSpacing = 12,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock title = new()
            {
                Text = slot.Title,
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
            };
            row.Children.Add(title);

            ComboBox comboBox = new()
            {
                MinWidth = 180,
                ItemsSource = slot.Options,
                ItemTemplate = (DataTemplate)Resources["MasterHeroStatusOptionTemplate"],
                SelectedValuePath = nameof(MasterHeroStatusOptionViewModel.Kind),
                Tag = slot.Index,
            };
            comboBox.SetBinding(Selector.SelectedValueProperty, new Binding
            {
                Source = slot,
                Path = new PropertyPath(nameof(MasterHeroStatusSlotViewModel.SelectedKind)),
                Mode = BindingMode.TwoWay,
            });
            comboBox.SelectionChanged += HeroStatusSlotComboBox_SelectionChanged;
            Grid.SetColumn(comboBox, 1);
            row.Children.Add(comboBox);

            slotHost.Children.Add(row);
        }

        ScrollViewer slotScroller = new()
        {
            Content = slotHost,
            MaxHeight = CalculateHeroStatusFlyoutListHeight(GetDialogXamlRoot()),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        root.Children.Add(slotScroller);
        HyperlinkButton restoreDefaultLink = new()
        {
            Content = _viewModel.RestoreDefaultHeroStatusLayoutText,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        restoreDefaultLink.Click += (_, _) =>
        {
            _heroStatusSelection.RunProgrammaticUpdate(() =>
            {
                _viewModel.ResetHeroStatusLayout();
                flyout.Content = BuildHeroStatusFlyoutContent(flyout);
            });
        };
        root.Children.Add(restoreDefaultLink);

        return root;
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
                _openSettings();
                break;
            case MasterControlTileAction.ImportConfiguration:
                _openSettings();
                break;
        }
    }

    /// <summary>Cancels and observes current-page loading when the page leaves the visual tree.</summary>
    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancellationTokenSource? lifetime = _pageLifetime;
        if (lifetime is null)
        {
            return;
        }

        _pageLifetime = null;
        lifetime.Cancel();
        await _loadTask;
        lifetime.Dispose();
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
            Text = _getString("Master.LatencyDialog.Running"),
            TextWrapping = TextWrapping.Wrap,
        };
        StackPanel content = BuildLatencyDialogContent(progressText, timeoutBar);

        ThemedContentDialog dialog = new()
        {
            Title = _getString("Master.LatencyDialog.Title"),
            Content = content,
            CloseButtonText = _getString("Command.Cancel"),
            XamlRoot = GetDialogXamlRoot(),
        };

        dialog.Closing += (_, _) => cancellation.Cancel();
        dialog.Opened += OnDialogOpened;

        await dialog.ShowManagedAsync();

        async void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            try
            {
                await RunLatencyTestWithProgressAsync(progressText, timeoutBar, cancellation.Token);
                if (!cancellation.IsCancellationRequested)
                {
                    sender.Hide();
                }
            }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                progressText.Text = _viewModel.LatencyTestFailedText;
            }
        }
    }

    /// <summary>Shows the startup prompt dialog from a functional tile.</summary>
    private async Task ShowStartupPromptDialogAsync()
    {
        CancellationToken cancellationToken = _pageLifetime?.Token
            ?? new CancellationToken(canceled: true);
        await _startupGuide.ShowAsync(GetDialogXamlRoot(), cancellationToken);
    }

    /// <summary>Runs startup conflict detection and shows the shared result dialog.</summary>
    private async Task ShowStartupConflictDialogAsync()
    {
        CancellationToken cancellationToken = _pageLifetime?.Token
            ?? new CancellationToken(canceled: true);
        try
        {
            await _showStartupConflicts(GetDialogXamlRoot(), cancellationToken);
        }
        catch (OperationCanceledException exception) when (
            ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
        }
        catch (Exception exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            await ReportStartupConflictFailureAsync(exception, cancellationToken);
        }
    }

    private async Task ReportStartupConflictFailureAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await _errorSink.ReportAsync(
                new ApplicationError("master-startup-conflict-detection", exception),
                cancellationToken);
        }
        catch (Exception sinkException) when (
            !ExceptionGraphClassifier.IsProcessFatal(sinkException))
        {
        }
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
        IReadOnlyList<ProxyNode> nodes = _getProxyNodes();
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
            IReadOnlyList<ProxyNode> testedNodes = await _testProxyLatencyAsync(nodes, cancellationToken);
            progressText.Text = string.Format(
                CultureInfo.CurrentCulture,
                _getString("Master.LatencyDialog.Completed.Format"),
                testedNodes.Count);
            timeoutBar.Value = 100;
            await LoadForCurrentPageAsync();
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            progressText.Text = _viewModel.LatencyTestFailedText;
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
        panel.Children.Add(new TextBlock
        {
            Text = _viewModel.InfoTileSelectionDescriptionText,
            Opacity = 0.72,
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        panel.Children.Add(optionList);

        ThemedContentDialog dialog = new()
        {
            Title = _viewModel.EditInfoTilesText,
            Content = panel,
            PrimaryButtonText = _getString("Command.Save"),
            CloseButtonText = _getString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = dialogRoot,
        };

        if (await dialog.ShowManagedAsync() is not ContentDialogResult.Primary)
        {
            return;
        }

        HashSet<string> selectedIds = optionList.SelectedOptions
            .Select(static option => option.Id)
            .ToHashSet(StringComparer.Ordinal);
        List<string> orderedSelectedIds = _viewModel.VisibleInfoTiles
            .Select(static tile => tile.Id)
            .Where(selectedIds.Contains)
            .ToList();
        HashSet<string> orderedIds = orderedSelectedIds.ToHashSet(StringComparer.Ordinal);
        foreach (SearchableOptionItem option in optionList.Options)
        {
            if (selectedIds.Contains(option.Id) && orderedIds.Add(option.Id))
            {
                orderedSelectedIds.Add(option.Id);
            }
        }

        _viewModel.SetVisibleInfoTileIds(orderedSelectedIds);
    }

    private void HeroStatusItemGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateHeroStatusItemWidths(e.NewSize.Width);
    }

    private void HeroStatusItemGrid_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        ApplyHeroStatusItemWidth(args.ItemContainer);
    }

    private void UpdateHeroStatusItemWidths(double availableWidth)
    {
        if (availableWidth <= 0)
        {
            return;
        }

        _heroStatusItemWidth = Math.Max(
            MinHeroStatusItemWidth,
            CalculateHeroStatusItemWidth(availableWidth));
        foreach (MasterHeroStatusItemViewModel itemViewModel in _viewModel.HeroStatusItems)
        {
            if (HeroStatusItemGrid.ContainerFromItem(itemViewModel) is FrameworkElement item)
            {
                ApplyHeroStatusItemWidth(item);
            }
        }
    }

    private void ApplyHeroStatusItemWidth(FrameworkElement item)
    {
        item.Width = _heroStatusItemWidth;
    }

    private static double CalculateHeroStatusItemWidth(double availableWidth)
    {
        return Math.Floor(
            (availableWidth / HeroStatusColumnCount) - HeroStatusHorizontalMargin);
    }

    private void InfoTileGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateInfoTileWidths(e.NewSize.Width);
    }

    private void InfoTileGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        ApplyInfoTileContainerWidth(args.ItemContainer);
    }

    private void InfoTileGrid_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        _viewModel.PersistInfoTileOrder();
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
        foreach (MasterControlInfoTileViewModel tile in _viewModel.VisibleInfoTiles)
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

    private static double CalculateHeroStatusFlyoutListHeight(XamlRoot dialogRoot)
    {
        double availableHeight = dialogRoot.Size.Height > 0
            ? dialogRoot.Size.Height - HeroStatusFlyoutVerticalChrome
            : HeroStatusFlyoutMaxListHeight;
        return Math.Clamp(
            availableHeight,
            HeroStatusFlyoutMinListHeight,
            HeroStatusFlyoutMaxListHeight);
    }

    private void ContentHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateMasterLayout(e.NewSize.Width);
    }

    private void UpdateMasterLayout(double contentWidth)
    {
        MasterControlLayout layout = MasterControlLayoutPolicy.Resolve(contentWidth);
        ModeColumn.Width = layout.IsSideBySide
            ? new GridLength(layout.ModeColumnWidth)
            : new GridLength(0);

        Grid.SetRow(HeroStatusCard, 0);
        Grid.SetColumn(HeroStatusCard, 0);
        Grid.SetColumnSpan(HeroStatusCard, layout.IsSideBySide ? 1 : 2);

        Grid.SetRow(ModeButtonGrid, layout.IsSideBySide ? 0 : 1);
        Grid.SetColumn(ModeButtonGrid, layout.IsSideBySide ? 1 : 0);
        Grid.SetColumnSpan(ModeButtonGrid, layout.IsSideBySide ? 1 : 2);
        ModeButtonGrid.Margin = layout.IsSideBySide
            ? new Thickness(0)
            : new Thickness(0, 2, 0, 0);
        ArrangeModeButtons(layout.IsSideBySide);
    }

    private void ArrangeModeButtons(bool isSideBySide)
    {
        ModeButtonGrid.RowSpacing = isSideBySide ? 10 : 0;
        Thickness narrowSecondRowMargin = isSideBySide
            ? new Thickness(0)
            : new Thickness(0, 10, 0, 0);
        DisabledModeButton.Margin = new Thickness(0);
        StandbyModeButton.Margin = new Thickness(0);
        RuleTakeoverModeButton.Margin = narrowSecondRowMargin;
        FullTakeoverModeButton.Margin = narrowSecondRowMargin;

        if (isSideBySide)
        {
            SetModeButtonLayout(DisabledModeButton, row: 0, column: 0, columnSpan: 2);
            SetModeButtonLayout(StandbyModeButton, row: 1, column: 0, columnSpan: 2);
            SetModeButtonLayout(RuleTakeoverModeButton, row: 2, column: 0, columnSpan: 2);
            SetModeButtonLayout(FullTakeoverModeButton, row: 3, column: 0, columnSpan: 2);
            return;
        }

        SetModeButtonLayout(DisabledModeButton, row: 0, column: 0, columnSpan: 1);
        SetModeButtonLayout(StandbyModeButton, row: 0, column: 1, columnSpan: 1);
        SetModeButtonLayout(RuleTakeoverModeButton, row: 1, column: 0, columnSpan: 1);
        SetModeButtonLayout(FullTakeoverModeButton, row: 1, column: 1, columnSpan: 1);
    }

    private static void SetModeButtonLayout(
        FrameworkElement button,
        int row,
        int column,
        int columnSpan)
    {
        Grid.SetRow(button, row);
        Grid.SetColumn(button, column);
        Grid.SetColumnSpan(button, columnSpan);
    }
}
