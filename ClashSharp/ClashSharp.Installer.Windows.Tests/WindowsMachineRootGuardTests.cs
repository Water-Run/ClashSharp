using System.Security.AccessControl;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMachineRootGuardTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string OtherSid = "S-1-5-21-100-200-300-1002";
    private const string UsersSid = "S-1-5-32-545";
    private const string Token =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task CreatesAndPinsOnlyTheTwoFixedProtectedRootChains()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeDirectoryNative(TargetSid);
        using WindowsMachineRootGuard guard =
            WindowsMachineRootGuard.CreateForTesting(plan, native);

        await guard.EnsureProtectedAsync(plan, CancellationToken.None);

        Assert.Equal(
            new[]
            {
                Path.Combine(plan.ProgramFilesRoot, "ClashSharp"),
                plan.MachineRoot,
                Path.Combine(plan.CommonApplicationDataRoot, "ClashSharp"),
                plan.ServiceDataRoot,
            },
            native.CreatedPaths);
        Assert.True(native.ActiveLeaseCount > 0);
        Assert.Equal(native.ActiveLeaseCount * 2, native.ObservationCount);
        Assert.All(
            native.OpenRequests,
            request => Assert.Equal(
                native.CreatedPaths.Contains(request.Path, StringComparer.OrdinalIgnoreCase),
                request.PreventRename));

        int opened = native.OpenCount;
        int created = native.CreatedPaths.Count;
        await guard.EnsureProtectedAsync(plan, CancellationToken.None);
        Assert.Equal(opened, native.OpenCount);
        Assert.Equal(created, native.CreatedPaths.Count);

        guard.Dispose();
        Assert.Equal(0, native.ActiveLeaseCount);
    }

    [Fact]
    public async Task ExactProtectedRootAclDriftFailsClosedOnRevalidation()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeDirectoryNative(TargetSid);
        using WindowsMachineRootGuard guard =
            WindowsMachineRootGuard.CreateForTesting(plan, native);
        await guard.EnsureProtectedAsync(plan, CancellationToken.None);
        native.Set(plan.MachineRoot, Protected(OtherSid));

        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                guard.EnsureProtectedAsync(plan, CancellationToken.None));

        Assert.Equal("installer.machine.root_acl_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public async Task ReadOnlyGuardPinsExistingRootsWithoutCreatingThem()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeDirectoryNative(TargetSid);
        native.Set(plan.MachineRoot, Protected(TargetSid));
        native.Set(plan.ServiceDataRoot, Protected(TargetSid));
        using WindowsMachineRootGuard guard =
            WindowsMachineRootGuard.CreateReadOnlyForTesting(plan, native);

        await guard.EnsureProtectedAsync(plan, CancellationToken.None);

        Assert.Empty(native.CreatedPaths);
        Assert.Contains(
            native.OpenRequests,
            request => string.Equals(
                    request.Path,
                    plan.MachineRoot,
                    StringComparison.OrdinalIgnoreCase)
                && request.PreventRename);
        Assert.Contains(
            native.OpenRequests,
            request => string.Equals(
                    request.Path,
                    plan.ServiceDataRoot,
                    StringComparison.OrdinalIgnoreCase)
                && request.PreventRename);
    }

    [Fact]
    public async Task UnsafeAncestorOrReparsePointIsRejected()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var unsafeAncestor = new FakeDirectoryNative(TargetSid);
        unsafeAncestor.Set(
            plan.ProgramFilesRoot,
            Anchor(Ace(UsersSid, FileSystemRights.Delete, AceFlags.None)));
        using WindowsMachineRootGuard ancestorGuard =
            WindowsMachineRootGuard.CreateForTesting(plan, unsafeAncestor);

        InstallerProtocolException ancestor =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                ancestorGuard.EnsureProtectedAsync(plan, CancellationToken.None));
        Assert.Equal(
            "installer.machine.root_ancestor_acl_invalid",
            ancestor.DiagnosticCode);

        var reparseNative = new FakeDirectoryNative(TargetSid);
        reparseNative.Set(
            plan.ProgramFilesRoot,
            Anchor() with { IsReparsePoint = true });
        using WindowsMachineRootGuard reparseGuard =
            WindowsMachineRootGuard.CreateForTesting(plan, reparseNative);
        InstallerProtocolException reparse =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                reparseGuard.EnsureProtectedAsync(plan, CancellationToken.None));
        Assert.Equal("installer.machine.root_reparse_rejected", reparse.DiagnosticCode);
    }

    [Fact]
    public async Task GuardCannotBeReusedForAnotherTargetOrRootPlan()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsMachineDeploymentPlan other = Plan(fixture, OtherSid);
        var native = new FakeDirectoryNative(TargetSid);
        using WindowsMachineRootGuard guard =
            WindowsMachineRootGuard.CreateForTesting(plan, native);

        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                guard.EnsureProtectedAsync(other, CancellationToken.None));

        Assert.Equal("installer.machine.root_plan_changed", exception.DiagnosticCode);
        Assert.Equal(0, native.OpenCount);
    }

    [Fact]
    public async Task PreCancellationCreatesOrOpensNothing()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeDirectoryNative(TargetSid);
        using WindowsMachineRootGuard guard =
            WindowsMachineRootGuard.CreateForTesting(plan, native);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            guard.EnsureProtectedAsync(plan, cancellation.Token));

        Assert.Empty(native.CreatedPaths);
        Assert.Equal(0, native.OpenCount);
    }

    private static WindowsInstallerDirectoryObservation Anchor(
        params WindowsInstallerDirectoryAce[] additional)
    {
        var entries = new List<WindowsInstallerDirectoryAce>
        {
            Ace(
                WindowsInstallerDirectorySecurityPolicy.LocalSystemSid,
                FileSystemRights.FullControl,
                AceFlags.None),
            Ace(
                WindowsInstallerDirectorySecurityPolicy.AdministratorsSid,
                FileSystemRights.FullControl,
                AceFlags.None),
            Ace(UsersSid, FileSystemRights.ReadAndExecute, AceFlags.None),
        };
        entries.AddRange(additional);
        return new WindowsInstallerDirectoryObservation(
            IsDirectory: true,
            IsReparsePoint: false,
            new WindowsInstallerDirectorySecuritySnapshot(
                WindowsInstallerDirectorySecurityPolicy.LocalSystemSid,
                HasDacl: true,
                DaclProtected: false,
                entries));
    }

    private static WindowsInstallerDirectoryObservation Protected(string targetSid)
    {
        const AceFlags inheritance = AceFlags.ContainerInherit | AceFlags.ObjectInherit;
        return new WindowsInstallerDirectoryObservation(
            IsDirectory: true,
            IsReparsePoint: false,
            new WindowsInstallerDirectorySecuritySnapshot(
                WindowsInstallerDirectorySecurityPolicy.AdministratorsSid,
                HasDacl: true,
                DaclProtected: true,
                [
                    Ace(
                        WindowsInstallerDirectorySecurityPolicy.LocalSystemSid,
                        FileSystemRights.FullControl,
                        inheritance),
                    Ace(
                        WindowsInstallerDirectorySecurityPolicy.AdministratorsSid,
                        FileSystemRights.FullControl,
                        inheritance),
                    Ace(
                        targetSid,
                        WindowsInstallerDirectorySecurityPolicy.TargetUserReadOnlyRights,
                        inheritance),
                ]));
    }

    private static WindowsInstallerDirectoryAce Ace(
        string sid,
        FileSystemRights rights,
        AceFlags flags) =>
        new(
            sid,
            WindowsInstallerDirectoryAceKind.Allow,
            (int)rights,
            flags,
            IsObjectSpecific: false);

    private static WindowsPayloadFixture Fixture() =>
        new(
            createPayload: false,
            removeCurrentUserCertificateOnDispose: false);

    private static WindowsMachineDeploymentPlan Plan(
        WindowsPayloadFixture fixture,
        string targetSid = TargetSid) =>
        WindowsMachineDeploymentPlan.Create(
            fixture.Request(targetSid: targetSid),
            fixture.Manifest,
            InstallerMachineAssociation.Create(targetSid, Token),
            Path.Combine(fixture.RootDirectory, "Program Files"),
            Path.Combine(fixture.RootDirectory, "ProgramData"),
            Path.Combine(fixture.RootDirectory, "Users", targetSid[^4..]));

    private sealed class FakeDirectoryNative : IWindowsInstallerDirectoryNative
    {
        private readonly Dictionary<string, MutableObservation> _observations =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly string _targetSid;

        internal FakeDirectoryNative(string targetSid)
        {
            _targetSid = targetSid;
        }

        internal List<string> CreatedPaths { get; } = [];

        internal int ActiveLeaseCount { get; private set; }

        internal int ObservationCount { get; private set; }

        internal int OpenCount { get; private set; }

        internal List<(string Path, bool PreventRename)> OpenRequests { get; } = [];

        public void CreateDirectory(string path, DirectorySecurity security)
        {
            ArgumentNullException.ThrowIfNull(security);
            CreatedPaths.Add(path);
            _observations.TryAdd(path, new MutableObservation(Protected(_targetSid)));
        }

        public IWindowsInstallerDirectoryLease OpenDirectory(
            string path,
            bool preventRename)
        {
            OpenCount++;
            OpenRequests.Add((path, preventRename));
            if (!_observations.TryGetValue(path, out MutableObservation? observation))
            {
                observation = new MutableObservation(Anchor());
                _observations.Add(path, observation);
            }

            ActiveLeaseCount++;
            return new FakeLease(this, observation);
        }

        internal void Set(string path, WindowsInstallerDirectoryObservation observation)
        {
            if (_observations.TryGetValue(path, out MutableObservation? existing))
            {
                existing.Value = observation;
            }
            else
            {
                _observations.Add(path, new MutableObservation(observation));
            }
        }

        private sealed class FakeLease : IWindowsInstallerDirectoryLease
        {
            private readonly FakeDirectoryNative _owner;
            private readonly MutableObservation _observation;
            private bool _disposed;

            internal FakeLease(
                FakeDirectoryNative owner,
                MutableObservation observation)
            {
                _owner = owner;
                _observation = observation;
            }

            public WindowsInstallerDirectoryObservation Observe()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _owner.ObservationCount++;
                return _observation.Value;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _owner.ActiveLeaseCount--;
                _disposed = true;
            }
        }

        private sealed class MutableObservation(
            WindowsInstallerDirectoryObservation value)
        {
            internal WindowsInstallerDirectoryObservation Value { get; set; } = value;
        }
    }
}
