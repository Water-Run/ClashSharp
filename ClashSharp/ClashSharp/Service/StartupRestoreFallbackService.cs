/*
 * Startup Restore Fallback Service
 * Registers a lightweight login helper that clears stale Windows proxy state
 *
 * @author: WaterRun
 * @file: Service/StartupRestoreFallbackService.cs
 * @date: 2026-06-25
 */

using System;
using System.Diagnostics;
using System.IO;
using ClashSharp.Model;
using Microsoft.Win32;

namespace ClashSharp.Service;

/// <summary>Current registration state for the startup restore fallback helper.</summary>
public readonly record struct StartupRestoreFallbackStatus(bool IsRegistered, string CommandLine);

/// <summary>Registers a lightweight current-user login helper for stale proxy cleanup.</summary>
/// <remarks>
/// Invariants: Registration is stored only under HKCU Run.
/// Thread safety: Public registry writes are serialized.
/// Side effects: Writes or removes one HKCU Run value and can execute startup proxy recovery.
/// </remarks>
public sealed class StartupRestoreFallbackService
{
    public const string HelperArgument = "--restore-proxy-on-startup";

    public static StartupRestoreFallbackService Instance { get; } = new();

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ClashSharp.ProxyRestoreFallback";

    private readonly object _syncLock = new();

    private StartupRestoreFallbackService()
    {
    }

    /// <summary>Gets whether the helper is currently registered.</summary>
    public bool IsRegistered() => GetStatus().IsRegistered;

    /// <summary>Gets the current helper registration status.</summary>
    public StartupRestoreFallbackStatus GetStatus()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        string commandLine = key?.GetValue(RunValueName) as string ?? string.Empty;
        bool isRegistered = commandLine.Contains(HelperArgument, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(commandLine).Contains("ClashSharp", StringComparison.OrdinalIgnoreCase);
        return new StartupRestoreFallbackStatus(isRegistered, commandLine);
    }

    /// <summary>Registers the current executable as the login helper.</summary>
    public void Register()
    {
        string executablePath = ResolveExecutablePath();
        string commandLine = Quote(executablePath) + " " + HelperArgument;

        lock (_syncLock)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("Could not open HKCU Run registry key.");
            key.SetValue(RunValueName, commandLine, RegistryValueKind.String);
        }
    }

    /// <summary>Unregisters the login helper.</summary>
    public void Uninstall()
    {
        lock (_syncLock)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Runs stale proxy recovery once for the helper process.</summary>
    public ProxyRecoveryResult RunRestoreOnce()
    {
        return ProxyRecoveryService.Instance.ApplyStartupRecoveryIfNeeded();
    }

    private static string ResolveExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return processPath;
        }

        return Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Could not resolve current executable path.");
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
