/*
 * Application Entry Point
 * Bootstraps the Clash# proxy management application, creates the main window, and manages its lifetime
 *
 * @author: WaterRun
 * @file: App.xaml.cs
 * @date: 2026-06-15
 */

using System;
using System.ComponentModel;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
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
        LocalizationService.Instance.CurrentLanguage = AppSettingsService.Instance.DisplayLanguage;
        if (args.Arguments.Contains(StartupRestoreFallbackService.HelperArgument, StringComparison.OrdinalIgnoreCase))
        {
            ApplyStartupRestoreFallback();
            Exit();
            return;
        }

        AppSettingsAuditLogService.Instance.Start();
        TriggerService.Instance.Start();
        _mainWindow = new MainWindow();
        _mainWindow.Activate();
        _ = Task.Run(ApplyStartupProxyRecovery);
        ConnectionSamplingService.Instance.StartIfEnabled();
    }

    /// <summary>Runs the lightweight login fallback helper path and exits without showing UI.</summary>
    private static void ApplyStartupRestoreFallback()
    {
        try
        {
            ProxyRecoveryResult result = StartupRestoreFallbackService.Instance.RunRestoreOnce();
            if (result.WasApplied)
            {
                LogStorageService.Instance.AppendLog("Info", "StartupRestoreFallback", result.Message, null);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            LogStorageService.Instance.AppendLog(
                "Warning",
                "StartupRestoreFallback",
                LocalizationService.Instance.GetString("ProxyRecovery.StartupFailed"),
                exception.Message);
        }
    }

    /// <summary>Applies startup stale proxy recovery after the main window is shown.</summary>
    /// <remarks>
    /// Recovery is best-effort: failures are persisted to the SQLite log store and do not prevent UI startup.
    /// </remarks>
    private static void ApplyStartupProxyRecovery()
    {
        try
        {
            ProxyRecoveryResult result = ProxyRecoveryService.Instance.ApplyStartupRecoveryIfNeeded();
            if (result.WasApplied)
            {
                LogStorageService.Instance.AppendLog("Info", "ProxyRecovery", result.Message, null);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            LogStorageService.Instance.AppendLog(
                "Warning",
                "ProxyRecovery",
                LocalizationService.Instance.GetString("ProxyRecovery.StartupFailed"),
                exception.Message);
        }
    }
}
