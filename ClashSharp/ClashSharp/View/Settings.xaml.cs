using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Components;
using ClashSharp.Model;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace ClashSharp.View;

/// <summary>Page for application-wide settings such as language, Windows proxy behavior, and core configuration.</summary>
/// <remarks>
/// Invariants: Loaded controls mirror the injected settings view model after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Persists settings when user-facing controls change.
/// </remarks>
public sealed partial class Settings : Page
{
    /// <summary>Owns settings state transitions and persistence.</summary>
    private readonly SettingsViewModel _viewModel;
    private readonly Func<string, string> _getString;
    private readonly Action<bool> _setRestartPending;
    private readonly Func<string, Windows.UI.Color> _parseAccentColor;
    private readonly Func<Windows.UI.Color, string> _formatAccentColor;
    private readonly IApplicationErrorSink _errorSink;
    private readonly IStartupGuidePresenter _startupGuide;
    private readonly DataPackageDialogPresenter _dataPackages;
    private readonly PageOperationSession _pageOperations;

    /// <summary>True while initial settings are being bound to controls.</summary>
    private bool _isLoadingSettings = true;

    private bool _isLoaded;
    private int _visit;
    private bool _isViewModelSubscribed;

    internal Settings(SettingsPageDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel
            ?? throw new ArgumentException("A settings view model is required.", nameof(dependencies));
        _getString = dependencies.GetString
            ?? throw new ArgumentException("A localization function is required.", nameof(dependencies));
        _setRestartPending = dependencies.SetRestartPending
            ?? throw new ArgumentException("A restart-state publisher is required.", nameof(dependencies));
        _parseAccentColor = dependencies.ParseAccentColor
            ?? throw new ArgumentException("An accent-color parser is required.", nameof(dependencies));
        _formatAccentColor = dependencies.FormatAccentColor
            ?? throw new ArgumentException("An accent-color formatter is required.", nameof(dependencies));
        _errorSink = dependencies.ErrorSink
            ?? throw new ArgumentException("An application error sink is required.", nameof(dependencies));
        _startupGuide = dependencies.StartupGuide
            ?? throw new ArgumentException("A startup-guide presenter is required.", nameof(dependencies));
        _dataPackages = dependencies.DataPackages
            ?? throw new ArgumentException("A data-package presenter is required.", nameof(dependencies));
        _pageOperations = new PageOperationSession(_errorSink, "settings-page-operation");
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Loads persisted settings into visible controls.</summary>
    private void LoadSettings()
    {
        _viewModel.Load();
        UpdateRestartRequiredState();
    }

    /// <summary>Updates shell restart-required state when settings requiring restart change.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.HasRestartRequiredSettings))
        {
            UpdateRestartRequiredState();
        }
    }

    /// <summary>Publishes the current restart-required settings state to the shell.</summary>
    private void UpdateRestartRequiredState()
    {
        _setRestartPending(_viewModel.HasRestartRequiredSettings);
    }

    /// <summary>Restores page-scoped subscriptions and cancellation after navigation back to this instance.</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        int visit = ++_visit;
        await _pageOperations.DrainAsync();
        if (!_isLoaded || visit != _visit)
        {
            return;
        }

