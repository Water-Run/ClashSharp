using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Execution;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerTransferPreferencesTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";

    [Theory]
    [InlineData(InstallerExecutionOutcome.Succeeded, true)]
    [InlineData(InstallerExecutionOutcome.Cancelled, false)]
    public async Task TransferAppliesTheCapturedPreferencesOnlyAfterTheCoreSucceeds(InstallerExecutionOutcome outcome, bool applied)
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        Store store = new();
        Factory factory = new() { Outcome = outcome };
        Setup setup = new(factory);
        Inspector inspector = new(fixture.Manifest.ExpectedPackageVersion);
        var coordinator = new WindowsInstallerPreferencesCoordinator(fixture.Manifest, Sid, store, new Shell(), setup: setup);
        using var engine = WindowsInstallerParentEngine.CreateForTesting(fixture.Manifest, Sid, factory, new AppLock(), inspector, coordinator);
        InstallerPreferences selected = InstallerPreferences.Default with
        { CreateDesktopShortcut = false, LaunchAtStartup = true, FollowSystemDefaults = false, EnableMcpServer = true };
        Func<InstallerOwnerTransferOffer, CancellationToken, Task<bool>> confirm = (_, _) => Task.FromResult(true);

        InstallerExecutionResult result = await engine.TransferWithPreferencesAsync(selected, confirm, null, CancellationToken.None);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(1, factory.Transfers);
        Assert.Same(confirm, factory.Confirmation);
        Assert.Equal(0, factory.OrdinarySessions);
        Assert.Equal(applied ? 1 : 0, setup.Calls);
        if (applied)
        {
            Assert.Equal(selected, store.Installed!.Preferences);
            Assert.True(store.Installed.CoreVerified);
            Assert.Null(store.Pending);
        }
        else
        {
            Assert.Null(store.Installed);
            Assert.False(store.Pending!.CoreVerified);
            Assert.Equal(selected, store.Pending.Preferences);
        }
    }

    [Fact]
    public async Task ServiceOptOutRejectsTransferBeforeAnyOfferOrUserSetup()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        Store store = new();
        Factory factory = new();
        Setup setup = new(factory);
        var coordinator = new WindowsInstallerPreferencesCoordinator(fixture.Manifest, Sid, store, new Shell(), setup: setup);
        using var engine = WindowsInstallerParentEngine.CreateForTesting(fixture.Manifest, Sid, factory, new AppLock(), preferences: coordinator);

        InstallerExecutionResult result = await engine.TransferWithPreferencesAsync(
            InstallerPreferences.Default with { InstallTransparentProxyService = false },
            (_, _) => throw new InvalidOperationException("A declined component cannot offer transfer."), null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal("installer.preferences.service_not_selected", result.DiagnosticCode);
        Assert.Equal(0, factory.Transfers);
        Assert.Equal(0, setup.Calls);
        Assert.Null(store.Pending);
    }

    [Fact]
    public async Task UserSetupRetryAfterTransferNeverRepeatsTransferOrCreatesAnOrdinarySession()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        Store store = new();
        Factory factory = new();
        Setup setup = new(factory) { Fail = true };
        Inspector inspector = new(fixture.Manifest.ExpectedPackageVersion);
        var coordinator = new WindowsInstallerPreferencesCoordinator(fixture.Manifest, Sid, store, new Shell(), setup: setup);
        using var engine = WindowsInstallerParentEngine.CreateForTesting(fixture.Manifest, Sid, factory, new AppLock(), inspector, coordinator);
        InstallerExecutionResult first = await engine.TransferWithPreferencesAsync(InstallerPreferences.Default,
            (_, _) => Task.FromResult(true), null, CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, first.Outcome);
        Assert.Equal(InstallerPreferencesWarnings.UserSetupPending, first.PreferencesWarnings);
        Assert.True(store.Pending!.CoreVerified);
        string id = store.Pending.Id;
        setup.Fail = false;
        inspector.Installed = true;

        InstallerExecutionResult retry = await engine.CompletePreferencesAsync(null, CancellationToken.None);

        Assert.Equal(InstallerPreferencesWarnings.None, retry.PreferencesWarnings);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, retry.Outcome);
        Assert.Equal(1, factory.Transfers);
        Assert.Equal(0, factory.OrdinarySessions);
        Assert.Equal(2, setup.Calls);
        Assert.Equal(id, store.Installed!.Id);
        Assert.Null(store.Pending);
    }

    private sealed class Inspector(string version) : IWindowsInstallerParentInspector
    {
        internal bool Installed { get; set; }
        public Task<InstallerRuntimeInspection> InspectAsync(InstallerRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new InstallerRuntimeInspection(new(true, Installed ? version : null, false, null), null, version));
    }

    private sealed class Factory : IWindowsInstallerExecutionSessionFactory, IWindowsInstallerOwnerTransferSessionFactory
    {
        internal InstallerExecutionOutcome Outcome { get; init; } = InstallerExecutionOutcome.Succeeded;
        internal int Transfers { get; private set; }
        internal int OrdinarySessions { get; private set; }
        internal bool Returned { get; private set; }
        internal Func<InstallerOwnerTransferOffer, CancellationToken, Task<bool>>? Confirmation { get; private set; }
        public Task<IWindowsInstallerExecutionSession> CreateAsync(CancellationToken cancellationToken)
        { OrdinarySessions++; throw new InvalidOperationException("Transfer completion cannot create an ordinary session."); }
        public Task<InstallerExecutionResult> TransferAndExecuteAsync(Func<InstallerOwnerTransferOffer, CancellationToken, Task<bool>> confirm,
            IProgress<InstallerProgress>? progress, CancellationToken cancellationToken)
        {
            Transfers++;
            Confirmation = confirm;
            Returned = true;
            return Task.FromResult(new InstallerExecutionResult(Outcome,
                Outcome == InstallerExecutionOutcome.Succeeded ? "installer.completed" : "installer.cancelled",
                Outcome == InstallerExecutionOutcome.Succeeded ? InstallerTransactionPhase.Verified : null, false));
        }
    }

    private sealed class AppLock : IWindowsInstallerApplicationLock
    {
        public IDisposable Acquire(string targetSid, CancellationToken cancellationToken) => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }

    private sealed class Setup(Factory factory) : IWindowsInstallerUserSetup
    {
        internal bool Fail { get; set; }
        internal int Calls { get; private set; }
        public Task<InstallerPreferencesWarnings> ApplyAsync(WindowsInstallerPreferencesDocument document, CancellationToken cancellationToken)
        {
            Assert.True(factory.Returned);
            Assert.True(document.CoreVerified);
            Calls++;
            return Fail ? Task.FromException<InstallerPreferencesWarnings>(new IOException("Synthetic user setup failure."))
                : Task.FromResult(InstallerPreferencesWarnings.None);
        }
    }

    private sealed class Store : IWindowsInstallerPreferencesStore
    {
        internal WindowsInstallerPreferencesDocument? Pending { get; private set; }
        internal WindowsInstallerPreferencesDocument? Installed { get; private set; }
        public WindowsInstallerPreferencesDocument? ReadPending() => Pending;
        public WindowsInstallerPreferencesDocument? ReadInstalled() => Installed;
        public void Stage(WindowsInstallerPreferencesDocument document, WindowsInstallerPreferencesDocument? expected)
        { Assert.Equal(expected, Pending); Pending = document; }
        public void Complete(WindowsInstallerPreferencesDocument document) { Assert.Equal(document, Pending); Installed = document; Pending = null; }
        public void RemoveInstalled(WindowsInstallerPreferencesDocument? expected) => throw new InvalidOperationException("Not an uninstall.");
        public void RemovePending(WindowsInstallerPreferencesDocument? expected) => throw new InvalidOperationException("Not an uninstall.");
    }

    private sealed class Shell : IWindowsInstallerShellIntegration
    {
        public Task ApplyAsync(InstallerPreferences preferences, string applicationId, string iconPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(string applicationId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
