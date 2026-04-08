/*
 * Settings Page
 * Stub page for application settings including language, theme, and proxy core configuration
 *
 * @author: WaterRun
 * @file: View/Settings.xaml.cs
 * @date: 2026-04-08
 */

using ClashSharp.Service;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Stub page for application-wide settings such as language, theme, and proxy core configuration.</summary>
/// <remarks>
/// Invariants: None at this stage.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: None.
/// </remarks>
public sealed partial class Settings : Page
{
    /// <summary>Initializes the settings page and applies localized text.</summary>
    public Settings()
    {
        InitializeComponent();
        PageTitleText.Text = LocalizationService.Instance.GetString("Nav.Settings");
        StubText.Text = LocalizationService.Instance.GetString("Page.Stub");
    }
}