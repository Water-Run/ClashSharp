/*
 * Rules Page
 * Provides the rule-provider and rule-hit inspection surface for Clash# takeover decisions
 *
 * @author: WaterRun
 * @file: View/Rules.xaml.cs
 * @date: 2026-06-15
 */

using ClashSharp.Service;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for rule-provider state, rule hit statistics, and route decisions.</summary>
/// <remarks>
/// Invariants: Visible text and active profile rule rows are loaded during construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Reads localized strings and active profile rule metadata during construction.
/// </remarks>
public sealed partial class Rules : Page
{
    /// <summary>Initializes the rules page and applies localized shell text.</summary>
    public Rules()
    {
        InitializeComponent();
        PageTitleText.Text = LocalizationService.Instance.GetString("Nav.Rules");
        DescriptionText.Text = LocalizationService.Instance.GetString("Page.Rules.Description");
        RulesList.ItemsSource = RuleCatalogService.Instance.GetRules();
    }
}
