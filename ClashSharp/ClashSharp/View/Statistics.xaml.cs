/*
 * Statistics Page
 * Stub page for traffic and performance statistics display
 *
 * @author: WaterRun
 * @file: View/Statistics.xaml.cs
 * @date: 2026-04-08
 */

using ClashSharp.Service;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Stub page for displaying traffic and performance statistics.</summary>
/// <remarks>
/// Invariants: None at this stage.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: None.
/// </remarks>
public sealed partial class Statistics : Page
{
    /// <summary>Initializes the statistics page and applies localized text.</summary>
    public Statistics()
    {
        InitializeComponent();
        PageTitleText.Text = LocalizationService.Instance.GetString("Nav.Statistics");
        StubText.Text = LocalizationService.Instance.GetString("Page.Stub");
    }
}