        await RunPageOperationAsync(async token =>
        {
            SubscribeToViewModel();
            CheckStartupConflictsButton.IsEnabled = true;
            _isLoadingSettings = true;
            try
            {
                LoadSettings();
            }
            finally
            {
                _isLoadingSettings = false;
            }

            await _viewModel.RefreshMihomoServiceStatusCommand.ExecuteObservedAsync(null, token);
        });
    }

    /// <summary>Stops page work and view-model notifications while the page is outside the visual tree.</summary>
    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isViewModelSubscribed)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _isViewModelSubscribed = false;
        }

        _isLoaded = false;
        _visit++;
        _pageOperations.Cancel();
        await _pageOperations.DrainAsync();
    }

    private void SubscribeToViewModel()
    {
        if (_isViewModelSubscribed)
        {
            return;
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _isViewModelSubscribed = true;
    }

    /// <summary>Returns the window-level XAML root so dialogs center in the visible window.</summary>
    /// <returns>Window root when available; otherwise the page root.</returns>
    private XamlRoot GetDialogXamlRoot()
    {
        return App.MainWindow?.Content is FrameworkElement root && root.XamlRoot is not null
            ? root.XamlRoot
            : XamlRoot;
    }

    /// <summary>Runs a connection test against the configured test URL.</summary>
    private async void ConnectionTestButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            ConnectionTestReport report = await _viewModel.RunConnectionTestAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await ShowConnectionTestResultAsync(report, cancellationToken);
        });
    }

    /// <summary>Shows the connection test result.</summary>
    private async Task ShowConnectionTestResultAsync(
        ConnectionTestReport report,
        CancellationToken cancellationToken)
    {
        await CenteredDialogOverlay.ShowAsync(
            GetDialogXamlRoot(),
            _viewModel.ConnectionTestUrlTitleText,
            BuildConnectionTestResultPanel(report),
            _getString("Command.Close"),
            720,
            cancellationToken);
    }

    private StackPanel BuildConnectionTestResultPanel(ConnectionTestReport report)
    {
        StackPanel panel = new()
        {
            Spacing = 12,
            MinWidth = 520,
            MaxWidth = 680,
        };

        Grid table = new()
        {
            RowSpacing = 8,
            ColumnSpacing = 12,
        };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddConnectionTestHeader(table);

        foreach (ConnectionTestTargetResult result in report.Results)
        {
            AddConnectionTestResultRow(table, result);
        }

        panel.Children.Add(table);
        panel.Children.Add(new TextBlock
        {
            Text = report.SummaryText,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Foreground = GetConnectionTestSummaryBrush(report.SummaryState),
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        return panel;
    }

    private void AddConnectionTestHeader(Grid table)
    {
        int rowIndex = table.RowDefinitions.Count;
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddConnectionTestText(table, rowIndex, 0, "URL", "CaptionTextBlockStyle", "TextFillColorSecondaryBrush");
        AddConnectionTestText(table, rowIndex, 1, _viewModel.ConnectionTestStatusColumnText, "CaptionTextBlockStyle", "TextFillColorSecondaryBrush");
        AddConnectionTestText(table, rowIndex, 2, _viewModel.ConnectionTestLatencyColumnText, "CaptionTextBlockStyle", "TextFillColorSecondaryBrush");
    }

    private static void AddConnectionTestResultRow(Grid table, ConnectionTestTargetResult result)
    {
        int rowIndex = table.RowDefinitions.Count;
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddConnectionTestText(table, rowIndex, 0, result.Url, "BodyTextBlockStyle", "TextFillColorPrimaryBrush");

        StackPanel statusPanel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        statusPanel.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new SolidColorBrush(result.Succeeded ? Windows.UI.Color.FromArgb(255, 16, 124, 16) : Windows.UI.Color.FromArgb(255, 196, 43, 28)),
        });
        statusPanel.Children.Add(new TextBlock
        {
            Text = result.StatusText,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        });
        Grid.SetRow(statusPanel, rowIndex);
        Grid.SetColumn(statusPanel, 1);
        table.Children.Add(statusPanel);

        AddConnectionTestText(table, rowIndex, 2, result.LatencyText, "BodyTextBlockStyle", "TextFillColorPrimaryBrush");
    }

    private static Brush GetConnectionTestSummaryBrush(ConnectionTestSummaryState summaryState)
    {
        return summaryState switch
        {
            ConnectionTestSummaryState.AllPassed => ResourceBrush(
                "SystemFillColorSuccessBrush",
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 16))),
            ConnectionTestSummaryState.PartialFailed => ResourceBrush(
                "SystemFillColorCautionBrush",
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 157, 93, 0))),
            ConnectionTestSummaryState.AllFailed => ResourceBrush(
                "SystemFillColorCriticalBrush",
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28))),
            _ => (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        };
    }

    private static void AddConnectionTestText(Grid table, int rowIndex, int columnIndex, string text, string styleKey, string brushKey)
    {
        TextBlock textBlock = new()
        {
            Text = text,
            Style = (Style)Application.Current.Resources[styleKey],
            Foreground = (Brush)Application.Current.Resources[brushKey],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(textBlock, rowIndex);
        Grid.SetColumn(textBlock, columnIndex);
        table.Children.Add(textBlock);
    }

    private static Brush ResourceBrush(string key, Brush fallback)
    {
        return Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush
            ? brush
            : fallback;
    }

    /// <summary>Opens the Windows-native network repair dialog.</summary>
    /// <param name="sender">Clicked button. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void OpenNetworkRepairButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            PageOperationSession diagnostics = new(_errorSink, "settings-diagnostic-dialog");
            ThemedContentDialog dialog = new()
            {
                Title = _viewModel.WindowsNativeTitleText,
                Content = BuildNetworkRepairPanel(diagnostics, cancellationToken),
                CloseButtonText = _getString("Command.Close"),
                XamlRoot = GetDialogXamlRoot(),
            };
            try
            {
                await dialog.ShowManagedAsync(cancellationToken);
            }
            finally
            {
                diagnostics.Cancel();
                await diagnostics.DrainAsync();
            }
        });
    }

    /// <summary>Shows restart guidance when accent color mode changes after initial binding.</summary>
    /// <param name="sender">Accent color mode combo box. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void AppAccentColorModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || !_viewModel.IsAppAccentColorRestartPending)
        {
            return;
        }

        await RunPageOperationAsync(ShowRestartRequiredDialogAsync);
    }

    private async void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || !_viewModel.IsDisplayLanguageRestartPending)
        {
            return;
        }

        await RunPageOperationAsync(ShowRestartRequiredDialogAsync);
    }

    /// <summary>Opens the application accent color picker and persists the selected color.</summary>
    /// <param name="sender">Clicked color swatch button. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void AppAccentColorSwatchButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            ColorPicker picker = new()
            {
                Color = _parseAccentColor(_viewModel.AppAccentColorValue),
                IsAlphaEnabled = false,
                Width = 320,
                MaxWidth = 320,
            };

            Grid pickerPanel = new()
            {
                Width = 340,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            pickerPanel.Children.Add(picker);

            ThemedContentDialog dialog = new()
            {
                Title = _viewModel.AppAccentColorTitleText,
                Content = pickerPanel,
                MaxWidth = 420,
                PrimaryButtonText = _viewModel.AppAccentColorPickText,
                CloseButtonText = _getString("Command.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = GetDialogXamlRoot(),
            };

            if (await dialog.ShowManagedAsync(cancellationToken) is not ContentDialogResult.Primary)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _viewModel.SetCustomAppAccentColor(_formatAccentColor(picker.Color));
            if (_viewModel.IsAppAccentColorRestartPending)
            {
                await ShowRestartRequiredDialogAsync(cancellationToken);
            }
        });
    }

    /// <summary>Opens the connection-test URL editor dialog.</summary>
    private async void EditConnectionTestUrlsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            TextBox proxyUrl1Box = new() { Text = _viewModel.ConnectionTestProxyUrl1, Width = 360 };
            TextBox proxyUrl2Box = new() { Text = _viewModel.ConnectionTestProxyUrl2, Width = 360 };
            TextBox directUrlBox = new() { Text = _viewModel.ConnectionTestDirectUrl, Width = 360 };
            InfoBar validationError = new()
            {
                Severity = InfoBarSeverity.Error,
                IsClosable = false,
                Message = _viewModel.ConnectionTestUrlValidationText,
            };
            StackPanel panel = BuildConnectionTestUrlsPanel(
                proxyUrl1Box, proxyUrl2Box, directUrlBox, validationError);

            ThemedContentDialog dialog = new()
            {
                Title = _viewModel.ConnectionTestUrlTitleText,
                Content = panel,
                PrimaryButtonText = _getString("Command.Save"),
                CloseButtonText = _getString("Command.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = GetDialogXamlRoot(),
            };

            void ValidateDraft(ContentDialog sender, ContentDialogButtonClickEventArgs args)
            {
                int invalidIndex = SettingsViewModel.GetInvalidConnectionTestUrlIndex(
                    proxyUrl1Box.Text, proxyUrl2Box.Text, directUrlBox.Text);
                args.Cancel = invalidIndex >= 0;
                validationError.IsOpen = args.Cancel;
                if (invalidIndex >= 0)
                {
                    TextBox invalidField = invalidIndex switch
                    {
                        0 => proxyUrl1Box,
                        1 => proxyUrl2Box,
                        _ => directUrlBox,
                    };
                    invalidField.Focus(FocusState.Programmatic);
                }
            }

            dialog.PrimaryButtonClick += ValidateDraft;
            try
            {
                ContentDialogResult result = await dialog.ShowManagedAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (result is ContentDialogResult.Primary)
                {
                    _viewModel.SetConnectionTestUrls(proxyUrl1Box.Text, proxyUrl2Box.Text, directUrlBox.Text);
                }
            }
            finally
            {
                dialog.PrimaryButtonClick -= ValidateDraft;
            }
        });
    }

    /// <summary>Builds the connection-test URL editor content.</summary>
    private StackPanel BuildConnectionTestUrlsPanel(
        TextBox proxyUrl1Box,
        TextBox proxyUrl2Box,
        TextBox directUrlBox,
        InfoBar validationError)
    {
        StackPanel panel = new()
        {
            Spacing = 10,
            MinWidth = 420,
        };
        AddConnectionTestUrlEditorRow(panel, _viewModel.ConnectionTestProxyUrl1TitleText, proxyUrl1Box);
        AddConnectionTestUrlEditorRow(panel, _viewModel.ConnectionTestProxyUrl2TitleText, proxyUrl2Box);
        AddConnectionTestUrlEditorRow(panel, _viewModel.ConnectionTestDirectUrlTitleText, directUrlBox);
        panel.Children.Add(validationError);

        Button restoreButton = new()
        {
            Name = "RestoreConnectionTestUrlsButton",
            Content = _viewModel.ResetText,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        restoreButton.Click += (_, _) =>
        {
            proxyUrl1Box.Text = SettingsViewModel.DefaultConnectionTestProxyUrl1;
            proxyUrl2Box.Text = SettingsViewModel.DefaultConnectionTestProxyUrl2;
            directUrlBox.Text = SettingsViewModel.DefaultConnectionTestDirectUrl;
            validationError.IsOpen = false;
        };
        panel.Children.Add(restoreButton);
        return panel;
    }

    private static void AddConnectionTestUrlEditorRow(StackPanel panel, string label, TextBox textBox)
    {
        AutomationProperties.SetName(textBox, label);
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });
        panel.Children.Add(textBox);
    }

    /// <summary>Shows a short prompt explaining that the edited setting applies after restart.</summary>
    private async Task ShowRestartRequiredDialogAsync(CancellationToken cancellationToken)
    {
        ThemedContentDialog dialog = new()
        {
            Title = _getString("Settings.RestartRequired.Title"),
            Content = _getString("Settings.RestartRequired.Message"),
            CloseButtonText = _getString("Command.Close"),
            XamlRoot = GetDialogXamlRoot(),
        };

        await dialog.ShowManagedAsync(cancellationToken);
    }

    private void TriggersEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings || sender is not ToggleSwitch toggle || toggle.IsOn == _viewModel.TriggersEnabled)
        {
            return;
        }

        _viewModel.SetTriggersEnabled(toggle.IsOn);
        UpdateRestartRequiredState();
    }

    private void TrayUseMonochromeInactiveIconToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings || sender is not ToggleSwitch toggle || toggle.IsOn == _viewModel.TrayUseMonochromeInactiveIcon)
        {
            return;
        }

        _viewModel.SetTrayUseMonochromeInactiveIcon(toggle.IsOn);
    }

    private async void ResetBasicSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetBasicSettingsToDefaults);
    }

    private async void ResetStartupSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetStartupSettingsToDefaultsAsync);
    }

    private async void ResetNotificationSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetNotificationSettingsToDefaults);
    }

    private async void ResetTriggerSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetTriggerSettingsToDefaults);
    }

    private async void ResetTraySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetTraySettingsToDefaults);
    }

    private async void ResetTransparentProxySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetTransparentProxySettingsToDefaults, includeServiceDeploymentNote: true);
    }

    private async void ResetProxySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetProxySettingsToDefaults, includeServiceDeploymentNote: true);
    }

    private async void ResetWindowsNativeSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetWindowsNativeSettingsToDefaults, includeServiceDeploymentNote: true);
    }

    private async void ResetMainlandChinaSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetMainlandChinaSettingsToDefaults);
    }

    /// <summary>Confirms and applies a settings-group default reset.</summary>
    /// <param name="resetAction">Group reset action. Must not be null.</param>
    /// <param name="includeServiceDeploymentNote">Whether to append the Installer-owned service notice.</param>
    private Task ResetSettingsGroupAsync(Action resetAction, bool includeServiceDeploymentNote = false)
    {
        ArgumentNullException.ThrowIfNull(resetAction);
        return ResetSettingsGroupAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            resetAction();
            return Task.CompletedTask;
        }, includeServiceDeploymentNote);
    }

    /// <summary>Owns an asynchronous group reset until its activation or compensation has drained.</summary>
    private Task ResetSettingsGroupAsync(
        Func<CancellationToken, Task> resetAction,
        bool includeServiceDeploymentNote = false)
    {
        return RunPageOperationAsync(token => ResetSettingsGroupCoreAsync(resetAction, includeServiceDeploymentNote, token));
    }

    private async Task ResetSettingsGroupCoreAsync(Func<CancellationToken, Task> resetAction, bool includeServiceDeploymentNote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resetAction);
        string message = _viewModel.ResetGroupConfirmMessageText;
        if (includeServiceDeploymentNote)
        {
            message = $"{message}{Environment.NewLine}{Environment.NewLine}{_viewModel.ResetGroupServiceDeploymentNoteText}";
        }

        ThemedContentDialog dialog = new()
        {
            Title = _viewModel.ResetGroupConfirmTitleText,
            Content = message,
            PrimaryButtonText = _viewModel.ResetGroupToDefaultsText,
            CloseButtonText = _getString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = GetDialogXamlRoot(),
        };

        if (await dialog.ShowManagedAsync(cancellationToken) is ContentDialogResult.Primary)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await resetAction(cancellationToken);
        }
    }

    /// <summary>Opens the searchable tray feature selector.</summary>
    private async void EditTrayVisibleFeaturesButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            HashSet<string> selectedIds = new(
                _viewModel.TrayVisibleFeatureIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
            SearchableOptionList optionList = new()
            {
                SearchPlaceholder = _viewModel.TrayVisibleFeatureSearchPlaceholderText,
                AllowMultiple = true,
                MaxListHeight = 360,
            };
            optionList.SetOptions(SettingsViewModel.TrayFeatureDefinitions.Select(feature => new SearchableOptionItem(
                feature.Id,
                _getString(feature.TitleKey),
                _viewModel.TraySectionTitleText,
                _getString(feature.DescriptionKey),
                feature.Glyph,
                feature.Id,
                selectedIds.Contains(feature.Id))));

            ThemedContentDialog dialog = new()
            {
                Title = _viewModel.TrayVisibleFeaturesTitleText,
                Content = optionList,
                PrimaryButtonText = _getString("Command.Save"),
                CloseButtonText = _getString("Command.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = GetDialogXamlRoot(),
            };

            if (await dialog.ShowManagedAsync(cancellationToken) is ContentDialogResult.Primary)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _viewModel.SetTrayVisibleFeatureIds(optionList.SelectedOptions.Select(static option => option.Id));
            }
        });
    }

    /// <summary>Runs startup conflict detection immediately and shows the shared result dialog.</summary>
    /// <param name="sender">Clicked button. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void CheckStartupConflictsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            IReadOnlyList<StartupConflictIssue> issues = await _viewModel.CheckStartupConflictsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await StartupConflictDialogPresenter.ShowAsync(
                GetDialogXamlRoot(), issues, _getString, _errorSink, cancellationToken);
        });
    }

    /// <summary>Shows the startup prompt immediately.</summary>
    private async void ShowStartupPromptButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            await _startupGuide.ShowAsync(GetDialogXamlRoot(), cancellationToken);
        });
    }

    /// <summary>Registers the startup restore fallback helper.</summary>
    private void RegisterStartupRestoreFallbackButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RegisterStartupRestoreFallback();
    }

    /// <summary>Refreshes startup restore fallback registration status.</summary>
    private void DetectStartupRestoreFallbackButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshStartupRestoreFallbackStatus();
    }

    /// <summary>Removes the startup restore fallback registration.</summary>
    private void RemoveStartupRestoreFallbackRegistrationButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RemoveStartupRestoreFallbackRegistration();
    }

    /// <summary>Exports settings through the shared, page-owned backup workflow.</summary>
    private async void ExportDataPackageButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(token => _dataPackages.ExportAsync(GetDialogXamlRoot(), token));
    }

    /// <summary>Imports settings through the shared transaction and reloads only after successful completion.</summary>
    private async void ImportDataPackageButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async token =>
        {
            if (await _dataPackages.ImportAsync(GetDialogXamlRoot(), token))
            {
                token.ThrowIfCancellationRequested();
                _viewModel.ReloadAfterDataImport();
            }
        });
    }

    private Task RunPageOperationAsync(Func<CancellationToken, Task> operation)
    {
        return !_isLoaded
            ? Task.CompletedTask
            : _pageOperations.RunAsync(async token =>
            {
                IsEnabled = false;
                try
                {
                    await operation(token);
                }
                finally
                {
                    IsEnabled = true;
                }
            });
    }

    /// <summary>Builds the network repair dialog content.</summary>
    /// <returns>Dialog content panel.</returns>
    private ScrollViewer BuildNetworkRepairPanel(PageOperationSession diagnostics, CancellationToken cancellationToken)
    {
        StackPanel panel = new()
        {
            Spacing = 8,
            MinWidth = 360,
            MaxWidth = 640,
        };

        AddDiagnosticRow(panel, _viewModel.WslDiagnosticTitleText, nameof(SettingsViewModel.WslDiagnosticStatusText), "Wsl", diagnostics, cancellationToken);
        AddDiagnosticRow(panel, _viewModel.TerminalDiagnosticTitleText, nameof(SettingsViewModel.TerminalDiagnosticStatusText), "Terminal", diagnostics, cancellationToken);
        AddDiagnosticRow(panel, _viewModel.StoreDiagnosticTitleText, nameof(SettingsViewModel.StoreDiagnosticStatusText), "MicrosoftStore", diagnostics, cancellationToken);

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(240, XamlRoot.Size.Height - 220),
            Padding = new Thickness(0, 0, 12, 0),
        };
    }

    /// <summary>Adds one diagnostic target row to the dialog panel.</summary>
    /// <param name="panel">Target panel. Must not be null.</param>
    /// <param name="title">Target title. Must not be null.</param>
    /// <param name="statusPropertyName">Bindable status property name. Must not be null.</param>
    /// <param name="targetTag">Diagnostic target tag. Must not be null.</param>
    /// <param name="diagnostics">Owns the diagnostic operations accepted by this dialog.</param>
    /// <param name="cancellationToken">Cancels work when the page leaves the visual tree.</param>
    private void AddDiagnosticRow(
        StackPanel panel,
        string title,
        string statusPropertyName,
        string targetTag,
        PageOperationSession diagnostics,
        CancellationToken cancellationToken)
    {
        Grid row = new()
        {
            Style = (Style)Application.Current.Resources["ClashCardGridStyle"],
            MinHeight = 68,
            RowSpacing = 8,
        };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        StackPanel textPanel = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
        };
        textPanel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
        });

        TextBlock statusText = new()
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        statusText.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(statusPropertyName) });
        textPanel.Children.Add(statusText);
        row.Children.Add(textPanel);

        StackPanel buttonPanel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetRow(buttonPanel, 1);
        AddDiagnosticButton(buttonPanel, "\uE9D9", _viewModel.DiagnoseText, $"{targetTag}:Diagnose", diagnostics, cancellationToken);
        AddDiagnosticButton(buttonPanel, "\uE73E", _viewModel.ApplyText, $"{targetTag}:Apply", diagnostics, cancellationToken);
        AddDiagnosticButton(buttonPanel, "\uE72C", _viewModel.ResetText, $"{targetTag}:Reset", diagnostics, cancellationToken);
        row.Children.Add(buttonPanel);

        panel.Children.Add(row);
    }

    /// <summary>Adds one command button to a diagnostic row.</summary>
    private void AddDiagnosticButton(
        StackPanel panel,
        string glyph,
        string text,
        string commandParameter,
        PageOperationSession diagnostics,
        CancellationToken cancellationToken)
    {
        Button button = new()
        {
            Command = new AsyncRelayCommand(
                _ => diagnostics.RunAsync(
                    token => _viewModel.WindowsDiagnosticCommand.ExecuteAsync(commandParameter, token),
                    cancellationToken),
                _errorSink,
                operationName: "settings-diagnostic-command"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        StackPanel content = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
        };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = text });
        button.Content = content;

        panel.Children.Add(button);
    }

    /// <summary>Shows a two-step confirmation and restores all settings to defaults.</summary>
    private async void ResetAllSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            if (!await ConfirmAsync(
                    _getString("Settings.ResetAllSettings.Title"),
                    _getString("Settings.ResetAllSettings.Confirm"),
                    _viewModel.ResetText, cancellationToken)
                || !await ConfirmAsync(
                    _getString("Settings.ResetAllSettings.SecondConfirm.Title"),
                    _getString("Settings.ResetAllSettings.SecondConfirm"),
                    _viewModel.ResetText, cancellationToken))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _viewModel.ResetAllSettingsAsync(cancellationToken);
        });
    }

    /// <summary>Shows a three-step confirmation and clears all local application data.</summary>
    private async void ClearAllDataButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            if (!await ConfirmAsync(
                    _getString("Settings.ClearAllData.Title"),
                    _getString("Settings.ClearAllData.Confirm"),
                    _viewModel.CleanupText, cancellationToken)
                || !await ConfirmAsync(
                    _getString("Settings.ClearAllData.SecondConfirm.Title"),
                    _getString("Settings.ClearAllData.SecondConfirm"),
                    _viewModel.CleanupText, cancellationToken)
                || !await ConfirmAsync(
                    _getString("Settings.ClearAllData.FinalConfirm.Title"),
                    _getString("Settings.ClearAllData.FinalConfirm"),
                    _viewModel.CleanupText, cancellationToken))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _viewModel.ClearAllDataAsync(cancellationToken);
        });
    }

    /// <summary>Shows a destructive-action confirmation dialog.</summary>
    private async Task<bool> ConfirmAsync(string title, string content, string primaryButtonText, CancellationToken cancellationToken)
    {
        ThemedContentDialog dialog = new()
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = _getString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = GetDialogXamlRoot(),
        };

        return await dialog.ShowManagedAsync(cancellationToken) is ContentDialogResult.Primary;
    }

}
