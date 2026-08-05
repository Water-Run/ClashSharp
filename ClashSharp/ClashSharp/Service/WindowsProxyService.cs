using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Model;

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
    public static WindowsProxyService Instance { get; } = new(
        new WindowsProxyRegistryStore(NotifyProxySettingsChanged),
        new WindowsProxyMutationJournalFileStore(
            System.IO.Path.Combine(
                AppDataPathService.ResolveLocalDataDirectory(),
                "WindowsProxyMutationJournal.json")),
        () => AppSettingsService.Instance.MixedPort);

    /// <summary>Synchronization object guarding registry writes for this service lifetime.</summary>
    private readonly object _syncLock = new();

    private readonly IWindowsProxyRegistryStore _registry;

    private readonly IWindowsProxyMutationJournalStore _mutationJournal;

    private readonly Func<int> _getManagedPort;

    /// <summary>WinINet option notifying consumers that settings changed.</summary>
    private const int InternetOptionSettingsChanged = 39;

    /// <summary>WinINet option refreshing current Internet settings.</summary>
    private const int InternetOptionRefresh = 37;

    /// <summary>Initializes a new Windows proxy service instance.</summary>
    internal WindowsProxyService(
        IWindowsProxyRegistryStore registry,
        IWindowsProxyMutationJournalStore mutationJournal,
        Func<int>? getManagedPort = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _mutationJournal = mutationJournal ?? throw new ArgumentNullException(nameof(mutationJournal));
        _getManagedPort = getManagedPort ?? (() => -1);
    }

    /// <summary>Reads the current user's Windows proxy state from the registry.</summary>
    /// <returns>The current <see cref="WindowsProxyState"/> snapshot.</returns>
    /// <exception cref="InvalidOperationException">The Windows Internet Settings registry key cannot be opened.</exception>
    public WindowsProxyState GetCurrentState()
    {
        WindowsProxyRegistrySnapshot snapshot = _registry.Read();
        return new WindowsProxyState(
            snapshot.ProxyEnable.Exists && snapshot.ProxyEnable.Value != 0,
            snapshot.ProxyServer.Value ?? string.Empty);
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
            WindowsProxyRegistrySnapshot current = _registry.Read();
            WindowsProxyMutationJournal? existing = _mutationJournal.Read();
            WindowsProxyRegistrySnapshot baseline = existing is null
                ? current
                : WindowsProxyOwnershipRestorer.MergeOwnedRestore(current, existing);
            WindowsProxyRegistrySnapshot applied = current with
            {
                ProxyEnable = new WindowsProxyDwordValue(true, 1),
                ProxyServer = new WindowsProxyStringValue(true, proxyServer, WindowsProxyStringKind.String),
                ProxyOverride = new WindowsProxyStringValue(true, "<local>", WindowsProxyStringKind.String),
                AutoConfigUrl = new WindowsProxyStringValue(false, null, WindowsProxyStringKind.None),
            };
            WindowsProxyMutationJournal pendingJournal = new(
                WindowsProxyMutationJournal.CurrentSchemaVersion,
                baseline,
                current,
                WindowsProxyMutationPhase.Applying,
                applied);

            // The recovery proof must reach durable storage before the first registry field changes.
            _mutationJournal.Write(pendingJournal);
            try
            {
                _registry.Write(applied);
                if (_registry.Read() != applied)
                {
                    throw new InvalidOperationException("Windows proxy settings did not match the journaled pending tuple after apply.");
                }

                _mutationJournal.Write(pendingJournal with
                {
                    Applied = applied,
                    Phase = WindowsProxyMutationPhase.Applied,
                    PendingApplied = null,
                });
            }
            catch (Exception applyFailure) when (!ExceptionGraphClassifier.IsProcessFatal(applyFailure))
            {
                try
                {
                    RestoreOwnedFields(pendingJournal);
                    _mutationJournal.Clear();
                }
                catch (Exception rollbackFailure) when (!ExceptionGraphClassifier.IsProcessFatal(rollbackFailure))
                {
                    throw new AggregateException(
                        "Windows proxy apply failed and its owned fields could not be restored.",
                        applyFailure,
                        rollbackFailure);
                }

                throw;
            }
        }
    }

    /// <summary>Releases Clash#-owned WinINet fields and restores their captured baseline values.</summary>
    /// <exception cref="InvalidOperationException">The Windows Internet Settings registry key cannot be opened.</exception>
    /// <exception cref="Win32Exception">Windows rejects the proxy change notification.</exception>
    public void DisableProxy()
    {
        lock (_syncLock)
        {
            WindowsProxyRegistrySnapshot current = _registry.Read();
            WindowsProxyMutationJournal? journal = _mutationJournal.Read();
            if (journal is not null)
            {
                WindowsProxyOwnershipRestorer.Restore(_registry, _mutationJournal);
                return;
            }

            if (IsLegacyManagedLoopbackProxy(current))
            {
                _registry.Write(current with
                {
                    ProxyEnable = new WindowsProxyDwordValue(true, 0),
                });
            }
        }
    }

    /// <summary>
    /// Restores only state proven by a durable ownership journal. Emergency crash recovery must
    /// not use the legacy loopback heuristic because an unowned same-port proxy is ambiguous.
    /// </summary>
    internal void RestoreOwnedProxy()
    {
        lock (_syncLock)
        {
            WindowsProxyOwnershipRestorer.Restore(_registry, _mutationJournal);
        }
    }

    /// <summary>Restores every field that still equals the corresponding Clash#-applied value.</summary>
    private void RestoreOwnedFields(WindowsProxyMutationJournal journal)
    {
        WindowsProxyRegistrySnapshot current = _registry.Read();
        _registry.Write(WindowsProxyOwnershipRestorer.MergeOwnedRestore(current, journal));
    }

    /// <summary>Recognizes only the exact current-port loopback endpoint used by pre-journal Clash# versions.</summary>
    private bool IsLegacyManagedLoopbackProxy(WindowsProxyRegistrySnapshot snapshot)
    {
        if (!snapshot.ProxyEnable.Exists
            || snapshot.ProxyEnable.Value == 0
            || !snapshot.ProxyServer.Exists
            || !Uri.TryCreate("http://" + snapshot.ProxyServer.Value, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return StringComparer.OrdinalIgnoreCase.Equals(uri.Host, "127.0.0.1")
            && uri.Port == _getManagedPort()
            && string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && string.IsNullOrEmpty(uri.UserInfo);
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
