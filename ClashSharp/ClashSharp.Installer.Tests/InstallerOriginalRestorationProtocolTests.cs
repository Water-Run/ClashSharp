using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerOriginalRestorationProtocolTests
{
    [Fact]
    public void CandidateContinuationReexecutesItsPrivateDecisionEvenThoughThePublicPhaseDoesNotAdvance()
    {
        var state = InstallerTransactionSnapshot.Create(Initial().TransitionTo(InstallerTransactionPhase.MachineReserved));
        var command = Command(InstallerMachineHelperVerb.ContinueCandidate, state);
        var guard = new InstallerMachineHelperSessionGuard(command.ToInvocation(), state);
        Assert.Equal(command, InstallerMachineHelperCommandCodec.Parse(InstallerMachineHelperCommandCodec.Serialize(command)));
        Assert.Equal(command.ToInvocation(), InstallerMachineHelperInvocation.Parse(command.ToInvocation().ToArguments()));
        Assert.Equal(state, command.GetExpectedSuccessfulState());

        Assert.Equal(InstallerMachineHelperSessionDisposition.Execute, guard.Begin(command, state));
        guard.Complete(InstallerMachineHelperResult.Succeeded(command, state), state);
        Assert.Equal(InstallerMachineHelperSessionDisposition.Execute, guard.Begin(command, state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestorationHasItsOwnCanonicalTerminalAndExactClearCapability(bool reserved)
    {
        using var storage = new Storage();
        InstallerTransactionSnapshot before = await PrepareAsync(storage, reserved);
        InstallerMachineHelperCommand command = Command(InstallerMachineHelperVerb.RestoreOriginal, before);
        Assert.Equal(command, InstallerMachineHelperCommandCodec.Parse(InstallerMachineHelperCommandCodec.Serialize(command)));
        Assert.Equal(command.ToInvocation(), InstallerMachineHelperInvocation.Parse(command.ToInvocation().ToArguments()));
        var operations = new Operations();
        InstallerMachineHelperAuthoritySession session = await SessionAsync(command, storage, operations);

        InstallerMachineHelperResult result = await session.ExecuteAsync(command, CancellationToken.None);

        InstallerTransactionSnapshot restored = result.ValidateAgainst(command);
        Assert.Equal(InstallerTransactionPhase.OriginalRestored, restored.Journal.Phase);
        Assert.Equal(before.Journal.Generation + 1, restored.Journal.Generation);
        Assert.Equal(restored, await storage.LoadAsync(CancellationToken.None));
        Assert.Equal(result, InstallerMachineHelperResultCodec.Parse(InstallerMachineHelperResultCodec.Serialize(result)));
        Assert.Equal(1, operations.RestoreEffects);
        await Assert.ThrowsAsync<InstallerProtocolException>(() => storage.ClearVerifiedAsync(
            restored.Journal.TransactionId, restored.ContentHash, CancellationToken.None));
        Assert.Throws<InstallerProtocolException>(() => restored.Journal.TransitionTo(InstallerTransactionPhase.Verified));

        InstallerMachineHelperCommand clear = Command(InstallerMachineHelperVerb.ClearOriginal, restored);
        Assert.Equal(clear, InstallerMachineHelperCommandCodec.Parse(InstallerMachineHelperCommandCodec.Serialize(clear)));
        InstallerMachineHelperResult cleared = await session.ExecuteAsync(clear, CancellationToken.None);

        Assert.Equal(InstallerMachineHelperOutcome.Succeeded, cleared.Outcome);
        Assert.Equal(restored, cleared.ValidateAgainst(clear));
        Assert.Null(cleared.DirectoryCleanupReport);
        Assert.Null(await storage.LoadAsync(CancellationToken.None));
        Assert.Equal(1, operations.ClearProofs);
        Assert.Equal(1, storage.OriginalClears);
    }

    [Fact]
    public async Task LostRestorationCommitReplyReplaysOnlyIndependentVerification()
    {
        using var storage = new Storage();
        InstallerTransactionSnapshot before = await PrepareAsync(storage, true);
        var command = Command(InstallerMachineHelperVerb.RestoreOriginal, before);
        var operations = new Operations();
        var session = await SessionAsync(command, storage, operations);
        storage.LoseRestoredCommitReply = true;

        await Assert.ThrowsAsync<IOException>(() => session.ExecuteAsync(command, CancellationToken.None));

        InstallerTransactionSnapshot? committed = await storage.LoadAsync(CancellationToken.None);
        Assert.Equal(InstallerTransactionPhase.OriginalRestored, committed?.Journal.Phase);
        var resumed = await SessionAsync(command, storage, operations);
        InstallerMachineHelperResult replayed = await resumed.ExecuteAsync(command, CancellationToken.None);
        Assert.Equal(InstallerMachineHelperOutcome.Succeeded, replayed.Outcome);
        Assert.Equal(committed, replayed.ToResultDurableState());
        Assert.Equal(1, operations.RestoreEffects);
        Assert.Equal(1, operations.ReplayObservations);
    }

    [Fact]
    public async Task LostClearReplyRequiresOriginalProofAgainWithoutASecondDelete()
    {
        using var storage = new Storage();
        InstallerTransactionSnapshot before = await PrepareAsync(storage, false);
        var restore = Command(InstallerMachineHelperVerb.RestoreOriginal, before);
        var operations = new Operations();
        var session = await SessionAsync(restore, storage, operations);
        var restored = (await session.ExecuteAsync(restore, CancellationToken.None)).ToResultDurableState();
        var clear = Command(InstallerMachineHelperVerb.ClearOriginal, restored);
        storage.LoseClearReply = true;

        await Assert.ThrowsAsync<IOException>(() => session.ExecuteAsync(clear, CancellationToken.None));

        Assert.Null(await storage.LoadAsync(CancellationToken.None));
        var resumed = await SessionAsync(clear, storage, operations);
        InstallerMachineHelperResult replayed = await resumed.ExecuteAsync(clear, CancellationToken.None);
        Assert.Equal(InstallerMachineHelperOutcome.Succeeded, replayed.Outcome);
        Assert.Equal(1, storage.OriginalClears);
        Assert.Equal(2, operations.ClearProofs);

        operations.FailProof = true;
        var missingProof = await SessionAsync(clear, storage, operations);
        InstallerMachineHelperResult failed = await missingProof.ExecuteAsync(clear, CancellationToken.None);
        Assert.Equal(InstallerMachineHelperOutcome.PostconditionFailed, failed.Outcome);
        Assert.False(failed.PostconditionVerified);
        Assert.Equal(1, storage.OriginalClears);
    }

    [Fact]
    public async Task FailedOriginalProofKeepsTheExactPendingJournal()
    {
        using var storage = new Storage();
        InstallerTransactionSnapshot before = await PrepareAsync(storage, true);
        var command = Command(InstallerMachineHelperVerb.RestoreOriginal, before);
        var operations = new Operations { FailProof = true };
        var session = await SessionAsync(command, storage, operations);

        InstallerMachineHelperResult result = await session.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(InstallerMachineHelperOutcome.Failed, result.Outcome);
        Assert.Equal(before, await storage.LoadAsync(CancellationToken.None));
        Assert.Equal(0, operations.RestoreEffects);
        Assert.Equal(0, storage.OriginalClears);
    }

    [Fact]
    public async Task OrdinaryExecutorCannotAuthorizeOriginalRestoration()
    {
        using var storage = new Storage();
        InstallerTransactionSnapshot before = await PrepareAsync(storage, false);
        var command = Command(InstallerMachineHelperVerb.RestoreOriginal, before);
        var session = await InstallerMachineHelperAuthoritySession.CreateAsync(command.ToInvocation(), InstallerTestData.Sid,
            storage, new OrdinaryOperations(), CancellationToken.None);

        InstallerMachineHelperResult result = await session.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal("installer.recovery.capability_unavailable", result.DiagnosticCode);
        Assert.Equal(before, await storage.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MissingDedicatedClearAuthorityRejectsBeforePrivateCompletionEffects()
    {
        using var storage = new Storage();
        InstallerTransactionSnapshot before = await PrepareAsync(storage, false);
        InstallerTransactionSnapshot restored = await storage.SaveAsync(before.Journal.TransitionTo(InstallerTransactionPhase.OriginalRestored),
            before.ContentHash, CancellationToken.None);
        var command = Command(InstallerMachineHelperVerb.ClearOriginal, restored);
        var operations = new Operations();
        var session = await InstallerMachineHelperAuthoritySession.CreateAsync(command.ToInvocation(), InstallerTestData.Sid,
            new OrdinaryStore(storage), operations, CancellationToken.None);

        InstallerMachineHelperResult result = await session.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal("installer.recovery.capability_unavailable", result.DiagnosticCode);
        Assert.Equal(restored, await storage.LoadAsync(CancellationToken.None));
        Assert.Equal(0, operations.ClearProofs);
        Assert.Equal(0, storage.OriginalClears);
    }

    [Theory]
    [InlineData(InstallerOperation.Install, false)]
    [InlineData(InstallerOperation.Uninstall, false)]
    [InlineData(InstallerOperation.Repair, true)]
    public void InstallationUninstallAndAccountTransferCannotUseRestoration(InstallerOperation operation, bool reassociation)
    {
        InstallerTransactionJournal journal = InstallerTestData.Journal(operation) with { AllowReassociation = reassociation };
        Assert.Throws<InstallerProtocolException>(() => journal.TransitionTo(InstallerTransactionPhase.OriginalRestored));
        Assert.Throws<InstallerProtocolException>(() => Command(InstallerMachineHelperVerb.RestoreOriginal, InstallerTransactionSnapshot.Create(journal)));
    }

    [Theory]
    [InlineData(InstallerTransactionPhase.PackageCommitted)]
    [InlineData(InstallerTransactionPhase.MachineCommitted)]
    [InlineData(InstallerTransactionPhase.Verified)]
    public void PackageCommitPermanentlyClosesOriginalRestoration(InstallerTransactionPhase phase)
    {
        InstallerTransactionJournal journal = Initial().TransitionTo(InstallerTransactionPhase.MachineReserved)
            .TransitionTo(InstallerTransactionPhase.PackageCommitted);
        if (phase is InstallerTransactionPhase.MachineCommitted or InstallerTransactionPhase.Verified)
        {
            journal = journal.TransitionTo(InstallerTransactionPhase.MachineCommitted);
        }
        if (phase == InstallerTransactionPhase.Verified) { journal = journal.TransitionTo(InstallerTransactionPhase.Verified); }
        Assert.Throws<InstallerProtocolException>(() => journal.TransitionTo(InstallerTransactionPhase.OriginalRestored));
        Assert.Throws<InstallerProtocolException>(() => Command(InstallerMachineHelperVerb.RestoreOriginal, InstallerTransactionSnapshot.Create(journal)));
        Assert.Throws<InstallerProtocolException>(() => Command(InstallerMachineHelperVerb.ClearOriginal, InstallerTransactionSnapshot.Create(journal)));
    }

    [Fact]
    public void OriginalTerminalCannotResumeCandidateDeploymentOrOrdinaryClear()
    {
        InstallerTransactionSnapshot terminal = InstallerTransactionSnapshot.Create(Initial().TransitionTo(InstallerTransactionPhase.OriginalRestored));
        foreach (InstallerMachineHelperVerb verb in Enum.GetValues<InstallerMachineHelperVerb>())
        {
            if (verb is InstallerMachineHelperVerb.RestoreOriginal or InstallerMachineHelperVerb.ClearOriginal) { continue; }
            Assert.Throws<InstallerProtocolException>(() => Command(verb, terminal));
        }
        Assert.Equal(terminal.Journal, InstallerTransactionCodec.Parse(InstallerTransactionCodec.Serialize(terminal.Journal)));
    }

    [Fact]
    public void ZeroGenerationNeverMakesAnUnsupportedOperationPhasePairValid()
    {
        foreach (InstallerOperation operation in Enum.GetValues<InstallerOperation>())
        {
            foreach (InstallerTransactionPhase phase in Enum.GetValues<InstallerTransactionPhase>())
            {
                Assert.Throws<InstallerProtocolException>(() => (InstallerTestData.Journal(operation) with
                {
                    Phase = phase,
                    Generation = 0,
                }).Validate());
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    public void RestorationTerminalRequiresOneOfItsTwoRealPredecessorGenerations(int generation) =>
        Assert.Throws<InstallerProtocolException>(() => (Initial() with { Phase = InstallerTransactionPhase.OriginalRestored, Generation = generation }).Validate());

    private static InstallerTransactionJournal Initial() => InstallerTestData.Journal(InstallerOperation.Repair) with { AllowReassociation = false };
    private static InstallerMachineHelperCommand Command(InstallerMachineHelperVerb verb, InstallerTransactionSnapshot state) =>
        InstallerMachineHelperCommand.Create(InstallerMachineHelperInvocation.Create(verb, state), state);
    private static Task<InstallerMachineHelperAuthoritySession> SessionAsync(InstallerMachineHelperCommand command, Storage store, Operations operations) =>
        InstallerMachineHelperAuthoritySession.CreateAsync(command.ToInvocation(), InstallerTestData.Sid, store, operations, CancellationToken.None);
    private static async Task<InstallerTransactionSnapshot> PrepareAsync(Storage store, bool reserved)
    {
        InstallerTransactionSnapshot state = await store.SaveAsync(Initial(), null, CancellationToken.None);
        return reserved ? await store.SaveAsync(state.Journal.TransitionTo(InstallerTransactionPhase.MachineReserved), state.ContentHash, CancellationToken.None) : state;
    }

    private sealed class Operations : IInstallerMachineHelperOperationExecutor, IInstallerMachineHelperOriginalRestoration
    {
        internal int RestoreEffects { get; private set; }
        internal int ReplayObservations { get; private set; }
        internal int ClearProofs { get; private set; }
        internal bool FailProof { get; set; }
        public Task ExecuteAsync(InstallerMachineHelperCommand command, InstallerMachineHelperSessionDisposition disposition, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task ExecuteOriginalRestorationAsync(InstallerMachineHelperCommand command, InstallerMachineHelperSessionDisposition disposition, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailProof) { throw new InstallerProtocolException("installer.recovery.original_state_changed"); }
            if (command.Verb == InstallerMachineHelperVerb.ClearOriginal) { ClearProofs++; }
            else if (disposition == InstallerMachineHelperSessionDisposition.VerifyCommittedReplay) { ReplayObservations++; }
            else { RestoreEffects++; }
            return Task.CompletedTask;
        }
    }
    private sealed class OrdinaryOperations : IInstallerMachineHelperOperationExecutor
    {
        public Task ExecuteAsync(InstallerMachineHelperCommand command, InstallerMachineHelperSessionDisposition disposition, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
    private sealed class OrdinaryStore(Storage inner) : IInstallerTransactionStore
    {
        public Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken) => inner.LoadAsync(cancellationToken);
        public Task<InstallerTransactionSnapshot> SaveAsync(InstallerTransactionJournal journal, string? expectedCurrentHash, CancellationToken cancellationToken) => inner.SaveAsync(journal, expectedCurrentHash, cancellationToken);
        public Task ClearVerifiedAsync(string transactionId, string expectedCurrentHash, CancellationToken cancellationToken) => throw new InvalidOperationException("Ordinary clear cannot remove restoration state.");
    }
    private sealed class Storage : IInstallerTransactionStore, IInstallerOriginalRestorationStore, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ClashSharp.OriginalRestoration." + Guid.NewGuid().ToString("N"));
        private readonly FileInstallerTransactionStore _inner;
        internal Storage() { Directory.CreateDirectory(_root); _inner = new(_root, new RootGuard()); }
        internal bool LoseRestoredCommitReply { get; set; }
        internal bool LoseClearReply { get; set; }
        internal int OriginalClears { get; private set; }
        public Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken) => _inner.LoadAsync(cancellationToken);
        public async Task<InstallerTransactionSnapshot> SaveAsync(InstallerTransactionJournal journal, string? expectedCurrentHash, CancellationToken cancellationToken)
        {
            InstallerTransactionSnapshot saved = await _inner.SaveAsync(journal, expectedCurrentHash, cancellationToken);
            if (LoseRestoredCommitReply && journal.Phase == InstallerTransactionPhase.OriginalRestored)
            {
                LoseRestoredCommitReply = false;
                throw new IOException("lost restoration commit reply");
            }
            return saved;
        }
        public Task ClearVerifiedAsync(string transactionId, string expectedCurrentHash, CancellationToken cancellationToken) =>
            _inner.ClearVerifiedAsync(transactionId, expectedCurrentHash, cancellationToken);
        public async Task ClearOriginalRestoredAsync(string transactionId, string expectedCurrentHash, CancellationToken cancellationToken)
        {
            await _inner.ClearOriginalRestoredAsync(transactionId, expectedCurrentHash, cancellationToken);
            OriginalClears++;
            if (LoseClearReply) { LoseClearReply = false; throw new IOException("lost restoration clear reply"); }
        }
        public void Dispose() { _inner.Dispose(); Directory.Delete(_root, recursive: true); }
    }
    private sealed class RootGuard : IInstallerTransactionRootGuard
    {
        public Task EnsureProtectedAsync(string absoluteRootPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Directory.Exists(absoluteRootPath));
            return Task.CompletedTask;
        }
    }
}
