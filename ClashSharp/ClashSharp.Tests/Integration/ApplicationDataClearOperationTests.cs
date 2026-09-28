extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Lifecycle;
using ApplicationDataClearOperation = ClashSharpUi::ClashSharp.Service.ApplicationDataClearOperation;
using LogStorageService = ClashSharpUi::ClashSharp.Service.LogStorageService;
using LogStorageServiceFactory = ClashSharpUi::ClashSharp.Service.LogStorageServiceFactory;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies data removal against real pooled SQLite and Windows file handles in isolated temporary directories.</summary>
public sealed class ApplicationDataClearOperationTests
{
    [Fact]
    public async Task ClearFiles_ReportsOwnedHandlesAndSucceedsAfterRepositoryAndWatchdogRelease()
    {
        string parent = Path.Combine(Path.GetTempPath(), "clashsharp-clear-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(parent, "LocalState");
        Directory.CreateDirectory(Path.Combine(root, "mihomo", "profiles"));
        string outside = Path.Combine(parent, "keep.txt");
        await File.WriteAllTextAsync(outside, "outside application data");
        await File.WriteAllTextAsync(Path.Combine(root, "mihomo", "profiles", "test.yaml"), "fixture");
        LogStorageService logs = LogStorageServiceFactory.CreateForDirectory(root, () => "fixture");
        FileStream? watchdog = null;
        try
        {
            logs.AppendLog("Info", "Acceptance", "old record", null);
            watchdog = new FileStream(Path.Combine(root, "RecoveryWatchdog.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            ApplicationDataClearOperation operation = Create(root);
            await operation.PrepareShutdownAsync(CancellationToken.None);
            await operation.ClearHostDataAsync(CancellationToken.None);

            AggregateException failed = await Assert.ThrowsAsync<AggregateException>(() => operation.ClearLocalFilesAsync(CancellationToken.None));
            Assert.NotEmpty(failed.InnerExceptions);
            Assert.True(File.Exists(logs.DatabasePath));
            Assert.True(File.Exists(Path.Combine(root, "RecoveryWatchdog.lock")));

            await logs.DisposeAsync();
            await watchdog.DisposeAsync();
            watchdog = null;
            await operation.ClearLocalFilesAsync(CancellationToken.None);

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Equal("outside application data", await File.ReadAllTextAsync(outside));
            Assert.Throws<ObjectDisposedException>(() => logs.AppendLog("Info", "late", "must not recreate data", null));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            if (watchdog is not null) { await watchdog.DisposeAsync(); }
            await logs.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Theory]
    [InlineData(RuntimeShutdownOutcome.Aborted, "shutdown-failed")]
    [InlineData(RuntimeShutdownOutcome.Degraded, "quiescence-restore-failed")]
    [InlineData(RuntimeShutdownOutcome.PreparedForHostDisposal, "runtime-stop-degraded")]
    [InlineData(RuntimeShutdownOutcome.PreparedForHostDisposal, "data-removal-network-unverified")]
    public async Task FailedOrDegradedShutdown_CannotDeletePreferencesOrFiles(RuntimeShutdownOutcome outcome, string errorCode)
    {
        string root = Path.Combine(Path.GetTempPath(), "clashsharp-clear-preserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "configuration.yaml");
        await File.WriteAllTextAsync(file, "must remain");
        bool preferencesDeleted = false;
        ApplicationDataClearOperation operation = new(
            _ => Task.FromResult(new RuntimeShutdownResult(outcome, errorCode, [])),
            () => preferencesDeleted = true, root);
        try
        {
            if (outcome == RuntimeShutdownOutcome.PreparedForHostDisposal)
            {
                await operation.PrepareShutdownAsync(CancellationToken.None);
            }
            else
            {
                await Assert.ThrowsAsync<RuntimeShutdownNotPreparedException>(() => operation.PrepareShutdownAsync(CancellationToken.None));
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ClearHostDataAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ClearLocalFilesAsync(CancellationToken.None));
            Assert.False(preferencesDeleted);
            Assert.Equal("must remain", await File.ReadAllTextAsync(file));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CredentialDeletionFailure_PreventsFileDeletion()
    {
        string root = Path.Combine(Path.GetTempPath(), "clashsharp-clear-credential-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "configuration.yaml");
        await File.WriteAllTextAsync(file, "fixture");
        ApplicationDataClearOperation operation = Create(root, () => throw new IOException("credential storage is unavailable"));
        try
        {
            await operation.PrepareShutdownAsync(CancellationToken.None);
            await Assert.ThrowsAsync<IOException>(() => operation.ClearHostDataAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ClearLocalFilesAsync(CancellationToken.None));
            Assert.True(File.Exists(file));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void VolumeRoot_IsRejectedBeforeAnySideEffect()
    {
        Assert.Throws<ArgumentException>(() => Create(Path.GetPathRoot(Path.GetTempPath())!));
    }

    private static ApplicationDataClearOperation Create(string root, Action? clear = null) => new(
        _ => Task.FromResult(new RuntimeShutdownResult(RuntimeShutdownOutcome.PreparedForHostDisposal, null, [])),
        clear ?? (() => { }), root);
}
