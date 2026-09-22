using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

internal sealed class DirectoryCleanupTestLedger : IWindowsInstallerDirectoryLedgerPersistence
{
    internal WindowsInstallerDirectoryLedger? Current { get; set; }
    internal Func<CancellationToken, Task>? BeforeRead { get; set; }
    internal int SaveCount { get; private set; }
    internal int DeleteCount { get; private set; }

    public async Task<WindowsInstallerDirectoryLedger?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeRead is not null) { await BeforeRead(cancellationToken); }
        return Current;
    }

    public Task SaveAsync(WindowsInstallerDirectoryLedger? expected, WindowsInstallerDirectoryLedger desired,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(expected is null ? null : WindowsInstallerDirectoryLedgerCodec.Serialize(expected),
            Current is null ? null : WindowsInstallerDirectoryLedgerCodec.Serialize(Current));
        Current = desired;
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(WindowsInstallerDirectoryLedger expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.NotNull(Current);
        Assert.Equal(WindowsInstallerDirectoryLedgerCodec.Serialize(expected), WindowsInstallerDirectoryLedgerCodec.Serialize(Current));
        Current = null;
        DeleteCount++;
        return Task.CompletedTask;
    }
}
