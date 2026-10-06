using System;

namespace ClashSharp.Service;

/// <summary>Restores only WinINet fields still proven to be owned by the durable Clash# journal.</summary>
internal static class WindowsProxyOwnershipRestorer
{
    internal static bool Restore(
        IWindowsProxyRegistryStore registry,
        IWindowsProxyMutationJournalStore mutationJournal)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(mutationJournal);
        WindowsProxyMutationJournal? journal = mutationJournal.Read();
        if (journal is null)
        {
            return false;
        }

        WindowsProxyRegistrySnapshot current = registry.Read();
        registry.Write(MergeOwnedRestore(current, journal));
        mutationJournal.Clear();
        return true;
    }

    /// <summary>Merges baseline restoration with independently changed external fields.</summary>
    internal static WindowsProxyRegistrySnapshot MergeOwnedRestore(
        WindowsProxyRegistrySnapshot current,
        WindowsProxyMutationJournal journal)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(journal);
        WindowsProxyRegistrySnapshot baseline = journal.Baseline;
        WindowsProxyRegistrySnapshot applied = journal.Applied;
        WindowsProxyRegistrySnapshot? pending = journal.PendingApplied;

        // The enabled switch selects the current manual server. A matching Boolean alone
        // cannot prove ownership after another application installs a different server.
        // During a partial apply, either journaled server still proves our switch ownership.
        bool ownsManualServer = current.ProxyServer == applied.ProxyServer || current.ProxyServer == pending?.ProxyServer;
        bool ownsEnable = ownsManualServer
            && (current.ProxyEnable == applied.ProxyEnable || current.ProxyEnable == pending?.ProxyEnable);

        return new WindowsProxyRegistrySnapshot(
            ownsEnable ? baseline.ProxyEnable : current.ProxyEnable,
            current.ProxyServer == applied.ProxyServer || current.ProxyServer == pending?.ProxyServer
                ? baseline.ProxyServer
                : current.ProxyServer,
            current.ProxyOverride == applied.ProxyOverride || current.ProxyOverride == pending?.ProxyOverride
                ? baseline.ProxyOverride
                : current.ProxyOverride,
            current.AutoConfigUrl == applied.AutoConfigUrl || current.AutoConfigUrl == pending?.AutoConfigUrl
                ? baseline.AutoConfigUrl
                : current.AutoConfigUrl);
    }
}
