/*
 * Application Theme Service
 * Applies user-selected display style to the WinUI root element
 *
 * @author: WaterRun
 * @file: Service/AppThemeService.cs
 * @date: 2026-06-17
 */

using ClashSharp.Model;
using Microsoft.UI.Xaml;

namespace ClashSharp.Service;

/// <summary>Applies the configured app display style to the active window.</summary>
internal static class AppThemeService
{
    /// <summary>Applies <paramref name="mode"/> to the main window root when available.</summary>
    public static void Apply(AppThemeMode mode)
    {
        if (App.MainWindow?.Content is not FrameworkElement root)
        {
            return;
        }

        Apply(root, mode);
    }

    /// <summary>Applies <paramref name="mode"/> to a specific root element.</summary>
    public static void Apply(FrameworkElement root, AppThemeMode mode)
    {
        root.RequestedTheme = mode switch
        {
            AppThemeMode.Light => ElementTheme.Light,
            AppThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }
}
