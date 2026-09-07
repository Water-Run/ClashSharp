using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

internal sealed class WindowsOwnerTransferExecutorFixture : IDisposable
{
    internal WindowsOwnerTransferFinalStateFixture State { get; }
    internal WindowsOwnerTransferAccessFixture Native => State.Native;
    internal InstallerOwnerTransferJournal Journal => State.Journal;
    internal OrdinaryStore Ordinary { get; }
    internal ServiceBackend Backend { get; }
    internal WindowsOwnerTransferStartupBlock Startup { get; }
    internal WindowsOwnerTransferPhaseExecutor Executor { get; }
    internal List<string> Mutations { get; } = [];

    internal WindowsOwnerTransferExecutorFixture(bool previous = true, bool next = true)
    {
        State = new(previous, next);
        State.Journal = State.Journal with { Phase = InstallerOwnerTransferPhase.Prepared, Generation = 1 };
        foreach (WindowsOwnerTransferAccessFixture.Entry entry in Native.Entries.Values)
        {
            if (entry.Security.AccessEntries.Any(ace => ace.Sid == WindowsOwnerTransferAccessFixture.NextSid))
            {
                entry.Security = WindowsOwnerTransferAccessFixture.OwnerSecurity(
                    WindowsOwnerTransferAccessFixture.PreviousSid, entry.Directory, entry.Inherited);
            }
        }
        Native.Entries[WindowsOwnerTransferAccessFixture.AssociationPath].Bytes =
            InstallerMachineAssociationCodec.Serialize(Journal.PreviousOwner.Association);
        Native.Entries.Remove(WindowsOwnerTransferAccessFixture.ContinuationPath);
        Native.Entries.Remove(State.Plan.ActivePath);
        if (Journal.PreviousCertificateLedger is { } ledger)
        {
            Native.Add(State.Plan.ActivePath, false, true).Bytes =
                ClashSharp.Installer.Certificates.InstallerCertificateOwnershipCodec.Serialize(ledger);
        }
        State.Certificates.State = new(Journal.PreviousCertificateLedger, null, Journal.NextCertificateLedger);
        Ordinary = new(this);
        Backend = new(this);
        var certificates = new CertificateFiles(this);
        Startup = new(State.Machine.Release, Backend, Native, certificates, Ordinary);
        Executor = new(State.Machine.Release, Ordinary, Backend, Native, new AssociationFiles(this), certificates);
    }

    public void Dispose() => State.Dispose();

    internal WindowsOwnerTransferPhaseExecutor CreateExecutor(IInstallerReleaseLease release) =>
        new(release, Ordinary, Backend, Native, new AssociationFiles(this), new CertificateFiles(this));

    internal sealed class OrdinaryStore(WindowsOwnerTransferExecutorFixture fixture) : IInstallerTransactionStore
    {
        internal int Saves { get; private set; }
        internal int Reads { get; private set; }
        internal string? Failure { get; set; }
        internal Func<int, CancellationToken, Task>? BeforeRead { get; set; }
        internal Func<CancellationToken, Task>? BeforeSave { get; set; }

        public async Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            if (BeforeRead is not null)
            {
                await BeforeRead(Reads, cancellationToken);
            }
            return fixture.Native.Entries.TryGetValue(WindowsOwnerTransferAccessFixture.ContinuationPath, out var entry)
                ? InstallerTransactionSnapshot.Create(InstallerTransactionCodec.Parse(entry.Bytes)) : null;
        }

        public async Task<InstallerTransactionSnapshot> SaveAsync(
            InstallerTransactionJournal journal, string? expectedCurrentHash, CancellationToken cancellationToken)
        {
            Assert.True(fixture.Native.LiveLeases > 0);
            Assert.Null(expectedCurrentHash);
            Assert.False(fixture.Native.Entries.ContainsKey(WindowsOwnerTransferAccessFixture.ContinuationPath));
            Assert.Equal(fixture.Journal.Continuation, journal);
            Saves++;
            if (BeforeSave is not null)
            {
                await BeforeSave(cancellationToken);
            }
            if (Failure == "before")
            {
                throw new IOException("Synthetic uncommitted failure.");
            }
            if (Failure != "no-change")
            {
                fixture.Native.Add(WindowsOwnerTransferAccessFixture.ContinuationPath, false, true).Bytes =
                    InstallerTransactionCodec.Serialize(journal);
                fixture.Mutations.Add("ordinary-prepared");
            }
            if (Failure == "after")
            {
                throw new IOException("Synthetic acknowledgement loss.");
            }
            return InstallerTransactionSnapshot.Create(journal);
        }

        public Task ClearVerifiedAsync(string transactionId, string expectedCurrentHash, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Owner transfer cannot clear the ordinary barrier.");
    }

