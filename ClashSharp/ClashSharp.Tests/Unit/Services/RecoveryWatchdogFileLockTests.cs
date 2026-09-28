extern alias RecoveryWatchdog;

using RecoveryWatchdogFileLock = RecoveryWatchdog::ClashSharp.Recovery.RecoveryWatchdogFileLock;

namespace ClashSharp.Tests.Unit.Services;

public sealed class RecoveryWatchdogFileLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClashSharp.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingLock_WhenRemoved_DoesNotRecreateFileOrDirectory(bool directoryRemains)
    {
        string path = Path.Combine(_root, "RecoveryWatchdog.lock");
        using (FileStream? parent = await RecoveryWatchdogFileLock.TryAcquireAsync(path, TimeSpan.Zero, CancellationToken.None))
        {
            Assert.NotNull(parent);
        }

        File.Delete(path);
        if (!directoryRemains)
        {
            Directory.Delete(_root);
        }

        using FileStream? result = await RecoveryWatchdogFileLock.TryAcquireExistingAsync(
            path, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Null(result);
        Assert.False(File.Exists(path));
        Assert.Equal(directoryRemains, Directory.Exists(_root));
    }

    [Fact]
    public async Task ExistingLock_RequiresParentRelease_AndRemainsExclusive()
    {
        string path = Path.Combine(_root, "RecoveryWatchdog.lock");
        using (FileStream? parent = await RecoveryWatchdogFileLock.TryAcquireAsync(path, TimeSpan.Zero, CancellationToken.None))
        {
            Assert.NotNull(parent);
            using FileStream? held = await RecoveryWatchdogFileLock.TryAcquireExistingAsync(path, TimeSpan.Zero, CancellationToken.None);
            Assert.Null(held);
        }

        using FileStream? acquired = await RecoveryWatchdogFileLock.TryAcquireExistingAsync(path, TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(acquired);
        using FileStream? competing = await RecoveryWatchdogFileLock.TryAcquireExistingAsync(path, TimeSpan.Zero, CancellationToken.None);
        Assert.Null(competing);
    }

    [Fact]
    public async Task ExistingLock_PreCancellation_DoesNotCreateData()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecoveryWatchdogFileLock.TryAcquireExistingAsync(
            Path.Combine(_root, "RecoveryWatchdog.lock"), TimeSpan.Zero, cancellation.Token));

        Assert.False(Directory.Exists(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
