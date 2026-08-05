using ClashSharp.Recovery;
using ClashSharp.Service;

return await RecoveryWatchdogProgram.RunAsync(args).ConfigureAwait(false);

internal static class RecoveryWatchdogProgram
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            RecoveryWatchdogInvocation invocation = RecoveryWatchdogInvocation.Parse(arguments);
            string localData = RecoveryWatchdogPaths.ResolveLocalDataDirectory();
            RecoveryWatchdogLeaseFileStore leaseStore = new(
                Path.Combine(localData, RecoveryWatchdogPaths.LeaseFileName));
            WindowsProxyMutationJournalFileStore journalStore = new(
                Path.Combine(localData, RecoveryWatchdogPaths.ProxyJournalFileName));
            WindowsProxyRegistryStore registryStore = new(
                RecoveryWatchdogRunner.NotifyProxySettingsChanged);
            RecoveryWatchdogRunner runner = new(
                RecoveryWatchdogRunner.WaitForParentExitAsync,
                async cancellationToken => await RecoveryWatchdogFileLock.TryAcquireAsync(
                    Path.Combine(localData, RecoveryWatchdogPaths.LockFileName),
                    TimeSpan.FromSeconds(10),
                    cancellationToken).ConfigureAwait(false),
                leaseStore.Read,
                leaseStore.ClearIfMatches,
                () => RecoveryWatchdogRunner.RestoreOwnedProxy(registryStore, journalStore));
            await runner.RunAsync(invocation, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            Console.Error.WriteLine($"ClashSharp recovery watchdog failed: {exception.GetType().Name}: {exception.Message}");
            return 2;
        }
    }
}
