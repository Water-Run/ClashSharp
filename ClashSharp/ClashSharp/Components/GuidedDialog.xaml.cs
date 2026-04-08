/*
 * Guided Dialog Window
 * A secondary Mica-backed window shell reserved for step-by-step guided user interactions
 *
 * @author: WaterRun
 * @file: Components/GuidedDialog.xaml.cs
 * @date: 2026-04-08
 */

using Microsoft.UI.Xaml;

namespace ClashSharp.Components;

/// <summary>A secondary window shell reserved for guided dialog workflows.</summary>
/// <remarks>
/// Invariants: None at this stage.
/// Thread safety: Must be created and accessed from the UI thread only.
/// Side effects: None.
/// </remarks>
public sealed partial class GuidedDialog : Window
{
    /// <summary>Initializes the guided dialog window and its XAML content.</summary>
    public GuidedDialog()
    {
        InitializeComponent();
    }
}