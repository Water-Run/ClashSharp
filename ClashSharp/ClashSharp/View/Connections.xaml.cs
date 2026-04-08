/*
 * Connections Page
 * Stub page for active connection monitoring (reserved, not currently in main navigation)
 *
 * @author: WaterRun
 * @file: View/Connections.xaml.cs
 * @date: 2026-04-08
 */

using ClashSharp.Service;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Stub page for monitoring and managing active network connections; reserved for future navigation integration.</summary>
/// <remarks>
/// Invariants: None at this stage.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: None.
/// </remarks>
public sealed partial class Connections : Page
{
    /// <summary>Initializes the connections page and applies localized text.</summary>
    public Connections()
    {
        InitializeComponent();
        PageTitleText.Text = LocalizationService.Instance.GetString("Nav.Connections");
        StubText.Text = LocalizationService.Instance.GetString("Page.Stub");
    }
}