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
using ClashSharp.Presentation.Lifecycle;
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
    private const double InfoTileEditorMinListHeight = 80;
    private const double InfoTileEditorMaxListHeight = 420;
    private const double InfoTileEditorMaxDialogHeight = 756;
    private const double InfoTileEditorVerticalChrome = 470;

    /// <summary>Bindable view model for this page.</summary>
    private readonly MasterControlViewModel _viewModel;
    private readonly Func<string, string> _getString;
    private readonly IApplicationErrorSink _errorSink;
    private readonly MasterControlTileActionSession _tileActions;
    private readonly DataPackageDialogPresenter _dataPackages;
    private readonly IStartupGuidePresenter _startupGuide;
    private readonly Func<XamlRoot, CancellationToken, Task> _showStartupConflicts;
    private readonly Func<IReadOnlyList<ProxyNode>> _getProxyNodes;
    private readonly Func<IReadOnlyList<ProxyNode>, CancellationToken, Task<IReadOnlyList<ProxyNode>>> _testProxyLatencyAsync;
    private readonly Action _openSettings;
    private readonly Func<string, string> _filterDisplayText;
    private readonly MasterHeroStatusSelectionGate _heroStatusSelection = new();

    /// <summary>Owns cancellable work for the current visit to this page.</summary>
    private CancellationTokenSource? _pageLifetime;

    /// <summary>Tracks the latest load so unload and a later reload can observe its completion.</summary>
    private Task _loadTask = Task.CompletedTask;

    private readonly PageLoadSession _refreshSession;
    private readonly PageLoadSession _loadSession;
    private readonly PageDataChangeSession _dataChanges;
    private readonly Func<Guid?> _getDataGenerationId;
    private Guid? _dataGenerationId;
    private bool _hasDataGeneration;
    private bool _dataReady;

    private double _heroStatusItemWidth = PreferredHeroStatusItemWidth;
    private double _infoTileItemWidth = PreferredInfoTileWidth;

    internal MasterControl(MasterControlPageDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel
            ?? throw new ArgumentException("A master-control view model is required.", nameof(dependencies));
        _getString = dependencies.GetString
            ?? throw new ArgumentException("A localization function is required.", nameof(dependencies));
        _filterDisplayText = dependencies.FilterDisplayText
            ?? throw new ArgumentException("A display text filter is required.", nameof(dependencies));
        _errorSink = dependencies.ErrorSink
            ?? throw new ArgumentException("An application error sink is required.", nameof(dependencies));
        _refreshSession = new PageLoadSession(_errorSink, "master-live-refresh");
        _tileActions = dependencies.TileActions
            ?? throw new ArgumentException("A tile action session is required.", nameof(dependencies));
        _loadSession = new(_errorSink, "master-data-load");
        _getDataGenerationId = dependencies.GetDataGenerationId
            ?? throw new ArgumentException("A data identity reader is required.", nameof(dependencies));
        _dataChanges = new(dependencies.SubscribeToDataChanges, action => DispatcherQueue.TryEnqueue(() => action()),
            [_loadSession.Cancel, _tileActions.Cancel], ReloadChangedDataAsync, _errorSink, "master-data-change");
        _dataPackages = dependencies.DataPackages
            ?? throw new ArgumentException("A data-package presenter is required.", nameof(dependencies));
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
        await Task.WhenAll(_tileActions.DrainAsync(), _refreshSession.DrainAsync());
        await _dataChanges.DrainAsync();
        if (!ReferenceEquals(_pageLifetime, lifetime))
        {
            return;
        }

        _dataChanges.Start();
        _tileActions.Activate(PresentTileActionAsync, () => _dataReady && !_dataChanges.IsInvalidated);
        await LoadForCurrentPageAsync();
        if (ReferenceEquals(_pageLifetime, lifetime))
        {
            await _refreshSession.RunAsync(async cancellationToken =>
            {
                using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    await LoadForCurrentPageAsync();
                }
            }, cancellationToken: lifetime.Token);
        }
    }

    private async Task LoadForCurrentPageAsync()
    {
        CancellationTokenSource? lifetime = _pageLifetime;
        if (lifetime is null || _dataChanges.IsInvalidated)
        {
            return;
        }

        Task previousLoad = _loadTask;
        await previousLoad;
        if (!ReferenceEquals(_pageLifetime, lifetime) || _dataChanges.IsInvalidated)
        {
            return;
        }

        if (_viewModel.LoadCommand.IsRunning)
        {
            await _loadTask;
            return;
        }

        _loadTask = _loadSession.RunAsync(async token =>
        {
            Guid? dataGenerationId = _getDataGenerationId();
            if (!_hasDataGeneration || _dataGenerationId != dataGenerationId)
            {
                _viewModel.InvalidateForDataChange();
                _dataGenerationId = dataGenerationId;
                _hasDataGeneration = true;
            }
            await _viewModel.LoadCommand.ExecuteObservedAsync(null, token);
            token.ThrowIfCancellationRequested();
            _dataReady = true;
        }, cancellationToken: lifetime.Token);
        await _loadTask;
    }

    private async Task ReloadChangedDataAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_tileActions.DrainAsync(), _loadSession.DrainAsync());
        cancellationToken.ThrowIfCancellationRequested();
        _dataReady = false;
        _dataGenerationId = _getDataGenerationId();
        _hasDataGeneration = true;
        _viewModel.InvalidateForDataChange();
        await _viewModel.LoadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _dataReady = true;
    }

    /// <summary>Opens the latency-test dialog and runs a timed progress workflow.</summary>
    private async void OpenLatencyDialogButton_Click(object sender, RoutedEventArgs e)
    {
        await _tileActions.ExecuteAsync(MasterControlTileAction.RunLatencyTest, CancellationToken.None);
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

    private async void HeroStatusSlotComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: int slotIndex, SelectedItem: MasterHeroStatusOptionViewModel option } comboBox)
        {
            return;
        }

        bool applied = await _heroStatusSelection.TryApplySelectionAsync(
            slotIndex,
            option.Kind,
            (index, kind) => _tileActions.RunAsync(token => _viewModel.SetHeroStatusSlotAsync(index, kind, token)));
        if (applied && slotIndex < _viewModel.HeroStatusSlots.Count)
        {
            // Native selection can move before a write succeeds; restore the committed one-way binding.
            _heroStatusSelection.RunProgrammaticUpdate(() => BindHeroStatusSelection(comboBox, _viewModel.HeroStatusSlots[slotIndex]));
        }
    }

    private static void BindHeroStatusSelection(ComboBox comboBox, MasterHeroStatusSlotViewModel slot)
    {
        comboBox.ClearValue(Selector.SelectedIndexProperty);
        comboBox.SetBinding(Selector.SelectedIndexProperty, new Binding
        {
            Source = slot,
            Path = new PropertyPath(nameof(MasterHeroStatusSlotViewModel.SelectedOptionIndex)),
            Mode = BindingMode.OneWay,
        });
    }

    private void BindLayoutEditing(Control control)
    {
        control.SetBinding(Control.IsEnabledProperty, new Binding
        {
            Source = _viewModel,
            Path = new PropertyPath(nameof(MasterControlViewModel.CanEditTileLayout)),
            Mode = BindingMode.OneWay,
        });
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
                Tag = slot.Index,
            };
            BindLayoutEditing(comboBox);
            BindHeroStatusSelection(comboBox, slot);
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
        BindLayoutEditing(restoreDefaultLink);
        restoreDefaultLink.Click += async (_, _) =>
        {
            CancellationTokenSource? lifetime = _pageLifetime;
            await _heroStatusSelection.RunProgrammaticUpdateAsync(async () =>
            {
                await _tileActions.RunAsync(_viewModel.ResetHeroStatusLayoutAsync);
                if (lifetime is not null && ReferenceEquals(lifetime, _pageLifetime))
                {
                    flyout.Content = BuildHeroStatusFlyoutContent(flyout);
                }
            });
        };
        root.Children.Add(restoreDefaultLink);

        return root;
    }

    /// <summary>Handles functional information-tile actions requested by the view model.</summary>
    private async Task PresentTileActionAsync(MasterControlTileAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (action)
        {
            case MasterControlTileAction.ShowStartupPrompt:
                await _startupGuide.ShowAsync(GetDialogXamlRoot(), cancellationToken);
                break;
            case MasterControlTileAction.CheckStartupConflicts:
                await _showStartupConflicts(GetDialogXamlRoot(), cancellationToken);
                break;
            case MasterControlTileAction.RunLatencyTest:
                await ShowLatencyDialogAsync(cancellationToken);
                break;
            case MasterControlTileAction.ExportConfiguration:
                await _dataPackages.ExportAsync(GetDialogXamlRoot(), cancellationToken);
                break;
            case MasterControlTileAction.ImportConfiguration:
                await _dataPackages.ImportAsync(GetDialogXamlRoot(),
                    token => RefreshAfterActionAsync(token, settingsImported: true), cancellationToken,
                    completionCancellationToken: _pageLifetime?.Token ?? cancellationToken);
                break;
            case MasterControlTileAction.OpenConnectionTest:
                await ShowNetworkCheckAsync(false, cancellationToken);
                break;
            case MasterControlTileAction.RefreshPublicIp:
                await ShowNetworkCheckAsync(true, cancellationToken);
                break;
            case MasterControlTileAction.CheckUpdates:
                await ShowNetworkCheckAsync(false, cancellationToken, update: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported tile action.");
        }
    }

    private async Task ShowNetworkCheckAsync(bool publicIp, CancellationToken cancellationToken, bool update = false)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TextBlock result = new()
        {
            Text = _getString("Master.Diagnostics.Testing"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            MaxWidth = 460,
        };
        ProgressRing progress = new() { IsActive = true, Width = 24, Height = 24 };
        StackPanel content = new() { Spacing = 12 };
        content.Children.Add(progress);
        content.Children.Add(new TextBlock
        {
            Text = _getString(update ? "About.Update.Description" : publicIp ? "Master.Tile.Description.PublicIp" : "Master.Diagnostics.CurrentRoute"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
        });
        content.Children.Add(result);
        ThemedContentDialog dialog = new()
        {
            Title = _getString(update ? "About.Update.Title" : publicIp ? "Master.Tile.PublicIp" : "Master.Tile.ConnectionTest"),
            Content = new ScrollViewer { Content = content, MaxHeight = Math.Max(160, GetDialogXamlRoot().Size.Height - 240) },
            CloseButtonText = _getString("Command.Cancel"),
            XamlRoot = GetDialogXamlRoot(),
        };
        Task operation = Task.CompletedTask;
        dialog.Opened += OnOpened;
        try { await dialog.ShowManagedAsync(cancellationToken); }
        finally
        {
            dialog.Opened -= OnOpened;
            await lifetime.CancelAsync();
            await operation;
        }

        void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => operation = RunAsync();
        async Task RunAsync()
        {
            try
            {
                if (update) { await _viewModel.CheckUpdatesAsync(lifetime.Token); }
                else if (publicIp) { await _viewModel.RefreshPublicIpAsync(lifetime.Token); }
                else { await _viewModel.CheckWebsitesAsync(lifetime.Token); }
                lifetime.Token.ThrowIfCancellationRequested();
                result.Text = update ? _viewModel.UpdateDetails : publicIp ? _viewModel.PublicIpDetails : _viewModel.WebsiteDetails;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                result.Text = _getString("Master.Diagnostics.Failed");
                await _errorSink.ReportAsync(new ApplicationError("master-network-check", exception), CancellationToken.None);
            }
            finally
            {
                progress.IsActive = false;
                progress.Visibility = Visibility.Collapsed;
                dialog.CloseButtonText = _getString("Command.Close");
            }
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
        _dataReady = false;
        _dataChanges.Stop();
        _tileActions.Deactivate();
        _loadSession.Cancel();
        _refreshSession.Cancel();
        lifetime.Cancel();
        try
        {
            await Task.WhenAll(_loadTask, _tileActions.DrainAsync(), _refreshSession.DrainAsync(), _dataChanges.DrainAsync());
        }
        finally
        {
            lifetime.Dispose();
        }
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
    private async Task ShowLatencyDialogAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
        ProgressRing progressRing = new() { IsActive = true, Width = 20, Height = 20 };
        StackPanel content = BuildLatencyDialogContent(progressText, timeoutBar, progressRing);
        StackPanel results = new() { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = _getString("Master.LatencyDialog.Description"),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new ScrollViewer
        {
            Content = results,
            MaxHeight = 180,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            Padding = new Thickness(0, 0, 12, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        });

        ThemedContentDialog dialog = new()
        {
            Title = _getString("Master.LatencyDialog.Title"),
            Content = content,
            CloseButtonText = _getString("Command.Cancel"),
            XamlRoot = GetDialogXamlRoot(),
        };

        Task latencyTask = Task.CompletedTask;
        dialog.Opened += OnDialogOpened;
        try
        {
            await dialog.ShowManagedAsync(cancellationToken);
        }
        finally
        {
            dialog.Opened -= OnDialogOpened;
            try
            {
                await cancellation.CancelAsync();
            }
            finally
            {
                await latencyTask;
            }
        }

        void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            latencyTask = RunLatencyTestWithProgressAsync(dialog, progressText, timeoutBar, progressRing, results, cancellation.Token);
        }
    }

    /// <summary>Builds latency-test dialog content using the RunOnce-style progress row and timeout bar.</summary>
    private static StackPanel BuildLatencyDialogContent(TextBlock progressText, ProgressBar timeoutBar, ProgressRing progressRing)
    {
        StackPanel content = new()
        {
            Spacing = 14,
            MinWidth = 360,
        };

        Grid progressRow = new()
        {
            ColumnSpacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(progressText, 1);
        progressRow.Children.Add(progressRing);
        progressRow.Children.Add(progressText);
        content.Children.Add(progressRow);
        content.Children.Add(timeoutBar);
        return content;
    }

    /// <summary>Runs proxy latency tests while updating a timed progress bar.</summary>
    private async Task RunLatencyTestWithProgressAsync(
        ThemedContentDialog dialog,
        TextBlock progressText,
        ProgressBar timeoutBar,
        ProgressRing progressRing,
        StackPanel results,
        CancellationToken cancellationToken)
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
            cancellationToken.ThrowIfCancellationRequested();
            progressText.Text = string.Format(
                CultureInfo.CurrentCulture,
                _getString("Master.LatencyDialog.Completed.Format"),
                testedNodes.Count);
            foreach (ProxyNode node in testedNodes)
            {
                Grid row = new() { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                TextBlock name = new() { Text = _filterDisplayText(node.Name), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                TextBlock latency = new()
                {
                    Text = ProxyNodeDisplay.FormatLatency(node, _getString),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = FlowDirection == FlowDirection.RightToLeft ? TextAlignment.Left : TextAlignment.Right,
                };
                Grid.SetColumn(latency, 1);
                row.Children.Add(name);
                row.Children.Add(latency);
                results.Children.Add(row);
            }
            timeoutBar.Value = 100;
            await RefreshAfterActionAsync(cancellationToken);
        }
        catch (Exception exception) when (ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            progressText.Text = _viewModel.LatencyTestFailedText;
            await _errorSink.ReportAsync(new ApplicationError("master-latency-test", exception), CancellationToken.None);
        }
        finally
        {
            timer.Stop();
            progressRing.IsActive = false;
            dialog.CloseButtonText = _getString("Command.Close");
        }
    }

    private async Task RefreshAfterActionAsync(CancellationToken cancellationToken, bool settingsImported = false)
    {
        await _loadTask;
        cancellationToken.ThrowIfCancellationRequested();
        if (settingsImported)
        {
            // The data-change refresh waits for this owned dialog to finish. Refresh directly here
            // so completion is presented against the committed data without waiting on ourselves.
            _dataGenerationId = _getDataGenerationId();
            _hasDataGeneration = true;
            _viewModel.InvalidateForDataChange();
            await _viewModel.LoadAsync(cancellationToken);
        }
        else
        {
            _viewModel.InvalidateAfterAction();
            await LoadForCurrentPageAsync();
        }
    }

    /// <summary>Opens a small editor that toggles which information tiles are visible.</summary>
    private async void EditInfoTilesButton_Click(object sender, RoutedEventArgs e)
    {
        await _tileActions.RunAsync(ShowInfoTilesEditorAsync);
    }

    /// <summary>Opens a small editor that toggles which information tiles are visible.</summary>
    private async Task ShowInfoTilesEditorAsync(CancellationToken cancellationToken)
    {
        XamlRoot dialogRoot = GetDialogXamlRoot();
        double editorWidth = CalculateInfoTilesEditorWidth(dialogRoot);
        SearchableOptionList optionList = new()
        {
            SearchPlaceholder = _viewModel.SearchInfoTilesPlaceholderText,
            EmptyText = _getString("Common.NoMatchingOptions"),
            AllowMultiple = true,
            MaxListHeight = CalculateInfoTilesEditorListHeight(dialogRoot),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        optionList.SetOptions(_viewModel.InfoTiles.Select(tile => new SearchableOptionItem(
            tile.Id,
            tile.Title,
            tile.TypeText,
            tile.Description,
            tile.Glyph,
            tile,
            tile.IsVisible)));

        IReadOnlyList<MasterInfoTilePreset> presets = MasterInfoTilePresetCatalog.Create(_getString, _viewModel.RecommendedInfoTileIds);
        IReadOnlyList<string>? templateOrder = null;
        bool applyingTemplate = false;
        ComboBox templatePicker = new()
        {
            Header = _getString("Master.Preset.Label"),
            PlaceholderText = _getString("Master.Preset.Custom"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (MasterInfoTilePreset preset in presets)
        {
            ComboBoxItem item = new() { Content = preset.Title, Tag = preset };
            ToolTipService.SetToolTip(item, preset.Description);
            templatePicker.Items.Add(item);
            if (_viewModel.VisibleInfoTiles.Select(static tile => tile.Id).SequenceEqual(preset.TileIds, StringComparer.Ordinal))
            {
                templatePicker.SelectedItem = item;
            }
        }

        StackPanel panel = new()
        {
            Spacing = 10,
            Width = editorWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        panel.Children.Add(new TextBlock
        {
            Text = _viewModel.InfoTileSelectionDescriptionText,
            Opacity = 0.72,
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        panel.Children.Add(templatePicker);
        panel.Children.Add(optionList);

        TextBlock selectionCount = new()
        {
            TextWrapping = TextWrapping.WrapWholeWords,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void RefreshSelectionCount()
        {
            selectionCount.Text = string.Format(
                CultureInfo.CurrentCulture,
                _getString("Master.Tile.SelectedCount.Format"),
                optionList.SelectedOptions.Count,
                optionList.Options.Count);
        }
        void SetAllChecked(bool isChecked)
        {
            // Bulk visibility includes options hidden by the search; only Save commits the draft.
            foreach (SearchableOptionItem option in optionList.Options)
            {
                option.IsChecked = isChecked;
            }
            RefreshSelectionCount();
        }
        optionList.SelectionChanged += (_, _) =>
        {
            if (applyingTemplate) { return; }
            templatePicker.SelectedIndex = -1;
            ToolTipService.SetToolTip(templatePicker, null);
            RefreshSelectionCount();
        };
        void ApplyTemplate(IReadOnlyList<string> order)
        {
            applyingTemplate = true;
            try
            {
                templateOrder = order.ToArray();
                HashSet<string> selected = order.ToHashSet(StringComparer.Ordinal);
                foreach (SearchableOptionItem option in optionList.Options) { option.IsChecked = selected.Contains(option.Id); }
                templatePicker.SelectedItem = templatePicker.Items.OfType<ComboBoxItem>().FirstOrDefault(
                    item => item.Tag is MasterInfoTilePreset preset && preset.TileIds.SequenceEqual(order, StringComparer.Ordinal));
                if (templatePicker.SelectedItem is ComboBoxItem { Tag: MasterInfoTilePreset chosen })
                {
                    ToolTipService.SetToolTip(templatePicker, chosen.Description);
                }
                RefreshSelectionCount();
            }
            finally { applyingTemplate = false; }
        }
        templatePicker.SelectionChanged += (_, _) =>
        {
            if (!applyingTemplate && templatePicker.SelectedItem is ComboBoxItem { Tag: MasterInfoTilePreset chosen })
            {
                ApplyTemplate(chosen.TileIds);
            }
        };
        Button showAll = new() { Content = _getString("Master.Tile.ShowAll") };
        Button hideAll = new() { Content = _getString("Master.Tile.HideAll") };
        showAll.Click += (_, _) => SetAllChecked(true);
        hideAll.Click += (_, _) => SetAllChecked(false);
        Grid selectionActions = new() { ColumnSpacing = 10 };
        selectionActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selectionActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selectionActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(hideAll, 1);
        Grid.SetColumn(selectionCount, 2);
        selectionActions.Children.Add(showAll);
        selectionActions.Children.Add(hideAll);
        selectionActions.Children.Add(selectionCount);
        panel.Children.Add(selectionActions);
        RefreshSelectionCount();

        HyperlinkButton restoreLayout = new()
        {
            Content = _getString("Master.Tile.RestoreRecommended"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        restoreLayout.Click += (_, _) => ApplyTemplate(_viewModel.RecommendedInfoTileIds);
        panel.Children.Add(restoreLayout);

        ThemedContentDialog dialog = new()
        {
            Title = _viewModel.EditInfoTilesText,
            Content = panel,
            PrimaryButtonText = _getString("Command.Save"),
            CloseButtonText = _getString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = dialogRoot,
        };
        dialog.Resources["ContentDialogMaxWidth"] = editorWidth + 48d;

        void ResizeEditor(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            // Virtualized rows and filtered results must not move the dialog or its controls.
            double width = CalculateInfoTilesEditorWidth(dialogRoot);
            if (panel.Width != width)
            {
                panel.Width = width;
                dialog.Resources["ContentDialogMaxWidth"] = width + 48d;
            }
            optionList.MaxListHeight = CalculateInfoTilesEditorListHeight(dialogRoot);
        }

        ContentDialogResult result;
        dialogRoot.Changed += ResizeEditor;
        try
        {
            result = await dialog.ShowManagedAsync(cancellationToken);
        }
        finally
        {
            dialogRoot.Changed -= ResizeEditor;
        }

        if (result is not ContentDialogResult.Primary)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        HashSet<string> selectedIds = optionList.SelectedOptions
            .Select(static option => option.Id)
            .ToHashSet(StringComparer.Ordinal);
        IEnumerable<string> initialOrder = templateOrder ?? _viewModel.VisibleInfoTiles.Select(static tile => tile.Id);
        List<string> orderedSelectedIds = initialOrder
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

        await _viewModel.SetVisibleInfoTileIdsAsync(orderedSelectedIds, cancellationToken);
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

    private async void InfoTileGrid_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        // Capture before queueing: another drag must not mutate this request while it waits.
        string[] requestedOrder = _viewModel.VisibleInfoTiles.Select(static tile => tile.Id).ToArray();
        try
        {
            await _tileActions.RunAsync(token => _viewModel.SetVisibleInfoTileIdsAsync(requestedOrder, token));
        }
        finally
        {
            // The page queue may cancel before invoking the write, leaving the native list reordered.
            _viewModel.RestoreCommittedInfoTileOrder();
        }
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
        // The native ContentDialog caps its outer height even on a tall desktop.
        // Reserve its title, template selector and action rows before sizing the list.
        double availableHeight = dialogRoot.Size.Height > 0
            ? Math.Min(dialogRoot.Size.Height, InfoTileEditorMaxDialogHeight) - InfoTileEditorVerticalChrome
            : InfoTileEditorMinListHeight;
        return Math.Clamp(availableHeight, InfoTileEditorMinListHeight, InfoTileEditorMaxListHeight);
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

    private void RootScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double contentWidth = e.NewSize.Width - RootScrollViewer.Padding.Left - RootScrollViewer.Padding.Right;
        if (!double.IsFinite(contentWidth) || contentWidth <= 0)
        {
            return;
        }

        // The content can retain a wider desired size when GridView containers resize.
        // Constrain it to the viewport before sizing the hero, modes and information tiles.
        ContentHost.Width = contentWidth;
        UpdateMasterLayout(contentWidth);
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
        foreach (RowDefinition row in ModeButtonGrid.RowDefinitions)
        {
            row.Height = isSideBySide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        }
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
