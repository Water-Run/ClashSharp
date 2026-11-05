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
using ClashSharp.Settings;
using ClashSharp.ViewModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace ClashSharp.View;

/// <summary>Page for the master control panel displaying proxy status overview and primary takeover state actions.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="MasterControlViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Starts page-scoped loading and forwards commands to injected application services.
/// </remarks>
public sealed partial class MasterControl : Page
{
    private const int HeroStatusColumnCount = 2;
    private const double HeroStatusHorizontalMargin = 10;
    private const double MinHeroStatusItemWidth = 120;
    private const double PreferredHeroStatusItemWidth = 220;
    private const double HeroStatusEditorMinListHeight = 80;
    private const double HeroStatusEditorMaxListHeight = 420;
    private const double HeroStatusEditorVerticalChrome = 220;
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
    private const double InfoTileEditorWindowChrome = 272;

    /// <summary>ContentDialog padding and border on both sides; the editor width excludes this frame.</summary>
    private const double InfoTileEditorDialogFrame = 50;

    /// <summary>Right gutter the option list keeps for its scroll bar; rows above and below align to it.</summary>
    private const double InfoTileEditorScrollGutter = 14;

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

    // The page owns Metro gestures and their visuals; GridView supplies semantic items
    // and layout. Only a completed move from this data/page lifetime may be persisted.
    private string[]? _infoTileDragInitialOrder;
    private CancellationTokenSource? _infoTileDragLifetime;
    private Guid? _infoTileDragGenerationId;

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
        InitializeInfoTilePointerReorder();
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
        ObserveInfoTileHostWindow();
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

    private async void SetHeroStatusDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        CancellationTokenSource? lifetime = _pageLifetime;
        if (sender is not FrameworkElement || lifetime is null || !_dataReady) { return; }
        XamlRoot visualRoot = GetDialogXamlRoot();
        StackPanel content = BuildHeroStatusEditorContent(out ScrollViewer slotScroller);
        ThemedContentDialog dialog = new() { XamlRoot = visualRoot, Content = content };
        dialog.Resources["ContentDialogMinWidth"] = 0d;
        dialog.Resources["ContentDialogMaxWidth"] = 688d;
        void MeasureBounds()
        {
            content.Width = Math.Min(560, Math.Max(1, visualRoot.Size.Width - 96));
            content.Measure(new Size(content.Width, double.PositiveInfinity));
            double chrome = Math.Max(0, content.DesiredSize.Height - slotScroller.DesiredSize.Height);
            slotScroller.MaxHeight = Math.Max(1, Math.Min(HeroStatusEditorMaxListHeight, visualRoot.Size.Height - 240 - chrome));
        }
        void UpdateBounds(XamlRoot root, XamlRootChangedEventArgs args) => MeasureBounds();
        dialog.Localize(() =>
        {
            dialog.Title = _getString("Master.Hero.SetDisplay");
            dialog.CloseButtonText = _getString("Command.Close");
            MeasureBounds();
        });
        visualRoot.Changed += UpdateBounds;
        try { await dialog.ShowManagedAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            await _errorSink.ReportAsync(new ApplicationError("master-hero-layout", exception), CancellationToken.None);
        }
        finally { visualRoot.Changed -= UpdateBounds; }
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