    internal sealed class ServiceBackend(WindowsOwnerTransferExecutorFixture fixture) : IWindowsOwnerTransferServiceBackend
    {
        internal bool Present { get; private set; } = true;
        private int _live;

        public string ResolveTargetProfile(string targetSid, CancellationToken cancellationToken) =>
            fixture.State.Machine.Backend.ResolveTargetProfile(targetSid, cancellationToken);

        public WindowsMachineDeploymentPlan CreatePlan(InstallerRequest request, InstallerReleaseManifest manifest,
            InstallerMachineAssociation association, string targetProfileRoot, bool removalPlan) =>
            fixture.State.Machine.Backend.CreatePlan(request, manifest, association, targetProfileRoot, removalPlan);

        public void VerifyServiceAbsent(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(Present);
        }

        public IWindowsMachineRootGuard CreateRootGuard(WindowsMachineDeploymentPlan plan, bool createMissing)
        {
            Assert.False(createMissing);
            _live++;
            return new Roots(this);
        }

        public IWindowsMachineAssociationStore CreateAssociationStore(WindowsMachineDeploymentPlan plan, IWindowsMachineRootGuard rootGuard)
        {
            Assert.Equal(1, _live);
            return new Association(fixture);
        }

        public Task StopDeleteServiceAsync(WindowsMachineDeploymentPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(1, _live);
            Assert.Equal(fixture.Journal.PreviousOwner.Association, plan.Association);
            if (Present)
            {
                Present = false;
                fixture.Mutations.Add("service-removed");
            }
            return Task.CompletedTask;
        }

        private sealed class Roots(ServiceBackend owner) : IWindowsMachineRootGuard
        {
            public Task EnsureProtectedAsync(WindowsMachineDeploymentPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;
            public void Dispose() => owner._live--;
        }

        private sealed class Association(WindowsOwnerTransferExecutorFixture fixture) : IWindowsMachineAssociationStore
        {
            public Task VerifyExactAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Equal(fixture.Journal.PreviousOwner.Association, InstallerMachineAssociationCodec.Parse(
                    fixture.Native.Entries[WindowsOwnerTransferAccessFixture.AssociationPath].Bytes));
                return Task.CompletedTask;
            }
            public Task<InstallerMachineAssociationObservation> InspectAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task WriteAndVerifyAsync(InstallerMachineAssociation association, CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task DeleteAndVerifyAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task VerifyAbsentAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
            public void Dispose() { }
        }
    }

    private sealed class AssociationFiles(WindowsOwnerTransferExecutorFixture fixture) : IWindowsOwnerTransferAssociationFileNative
    {
        public Task<WindowsOwnerTransferAssociationObservation> InspectAsync(
            WindowsOwnerTransferAssociationPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new WindowsOwnerTransferAssociationObservation(InstallerMachineAssociationCodec.Parse(
                fixture.Native.Entries[WindowsOwnerTransferAccessFixture.AssociationPath].Bytes), false));
        }
        public Task ReplaceExactAsync(WindowsOwnerTransferAssociationPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(fixture.Native.LiveLeases > 0);
            fixture.Native.Entries[WindowsOwnerTransferAccessFixture.AssociationPath].Bytes = InstallerMachineAssociationCodec.Serialize(plan.Next);
            fixture.Mutations.Add("association-transferred");
            return Task.CompletedTask;
        }
    }

    private sealed class CertificateFiles(WindowsOwnerTransferExecutorFixture fixture) : IWindowsOwnerTransferCertificateFileNative
    {
        public Task<InstallerOwnerTransferCertificateState> ReadAsync(WindowsOwnerTransferCertificatePlan plan, CancellationToken cancellationToken) =>
            fixture.State.Certificates.ReadAsync(plan, cancellationToken);

        public Task ApplyAsync(WindowsOwnerTransferCertificatePlan plan, InstallerOwnerTransferCertificateState expected,
            InstallerOwnerTransferCertificateStep step, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(fixture.State.Certificates.State, expected);
            Assert.Equal(expected.GetNextStep(plan.Journal), step);
            Assert.True(fixture.Native.LiveLeases > 0);
            fixture.State.Certificates.State = step.After;
            if (step.Action == InstallerOwnerTransferCertificateAction.ActivateNext)
            {
                fixture.Native.Entries.Remove(plan.ActivePath);
                if (step.After.Active is { } ledger)
                {
                    fixture.Native.Add(plan.ActivePath, false, true).Bytes =
                        ClashSharp.Installer.Certificates.InstallerCertificateOwnershipCodec.Serialize(ledger);
                }
            }
            fixture.Mutations.Add(step.Action.ToString());
            return Task.CompletedTask;
        }
    }
}
