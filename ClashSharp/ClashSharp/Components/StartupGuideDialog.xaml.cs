using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Components;

/// <summary>Renders a pre-collected startup-health snapshot in a reusable content dialog.</summary>
/// <remarks>
/// Invariants: The dialog renders exactly the immutable check snapshot supplied by its presenter.
/// Thread safety: Must be created and shown on the UI thread.
/// Side effects: None until the dialog is shown by a caller.
/// </remarks>
public sealed partial class StartupGuideDialog : ContentDialog, ILocalizedPresentation
{
    private XamlRoot? _layoutRoot;
    private readonly Func<string, string> _getString;
    private readonly bool _allowNavigation;
    private StartupCheckDisplay[] _rows = [];

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
        DefaultButton = ContentDialogButton.Close;
        UpdateSnapshot(checks);
        Loaded += OnLoaded;
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

    internal void ShowRefreshFailure()
    {
        RefreshError.Visibility = Visibility.Visible;
        RefreshError.IsOpen = true;
    }

    internal void UpdateSnapshot(IReadOnlyList<StartupCheckItem> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        _rows = checks.Select(check => new StartupCheckDisplay(check, _getString, _allowNavigation)).ToArray();
        ChecksList.ItemsSource = _rows;
        RefreshLocalization();
        RefreshError.IsOpen = false;
        RefreshError.Visibility = Visibility.Collapsed;
    }

    /// <summary>Relocalizes the retained snapshot without repeating checks or clearing progress and failure state.</summary>
    public void RefreshLocalization()
    {
        Title = _getString("StartupPrompt.Title");
        CloseButtonText = _getString("Command.Close");
        GuideDescriptionText.Text = _getString("StartupPrompt.Description");
        RefreshButton.Content = _getString("StartupPrompt.Recheck");
        BusyText.Text = _getString("StartupPrompt.Checking");
        RefreshError.Message = _getString("StartupPrompt.RefreshFailed");
        foreach (StartupCheckDisplay row in _rows) { row.RefreshLocalization(_getString); }
        int attention = _rows.Count(static row => row.Model.State is StartupCheckState.Attention or StartupCheckState.Unavailable);
        int optional = _rows.Count(static row => row.Model.State == StartupCheckState.Optional);
        SummaryBar.Severity = attention > 0 ? InfoBarSeverity.Warning
            : optional > 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Success;
        SummaryBar.Title = attention > 0
            ? string.Format(CultureInfo.CurrentCulture, _getString("StartupPrompt.Summary.Attention.Format"), attention)
            : _getString("StartupPrompt.Summary.Ready");
        SummaryBar.Message = string.Format(CultureInfo.CurrentCulture,
            _getString(optional > 0 ? "StartupPrompt.Summary.Optional.Format" : "StartupPrompt.Summary.Checked.Format"),
            optional > 0 ? optional : _rows.Length);
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => UpdateContentBounds();

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _layoutRoot = XamlRoot;
        if (_layoutRoot is not null)
        {
            _layoutRoot.Changed += OnRootChanged;
            UpdateContentBounds();
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

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateContentBounds();

    private void UpdateContentBounds()
    {
        if (XamlRoot is { } root)
        {
            // A preferred reading width avoids the narrow, content-sized column of stacked diagnostics.
            GuideContent.Width = Math.Clamp(root.Size.Width - 96, 240, 640);
            // ItemsControl templates can otherwise measure Auto columns beyond the scroll viewport.
            // Constrain the entire row, including its status, before text is measured; reserve the scrollbar.
            ChecksList.Width = Math.Max(0, GuideContent.Width - ChecksScroll.Padding.Right - 16);
            // The Grid gives the checks the native dialog's remaining height after its scaled header.
        }
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
