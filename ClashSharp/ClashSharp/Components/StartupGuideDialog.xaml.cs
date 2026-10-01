using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using ClashSharp.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Components;

/// <summary>Renders a pre-collected startup-health snapshot in a reusable content dialog.</summary>
/// <remarks>
/// Invariants: The dialog renders exactly the immutable check snapshot supplied by its presenter.
/// Thread safety: Must be created and shown on the UI thread.
/// Side effects: None until the dialog is shown by a caller.
/// </remarks>
public sealed partial class StartupGuideDialog : ContentDialog
{
    private XamlRoot? _layoutRoot;
    private readonly Func<string, string> _getString;
    private readonly bool _allowNavigation;

    /// <summary>Initializes the startup guide from a pre-collected health snapshot.</summary>
    /// <param name="checks">Health rows collected before the visual component is created.</param>
    /// <param name="getString">Localization dependency for dialog-only display text.</param>
    /// <param name="allowNavigation">Whether the owning presenter can handle related-page actions.</param>
    public StartupGuideDialog(
        IReadOnlyList<StartupCheckItem> checks,
        Func<string, string> getString,
        bool allowNavigation = true)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(getString);
        _getString = getString;
        _allowNavigation = allowNavigation;
        InitializeComponent();
        Title = getString("StartupPrompt.Title");
        CloseButtonText = getString("Command.Close");
        DefaultButton = ContentDialogButton.Close;
        GuideDescriptionText.Text = getString("StartupPrompt.Description");
        RefreshButton.Content = getString("StartupPrompt.Recheck");
        BusyText.Text = getString("StartupPrompt.Checking");
        RefreshError.Message = getString("StartupPrompt.RefreshFailed");
        UpdateSnapshot(checks);
        Opened += OnOpened;
        Closed += OnClosed;
    }

    internal string? RequestedNavigationTag { get; private set; }

    internal void SetRefreshCommand(ICommand command) => RefreshButton.Command = command;

    internal void SetRefreshing(bool refreshing)
    {
        BusyPanel.Visibility = refreshing ? Visibility.Visible : Visibility.Collapsed;
        RefreshProgress.IsActive = refreshing;
        ChecksList.IsEnabled = !refreshing;
    }

    internal void ShowRefreshFailure() => RefreshError.IsOpen = true;

    internal void UpdateSnapshot(IReadOnlyList<StartupCheckItem> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        StartupCheckItem[] snapshot = checks.ToArray();
        ChecksList.ItemsSource = snapshot;
        int attention = snapshot.Count(static check => check.State is StartupCheckState.Attention or StartupCheckState.Unavailable);
        int optional = snapshot.Count(static check => check.State == StartupCheckState.Optional);
        SummaryBar.Severity = attention > 0 ? InfoBarSeverity.Warning
            : optional > 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Success;
        SummaryBar.Title = attention > 0
            ? string.Format(CultureInfo.CurrentCulture, _getString("StartupPrompt.Summary.Attention.Format"), attention)
            : _getString("StartupPrompt.Summary.Ready");
        SummaryBar.Message = string.Format(CultureInfo.CurrentCulture,
            _getString(optional > 0 ? "StartupPrompt.Summary.Optional.Format" : "StartupPrompt.Summary.Checked.Format"),
            optional > 0 ? optional : snapshot.Length);
        RefreshError.IsOpen = false;
    }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _layoutRoot = XamlRoot;
        if (_layoutRoot is not null)
        {
            _layoutRoot.Changed += OnRootChanged;
            UpdateChecksHeight();
        }
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        if (_layoutRoot is not null)
        {
            _layoutRoot.Changed -= OnRootChanged;
            _layoutRoot = null;
        }
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateChecksHeight();

    private void UpdateChecksHeight()
    {
        if (_layoutRoot is not null)
        {
            ChecksScroll.MaxHeight = Math.Max(120, _layoutRoot.Size.Height - 380);
        }
    }

    private void StatusIcon_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FontIcon { DataContext: StartupCheckItem check } icon)
        {
            icon.Glyph = check.State switch
            {
                StartupCheckState.Healthy => "\uE73E",
                StartupCheckState.Optional => "\uE946",
                _ => "\uE7BA",
            };
        }
    }

    private void StatusText_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock { DataContext: StartupCheckItem check } text)
        {
            text.Text = _getString($"StartupPrompt.State.{check.State}");
        }
    }

    private void CheckAction_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not HyperlinkButton { DataContext: StartupCheckItem check } action) { return; }
        string? route = check.Kind switch
        {
            StartupCheckKind.Subscription => "Links",
            StartupCheckKind.TransparentProxy or StartupCheckKind.StartupRecovery or StartupCheckKind.SystemProxy => "Settings",
            _ => null,
        };
        action.Visibility = _allowNavigation && route is not null ? Visibility.Visible : Visibility.Collapsed;
        action.Tag = route;
        action.Content = _getString(check.Kind == StartupCheckKind.Subscription
            ? "StartupPrompt.Action.Subscription" : "StartupPrompt.Action.Settings");
        AutomationProperties.SetName(action, $"{check.Title}: {action.Content}");
    }

    private void CheckAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton { Tag: string route } && _allowNavigation)
        {
            RequestedNavigationTag = route;
            Hide();
        }
    }
}
