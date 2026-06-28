/*
 * Themed Content Dialog
 * Ensures WinUI content dialogs follow the app-selected theme
 *
 * @author: WaterRun
 * @file: View/ThemedContentDialog.cs
 * @date: 2026-06-28
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>ContentDialog that inherits the current application root theme.</summary>
internal sealed class ThemedContentDialog : ContentDialog
{
    public ThemedContentDialog()
    {
        if (App.MainWindow?.Content is FrameworkElement root)
        {
            RequestedTheme = root.RequestedTheme;
        }
    }
}
