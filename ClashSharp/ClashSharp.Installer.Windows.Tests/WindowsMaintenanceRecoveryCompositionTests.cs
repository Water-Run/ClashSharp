using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Execution;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMaintenanceRecoveryCompositionTests
{
    [Fact]
    public async Task AuthenticatedFactoryPassesRetainedMachineAndExactApplicationAuthorityToRecovery()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        var resources = new ResourcesFactory(fixture);
        var locks = new Locks(fixture.Outer);
        var admission = new Admission();
        var factory = new WindowsMachineHelperAuthorityFactory(resources, locks, locks, admission, admission);
        var command = MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.Prepare,
            InstallerTransactionSnapshot.Create(InstallerTransactionJournal.Create(fixture.Request)));
        IWindowsMachineHelperAuthorityLease lease = await factory.CreateAsync(command.ToInvocation(), MaintenanceRecoveryFixture.Owner, CancellationToken.None);
        try
        {
            Assert.True(resources.ReceivedAuthority);
            Assert.False(fixture.Outer.Expired);
            var result = await lease.Session.ExecuteAsync(command, CancellationToken.None);
            Assert.Equal(InstallerMachineHelperOutcome.Succeeded, result.Outcome);
            Assert.Equal(1, fixture.Captures);
        }
        finally { await lease.DisposeAsync(); }
        Assert.True(resources.DisposedBeforeAuthority);
        Assert.True(fixture.Outer.Expired);
    }

    [Fact]
    public async Task ParentBuildsExactRecoveryRequestAndRetainsApplicationBarrierUntilSessionDisposal()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        var sessions = new ParentSessions();
        using var engine = WindowsInstallerParentEngine.CreateForTesting(fixture.Manifest, MaintenanceRecoveryFixture.Owner, sessions, sessions);
        Assert.True(engine.SupportsOriginalRestoration);

        InstallerExecutionResult result = await engine.RestoreOriginalAsync(null, CancellationToken.None);

        Assert.Equal(InstallerTransactionPhase.OriginalRestored, result.LastDurablePhase);
        InstallerRequest request = Assert.IsType<InstallerRequest>(sessions.Request);
        Assert.Equal(InstallerOperation.Repair, request.Operation);
        Assert.Equal(MaintenanceRecoveryFixture.Owner, request.TargetSid);
        Assert.False(request.AllowReassociation);
        Assert.Equal(fixture.Manifest.ExpectedPackageVersion, request.ExpectedPackageVersion);
        Assert.Equal(fixture.Manifest.InstallerPayloadSha256, request.InstallerPayloadSha256);
        Assert.True(sessions.SessionDisposed);
        Assert.False(sessions.BarrierHeld);
    }

    private sealed class Locks(MaintenanceRecoveryFixture.Authority authority) : IWindowsInstallerAuthorityLock, IWindowsInstallerApplicationLock
    {
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable>(authority);
        public IDisposable Acquire(string targetSid, CancellationToken cancellationToken)
        {
            Assert.Equal(authority.TargetSid, targetSid);
            return authority;
        }
    }
    private sealed class Admission : IWindowsInstallerOwnerTransferAdmission, IWindowsInstallerRetiredUninstallAdmission
    {
        public Task EnsureOrdinaryActionAllowedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task EnsureNoRetiredUninstallAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class ResourcesFactory(MaintenanceRecoveryFixture fixture) : IWindowsMachineHelperRecoveryResourcesFactory
    {
        internal bool ReceivedAuthority { get; private set; }
        internal bool DisposedBeforeAuthority { get; set; }
        public IWindowsMachineHelperAuthorityResources Create(string targetSid) => throw new InvalidOperationException("Recovery requires retained authority.");
        public IWindowsMachineHelperAuthorityResources Create(string targetSid, IWindowsInstallerAuthorityLease machine, IWindowsInstallerApplicationLease application)
        {
            Assert.Same(fixture.Outer, machine);
            Assert.Same(fixture.Outer, application);
            Assert.Equal(targetSid, application.TargetSid);
            Assert.False(fixture.Outer.Expired);
            ReceivedAuthority = true;
            return new Resources(fixture, this);
        }
    }
    private sealed class Resources : IWindowsMachineHelperAuthorityResources
    {
        private readonly MaintenanceRecoveryFixture _fixture;
        private readonly ResourcesFactory _factory;
        private readonly WindowsMachineHelperOperationExecutor _operations;
        internal Resources(MaintenanceRecoveryFixture fixture, ResourcesFactory factory)
        {
            _fixture = fixture;
            _factory = factory;
            _operations = fixture.Executor();
        }
        public IInstallerTransactionStore TransactionStore => _fixture.Public;
        public IInstallerMachineHelperOperationExecutor Operations => _operations;
        public Task<InstallerDirectoryCleanupReport> CompleteUninstallAsync(InstallerTransactionSnapshot verified, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public ValueTask DisposeAsync()
        {
            _factory.DisposedBeforeAuthority = !_fixture.Outer.Expired;
            _operations.Dispose();
            return ValueTask.CompletedTask;
        }
    }
    private sealed class ParentSessions : IWindowsInstallerOriginalRestorationSessionFactory, IWindowsInstallerApplicationLock, IDisposable
    {
        internal InstallerRequest? Request { get; set; }
        internal bool BarrierHeld { get; private set; }
        internal bool SessionDisposed { get; set; }
        public IDisposable Acquire(string targetSid, CancellationToken cancellationToken) { BarrierHeld = true; return this; }
        public Task<IWindowsInstallerExecutionSession> CreateAsync(CancellationToken cancellationToken)
        {
            Assert.True(BarrierHeld);
            return Task.FromResult<IWindowsInstallerExecutionSession>(new ParentSession(this));
        }
        public void Dispose() { Assert.True(SessionDisposed); BarrierHeld = false; }
    }
    private sealed class ParentSession(ParentSessions owner) : IWindowsInstallerExecutionSession, IWindowsInstallerOriginalRestorationSession
    {
        public Task<InstallerExecutionResult> ExecuteAsync(InstallerRequest request, IProgress<InstallerProgress>? progress, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<InstallerExecutionResult> RestoreOriginalAsync(InstallerRequest request, IProgress<InstallerProgress>? progress, CancellationToken cancellationToken)
        {
            Assert.True(owner.BarrierHeld);
            owner.Request = request;
            return Task.FromResult(new InstallerExecutionResult(InstallerExecutionOutcome.Succeeded,
                "installer.recovery.original_restored", InstallerTransactionPhase.OriginalRestored, false));
        }
        public ValueTask DisposeAsync() { Assert.True(owner.BarrierHeld); owner.SessionDisposed = true; return ValueTask.CompletedTask; }
    }
}
