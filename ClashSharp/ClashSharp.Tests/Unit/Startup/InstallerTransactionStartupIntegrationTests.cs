extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using InstallerTransactionStartupGate =
    ClashSharpUi::ClashSharp.Hosting.Startup.InstallerTransactionStartupGate;
using InstallerTransactionState =
    ClashSharpUi::ClashSharp.Service.InstallerTransactionState;
using InstallerTransactionStateReader =
    ClashSharpUi::ClashSharp.Service.InstallerTransactionStateReader;

namespace ClashSharp.Tests.Unit.Startup;

/// <summary>Exercises the real Installer journal writer against the App startup admission boundary.</summary>
public sealed class InstallerTransactionStartupIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ClashSharp-InstallerStartupIntegrationTests",
        Guid.NewGuid().ToString("N"));

    /// <summary>Every persisted phase blocks a fresh App until the authority clears verified state.</summary>
    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    [InlineData(InstallerOperation.Uninstall)]
    public async Task DurableTransaction_BlocksStartupUntilVerifiedClear(InstallerOperation operation)
    {
        string stateRoot = Path.Combine(
            _root,
            InstallerStateLayout.ProductDirectoryName,
            InstallerStateLayout.InstallerDirectoryName,
            InstallerStateLayout.VersionDirectoryName);
        Directory.CreateDirectory(stateRoot);
        using FileInstallerTransactionStore store = new(stateRoot, new TemporaryRootGuard(stateRoot));
        InstallerRequest request = new(
            operation,
            "S-1-5-21-100-200-300-1001",
            AllowReassociation: false,
            "1.0.0.0",
            new string('a', 64));
        InstallerTransactionJournal journal = InstallerTransactionJournal.Create(request);
        InstallerTransactionSnapshot? durable = null;
        InstallerTransactionPhase[] phases = operation == InstallerOperation.Uninstall
            ? [InstallerTransactionPhase.Prepared, InstallerTransactionPhase.MachineRemovalAuthorized,
                InstallerTransactionPhase.MachineCommitted, InstallerTransactionPhase.PackageCommitted,
                InstallerTransactionPhase.Verified]
            : [InstallerTransactionPhase.Prepared, InstallerTransactionPhase.MachineReserved,
                InstallerTransactionPhase.PackageCommitted, InstallerTransactionPhase.MachineCommitted,
                InstallerTransactionPhase.Verified];

        foreach (InstallerTransactionPhase phase in phases)
        {
            journal = journal.TransitionTo(phase);
            durable = await store.SaveAsync(journal, durable?.ContentHash, CancellationToken.None);
            InstallerTransactionStateReader reader = new(_root);
            MutationAdmissionBarrier barrier = new();
            InstallerTransactionState state = reader.Read();
            InstallerTransactionStartupGate gate = new(state, barrier);

            StartupStepResult result = await gate.ExecuteAsync(
                new AppLaunchRequest(string.Empty),
                CancellationToken.None);

            Assert.Equal(InstallerTransactionState.Pending, state);
            Assert.Equal(StartupStepOutcome.Fatal, result.Outcome);
            Assert.Equal(InstallerTransactionStartupGate.PendingDiagnosticCode, result.DiagnosticCode);
            Assert.Equal(MutationAdmissionState.ClosedForShutdown, barrier.State);
            Assert.Equal(durable, await store.LoadAsync(CancellationToken.None));
        }

        Assert.NotNull(durable);
        await store.ClearVerifiedAsync(durable.Journal.TransactionId, durable.ContentHash, CancellationToken.None);
        InstallerTransactionStateReader clearedReader = new(_root);
        MutationAdmissionBarrier clearedBarrier = new();
        InstallerTransactionState clearedState = clearedReader.Read();
        InstallerTransactionStartupGate clearedGate = new(clearedState, clearedBarrier);

        StartupStepResult clearedResult = await clearedGate.ExecuteAsync(
            new AppLaunchRequest(string.Empty),
            CancellationToken.None);

        Assert.Equal(InstallerTransactionState.Clear, clearedState);
        Assert.Equal(StartupStepOutcome.Succeeded, clearedResult.Outcome);
        Assert.Equal(MutationAdmissionState.Open, clearedBarrier.State);
        Assert.Empty(Directory.EnumerateFileSystemEntries(stateRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // Only the isolated test directory bypasses machine ACL verification; the writer is unmodified.
    private sealed class TemporaryRootGuard(string expectedRoot) : IInstallerTransactionRootGuard
    {
        public Task EnsureProtectedAsync(string absoluteRootPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(expectedRoot, absoluteRootPath);
            Assert.True(Directory.Exists(absoluteRootPath));
            return Task.CompletedTask;
        }
    }
}
