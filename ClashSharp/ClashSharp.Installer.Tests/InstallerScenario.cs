using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Execution;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

internal sealed class InstallerScenario :
    IInstallerEnvironment,
    IInstallerReleaseVerifier,
    IInstallerCertificateMutation,
    IInstallerPackageMutation,
    IInstallerMachineMutation,
    IInstallerFinalVerifier
{
    internal InstallerScenario(InstallerTransactionJournal? initialJournal = null)
    {
        Store = new MemoryInstallerTransactionStore(Events, initialJournal);
    }

    internal List<string> Events { get; } = [];

    internal InstallerEnvironmentSnapshot Environment { get; set; } = new(true, null, false, null);

    internal VerifiedInstallerRelease Release { get; set; } = InstallerTestData.Release();

    internal InstallerReleaseManifest? ReleaseManifest { get; set; }

    internal IReadOnlyList<IInstallerLockedPayloadFile>? LockedFiles { get; set; }

    internal Func<CancellationToken, Task<InstallerEnvironmentSnapshot>>? EnvironmentAction { get; set; }

    internal Func<CancellationToken, Task>? PackageAction { get; set; }

    internal Func<CancellationToken, Task>? CertificateAction { get; set; }

    internal Func<CancellationToken, Task>? MachineAction { get; set; }

    internal Func<CancellationToken, Task>? MachineResponseAction { get; set; }

    internal Func<CancellationToken, Task>? MachinePrepareAction { get; set; }

    internal Func<CancellationToken, Task>? MachinePrepareAdmissionAction { get; set; }

    internal Func<CancellationToken, Task>? PackageCommitAction { get; set; }

    internal Func<CancellationToken, Task>? ReleaseReverifyAction { get; set; }

    internal Func<CancellationToken, Task>? FinalVerifyAction { get; set; }

    internal Func<CancellationToken, Task>? FinalClearResponseAction { get; set; }

    internal Func<InstallerTransactionSnapshot, InstallerTransactionSnapshot>?
        MachineResultFactory
    { get; set; }

    internal Func<InstallerTransactionSnapshot, InstallerTransactionSnapshot>?
        MachinePrepareResultFactory
    { get; set; }

    internal Func<InstallerTransactionSnapshot, InstallerTransactionSnapshot>?
        PackageCommitResultFactory
    { get; set; }

    internal Func<InstallerTransactionSnapshot, InstallerTransactionSnapshot>?
        FinalResultFactory
    { get; set; }

    internal List<InstallerTransactionSnapshot> MachineIntents { get; } = [];

    internal List<InstallerTransactionSnapshot> MachinePreparationIntents { get; } = [];

    internal List<InstallerTransactionSnapshot> PackageCommitIntents { get; } = [];

    internal List<InstallerTransactionSnapshot> FinalStates { get; } = [];

    internal MemoryInstallerTransactionStore Store { get; }

    internal TestInstallerReleaseLease? LastReleaseLease { get; private set; }

    internal InstallerCoordinator CreateCoordinator() =>
        new(this, this, this, this, this, this, Store);

    public Task<InstallerEnvironmentSnapshot> InspectAsync(
        InstallerRequest request,
        CancellationToken cancellationToken)
    {
        Events.Add("environment.inspect");
        return EnvironmentAction?.Invoke(cancellationToken) ?? Task.FromResult(Environment);
    }

    public Task<IInstallerReleaseLease> VerifyAsync(
        InstallerRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add("release.verify");
        LastReleaseLease = InstallerTestData.Lease(
            Release,
            async (_, token) =>
            {
                Events.Add("release.reverify");
                if (ReleaseReverifyAction is not null)
                {
                    await ReleaseReverifyAction(token);
                }
            },
            () =>
            {
                Events.Add("release.dispose");
                return ValueTask.CompletedTask;
            },
            ReleaseManifest,
            LockedFiles);
        return Task.FromResult<IInstallerReleaseLease>(LastReleaseLease);
    }

    async Task IInstallerPackageMutation.ApplyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        Events.Add($"package.apply:{request.Operation}");
        if (PackageAction is not null)
        {
            await PackageAction(cancellationToken);
        }
    }

    async Task IInstallerCertificateMutation.ApplyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        Events.Add($"certificate.apply:{request.Operation}");
        if (CertificateAction is not null)
        {
            await CertificateAction(cancellationToken);
        }
    }

    async Task<InstallerTransactionSnapshot> IInstallerMachineMutation.ApplyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durableIntent,
        CancellationToken cancellationToken)
    {
        MachineIntents.Add(durableIntent);
        Events.Add($"machine.apply:{request.Operation}");
        if (MachineAction is not null)
        {
            await MachineAction(cancellationToken);
        }

        InstallerTransactionSnapshot committed = InstallerTransactionSnapshot.Create(
            durableIntent.Journal.TransitionTo(
                InstallerTransactionPhase.MachineCommitted));
        await Store.CommitHelperStateAsync(durableIntent, committed, cancellationToken);
        if (MachineResponseAction is not null)
        {
            await MachineResponseAction(cancellationToken);
        }

        return MachineResultFactory?.Invoke(durableIntent) ?? committed;
    }

    async Task<InstallerTransactionSnapshot> IInstallerMachineMutation.PrepareAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durableIntent,
        CancellationToken cancellationToken)
    {
        MachinePreparationIntents.Add(durableIntent);
        Events.Add($"machine.prepare:{request.Operation}");
        if (MachinePrepareAdmissionAction is not null)
        {
            await MachinePrepareAdmissionAction(cancellationToken);
        }

        await Store.PersistHelperIntentAsync(durableIntent, cancellationToken);
        if (MachinePrepareAction is not null)
        {
            await MachinePrepareAction(cancellationToken);
        }

        InstallerTransactionPhase committedPhase = request.Operation == InstallerOperation.Uninstall
            ? InstallerTransactionPhase.MachineRemovalAuthorized
            : InstallerTransactionPhase.MachineReserved;
        InstallerTransactionSnapshot committed = InstallerTransactionSnapshot.Create(
            durableIntent.Journal.TransitionTo(committedPhase));
        await Store.CommitHelperStateAsync(durableIntent, committed, cancellationToken);
        return MachinePrepareResultFactory?.Invoke(durableIntent) ?? committed;
    }

    async Task<InstallerTransactionSnapshot> IInstallerMachineMutation.CommitPackageAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durableIntent,
        CancellationToken cancellationToken)
    {
        PackageCommitIntents.Add(durableIntent);
        Events.Add($"machine.commit_package:{request.Operation}");
        if (PackageCommitAction is not null)
        {
            await PackageCommitAction(cancellationToken);
        }

        InstallerTransactionSnapshot committed = InstallerTransactionSnapshot.Create(
            durableIntent.Journal.TransitionTo(
                InstallerTransactionPhase.PackageCommitted));
        await Store.CommitHelperStateAsync(durableIntent, committed, cancellationToken);
        return PackageCommitResultFactory?.Invoke(durableIntent) ?? committed;
    }

    public async Task<InstallerTransactionSnapshot> VerifyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durableState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FinalStates.Add(durableState);
        Events.Add("final.verify");
        if (FinalVerifyAction is not null)
        {
            await FinalVerifyAction(cancellationToken);
        }

        InstallerTransactionSnapshot committed = InstallerTransactionSnapshot.Create(
            durableState.Journal.TransitionTo(InstallerTransactionPhase.Verified));
        await Store.CommitHelperStateAsync(durableState, committed, cancellationToken);
        return FinalResultFactory?.Invoke(durableState) ?? committed;
    }

    public async Task<InstallerTransactionSnapshot> ClearVerifiedAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot verifiedState,
        CancellationToken cancellationToken)
    {
        request.Validate();
        verifiedState.Validate();
        Assert.True(verifiedState.Journal.Matches(request));
        await Store.ClearVerifiedAsync(
            verifiedState.Journal.TransactionId,
            verifiedState.ContentHash,
            cancellationToken);
        if (FinalClearResponseAction is not null)
        {
            await FinalClearResponseAction(cancellationToken);
        }

        return verifiedState;
    }
}

