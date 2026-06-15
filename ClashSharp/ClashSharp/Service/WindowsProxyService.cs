/*
 * Windows Proxy Service
 * Provides controlled access to per-user Windows system proxy settings
 *
 * @author: WaterRun
 * @file: Service/WindowsProxyService.cs
 * @date: 2026-06-15
 */

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ClashSharp.Model;
using Microsoft.Win32;

namespace ClashSharp.Service;

/// <summary>Reads and updates Windows per-user system proxy settings.</summary>
/// <remarks>
/// Invariants: All writes target the current user's Internet Settings registry key.
/// Thread safety: Public methods serialize registry writes through a private lock.
/// Side effects: Mutates HKCU proxy settings and notifies WinINet consumers after writes.
/// </remarks>
public sealed class WindowsProxyService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="WindowsProxyService"/> instance.</value>
    public static WindowsProxyService Instance { get; } = new();

    /// <summary>Synchronization object guarding registry writes for this service lifetime.</summary>
    private readonly object _syncLock = new();

    /// <summary>Registry path for current-user Windows Internet proxy settings.</summary>
    private const string InternetSettingsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>Registry value name for the Windows proxy enabled switch.</summary>
    private const string ProxyEnableValueName = "ProxyEnable";

    /// <summary>Registry value name for the Windows proxy server string.</summary>
    private const string ProxyServerValueName = "ProxyServer";

    /// <summary>WinINet option notifying consumers that settings changed.</summary>
    private const int InternetOptionSettingsChanged = 39;

    /// <summary>WinINet option refreshing current Internet settings.</summary>
    private const int InternetOptionRefresh = 37;

    /// <summary>Initializes a new Windows proxy service instance.</summary>
    private WindowsProxyService()
    {
    }

    /// <summary>Reads the current user's Windows proxy state from the registry.</summary>
    /// <returns>The current <see cref="WindowsProxyState"/> snapshot.</returns>
    /// <exception cref="InvalidOperationException">The Windows Internet Settings registry key cannot be opened.</exception>
    public WindowsProxyState GetCurrentState()
    {
        using RegistryKey key = OpenInternetSettingsKey(writable: false);
        bool isEnabled = key.GetValue(ProxyEnableValueName) is int enabledValue && enabledValue != 0;
        string proxyServer = key.GetValue(ProxyServerValueName) as string ?? string.Empty;
        return new WindowsProxyState(isEnabled, proxyServer);
    }

    /// <summary>Enables Windows system proxy for the current user with <paramref name="proxyServer"/>.</summary>
    /// <param name="proxyServer">Proxy server string accepted by Windows, such as "127.0.0.1:7890"; must not be null or whitespace.</param>
    /// <exception cref="ArgumentNullException"><paramref name="proxyServer"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="proxyServer"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The Windows Internet Settings registry key cannot be opened.</exception>
    /// <exception cref="Win32Exception">Windows rejects the proxy change notification.</exception>
    public void EnableProxy(string proxyServer)
    {
        ArgumentNullException.ThrowIfNull(proxyServer);

        if (string.IsNullOrWhiteSpace(proxyServer))
        {
            throw new ArgumentException("Proxy server must not be empty.", nameof(proxyServer));
        }

        lock (_syncLock)
        {
            using RegistryKey key = OpenInternetSettingsKey(writable: true);
            key.SetValue(ProxyServerValueName, proxyServer, RegistryValueKind.String);
            key.SetValue(ProxyEnableValueName, 1, RegistryValueKind.DWord);
            NotifyProxySettingsChanged();
        }
    }

    /// <summary>Disables Windows system proxy for the current user while preserving the stored server string.</summary>
    /// <exception cref="InvalidOperationException">The Windows Internet Settings registry key cannot be opened.</exception>
    /// <exception cref="Win32Exception">Windows rejects the proxy change notification.</exception>
    public void DisableProxy()
    {
        lock (_syncLock)
        {
            using RegistryKey key = OpenInternetSettingsKey(writable: true);
            key.SetValue(ProxyEnableValueName, 0, RegistryValueKind.DWord);
            NotifyProxySettingsChanged();
        }
    }

    /// <summary>Opens the current user's Windows Internet Settings registry key.</summary>
    /// <param name="writable">True to request write access; false to request read-only access.</param>
    /// <returns>An opened registry key owned by the caller.</returns>
    /// <exception cref="InvalidOperationException">The registry key cannot be opened.</exception>
    private static RegistryKey OpenInternetSettingsKey(bool writable)
    {
        return Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable)
            ?? throw new InvalidOperationException("Windows Internet Settings registry key could not be opened.");
    }

    /// <summary>Notifies WinINet consumers that Windows proxy settings changed.</summary>
    /// <exception cref="Win32Exception">A WinINet notification call fails.</exception>
    private static void NotifyProxySettingsChanged()
    {
        if (!InternetSetOption(nint.Zero, InternetOptionSettingsChanged, nint.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!InternetSetOption(nint.Zero, InternetOptionRefresh, nint.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Sets a WinINet option for the current process or global settings.</summary>
    /// <param name="internet">Internet handle; zero applies the option globally for supported options.</param>
    /// <param name="option">WinINet option identifier.</param>
    /// <param name="buffer">Option data buffer pointer; may be zero for notification options.</param>
    /// <param name="bufferLength">Option data buffer length in bytes.</param>
    /// <returns>True when the option call succeeds; otherwise false.</returns>
    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(nint internet, int option, nint buffer, int bufferLength);
}
