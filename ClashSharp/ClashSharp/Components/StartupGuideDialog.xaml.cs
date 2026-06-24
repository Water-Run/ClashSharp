/*
 * Startup Guide Dialog
 * Reserves the reusable startup guide dialog shell for quick-start content
 *
 * @author: WaterRun
 * @file: Components/StartupGuideDialog.xaml.cs
 * @date: 2026-06-24
 */

using ClashSharp.Service;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Components;

/// <summary>Reusable dialog shell reserved for startup guide content.</summary>
/// <remarks>
/// Invariants: Dialog text is resolved from localization resources during construction.
/// Thread safety: Must be created and shown on the UI thread.
/// Side effects: None until the dialog is shown by a caller.
/// </remarks>
public sealed partial class StartupGuideDialog : ContentDialog
{
    /// <summary>Initializes the startup guide dialog shell with localized text.</summary>
    public StartupGuideDialog()
    {
        InitializeComponent();
        Title = LocalizationService.Instance.GetString("Settings.StartupGuide.Title");
        CloseButtonText = LocalizationService.Instance.GetString("Command.Close");
        GuideDescriptionText.Text = LocalizationService.Instance.GetString("Settings.StartupGuide.Description");
    }
}