internal sealed class MemoryInstallerTransactionStore : IInstallerTransactionStore
{
    private readonly List<string> _events;

    internal MemoryInstallerTransactionStore(
        List<string> events,
        InstallerTransactionJournal? initialJournal)
    {
        _events = events;
        Current = initialJournal is null
            ? null
            : InstallerTransactionSnapshot.Create(initialJournal);
    }

    internal InstallerTransactionSnapshot? Current { get; private set; }

    internal Func<CancellationToken, Task<InstallerTransactionSnapshot?>>? LoadAction { get; set; }

    public async Task<InstallerTransactionSnapshot?> LoadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add("journal.load");
        return LoadAction is null
            ? Current
            : await LoadAction(cancellationToken);
    }

    internal async Task PersistHelperIntentAsync(
        InstallerTransactionSnapshot durableIntent,
        CancellationToken cancellationToken)
    {
        durableIntent.Validate();
        if (Current is null)
        {
            Assert.Equal(InstallerTransactionPhase.Prepared, durableIntent.Journal.Phase);
            _ = await SaveAsync(
                durableIntent.Journal,
                expectedCurrentHash: null,
                cancellationToken);
            return;
        }

        Assert.Equal(durableIntent, Current);
    }

    internal async Task CommitHelperStateAsync(
        InstallerTransactionSnapshot durableIntent,
        InstallerTransactionSnapshot committedState,
        CancellationToken cancellationToken)
    {
        durableIntent.Validate();
        committedState.Validate();
        Assert.NotNull(Current);
        if (Current == committedState)
        {
            return;
        }

        Assert.Equal(durableIntent, Current);
        _ = await SaveAsync(
            committedState.Journal,
            durableIntent.ContentHash,
            cancellationToken);
    }

    public Task<InstallerTransactionSnapshot> SaveAsync(
        InstallerTransactionJournal journal,
        string? expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        journal.Validate();
        if (Current is null)
        {
            Assert.Null(expectedCurrentHash);
            Assert.Equal(1, journal.Generation);
        }
        else
        {
            Assert.Equal(Current.ContentHash, expectedCurrentHash);
            Assert.Equal(Current.Journal.TransitionTo(journal.Phase), journal);
        }

        Current = InstallerTransactionSnapshot.Create(journal);
        _events.Add($"journal.save:{journal.Phase}");
        return Task.FromResult(Current);
    }

    public Task ClearVerifiedAsync(
        string transactionId,
        string expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.NotNull(Current);
        Assert.Equal(InstallerTransactionPhase.Verified, Current.Journal.Phase);
        Assert.Equal(transactionId, Current.Journal.TransactionId);
        Assert.Equal(expectedCurrentHash, Current.ContentHash);
        Current = null;
        _events.Add("journal.clear");
        return Task.CompletedTask;
    }
}
