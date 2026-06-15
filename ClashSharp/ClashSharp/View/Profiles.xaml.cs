/*
 * Profiles Page
 * Stub page for subscription profile management
 *
 * @author: WaterRun
 * @file: View/Profiles.xaml.cs
 * @date: 2026-06-15
 */

using ClashSharp.Service;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Stub page for managing subscription configuration profiles.</summary>
/// <remarks>
/// Invariants: None at this stage.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: None.
/// </remarks>
public sealed partial class Profiles : Page
{
    /// <summary>Initializes the profiles page and applies localized text.</summary>
    public Profiles()
    {
        InitializeComponent();
        PageTitleText.Text = LocalizationService.Instance.GetString("Nav.Profiles");
        DescriptionText.Text = LocalizationService.Instance.GetString("Page.Profiles.Description");
    }
}
