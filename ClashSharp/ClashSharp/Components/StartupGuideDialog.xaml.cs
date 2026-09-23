using System;
using System.Collections.Generic;
using ClashSharp.Model;
using Microsoft.UI.Xaml;
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

    /// <summary>Initializes the startup guide from a pre-collected health snapshot.</summary>
    /// <param name="checks">Health rows collected before the visual component is created.</param>
    /// <param name="getString">Localization dependency for dialog-only display text.</param>
    public StartupGuideDialog(
        IReadOnlyList<StartupCheckItem> checks,
        Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(getString);
        InitializeComponent();
        Title = getString("Settings.StartupGuide.Title");
        CloseButtonText = getString("Command.Close");
        GuideDescriptionText.Text = getString("Settings.StartupGuide.Description");
        ChecksList.ItemsSource = checks;
        Opened += OnOpened;
        Closed += OnClosed;
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
            ChecksScroll.MaxHeight = Math.Max(120, _layoutRoot.Size.Height - 260);
        }
    }

    private void StatusIcon_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FontIcon { DataContext: StartupCheckItem check } icon)
        {
            icon.Glyph = check.IsHealthy ? "\uE73E" : "\uE7BA";
        }
    }
}
