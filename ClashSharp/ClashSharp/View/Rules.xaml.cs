/*
 * Rules Page
 * Provides the rule-provider and rule-hit inspection surface for ClashSharp takeover decisions
 *
 * @author: WaterRun
 * @file: View/Rules.xaml.cs
 * @date: 2026-06-15
 */

using ClashSharp.Service;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page reserved for rule-provider state, rule hit statistics, and route decisions.</summary>
/// <remarks>
/// Invariants: The page currently exposes static shell text only.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Reads localized strings during construction.
/// </remarks>
public sealed partial class Rules : Page
{
    /// <summary>Initializes the rules page and applies localized shell text.</summary>
    public Rules()
    {
        InitializeComponent();
        PageTitleText.Text = LocalizationService.Instance.GetString("Nav.Rules");
        DescriptionText.Text = LocalizationService.Instance.GetString("Page.Rules.Description");
    }
}
