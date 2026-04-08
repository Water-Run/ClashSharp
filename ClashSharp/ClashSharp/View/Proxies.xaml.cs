/*
 * Proxies Page
 * Stub page for proxy group and node management
 *
 * @author: WaterRun
 * @file: View/Proxies.xaml.cs
 * @date: 2026-04-08
 */

using ClashSharp.Service;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Stub page for managing proxy groups and individual proxy nodes.</summary>
/// <remarks>
/// Invariants: None at this stage.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: None.
/// </remarks>
public sealed partial class Proxies : Page
{
    /// <summary>Initializes the proxies page and applies localized text.</summary>
    public Proxies()
    {
        InitializeComponent();
        PageTitleText.Text = LocalizationService.Instance.GetString("Nav.Proxies");
        StubText.Text = LocalizationService.Instance.GetString("Page.Stub");
    }
}