using System.IO.Compression;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Execution;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMaintenanceRecoveryAuthorityLeaseTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task CombinedAuthorityRetainsEveryOriginalAndMachineFileAndBorrowsOuterExclusion()
    {
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        (WindowsMachineDeploymentPlan plan, string package) = Prepare(fixture);
        var machine = new Machine();
        using IDisposable appOwner = AppBarrier(plan);
        IWindowsInstallerApplicationLease app = Assert.IsAssignableFrom<IWindowsInstallerApplicationLease>(appOwner);
        using WindowsMaintenanceRecoveryAuthorityLease lease = await WindowsMaintenanceRecoveryAuthorityLease
            .AcquireAsync(plan, package, machine, app, CancellationToken.None);

        await lease.ReverifyAsync(CancellationToken.None);
        Assert.Equal(7, lease.PayloadFingerprints.Count);
        Assert.Equal(new WindowsMaintenancePackageStateReader().Read(package, CancellationToken.None), lease.PackageContentsSha256);
        Assert.Throws<IOException>(() => File.Delete(plan.PayloadTargets[0].DestinationPath));
        Assert.Throws<IOException>(() => File.Delete(Path.Combine(package, "AppxManifest.xml")));

        lease.Dispose();
        Assert.False(machine.Disposed);
        app.Reverify(CancellationToken.None);
        using var original = new FileStream(Path.Combine(package, "AppxManifest.xml"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var deployed = new FileStream(plan.PayloadTargets[0].DestinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredOuterAuthorityFailsClosedWithoutReleasingOriginalEvidence(bool expireApplication)
    {
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        (WindowsMachineDeploymentPlan plan, string package) = Prepare(fixture);
        var machine = new Machine();
        using IDisposable appOwner = AppBarrier(plan);
        IWindowsInstallerApplicationLease app = Assert.IsAssignableFrom<IWindowsInstallerApplicationLease>(appOwner);
        using WindowsMaintenanceRecoveryAuthorityLease lease = await WindowsMaintenanceRecoveryAuthorityLease
            .AcquireAsync(plan, package, machine, app, CancellationToken.None);
        if (expireApplication) { appOwner.Dispose(); }
        else { machine.Expired = true; }

        await Assert.ThrowsAsync<InstallerProtocolException>(() => lease.ReverifyAsync(CancellationToken.None));
        Assert.Throws<IOException>(() => File.Delete(plan.PayloadTargets[0].DestinationPath));
        Assert.Throws<IOException>(() => File.Delete(Path.Combine(package, "AppxManifest.xml")));
    }

    [Fact]
    public async Task MissingDeployedFileReleasesEveryNewPinAndKeepsBorrowedAuthority()
    {
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        (WindowsMachineDeploymentPlan plan, string package) = Prepare(fixture);
        File.Delete(plan.PayloadTargets[0].DestinationPath);
        var machine = new Machine();
        using IDisposable appOwner = AppBarrier(plan);
        IWindowsInstallerApplicationLease app = Assert.IsAssignableFrom<IWindowsInstallerApplicationLease>(appOwner);

        await Assert.ThrowsAsync<InstallerProtocolException>(() => WindowsMaintenanceRecoveryAuthorityLease
            .AcquireAsync(plan, package, machine, app, CancellationToken.None));

        Assert.False(machine.Disposed);
        app.Reverify(CancellationToken.None);
        using var original = new FileStream(Path.Combine(package, "AppxManifest.xml"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Directory.Move(plan.CurrentRoot, plan.PreviousRoot);
    }

    [Fact]
    public async Task UIOrVerificationCancellationNeverReleasesAdmittedRecoveryPins()
    {
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        (WindowsMachineDeploymentPlan plan, string package) = Prepare(fixture);
        using IDisposable appOwner = AppBarrier(plan);
        IWindowsInstallerApplicationLease app = Assert.IsAssignableFrom<IWindowsInstallerApplicationLease>(appOwner);
        using WindowsMaintenanceRecoveryAuthorityLease lease = await WindowsMaintenanceRecoveryAuthorityLease
            .AcquireAsync(plan, package, new Machine(), app, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => lease.ReverifyAsync(cancellation.Token));
        Assert.Throws<IOException>(() => File.Delete(plan.PayloadTargets[0].DestinationPath));
        await lease.ReverifyAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AnotherAccountCannotBorrowTheAuthenticatedApplicationBarrier()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        WindowsMachineDeploymentPlan plan = Plan(fixture);

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            WindowsMaintenanceRecoveryAuthorityLease.AcquireAsync(plan, Path.Combine(fixture.RootDirectory, "missing"),
                new Machine(), new WrongAccount(), CancellationToken.None));

        Assert.Equal("installer.recovery.authority_mismatch", failure.DiagnosticCode);
        Assert.False(Directory.Exists(Path.Combine(fixture.RootDirectory, "missing")));
    }

    private static (WindowsMachineDeploymentPlan Plan, string Package) Prepare(WindowsPayloadFixture fixture)
    {
        string package = Path.Combine(fixture.RootDirectory, "registered");
        ZipFile.ExtractToDirectory(fixture.PrimaryPath, package);
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        foreach (WindowsMachinePayloadTarget target in plan.PayloadTargets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target.DestinationPath)!);
            File.Copy(Path.Combine(package, target.Source.Path.Replace('/', Path.DirectorySeparatorChar)), target.DestinationPath);
        }
        return (plan, package);
    }
    private static WindowsMachineDeploymentPlan Plan(WindowsPayloadFixture fixture) => WindowsMachineDeploymentPlan.Create(
        fixture.Request(InstallerOperation.Repair, Owner), fixture.Manifest, InstallerMachineAssociation.Create(Owner, Hash),
        Path.Combine(fixture.RootDirectory, "ProgramFiles"), Path.Combine(fixture.RootDirectory, "ProgramData"), Path.Combine(fixture.RootDirectory, "profile"));
    private static IDisposable AppBarrier(WindowsMachineDeploymentPlan plan)
    {
        string localData = Path.Combine(plan.TargetProfileRoot, "AppData", "Local");
        Directory.CreateDirectory(localData);
        return new WindowsInstallerApplicationLock(true, (_, _) => plan.TargetProfileRoot, () => Owner, () => localData)
            .Acquire(Owner, CancellationToken.None);
    }
    private sealed class Machine : IWindowsInstallerAuthorityLease
    {
        internal bool Expired { get; set; }
        internal bool Disposed { get; private set; }
        public Task ReverifyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Expired || Disposed ? throw new InstallerProtocolException("installer.machine_helper.authority_expired") : Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class WrongAccount : IWindowsInstallerApplicationLease
    {
        public string TargetSid => "S-1-5-21-100-200-300-1002";
        public void Reverify(CancellationToken cancellationToken) => throw new InvalidOperationException("Wrong account must fail before verification or I/O.");
        public void Dispose() => throw new InvalidOperationException("Borrowed authority must not be disposed.");
    }
}