    private static void BindHeroStatusRow(TextBlock title, ComboBox comboBox, MasterHeroStatusSlotViewModel slot)
    {
        title.SetBinding(TextBlock.TextProperty, new Binding { Source = slot, Path = new PropertyPath(nameof(MasterHeroStatusSlotViewModel.Title)) });
        comboBox.SetBinding(AutomationProperties.NameProperty, new Binding { Source = slot, Path = new PropertyPath(nameof(MasterHeroStatusSlotViewModel.Title)) });
        if (!ReferenceEquals(comboBox.ItemsSource, slot.Options)) { comboBox.ItemsSource = slot.Options; }
        comboBox.Tag = slot.Index;
        BindHeroStatusSelection(comboBox, slot);
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

    private StackPanel BuildHeroStatusEditorContent(out ScrollViewer slotScroller)
    {
        StackPanel root = new()
        {
            Spacing = 10,
        };
        AutomationProperties.SetAutomationId(root, "HeroStatusLayoutEditor");

        StackPanel slotHost = new()
        {
            Spacing = 8,
        };
        List<(TextBlock Title, ComboBox Selector)> editors = [];
        foreach (MasterHeroStatusSlotViewModel slot in _viewModel.HeroStatusSlots)
        {
            Grid row = new()
            {
                ColumnSpacing = 12,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

            TextBlock title = new()
            {
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(title);

            ComboBox comboBox = new()
            {
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemTemplate = (DataTemplate)Resources["MasterHeroStatusOptionTemplate"],
            };
            AutomationProperties.SetAutomationId(comboBox, "HeroStatusSlot" + slot.Index.ToString(CultureInfo.InvariantCulture));
            BindLayoutEditing(comboBox);
            BindHeroStatusRow(title, comboBox, slot);
            comboBox.SelectionChanged += HeroStatusSlotComboBox_SelectionChanged;
            Grid.SetColumn(comboBox, 1);
            row.Children.Add(comboBox);
            editors.Add((title, comboBox));

            slotHost.Children.Add(row);
        }

        slotScroller = new()
        {
            Content = slotHost,
            MaxHeight = CalculateHeroStatusEditorListHeight(GetDialogXamlRoot()),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        root.Children.Add(slotScroller);
        HyperlinkButton restoreDefaultLink = new()
        {
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        TextBlock restoreText = new() { TextWrapping = TextWrapping.Wrap };
        restoreText.SetBinding(TextBlock.TextProperty, new Binding { Source = _viewModel, Path = new PropertyPath(nameof(MasterControlViewModel.RestoreDefaultHeroStatusLayoutText)) });
        restoreDefaultLink.Content = restoreText;
        restoreDefaultLink.SetBinding(AutomationProperties.NameProperty, new Binding { Source = _viewModel, Path = new PropertyPath(nameof(MasterControlViewModel.RestoreDefaultHeroStatusLayoutText)) });
        AutomationProperties.SetAutomationId(restoreDefaultLink, "RestoreHeroStatusLayoutButton");
        BindLayoutEditing(restoreDefaultLink);
        restoreDefaultLink.Click += async (_, _) =>
        {
            CancellationTokenSource? lifetime = _pageLifetime;
            await _heroStatusSelection.RunProgrammaticUpdateAsync(async () =>
            {
                await _tileActions.RunAsync(_viewModel.ResetHeroStatusLayoutAsync);
                if (lifetime is not null && ReferenceEquals(lifetime, _pageLifetime) && root.IsLoaded && root.XamlRoot is XamlRoot visualRoot)
                {
                    WindowDialogCoordinator.TryRefreshDialogPresentation(visualRoot, () =>
                    {
                        for (int index = 0; index < editors.Count && index < _viewModel.HeroStatusSlots.Count; index++)
                        {
                            // Native selection can replace its one-way binding; restore it without recreating controls.
                            BindHeroStatusRow(editors[index].Title, editors[index].Selector, _viewModel.HeroStatusSlots[index]);
                        }
                    });
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
        StopObservingInfoTileHostWindow();
        _openInfoTileContextMenu?.Hide();
        _openInfoTileContextMenu = null;
        CancelInfoTileReorder();
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

    /// <summary>Opens the tile editor from the dashboard's edit command.</summary>
    private async void EditInfoTilesButton_Click(object sender, RoutedEventArgs e)
    {
        await _tileActions.RunAsync(ShowInfoTilesEditorAsync);
    }

    /// <summary>Opens the tile editor: visible tiles, layout templates and the saved template list.</summary>
    /// <remarks>
    /// Tile visibility is a draft committed only by Save. Template list changes (save as, delete, restore defaults)
    /// are explicit actions that persist immediately, so they survive Cancel.
    /// </remarks>
    private async Task ShowInfoTilesEditorAsync(CancellationToken cancellationToken)
    {
        XamlRoot dialogRoot = GetDialogXamlRoot();
        double editorWidth = CalculateInfoTilesEditorWidth(dialogRoot);
        SearchableOptionList optionList = new()
        {
            SearchPlaceholder = _viewModel.SearchInfoTilesPlaceholderText,
            EmptyText = _getString("Common.NoMatchingOptions"),
            AllowMultiple = true,
            MaxListHeight = InfoTileEditorMinListHeight,
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

        IReadOnlyList<MasterInfoTilePresetEntry> templateEntries = _viewModel.InfoTilePresetEntries;
        IReadOnlyList<MasterInfoTilePreset> presets = [];
        IReadOnlyList<string>? templateOrder = null;
        bool applyingTemplate = false;
        bool restoreTemplatesEnabled = true;
        TextBlock templateHeader = new() { Text = _getString("Master.Preset.Label") };
        ComboBox templatePicker = new()
        {
            PlaceholderText = _getString("Master.Preset.Custom"),
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(templatePicker, "InfoTilePresetPicker");
        AutomationProperties.SetName(templatePicker, templateHeader.Text);

        TextBlock saveTemplateLabel = new() { Text = _getString("Master.Preset.SaveAs") };
        StackPanel saveTemplateContent = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        saveTemplateContent.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
        saveTemplateContent.Children.Add(saveTemplateLabel);
        Button saveTemplate = new() { Content = saveTemplateContent, VerticalAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetAutomationId(saveTemplate, "SaveInfoTilePresetButton");
        Button deleteTemplate = new()
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Width = 36,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            IsEnabled = false,
        };
        AutomationProperties.SetAutomationId(deleteTemplate, "DeleteInfoTilePresetButton");
        void LocalizeDeleteTemplate()
        {
            string label = _getString("Master.Preset.Delete");
            AutomationProperties.SetName(deleteTemplate, label);
            ToolTipService.SetToolTip(deleteTemplate, label);
        }
        LocalizeDeleteTemplate();

        Grid templateRow = new() { ColumnSpacing = 8, Margin = new Thickness(0, 0, InfoTileEditorScrollGutter, 0) };
        templateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        templateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        templateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(saveTemplate, 1);
        Grid.SetColumn(deleteTemplate, 2);
        templateRow.Children.Add(templatePicker);
        templateRow.Children.Add(saveTemplate);
        templateRow.Children.Add(deleteTemplate);

        // Save-as flyout: names a template from the current draft; an existing custom name is updated in place.
        TextBlock saveTemplateTitle = new() { Text = _getString("Master.Preset.SaveAs"), Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"] };
        TextBox templateName = new()
        {
            PlaceholderText = _getString("Master.Preset.NamePlaceholder"),
            MaxLength = MasterInfoTilePresetList.MaximumNameLength,
            Width = 260,
        };
        AutomationProperties.SetAutomationId(templateName, "InfoTilePresetNameBox");
        AutomationProperties.SetName(templateName, _getString("Master.Preset.NamePlaceholder"));
        TextBlock templateError = new()
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 260,
            Visibility = Visibility.Collapsed,
            Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemFillColorCriticalBrush"],
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CaptionTextBlockStyle"],
        };
        AutomationProperties.SetLiveSetting(templateError, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        Button confirmSaveTemplate = new()
        {
            Content = _getString("Command.Save"),
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 96,
        };
        StackPanel saveTemplatePanel = new() { Spacing = 10 };
        saveTemplatePanel.Children.Add(saveTemplateTitle);
        saveTemplatePanel.Children.Add(templateName);
        saveTemplatePanel.Children.Add(templateError);
        saveTemplatePanel.Children.Add(confirmSaveTemplate);
        Flyout saveTemplateFlyout = new() { Content = saveTemplatePanel, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        saveTemplate.Flyout = saveTemplateFlyout;

        StackPanel panel = new()
        {
            Spacing = 10,
            Width = editorWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        TextBlock editorDescription = new()
        {
            Text = _viewModel.InfoTileSelectionDescriptionText,
            Opacity = 0.72,
            TextWrapping = TextWrapping.WrapWholeWords,
        };
        panel.Children.Add(editorDescription);
        panel.Children.Add(templateHeader);
        panel.Children.Add(templateRow);
        panel.Children.Add(optionList);

        TextBlock selectionCount = new()
        {
            FlowDirection = FlowDirection.LeftToRight,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void RefreshSelectionCount()
        {
            string description = string.Format(
                CultureInfo.CurrentCulture,
                _getString("Master.Tile.SelectedCount.Format"),
                optionList.SelectedOptions.Count,
                optionList.Options.Count);
            selectionCount.Text = string.Format(
                CultureInfo.CurrentCulture,
                "{0} / {1}",
                optionList.SelectedOptions.Count,
                optionList.Options.Count);
            AutomationProperties.SetName(selectionCount, description);
            ToolTipService.SetToolTip(selectionCount, description);
        }

        // The draft order: the applied template or the current layout first, then newly checked tiles.
        List<string> CurrentDraftOrder()
        {
            HashSet<string> selectedIds = optionList.SelectedOptions
                .Select(static option => option.Id)
                .ToHashSet(StringComparer.Ordinal);
            IEnumerable<string> initialOrder = templateOrder ?? _viewModel.VisibleInfoTiles.Select(static tile => tile.Id);
            List<string> ordered = initialOrder.Where(selectedIds.Contains).ToList();
            HashSet<string> orderedIds = ordered.ToHashSet(StringComparer.Ordinal);
            foreach (SearchableOptionItem option in optionList.Options)
            {
                if (selectedIds.Contains(option.Id) && orderedIds.Add(option.Id))
                {
                    ordered.Add(option.Id);
                }
            }

            return ordered;
        }

        void SelectTemplate(MasterInfoTilePreset? preset)
        {
            templatePicker.SelectedItem = preset is null
                ? null
                : templatePicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is MasterInfoTilePreset candidate && candidate.Id == preset.Id);
            ToolTipService.SetToolTip(templatePicker, (templatePicker.SelectedItem as ComboBoxItem)?.Tag is MasterInfoTilePreset chosen ? chosen.Description : null);
            deleteTemplate.IsEnabled = templatePicker.SelectedItem is not null;
        }

        void RebuildTemplates(string? selectId)
        {
            bool previousApplying = applyingTemplate;
            applyingTemplate = true;
            try
            {
                presets = MasterInfoTilePresetCatalog.Create(
                    _getString,
                    _viewModel.RecommendedInfoTileIds,
                    _viewModel.InfoTiles.Select(static tile => tile.Id).ToArray(),
                    templateEntries);
                templatePicker.Items.Clear();
                foreach (MasterInfoTilePreset preset in presets)
                {
                    TextBlock label = new() { Text = preset.Title, TextTrimming = TextTrimming.CharacterEllipsis };
                    ComboBoxItem item = new() { Content = label, Tag = preset };
                    AutomationProperties.SetName(item, preset.Title);
                    ToolTipService.SetToolTip(item, preset.Description);
                    templatePicker.Items.Add(item);
                }

                List<string> draft = CurrentDraftOrder();
                SelectTemplate(presets.FirstOrDefault(preset => preset.Id == selectId)
                    ?? presets.FirstOrDefault(preset => preset.TileIds.SequenceEqual(draft, StringComparer.Ordinal)));
            }
            finally { applyingTemplate = previousApplying; }
        }

        void SetAllChecked(bool isChecked)
        {
            // Bulk visibility includes options hidden by the search; only Save commits the draft.
            foreach (SearchableOptionItem option in optionList.Options)
            {
                option.IsChecked = isChecked;
            }
            SelectTemplate(null);
            RefreshSelectionCount();
        }
        optionList.SelectionChanged += (_, _) =>
        {
            if (applyingTemplate) { return; }
            SelectTemplate(null);
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
                SelectTemplate(presets.FirstOrDefault(preset => preset.TileIds.SequenceEqual(order, StringComparer.Ordinal)));
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

        // Template list changes persist immediately and report failures without discarding the tile draft.
        void SetTemplateControlsEnabled(bool enabled)
        {
            templatePicker.IsEnabled = enabled;
            saveTemplate.IsEnabled = enabled;
            confirmSaveTemplate.IsEnabled = enabled;
            restoreTemplatesEnabled = enabled;
            deleteTemplate.IsEnabled = enabled && templatePicker.SelectedItem is not null;
        }

        async Task<bool> PersistTemplatesAsync(Func<Task<IReadOnlyList<MasterInfoTilePresetEntry>>> change, string? selectId)
        {
            SetTemplateControlsEnabled(false);
            try
            {
                templateEntries = await change();
                RebuildTemplates(selectId);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                await _errorSink.ReportAsync(new ApplicationError("master-tile-templates", exception), CancellationToken.None);
                return false;
            }
            finally
            {
                SetTemplateControlsEnabled(true);
            }
        }

        void ShowTemplateError(string message)
        {
            templateError.Text = message;
            templateError.Visibility = Visibility.Visible;
        }
        saveTemplateFlyout.Opening += (_, _) =>
        {
            templateName.Text = (templatePicker.SelectedItem as ComboBoxItem)?.Tag is MasterInfoTilePreset { IsBuiltIn: false } custom
                ? custom.Title
                : string.Empty;
            templateError.Visibility = Visibility.Collapsed;
        };
        saveTemplateFlyout.Opened += (_, _) => templateName.Focus(FocusState.Programmatic);
        async Task SaveTemplateAsync()
        {
            if (!MasterInfoTilePresetList.TryNormalizeName(templateName.Text, out string name))
            {
                ShowTemplateError(_getString("Master.Preset.NameInvalid"));
                return;
            }

            List<string> draft = CurrentDraftOrder();
            if (draft.Count == 0)
            {
                ShowTemplateError(_getString("Master.Preset.EmptySelection"));
                return;
            }

            MasterInfoTilePresetEntry template = MasterInfoTilePresetEntry.Custom(name, draft);
            List<MasterInfoTilePresetEntry> next = templateEntries.ToList();
            int existing = next.FindIndex(entry => !entry.IsBuiltIn && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                next[existing] = template;
            }
            else if (next.Count >= MasterInfoTilePresetList.MaximumEntries)
            {
                ShowTemplateError(string.Format(CultureInfo.CurrentCulture, _getString("Master.Preset.TooMany.Format"), MasterInfoTilePresetList.MaximumEntries));
                return;
            }
            else
            {
                next.Add(template);
            }

            templateOrder = draft;
            if (await PersistTemplatesAsync(() => _viewModel.SaveInfoTilePresetsAsync(next, cancellationToken), "custom:" + name))
            {
                saveTemplateFlyout.Hide();
            }
        }
        confirmSaveTemplate.Click += async (_, _) => await SaveTemplateAsync();
        templateName.KeyDown += async (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter) { return; }
            args.Handled = true;
            await SaveTemplateAsync();
        };
        deleteTemplate.Click += async (_, _) =>
        {
            if ((templatePicker.SelectedItem as ComboBoxItem)?.Tag is not MasterInfoTilePreset { Entry: { } removed })
            {
                return;
            }

            List<MasterInfoTilePresetEntry> next = templateEntries.Where(entry => !ReferenceEquals(entry, removed)).ToList();
            await PersistTemplatesAsync(() => _viewModel.SaveInfoTilePresetsAsync(next, cancellationToken), null);
        };

        Button showAll = new() { Content = _getString("Master.Tile.ShowAll") };
        Button hideAll = new() { Content = _getString("Master.Tile.HideAll") };
        AutomationProperties.SetAutomationId(showAll, "ShowAllTilesButton");
        AutomationProperties.SetAutomationId(hideAll, "HideAllTilesButton");
        showAll.Click += (_, _) => SetAllChecked(true);
        hideAll.Click += (_, _) => SetAllChecked(false);
        TextBlock restoreLabel = new() { Text = _getString("Master.Tile.RestoreRecommended"), TextWrapping = TextWrapping.WrapWholeWords };
        HyperlinkButton restoreLayout = new() { Content = restoreLabel, Padding = new Thickness(0) };
        AutomationProperties.SetAutomationId(restoreLayout, "RestoreRecommendedTilesButton");
        restoreLayout.Click += (_, _) => ApplyTemplate(_viewModel.RecommendedInfoTileIds);
        TextBlock restoreTemplatesLabel = new() { Text = _getString("Master.Preset.RestoreDefaults"), TextWrapping = TextWrapping.WrapWholeWords };
        HyperlinkButton restoreTemplates = new() { Content = restoreTemplatesLabel, Padding = new Thickness(0) };
        AutomationProperties.SetAutomationId(restoreTemplates, "RestoreDefaultInfoTilePresetsButton");
        restoreTemplates.Click += async (_, _) =>
        {
            if (!restoreTemplatesEnabled) { return; }
            await PersistTemplatesAsync(() => _viewModel.RestoreDefaultInfoTilePresetsAsync(cancellationToken), null);
        };
        StackPanel restoreLinks = new() { Orientation = Orientation.Horizontal, Spacing = 20 };
        restoreLinks.Children.Add(restoreLayout);
        restoreLinks.Children.Add(restoreTemplates);

        // Right edges line up with the list rows, which keep a gutter for the vertical scroll bar.
        Grid selectionActions = new() { ColumnSpacing = 8, RowSpacing = 8, Margin = new Thickness(0, 0, InfoTileEditorScrollGutter, 0) };
        selectionActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selectionActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selectionActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selectionActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        selectionActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(hideAll, 1);
        Grid.SetColumn(selectionCount, 2);
        selectionCount.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(restoreLinks, 1);
        Grid.SetColumnSpan(restoreLinks, 3);
        selectionActions.Children.Add(showAll);
        selectionActions.Children.Add(hideAll);
        selectionActions.Children.Add(selectionCount);
        selectionActions.Children.Add(restoreLinks);
        panel.Children.Add(selectionActions);
        RebuildTemplates(null);
        RefreshSelectionCount();

        ThemedContentDialog dialog = new()
        {
            Title = _viewModel.EditInfoTilesText,
            Content = panel,
            PrimaryButtonText = _getString("Command.Save"),
            CloseButtonText = _getString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = dialogRoot,
        };
        dialog.Resources["ContentDialogMaxWidth"] = editorWidth + InfoTileEditorDialogFrame;

        void UpdateEditorBounds()
        {
            // Virtualized rows and filtered results must not move the dialog or its controls.
            double width = CalculateInfoTilesEditorWidth(dialogRoot);
            if (panel.Width != width)
            {
                panel.Width = width;
                dialog.Resources["ContentDialogMaxWidth"] = width + InfoTileEditorDialogFrame;
            }
            optionList.MaxListHeight = CalculateInfoTilesEditorListHeight(dialogRoot, panel, optionList);
        }
        void ResizeEditor(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateEditorBounds();
        void EditorLoaded(object sender, RoutedEventArgs args) => UpdateEditorBounds();
        void ActionsSizeChanged(object sender, SizeChangedEventArgs args) => UpdateEditorBounds();

        dialog.Localize(() =>
        {
            // Refresh display metadata only; the selected template and its ordered draft remain captured.
            _viewModel.RefreshLocalization();
            dialog.Title = _viewModel.EditInfoTilesText;
            dialog.PrimaryButtonText = _getString("Command.Save");
            dialog.CloseButtonText = _getString("Command.Cancel");
            editorDescription.Text = _viewModel.InfoTileSelectionDescriptionText;
            optionList.SearchPlaceholder = _viewModel.SearchInfoTilesPlaceholderText;
            optionList.EmptyText = _getString("Common.NoMatchingOptions");
            templateHeader.Text = _getString("Master.Preset.Label");
            AutomationProperties.SetName(templatePicker, templateHeader.Text);
            templatePicker.PlaceholderText = _getString("Master.Preset.Custom");
            saveTemplateLabel.Text = _getString("Master.Preset.SaveAs");
            saveTemplateTitle.Text = saveTemplateLabel.Text;
            templateName.PlaceholderText = _getString("Master.Preset.NamePlaceholder");
            AutomationProperties.SetName(templateName, templateName.PlaceholderText);
            confirmSaveTemplate.Content = _getString("Command.Save");
            LocalizeDeleteTemplate();
            showAll.Content = _getString("Master.Tile.ShowAll");
            hideAll.Content = _getString("Master.Tile.HideAll");
            restoreLabel.Text = _getString("Master.Tile.RestoreRecommended");
            AutomationProperties.SetName(restoreLayout, restoreLabel.Text);
            restoreTemplatesLabel.Text = _getString("Master.Preset.RestoreDefaults");
            AutomationProperties.SetName(restoreTemplates, restoreTemplatesLabel.Text);
            bool previousApplying = applyingTemplate;
            applyingTemplate = true;
            try
            {
                foreach (SearchableOptionItem option in optionList.Options)
                {
                    if (option.Payload is MasterControlInfoTileViewModel tile)
                    {
                        option.UpdateDisplay(tile.Title, tile.TypeText, tile.Description);
                    }
                }
                RebuildTemplates((templatePicker.SelectedItem as ComboBoxItem)?.Tag is MasterInfoTilePreset selected ? selected.Id : null);
                optionList.RefreshFilter();
            }
            finally { applyingTemplate = previousApplying; }
            RefreshSelectionCount();
            UpdateEditorBounds();
        });

        ContentDialogResult result;
        UpdateEditorBounds();
        dialog.Loaded += EditorLoaded;
        selectionActions.SizeChanged += ActionsSizeChanged;
        dialogRoot.Changed += ResizeEditor;
        try
        {
            result = await dialog.ShowManagedAsync(cancellationToken);
        }
        finally
        {
            dialog.Loaded -= EditorLoaded;
            selectionActions.SizeChanged -= ActionsSizeChanged;
            dialogRoot.Changed -= ResizeEditor;
        }

        if (result is not ContentDialogResult.Primary)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _viewModel.SetVisibleInfoTileIdsAsync(CurrentDraftOrder(), cancellationToken);
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
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return;
        }

        _heroStatusItemWidth = Math.Min(availableWidth, Math.Max(
            MinHeroStatusItemWidth,
            CalculateHeroStatusItemWidth(availableWidth)));
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
        if (args.InRecycleQueue)
        {
            args.ItemContainer.ContextFlyout = null;
            args.ItemContainer.ClearValue(AutomationProperties.NameProperty);
            args.ItemContainer.ClearValue(AutomationProperties.HelpTextProperty);
        }
        else if (args.Item is MasterControlInfoTileViewModel tile)
        {
            args.ItemContainer.ContextFlyout = CreateInfoTileContextMenu(tile.Id);
            args.ItemContainer.SetBinding(AutomationProperties.NameProperty,
                new Binding { Source = tile, Path = new PropertyPath(nameof(tile.Title)), Mode = BindingMode.OneWay });
            args.ItemContainer.SetBinding(AutomationProperties.HelpTextProperty,
                new Binding { Source = tile, Path = new PropertyPath(nameof(tile.ReorderHint)), Mode = BindingMode.OneWay });
        }
    }

    private void InfoTileGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_infoTileDragInitialOrder is not null || _fallbackPointer is not null || _suppressFallbackClick
            || !_dataReady || _dataChanges.IsInvalidated)
        {
            return;
        }

        if (InfoTileGrid.ContainerFromItem(e.ClickedItem) is GridViewItem { ContentTemplateRoot: MasterInfoTile tile })
        {
            tile.Activate();
        }
    }

    private async Task CompleteInfoTileReorderAsync(bool acceptedMove)
    {
        string[]? initialOrder = _infoTileDragInitialOrder;
        bool canCommit = initialOrder is not null && ReferenceEquals(_infoTileDragLifetime, _pageLifetime)
            && _pageLifetime is not null && !_dataChanges.IsInvalidated
            && _infoTileDragGenerationId == _getDataGenerationId()
            && acceptedMove;
        _infoTileDragInitialOrder = null;
        _infoTileDragLifetime = null;
        _infoTileDragGenerationId = null;
        if (!canCommit)
        {
            RestoreInfoTileOrderWithMotion();
            DispatcherQueue.TryEnqueue(RefreshInfoTileFocusAppearance);
            return;
        }

        if (!initialOrder!.SequenceEqual(_viewModel.VisibleInfoTiles.Select(static tile => tile.Id), StringComparer.Ordinal))
        {
            await CommitInfoTileOrderAsync();
        }
        DispatcherQueue.TryEnqueue(RefreshInfoTileFocusAppearance);
    }

    private void CancelInfoTileReorder()
    {
        ResetInfoTilePointerReorder();
        if (_infoTileDragInitialOrder is null)
        {
            return;
        }

        _infoTileDragInitialOrder = null;
        _infoTileDragLifetime = null;
        _infoTileDragGenerationId = null;
        RestoreInfoTileOrderWithMotion();
        DispatcherQueue.TryEnqueue(RefreshInfoTileFocusAppearance);
    }

    private async Task CommitInfoTileOrderAsync()
    {
        // Capture before queueing: another gesture must not mutate this request while it waits.
        string[] requestedOrder = _viewModel.VisibleInfoTiles.Select(static tile => tile.Id).ToArray();
        try
        {
            await _tileActions.RunAsync(token => _viewModel.SetVisibleInfoTileIdsAsync(requestedOrder, token));
        }
        finally
        {
            // The page queue may cancel before invoking the write, leaving the local order reordered.
            RestoreInfoTileOrderWithMotion();
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

    private static double CalculateInfoTilesEditorListHeight(
        XamlRoot dialogRoot,
        FrameworkElement content,
        SearchableOptionList optionList)
    {
        // Measure translated descriptions and action rows at the actual width. Their wrapping
        // must reduce the scrolling list, rather than push the dialog into the title bar.
        content.Measure(new Size(content.Width, double.PositiveInfinity));
        double contentChrome = Math.Max(0, content.DesiredSize.Height - optionList.MaxListHeight);
        double availableHeight = dialogRoot.Size.Height > 0
            ? Math.Min(dialogRoot.Size.Height, InfoTileEditorMaxDialogHeight) - InfoTileEditorWindowChrome - contentChrome
            : InfoTileEditorMinListHeight;
        return Math.Clamp(availableHeight, InfoTileEditorMinListHeight, InfoTileEditorMaxListHeight);
    }

    private static double CalculateHeroStatusEditorListHeight(XamlRoot dialogRoot)
    {
        double availableHeight = dialogRoot.Size.Height > 0
            ? dialogRoot.Size.Height - HeroStatusEditorVerticalChrome
            : HeroStatusEditorMaxListHeight;
        return Math.Clamp(
            availableHeight,
            HeroStatusEditorMinListHeight,
            HeroStatusEditorMaxListHeight);
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

    private Pointer? _fallbackPointer;
    private string? _fallbackTileId;
    private Point _fallbackPressPoint;
    private Point _fallbackPreviewOffset;
    private Point _fallbackLastPoint;
    private Point _fallbackLastRootPoint;
    private DispatcherQueueTimer? _fallbackScrollTimer;
    private bool _fallbackCaptureTransfer;
    private bool _suppressFallbackClick;
    private AppWindow? _infoTileHostWindow;
    private MenuFlyout? _openInfoTileContextMenu;
    private bool _isSettlingInfoTile;
    private bool _infoTilePreviewIsMoving;
    private Storyboard? _infoTileDropAnimation;
    private Storyboard? _infoTilePickUpAnimation;
    private TaskCompletionSource<bool>? _infoTileDropCompletion;

    private void InitializeInfoTilePointerReorder()
    {
        // One in-window gesture owner supplies the same position-sensitive Metro
        // response for mouse/touch and for ordinary/elevated processes.
        InfoTileGrid.AddHandler(PointerPressedEvent, new PointerEventHandler(InfoTileGrid_PointerPressed), true);
        InfoTileGrid.AddHandler(PointerMovedEvent, new PointerEventHandler(InfoTileGrid_PointerMoved), true);
        InfoTileGrid.AddHandler(PointerReleasedEvent, new PointerEventHandler(InfoTileGrid_PointerReleased), true);
        InfoTileGrid.AddHandler(PointerCanceledEvent, new PointerEventHandler(InfoTileGrid_PointerCanceled), true);
        InfoTileGrid.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(InfoTileGrid_PointerCaptureLost), true);
        InfoTileGrid.AddHandler(KeyDownEvent, new KeyEventHandler(InfoTileGrid_KeyDown), true);
        InfoTileGrid.GotFocus += InfoTileGrid_GotFocus;
    }

    private void ObserveInfoTileHostWindow()
    {
        if (XamlRoot is XamlRoot visualRoot
            && AppWindow.GetFromWindowId(visualRoot.ContentIslandEnvironment.AppWindowId) is AppWindow hostWindow)
        {
            _infoTileHostWindow = hostWindow;
            hostWindow.Changed += InfoTileHostWindow_Changed;
        }
    }

    private void StopObservingInfoTileHostWindow()
    {
        if (_infoTileHostWindow is not AppWindow hostWindow) { return; }
        hostWindow.Changed -= InfoTileHostWindow_Changed;
        _infoTileHostWindow = null;
    }

    private void InfoTileHostWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!sender.IsVisible || sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            CancelInfoTileReorder();
            _openInfoTileContextMenu?.Hide();
        }
    }

    private void InfoTileGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Touch and pen keep the native scroll, tap and press-and-hold context gestures.
        // A left mouse hold remains a click until actual movement starts a reorder.
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) { return; }
        if (_isSettlingInfoTile || _fallbackPointer is not null || _infoTileDragInitialOrder is not null
            || !_dataReady || _dataChanges.IsInvalidated || !_viewModel.CanEditTileLayout)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(InfoTileGrid);
        if (!(e.Pointer.PointerDeviceType == PointerDeviceType.Mouse
                ? point.Properties.IsLeftButtonPressed : e.Pointer.IsInContact)
            || FindInfoTileAt(point.Position) is not GridViewItem { Content: MasterControlInfoTileViewModel tile } container)
        {
            return;
        }

        _fallbackPointer = e.Pointer;
        _fallbackTileId = tile.Id;
        _fallbackPressPoint = _fallbackLastPoint = point.Position;
        _fallbackLastRootPoint = e.GetCurrentPoint(InfoTileDragOverlay).Position;
        Rect bounds = GetInfoTileBounds(container);
        _fallbackPreviewOffset = new Point(point.Position.X - bounds.X, point.Position.Y - bounds.Y);
        // The item is now the only child input owner. Release its capture before the grid
        // takes the pointer, so movement remains routed even after the source slot moves.
        _fallbackCaptureTransfer = true;
        bool captured;
        try
        {
            container.ReleasePointerCapture(e.Pointer);
            captured = InfoTileGrid.CapturePointer(e.Pointer);
        }
        finally { _fallbackCaptureTransfer = false; }
        if (!captured) { ResetInfoTilePointerReorder(); return; }
        container.UseSystemFocusVisuals = false;
        container.Focus(FocusState.Pointer);
        (container.ContentTemplateRoot as MasterInfoTile)?.BeginPressFeedback(
            new Point(point.Position.X - bounds.X, point.Position.Y - bounds.Y));
        e.Handled = true;
    }

    private void InfoTileGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_fallbackPointer?.PointerId != e.Pointer.PointerId) { return; }
        if (!_viewModel.CanEditTileLayout || _dataChanges.IsInvalidated) { CancelInfoTileReorder(); return; }
        PointerPoint point = e.GetCurrentPoint(InfoTileGrid);
        _fallbackLastPoint = point.Position;
        _fallbackLastRootPoint = e.GetCurrentPoint(InfoTileDragOverlay).Position;
        if (_infoTileDragInitialOrder is null)
        {
            double x = point.Position.X - _fallbackPressPoint.X;
            double y = point.Position.Y - _fallbackPressPoint.Y;
            if (!MasterTileMotionPolicy.ShouldStartMouseDrag(x, y))
            {
                if (FindInfoTileContainer(_fallbackTileId)?.ContentTemplateRoot is MasterInfoTile pressed)
                {
                    pressed.UpdatePressFeedback(e.GetCurrentPoint(pressed).Position);
                }
                return;
            }
            if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
            {
                // A touch pan before the hold is scrolling, never a reorder.
                CancelInfoTileReorder();
                return;
            }
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)
            {
                CancelInfoTileReorder();
                return;
            }
            BeginInfoTilePointerReorder();
        }

        if (_infoTileDragInitialOrder is null) { return; }
        e.Handled = true;
        double moveX = point.Position.X - _fallbackPressPoint.X;
        double moveY = point.Position.Y - _fallbackPressPoint.Y;
        if (!_infoTilePreviewIsMoving && MasterTileMotionPolicy.ShouldStartMouseDrag(moveX, moveY))
        {
            _infoTilePreviewIsMoving = true;
            InfoTileDragPreview.Opacity = MasterTileMotionPolicy.DragOpacity;
        }
        UpdateInfoTilePointerPreview();
        MoveInfoTilePointerTarget();
    }

    private void MoveInfoTilePointerTarget()
    {
        if (FindInfoTileAt(_fallbackLastPoint) is GridViewItem { Content: MasterControlInfoTileViewModel target }
            && _fallbackTileId is string sourceId && !StringComparer.Ordinal.Equals(sourceId, target.Id))
        {
            Dictionary<string, Point> positions = CaptureInfoTilePositions();
            if (_viewModel.MoveVisibleInfoTile(sourceId, target.Id))
            {
                InfoTileGrid.UpdateLayout();
                AnimateInfoTileReflow(positions);
                UpdateInfoTileReorderAppearance();
            }
        }
    }

    private void BeginInfoTilePointerReorder()
    {
        if (_fallbackPointer is null || !_viewModel.CanEditTileLayout || _dataChanges.IsInvalidated
            || FindInfoTileContainer(_fallbackTileId) is not GridViewItem { Content: MasterControlInfoTileViewModel tile } container)
        {
            CancelInfoTileReorder();
            return;
        }

        _infoTileDragInitialOrder = _viewModel.VisibleInfoTiles.Select(static item => item.Id).ToArray();
        _infoTileDragLifetime = _pageLifetime;
        _infoTileDragGenerationId = _getDataGenerationId();
        _suppressFallbackClick = true;
        InfoTileDragContent.ContentTemplate = InfoTileGrid.ItemTemplate;
        InfoTileDragContent.Content = tile;
        InfoTileDragPreview.Width = container.ActualWidth;
        InfoTileDragPreview.Height = container.ActualHeight;
        InfoTileDragScale.CenterX = _fallbackPreviewOffset.X;
        InfoTileDragScale.CenterY = _fallbackPreviewOffset.Y;
        InfoTileDragScale.ScaleX = InfoTileDragScale.ScaleY = 1;
        InfoTileDragPreview.Opacity = 1;
        _infoTilePreviewIsMoving = false;
        InfoTileDragOverlay.Visibility = Visibility.Visible;
        Storyboard pickUp = new();
        double pickUpScale = MasterTileMotionPolicy.PickUpScale;
        AddInfoTileDropMotion(pickUp, InfoTileDragScale, nameof(ScaleTransform.ScaleX), pickUpScale);
        AddInfoTileDropMotion(pickUp, InfoTileDragScale, nameof(ScaleTransform.ScaleY), pickUpScale);
        _infoTilePickUpAnimation = pickUp;
        pickUp.Begin();
        (container.ContentTemplateRoot as MasterInfoTile)?.ResetPointerFeedback();
        UpdateInfoTilePointerPreview();
        UpdateInfoTileReorderAppearance();
        DispatcherQueueTimer scrollTimer = DispatcherQueue.CreateTimer();
        scrollTimer.Interval = TimeSpan.FromMilliseconds(50);
        scrollTimer.Tick += (_, _) => ScrollInfoTilePointerIntoView();
        _fallbackScrollTimer = scrollTimer;
        scrollTimer.Start();
    }

    private void ScrollInfoTilePointerIntoView()
    {
        if (_infoTileDragInitialOrder is null) { return; }
        Point viewport = InfoTileDragOverlay.TransformToVisual(RootScrollViewer).TransformPoint(_fallbackLastRootPoint);
        if (viewport.X < 0 || viewport.X > RootScrollViewer.ActualWidth) { return; }
        double delta = viewport.Y < 40 ? -16 : viewport.Y > RootScrollViewer.ActualHeight - 40 ? 16 : 0;
        double offset = Math.Clamp(RootScrollViewer.VerticalOffset + delta, 0, RootScrollViewer.ScrollableHeight);
        if (delta == 0 || Math.Abs(offset - RootScrollViewer.VerticalOffset) < 1) { return; }
        RootScrollViewer.ChangeView(null, offset, null, disableAnimation: true);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_infoTileDragInitialOrder is null) { return; }
            _fallbackLastPoint = InfoTileDragOverlay.TransformToVisual(InfoTileGrid).TransformPoint(_fallbackLastRootPoint);
            MoveInfoTilePointerTarget();
            UpdateInfoTileReorderAppearance();
        });
    }

    private async void InfoTileGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_fallbackPointer?.PointerId != e.Pointer.PointerId) { return; }
        Point position = e.GetCurrentPoint(InfoTileGrid).Position;
        bool dragging = _infoTileDragInitialOrder is not null;
        GridViewItem? clicked = !dragging ? FindInfoTileAt(position) : null;
        bool inside = position.X >= 0 && position.Y >= 0
            && position.X <= InfoTileGrid.ActualWidth && position.Y <= InfoTileGrid.ActualHeight;
        Point viewport = e.GetCurrentPoint(RootScrollViewer).Position;
        inside &= viewport.X >= 0 && viewport.Y >= 0
            && viewport.X <= RootScrollViewer.ActualWidth && viewport.Y <= RootScrollViewer.ActualHeight;
        string? tileId = _fallbackTileId;
        _suppressFallbackClick = true;
        e.Handled = true;
        if (dragging)
        {
            // A drop is accepted at release. Its visual settles independently; a later
            // tap or rendering suspension must not turn that accepted move into a cancel.
            Pointer pointer = _fallbackPointer;
            _fallbackPointer = null;
            _fallbackScrollTimer?.Stop();
            InfoTileGrid.ReleasePointerCapture(pointer);
            _isSettlingInfoTile = true;
            try
            {
                Task<bool> settling = inside ? SettleInfoTilePreviewAsync() : Task.FromResult(false);
                Task commit = CompleteInfoTileReorderAsync(inside);
                await Task.WhenAll(settling, commit);
            }
            finally
            {
                ResetInfoTilePointerReorder();
                _isSettlingInfoTile = false;
            }
        }
        else
        {
            ResetInfoTilePointerReorder();
            if (clicked is { Content: MasterControlInfoTileViewModel tile, ContentTemplateRoot: MasterInfoTile control }
                && StringComparer.Ordinal.Equals(tile.Id, tileId))
            {
                control.Activate();
            }
        }
    }

    private void InfoTileGrid_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (!_fallbackCaptureTransfer && _fallbackPointer?.PointerId == e.Pointer.PointerId) { CancelInfoTileReorder(); }
    }

    private void InfoTileGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_fallbackCaptureTransfer || _fallbackPointer?.PointerId != e.Pointer.PointerId) { return; }
        Pointer pointer = _fallbackPointer;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(_fallbackPointer, pointer)
                && InfoTileGrid.PointerCaptures?.Any(candidate => candidate.PointerId == pointer.PointerId) != true)
            {
                CancelInfoTileReorder();
            }
        });
    }

    private void InfoTileGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape
            || _fallbackPointer is null && !_isSettlingInfoTile) { return; }
        _suppressFallbackClick = true;
        CancelInfoTileReorder();
        e.Handled = true;
    }

    private GridViewItem? FindInfoTileContainer(string? tileId) => tileId is null ? null
        : _viewModel.VisibleInfoTiles.Where(tile => StringComparer.Ordinal.Equals(tile.Id, tileId))
            .Select(tile => InfoTileGrid.ContainerFromItem(tile) as GridViewItem).FirstOrDefault();

    private Rect GetInfoTileBounds(GridViewItem container) =>
        container.TransformToVisual(InfoTileGrid).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

    private GridViewItem? FindInfoTileAt(Point position) => _viewModel.VisibleInfoTiles
        .Select(tile => InfoTileGrid.ContainerFromItem(tile) as GridViewItem)
        .FirstOrDefault(container => container is not null && GetInfoTileBounds(container).Contains(position));

    private void UpdateInfoTilePointerPreview()
    {
        Point position = InfoTileGrid.TransformToVisual(InfoTileDragOverlay).TransformPoint(_fallbackLastPoint);
        Canvas.SetLeft(InfoTileDragPreview, position.X - _fallbackPreviewOffset.X);
        Canvas.SetTop(InfoTileDragPreview, position.Y - _fallbackPreviewOffset.Y);
    }

    private void UpdateInfoTileReorderAppearance()
    {
        foreach (MasterControlInfoTileViewModel tile in _viewModel.VisibleInfoTiles)
        {
            if (InfoTileGrid.ContainerFromItem(tile) is not GridViewItem container) { continue; }
            bool source = StringComparer.Ordinal.Equals(tile.Id, _fallbackTileId);
            container.Opacity = source ? 0 : 1;
            container.UseSystemFocusVisuals = false;

        }
    }

    private async Task<bool> SettleInfoTilePreviewAsync()
    {
        if (FindInfoTileContainer(_fallbackTileId) is not GridViewItem container
            || _pageLifetime is null || _dataChanges.IsInvalidated)
        {
            return false;
        }
        CancellationToken lifetime = _pageLifetime.Token;
        InfoTileGrid.UpdateLayout();
        double scaleX = InfoTileDragScale.ScaleX;
        double scaleY = InfoTileDragScale.ScaleY;
        _infoTilePickUpAnimation?.Stop();
        _infoTilePickUpAnimation = null;
        InfoTileDragScale.ScaleX = scaleX;
        InfoTileDragScale.ScaleY = scaleY;
        Rect target = container.TransformToVisual(InfoTileDragOverlay)
            .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
        Storyboard animation = new();
        AddInfoTileDropMotion(animation, InfoTileDragPreview, "(Canvas.Left)", target.X);
        AddInfoTileDropMotion(animation, InfoTileDragPreview, "(Canvas.Top)", target.Y);
        AddInfoTileDropMotion(animation, InfoTileDragPreview, nameof(Opacity), 1);
        double finalScale = 1;
        AddInfoTileDropMotion(animation, InfoTileDragScale, nameof(ScaleTransform.ScaleX), finalScale);
        AddInfoTileDropMotion(animation, InfoTileDragScale, nameof(ScaleTransform.ScaleY), finalScale);
        AddInfoTileDropMotion(animation, InfoTileDragScale, nameof(ScaleTransform.CenterX), container.ActualWidth / 2);
        AddInfoTileDropMotion(animation, InfoTileDragScale, nameof(ScaleTransform.CenterY), container.ActualHeight / 2);
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _infoTileDropCompletion = completion;
        _infoTileDropAnimation = animation;
        animation.Completed += (_, _) => completion.TrySetResult(true);
        animation.Begin();
        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(1), lifetime);
        }
        catch (TimeoutException)
        {
            // Occlusion or a disconnected presentation surface must not strand a drop.
            // The caller still verifies data/page identity before accepting the order.
            return true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return false;
        }
    }

    private static void AddInfoTileDropMotion(Storyboard owner, DependencyObject target, string property, double value)
    {
        DoubleAnimation animation = new()
        {
            To = value,
            Duration = TimeSpan.FromMilliseconds(MasterTileMotionPolicy.ReleaseMilliseconds),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        owner.Children.Add(animation);
    }

    private void ResetInfoTilePointerReorder()
    {
        _infoTileDropAnimation?.Stop();
        _infoTilePickUpAnimation?.Stop();
        _infoTilePickUpAnimation = null;
        _infoTileDropCompletion?.TrySetResult(false);
        _infoTileDropAnimation = null;
        _infoTileDropCompletion = null;
        Pointer? pointer = _fallbackPointer;
        _fallbackPointer = null;
        _fallbackTileId = null;
        _fallbackScrollTimer?.Stop();
        _fallbackScrollTimer = null;
        if (pointer is not null) { InfoTileGrid.ReleasePointerCapture(pointer); }
        InfoTileDragOverlay.Visibility = Visibility.Collapsed;
        InfoTileDragContent.Content = null;
        foreach (MasterControlInfoTileViewModel tile in _viewModel.VisibleInfoTiles)
        {
            if (InfoTileGrid.ContainerFromItem(tile) is GridViewItem container)
            {
                container.Opacity = 1;
                container.UseSystemFocusVisuals = container.FocusState == FocusState.Keyboard;
                if (container.ContentTemplateRoot is MasterInfoTile control)
                {
                    control.ResetPointerFeedback();
                }
            }
        }
        if (!DispatcherQueue.TryEnqueue(() => _suppressFallbackClick = false)) { _suppressFallbackClick = false; }
    }

    private Dictionary<string, Point> CaptureInfoTilePositions()
    {
        Dictionary<string, Point> positions = new(StringComparer.Ordinal);
        foreach (MasterControlInfoTileViewModel tile in _viewModel.VisibleInfoTiles)
        {
            if (InfoTileGrid.ContainerFromItem(tile) is not GridViewItem { ContentTemplateRoot: MasterInfoTile control } container) { continue; }
            Rect bounds = GetInfoTileBounds(container);
            Point offset = control.ReflowOffset;
            positions[tile.Id] = new Point(bounds.X + offset.X, bounds.Y + offset.Y);
        }
        return positions;
    }

    private void AnimateInfoTileReflow(IReadOnlyDictionary<string, Point> previous)
    {
        // Layout slots remain stable for hit testing. Only the content moves, retargeted
        // from its current rendered position when the pointer crosses another slot.
        foreach (MasterControlInfoTileViewModel tile in _viewModel.VisibleInfoTiles)
        {
            if (StringComparer.Ordinal.Equals(tile.Id, _fallbackTileId)
                || !previous.TryGetValue(tile.Id, out Point old)
                || InfoTileGrid.ContainerFromItem(tile) is not GridViewItem { ContentTemplateRoot: MasterInfoTile control } container) { continue; }
            Rect bounds = GetInfoTileBounds(container);
            control.ReflowFrom(old.X - bounds.X, old.Y - bounds.Y);
        }
    }

    private void RestoreInfoTileOrderWithMotion()
    {
        if (_pageLifetime is null || _dataChanges.IsInvalidated)
        {
            _viewModel.RestoreCommittedInfoTileOrder();
            return;
        }
        string[] before = _viewModel.VisibleInfoTiles.Select(static tile => tile.Id).ToArray();
        Dictionary<string, Point> positions = CaptureInfoTilePositions();
        _viewModel.RestoreCommittedInfoTileOrder();
        if (before.SequenceEqual(_viewModel.VisibleInfoTiles.Select(static tile => tile.Id), StringComparer.Ordinal)) { return; }
        InfoTileGrid.UpdateLayout();
        AnimateInfoTileReflow(positions);
    }

    private void RefreshInfoTileFocusAppearance()
    {
        foreach (MasterControlInfoTileViewModel tile in _viewModel.VisibleInfoTiles)
        {
            if (InfoTileGrid.ContainerFromItem(tile) is GridViewItem container)
            {
                container.UseSystemFocusVisuals = container.FocusState == FocusState.Keyboard;
            }
        }
    }

    private void InfoTileGrid_GotFocus(object sender, RoutedEventArgs e)
    {
        for (DependencyObject? node = e.OriginalSource as DependencyObject;
            node is not null && !ReferenceEquals(node, InfoTileGrid); node = VisualTreeHelper.GetParent(node))
        {
            if (node is not GridViewItem container) { continue; }
            bool keyboard = container.FocusState == FocusState.Keyboard;
            container.UseSystemFocusVisuals = keyboard;
            return;
        }
    }

    private MenuFlyout CreateInfoTileContextMenu(string tileId)
    {
        MenuFlyout menu = new();
        MenuFlyoutItem unpin = new() { Text = _getString("Master.Tile.Unpin") };
        MenuFlyoutItem arrange = new() { Text = _viewModel.EditInfoTilesText };
        menu.Items.Add(unpin);
        menu.Items.Add(arrange);
        CancellationTokenSource? openedLifetime = null;
        Guid? openedGeneration = null;
        menu.Opening += (_, _) =>
        {
            unpin.Text = _getString("Master.Tile.Unpin");
            arrange.Text = _viewModel.EditInfoTilesText;
            CancelInfoTileReorder();
            _openInfoTileContextMenu = menu;
            bool ready = _pageLifetime is not null && _dataReady && !_dataChanges.IsInvalidated && _viewModel.CanEditTileLayout;
            unpin.IsEnabled = arrange.IsEnabled = ready;
            openedLifetime = ready ? _pageLifetime : null;
            openedGeneration = ready ? _getDataGenerationId() : null;
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openInfoTileContextMenu, menu)) { _openInfoTileContextMenu = null; }
        };
        unpin.Click += async (_, _) =>
        {
            if (openedLifetime is null || !ReferenceEquals(openedLifetime, _pageLifetime)
                || _dataChanges.IsInvalidated || openedGeneration != _getDataGenerationId()) { return; }
            string[] requested = _viewModel.VisibleInfoTiles.Where(tile => !StringComparer.Ordinal.Equals(tile.Id, tileId))
                .Select(static tile => tile.Id).ToArray();
            await _tileActions.RunAsync(token => _viewModel.SetVisibleInfoTileIdsAsync(requested, token));
        };
        arrange.Click += async (_, _) =>
        {
            if (openedLifetime is null || !ReferenceEquals(openedLifetime, _pageLifetime)
                || _dataChanges.IsInvalidated || openedGeneration != _getDataGenerationId()) { return; }
            await _tileActions.RunAsync(ShowInfoTilesEditorAsync);
        };
        return menu;
    }
}
