/*
 * Master Control Page
 * Stub page serving as the main dashboard with proxy status overview and quick actions
 *
 * @author: WaterRun
 * @file: View/MasterControl.xaml.cs
 * @date: 2026-04-08
 */

using ClashSharp.Service;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Stub page for the master control panel displaying proxy status overview and quick actions.</summary>
/// <remarks>
/// Invariants: None at this stage.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: None.
/// </remarks>
public sealed partial class MasterControl : Page
{
    /// <summary>Initializes the master control page and applies localized text.</summary>
    public MasterControl()
    {
        InitializeComponent();
        PageTitleText.Text = LocalizationService.Instance.GetString("Nav.MasterControl");
        StubText.Text = LocalizationService.Instance.GetString("Page.Stub");
    }
}