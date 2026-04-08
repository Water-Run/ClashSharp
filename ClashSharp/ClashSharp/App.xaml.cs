/*
 * Application Entry Point
 * Bootstraps the ClashSharp proxy management application, creates the main window, and manages its lifetime
 *
 * @author: WaterRun
 * @file: App.xaml.cs
 * @date: 2026-04-08
 */

using Microsoft.UI.Xaml;

namespace ClashSharp;

/// <summary>Application root class responsible for lifecycle management and global window access.</summary>
/// <remarks>
/// Invariants: <see cref="MainWindow"/> is assigned exactly once during <see cref="OnLaunched"/> and remains non-null thereafter.
/// Thread safety: All access occurs on the UI thread.
/// Side effects: Creates and activates the primary application window.
/// </remarks>
public partial class App : Application
{
    /// <summary>Backing field for the singleton main window reference.</summary>
    private static Window? _mainWindow;

    /// <summary>Gets the primary application window instance for global access.</summary>
    /// <value>The <see cref="Window"/> instance created during launch; null before <see cref="OnLaunched"/> completes.</value>
    public static Window? MainWindow => _mainWindow;

    /// <summary>Initializes the singleton application object and its XAML resources.</summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>Creates the main window and activates it when the application is launched.</summary>
    /// <param name="args">Launch activation details provided by the platform. Not null.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }
}