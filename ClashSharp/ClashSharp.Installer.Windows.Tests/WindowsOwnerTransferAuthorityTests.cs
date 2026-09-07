using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Execution;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed partial class WindowsOwnerTransferAuthorityTests
{
    [Fact]
    public async Task TransfersThroughRealPhasesThenRetainsAllAuthorityAcrossOrdinaryPrepare()
    {
        using var fixture = new Fixture();
        Assert.Empty(fixture.Events);

        WindowsOwnerTransferHandoff handoff = await fixture.CreateAsync();
        try
        {
            Assert.Equal(InstallerTransactionSnapshot.Create(fixture.Inner.Journal.Continuation), handoff.Continuation);
            Assert.Null(fixture.Persistence.Bytes);
            Assert.Equal(["global", "release", "previous-app", "next-app", "transfer", "ordinary"], fixture.Active);
            Assert.True(fixture.Events.IndexOf("private-clear") < fixture.Events.IndexOf("ordinary-open"));
            Assert.Equal(6, fixture.Events.Count(value => value.StartsWith("phase:", StringComparison.Ordinal)));
            Assert.Equal(1, fixture.Inner.Ordinary.Saves);

            InstallerMachineHelperCommand prepare = InstallerMachineHelperCommand.Create(
                InstallerMachineHelperInvocation.Create(InstallerMachineHelperVerb.Prepare, handoff.Continuation), handoff.Continuation);
            InstallerMachineHelperResult result = await handoff.Authority.Session.ExecuteAsync(prepare, CancellationToken.None);

            Assert.Equal(InstallerMachineHelperOutcome.Succeeded, result.Outcome);
            Assert.Contains("normal:Prepare", fixture.Events);
            fixture.RequireHeld();
            Assert.Equal(InstallerTransactionPhase.MachineReserved,
                (await fixture.OrdinaryStore.LoadAsync(CancellationToken.None))!.Journal.Phase);
        }
        finally
        {
            await handoff.Authority.DisposeAsync();
        }
        Assert.Empty(fixture.Active);
        Assert.Equal(["ordinary", "transfer", "next-app", "previous-app", "release", "global"], fixture.Disposed);
        Assert.Equal(0, fixture.Inner.Native.LiveLeases);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task RecoveryUsesExactExistingPrivateIdentityAtEveryPhase(int phase)
    {
        using var fixture = new Fixture();
        InstallerOwnerTransferJournal current = fixture.Inner.Journal;
        while ((int)current.Phase < Math.Min(phase, 6))
        {
            var next = (InstallerOwnerTransferPhase)((int)current.Phase + 1);
            await fixture.Inner.Executor.ApplyAndVerifyAsync(current, next, CancellationToken.None);
            current = current.TransitionTo(next);
        }
        if (phase == 7)
        {
            current = current.TransitionTo(InstallerOwnerTransferPhase.Verified);
        }
        fixture.Confirmed = new(current, InstallerOwnerTransferSnapshot.Create(current));
        fixture.Persistence.Bytes = InstallerOwnerTransferCodec.Serialize(current);

        WindowsOwnerTransferHandoff handoff = await fixture.CreateAsync();
        await handoff.Authority.DisposeAsync();

        Assert.DoesNotContain("preparation", fixture.Events);
        Assert.Equal(Math.Max(0, 6 - phase), fixture.Events.Count(value => value.StartsWith("phase:", StringComparison.Ordinal)));
        Assert.Equal(InstallerTransactionSnapshot.Create(current.Continuation), handoff.Continuation);
        Assert.Null(fixture.Persistence.Bytes);
        Assert.Empty(fixture.Active);
        Assert.Equal(1, fixture.Inner.Ordinary.Saves);
    }

    [Theory]
    [InlineData("global-acquire")]
    [InlineData("release-verify")]
    [InlineData("release-reverify")]
    [InlineData("previous-app-acquire")]
    [InlineData("next-app-acquire")]
    [InlineData("transfer-open")]
    [InlineData("private-read")]
    [InlineData("preparation")]
    [InlineData("phase:StartupBlocked")]
    [InlineData("phase:InstallerAccessTransferred")]
    [InlineData("completion")]
    [InlineData("ordinary-open")]
    [InlineData("ordinary-read")]
    public async Task EveryFailedAcquisitionOrOwnedStepReleasesAllEarlierLeases(string failure)
    {
        using var fixture = new Fixture { Failure = failure };

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.CreateAsync());

        Assert.Empty(fixture.Active);
        Assert.Equal(0, fixture.Inner.Native.LiveLeases);
        Assert.Equal(0, fixture.Disposed.GroupBy(value => value).Count(group => group.Count() != 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AChangedPrivateSnapshotCannotBeginAnyPhase(bool recovering)
    {
        using var fixture = new Fixture();
        if (recovering)
        {
            fixture.Confirmed = new(fixture.Inner.Journal, InstallerOwnerTransferSnapshot.Create(fixture.Inner.Journal));
        }
        var different = fixture.Inner.Journal with
        {
            Continuation = fixture.Inner.Journal.Continuation with { TransactionId = new string('f', 64) },
        };
        fixture.Persistence.Bytes = InstallerOwnerTransferCodec.Serialize(different);

        var error = await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.CreateAsync());

        Assert.Equal("installer.owner_transfer.confirmation_state_changed", error.DiagnosticCode);
        Assert.DoesNotContain(fixture.Events, value => value.StartsWith("phase:", StringComparison.Ordinal));
        Assert.Empty(fixture.Inner.Mutations);
        Assert.Equal(different, InstallerOwnerTransferCodec.Parse(fixture.Persistence.Bytes!));
        Assert.Empty(fixture.Active);
    }

    [Fact]
    public async Task MissingRecoveryDoesNotStartANewTransferOrRegenerateState()
    {
        using var fixture = new Fixture();
        fixture.Confirmed = new(fixture.Inner.Journal, InstallerOwnerTransferSnapshot.Create(fixture.Inner.Journal));

        var error = await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.CreateAsync());

        Assert.Equal("installer.owner_transfer.confirmation_state_changed", error.DiagnosticCode);
        Assert.DoesNotContain("preparation", fixture.Events);
        Assert.DoesNotContain("private-write", fixture.Events);
        Assert.Empty(fixture.Inner.Mutations);
        Assert.Empty(fixture.Active);
    }

    [Fact]
    public async Task WrongAuthenticatedAccountIsRejectedBeforeAnyAcquisition()
    {
        using var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.Factory.CreateAsync(
            fixture.Confirmed, WindowsOwnerTransferAccessFixture.PreviousSid, CancellationToken.None));

        Assert.Equal("installer.owner_transfer.target_sid_mismatch", error.DiagnosticCode);
        Assert.Empty(fixture.Events);
        Assert.Empty(fixture.Active);
    }

    [Fact]
    public async Task PreCancellationDoesNotAcquireOrCreateState()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.CreateAsync(cancellation.Token));

        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task CancellationDrainsThePhaseBeforeClosingEitherAppOrGlobalAuthority()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforePhase = (phase, _) =>
        {
            if (phase != InstallerOwnerTransferPhase.MachineAccessTransferred)
            {
                return Task.CompletedTask;
            }
            entered.TrySetResult();
            return drain.Task;
        };
        Task pending = fixture.CreateAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(pending.IsCompleted);
            fixture.RequireHeld();
            Assert.Empty(fixture.Disposed);
        }
        finally
        {
            cancellation.Cancel();
            drain.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.Equal(InstallerOwnerTransferPhase.PreviousServiceRemoved,
            InstallerOwnerTransferCodec.Parse(fixture.Persistence.Bytes!).Phase);
        Assert.Empty(fixture.Active);
    }

    [Fact]
    public async Task ConcurrentDisposalAwaitsTheSameOrdinaryDrainAndRetainsAllEarlierLeases()
    {
        using var fixture = new Fixture();
        WindowsOwnerTransferHandoff handoff = await fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeOrdinaryDispose = () =>
        {
            entered.TrySetResult();
            return drain.Task;
        };
        Task first = handoff.Authority.DisposeAsync().AsTask();
        Task second = handoff.Authority.DisposeAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(first, second);
            Assert.False(first.IsCompleted);
            fixture.RequireHeld();
            Assert.Empty(fixture.Disposed);
        }
        finally
        {
            drain.TrySetResult();
            await Task.WhenAll(first, second);
        }
        Assert.Equal(6, fixture.Disposed.Count);
        Assert.Empty(fixture.Active);
    }

    [Fact]
    public async Task CleanupContinuesThroughMultipleDisposalFailures()
    {
        using var fixture = new Fixture();
        WindowsOwnerTransferHandoff handoff = await fixture.CreateAsync();
        fixture.DisposalFailures.UnionWith(["ordinary", "transfer", "next-app", "release"]);

        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => handoff.Authority.DisposeAsync().AsTask());

        Assert.Equal(4, error.InnerExceptions.Count);
        Assert.Empty(fixture.Active);
        Assert.Equal(["ordinary", "transfer", "next-app", "previous-app", "release", "global"], fixture.Disposed);
    }

    [Theory]
    [InlineData("previous-profile-changed")]
    [InlineData("next-profile-changed")]
    [InlineData("different-roots")]
    public async Task ProfileChangesWhileAcquiringAppLocksCannotCreatePrivatePreparation(string failure)
    {
        using var fixture = new Fixture();
        fixture.AfterAppAcquired = name =>
        {
            if (name == "next-app")
            {
                fixture.Inner.State.Machine.Failure = failure;
            }
        };

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.CreateAsync());

        Assert.DoesNotContain("transfer-open", fixture.Events);
        Assert.DoesNotContain("private-write", fixture.Events);
        Assert.Empty(fixture.Inner.Mutations);
        Assert.Empty(fixture.Active);
        Assert.Null(fixture.Persistence.Bytes);
    }

    [Fact]
    public async Task ConflictingTargetCertificateCannotCreatePrivatePreparation()
    {
        using var fixture = new Fixture();
        fixture.Confirmed = fixture.Confirmed with
        {
            Journal = fixture.Confirmed.Journal with
            {
                NextCertificateLedger = fixture.Confirmed.Journal.NextCertificateLedger! with
                {
                    CertificateSha256 = new string('f', 64),
                },
            },
        };

        var error = await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.CreateAsync());

        Assert.Equal("installer.owner_transfer.target_certificate_mismatch", error.DiagnosticCode);
        Assert.DoesNotContain("transfer-open", fixture.Events);
        Assert.DoesNotContain("private-write", fixture.Events);
        Assert.Empty(fixture.Inner.Mutations);
        Assert.Empty(fixture.Active);
    }

    private sealed class Fixture : IDisposable
    {
        internal WindowsOwnerTransferExecutorFixture Inner { get; } = new();
        internal List<string> Events { get; } = [];
        internal List<string> Active { get; } = [];
        internal List<string> Disposed { get; } = [];
        internal HashSet<string> DisposalFailures { get; } = [];
        internal string? Failure { get; set; }
        internal Func<InstallerOwnerTransferPhase, CancellationToken, Task>? BeforePhase { get; set; }
        internal Func<Task>? BeforeOrdinaryDispose { get; set; }
        internal Action<string>? AfterAppAcquired { get; set; }
        internal PrivatePersistence Persistence { get; }
        internal NormalStore OrdinaryStore { get; }
        internal WindowsOwnerTransferConfirmedState Confirmed { get; set; }
        internal WindowsOwnerTransferAuthorityFactory Factory { get; }

        internal Fixture()
        {
            Confirmed = new(Inner.Journal, null);
            Persistence = new(this);
            OrdinaryStore = new(this);
            Factory = new(new GlobalLock(this), new AppLocks(this), new ReleaseVerifier(this),
                Inner.Backend, new TransferFactory(this), new NormalFactory(this));
        }
        internal Task<WindowsOwnerTransferHandoff> CreateAsync(CancellationToken cancellationToken = default) =>
            Factory.CreateAsync(Confirmed, WindowsOwnerTransferAccessFixture.NextSid, cancellationToken);
        internal void Hit(string value)
        {
            Events.Add(value);
            if (Failure == value)
            {
                throw new InstallerProtocolException("installer.owner_transfer.injected_refusal");
            }
        }
        internal void RequireHeld()
        {
            foreach (string resource in new[] { "global", "release", "previous-app", "next-app" })
            {
                Assert.Contains(resource, Active);
            }
        }
        internal void Open(string value)
        {
            Assert.DoesNotContain(value, Active);
            Active.Add(value);
        }
        internal void Close(string value)
        {
            Assert.True(Active.Remove(value));
            Disposed.Add(value);
            if (DisposalFailures.Contains(value))
            {
                throw new IOException("Synthetic disposal failure.");
            }
        }
        public void Dispose() => Inner.Dispose();
    }

    private sealed class GlobalLock(Fixture fixture) : IWindowsInstallerAuthorityLock
    {
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.Hit("global-acquire");
            fixture.Open("global");
            return Task.FromResult<IAsyncDisposable>(new AsyncLease(fixture, "global"));
        }
    }
    private sealed class AsyncLease(Fixture fixture, string name) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { fixture.Close(name); return ValueTask.CompletedTask; }
    }
    private sealed class SyncLease(Fixture fixture, string name) : IDisposable
    {
        public void Dispose() => fixture.Close(name);
    }
    private sealed class AppLocks(Fixture fixture) : IWindowsInstallerApplicationLock
    {
        public IDisposable Acquire(string targetSid, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Contains("global", fixture.Active);
            Assert.Contains("release", fixture.Active);
            string name = targetSid == WindowsOwnerTransferAccessFixture.PreviousSid ? "previous-app" : "next-app";
            fixture.Hit(name + "-acquire");
            fixture.Open(name);
            fixture.AfterAppAcquired?.Invoke(name);
            return new SyncLease(fixture, name);
        }
    }
    private sealed class ReleaseVerifier(Fixture fixture) : IInstallerReleaseVerifier
    {
        public Task<IInstallerReleaseLease> VerifyAsync(InstallerRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Contains("global", fixture.Active);
            fixture.Hit("release-verify");
            fixture.Open("release");
            return Task.FromResult<IInstallerReleaseLease>(new OwnedRelease(fixture));
        }
    }
    private sealed class OwnedRelease(Fixture fixture) : IInstallerReleaseLease
    {
        public VerifiedInstallerRelease Release => fixture.Inner.State.Machine.Release.Release;
        public InstallerReleaseManifest Manifest => fixture.Inner.State.Machine.Release.Manifest;
        public IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles => [];
        public Task ReverifyAsync(InstallerRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Contains("release", fixture.Active);
            Assert.True(fixture.Inner.Journal.Continuation.Matches(request with { Operation = fixture.Inner.Journal.Continuation.Operation }));
            fixture.Hit("release-reverify");
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { fixture.Close("release"); return ValueTask.CompletedTask; }
    }
    private sealed class TransferFactory(Fixture fixture) : IWindowsOwnerTransferAuthorityResourcesFactory
    {
        public IWindowsOwnerTransferAuthorityResources Create(InstallerOwnerTransferJournal journal, IInstallerReleaseLease release, bool newTransfer)
        {
            fixture.RequireHeld();
            Assert.Equal(fixture.Confirmed.ExpectedPrivateState is null, newTransfer);
            fixture.Hit("transfer-open");
            fixture.Open("transfer");
            return new TransferResources(fixture, release);
        }
    }
    private sealed class TransferResources(Fixture fixture, IInstallerReleaseLease release) : IWindowsOwnerTransferAuthorityResources
    {
        public IInstallerOwnerTransferStore Store { get; } = new InstallerOwnerTransferStore(fixture.Persistence);
        public IInstallerOwnerTransferPhaseExecutor Executor { get; } = new ObservedPhases(fixture, fixture.Inner.CreateExecutor(release));
        public Task VerifyNewPreparationAsync(WindowsMachineDeploymentRoots roots, InstallerOwnerTransferJournal journal, CancellationToken cancellationToken)
        {
            fixture.RequireHeld();
            fixture.Hit("preparation");
            return new WindowsOwnerTransferPreparationVerifier(fixture.Inner.Native, fixture.Inner.State.Certificates, fixture.Inner.Ordinary)
                .VerifyAsync(new(roots, journal), cancellationToken);
        }
        public void Dispose() => fixture.Close("transfer");
    }
    private sealed class ObservedPhases(Fixture fixture, IInstallerOwnerTransferPhaseExecutor inner) : IInstallerOwnerTransferPhaseExecutor
    {
        public async Task ApplyAndVerifyAsync(InstallerOwnerTransferJournal current, InstallerOwnerTransferPhase nextPhase, CancellationToken cancellationToken)
        {
            fixture.RequireHeld();
            fixture.Hit("phase:" + nextPhase);
            if (fixture.BeforePhase is not null)
            {
                await fixture.BeforePhase(nextPhase, cancellationToken);
            }
            await inner.ApplyAndVerifyAsync(current, nextPhase, cancellationToken);
            fixture.RequireHeld();
        }
        public Task VerifyCompletedAsync(InstallerOwnerTransferJournal current, CancellationToken cancellationToken)
        {
            fixture.RequireHeld();
            fixture.Hit("completion");
            return inner.VerifyCompletedAsync(current, cancellationToken);
        }
    }
    private sealed class PrivatePersistence(Fixture fixture) : IInstallerOwnerTransferPersistence
    {
        internal byte[]? Bytes { get; set; }
        public Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.RequireHeld();
            fixture.Hit("private-read");
            return Task.FromResult(Bytes?.ToArray());
        }
        public Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.RequireHeld();
            fixture.Hit("private-write");
            Bytes = bytes.ToArray();
            return Task.CompletedTask;
        }
        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.RequireHeld();
            Assert.Equal(InstallerOwnerTransferPhase.Verified, InstallerOwnerTransferCodec.Parse(Bytes!).Phase);
            fixture.Hit("private-clear");
            Bytes = null;
            return Task.CompletedTask;
        }
    }
    private sealed class NormalFactory(Fixture fixture) : IWindowsMachineHelperAuthorityResourcesFactory
    {
        public IWindowsMachineHelperAuthorityResources Create(string targetSid)
        {
            fixture.RequireHeld();
            Assert.Equal(WindowsOwnerTransferAccessFixture.NextSid, targetSid);
            Assert.Null(fixture.Persistence.Bytes);
            Assert.All(fixture.Inner.State.InstallerPaths, path => Assert.Contains(fixture.Inner.Native.Entries[path].Security.AccessEntries,
                ace => ace.Sid == WindowsOwnerTransferAccessFixture.NextSid));
            fixture.Hit("ordinary-open");
            fixture.Open("ordinary");
            return new NormalResources(fixture);
        }
    }
    private sealed class NormalResources(Fixture fixture) : IWindowsMachineHelperAuthorityResources
    {
        public IInstallerTransactionStore TransactionStore => fixture.OrdinaryStore;
        public IInstallerMachineHelperOperationExecutor Operations { get; } = new NormalOperations(fixture);
        public async ValueTask DisposeAsync()
        {
            if (fixture.BeforeOrdinaryDispose is not null)
            {
                await fixture.BeforeOrdinaryDispose();
            }
            fixture.Close("ordinary");
        }
    }
    private sealed class NormalOperations(Fixture fixture) : IInstallerMachineHelperOperationExecutor
    {
        public Task ExecuteAsync(InstallerMachineHelperCommand command,
            InstallerMachineHelperSessionDisposition disposition, CancellationToken cancellationToken)
        {
            fixture.RequireHeld();
            Assert.False(command.ToDurableState().Journal.AllowReassociation);
            fixture.Hit("normal:" + command.Verb);
            return Task.CompletedTask;
        }
    }
    private sealed class NormalStore(Fixture fixture) : IInstallerTransactionStore
    {
        public Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken)
        {
            fixture.RequireHeld();
            fixture.Hit("ordinary-read");
            return fixture.Inner.Ordinary.LoadAsync(cancellationToken);
        }
        public async Task<InstallerTransactionSnapshot> SaveAsync(
            InstallerTransactionJournal journal, string? expectedCurrentHash, CancellationToken cancellationToken)
        {
            InstallerTransactionSnapshot? before = await LoadAsync(cancellationToken);
            Assert.NotNull(before);
            Assert.Equal(expectedCurrentHash, before.ContentHash);
            Assert.Equal(before.Journal.TransitionTo(journal.Phase), journal);
            fixture.Inner.Native.Entries[WindowsOwnerTransferAccessFixture.ContinuationPath].Bytes = InstallerTransactionCodec.Serialize(journal);
            return InstallerTransactionSnapshot.Create(journal);
        }
        public async Task ClearVerifiedAsync(string transactionId, string expectedCurrentHash, CancellationToken cancellationToken)
        {
            InstallerTransactionSnapshot current = (await LoadAsync(cancellationToken))!;
            Assert.Equal(InstallerTransactionPhase.Verified, current.Journal.Phase);
            Assert.Equal(transactionId, current.Journal.TransactionId);
            Assert.Equal(expectedCurrentHash, current.ContentHash);
            fixture.Inner.Native.Entries.Remove(WindowsOwnerTransferAccessFixture.ContinuationPath);
        }
    }
}
