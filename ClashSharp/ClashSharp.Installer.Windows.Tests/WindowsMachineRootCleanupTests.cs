using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMachineRootCleanupTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string Token =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void MissingRootsAreIdempotentAndNeedNoDelete()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeCleanupNative(plan);
        var cleanup = new WindowsMachineRootCleanup(native);

        cleanup.RemoveAndVerify(plan, CancellationToken.None);
        cleanup.VerifyAbsent(plan, CancellationToken.None);

        Assert.Equal(0, native.DeleteCalls);
    }

    [Fact]
    public void ProfileIndependentVerificationIsReadOnlyAndRejectsRemainingRoot()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeCleanupNative(plan);
        native.Set(plan.MachineRoot, WindowsMachineRootState.EmptyOrdinaryDirectory);
        var cleanup = new WindowsMachineRootCleanup(native);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            cleanup.VerifyAbsent(plan.Roots, CancellationToken.None));

        Assert.Equal(
            "installer.machine.root_removal_verification_failed",
            exception.DiagnosticCode);
        Assert.Equal(0, native.DeleteCalls);
    }

    [Fact]
    public void ExactEmptyRootsAreDeletedAndVerified()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeCleanupNative(plan);
        native.Set(plan.MachineRoot, WindowsMachineRootState.EmptyOrdinaryDirectory);
        native.Set(plan.ServiceDataRoot, WindowsMachineRootState.EmptyOrdinaryDirectory);
        var cleanup = new WindowsMachineRootCleanup(native);

        cleanup.RemoveAndVerify(plan, CancellationToken.None);

        Assert.Equal(2, native.DeleteCalls);
        cleanup.VerifyAbsent(plan, CancellationToken.None);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void NonemptyOrUnsafeRootIsNeverDeleted(int stateValue)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeCleanupNative(plan);
        native.Set(plan.MachineRoot, (WindowsMachineRootState)stateValue);
        var cleanup = new WindowsMachineRootCleanup(native);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            cleanup.RemoveAndVerify(plan, CancellationToken.None));

        Assert.Equal("installer.machine.root_cleanup_unsafe", exception.DiagnosticCode);
        Assert.Equal(0, native.DeleteCalls);
    }

    [Fact]
    public void LostDeleteAcknowledgementIsAcceptedOnlyAfterMissingIsObserved()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeCleanupNative(plan)
        {
            DeleteFailure = new Win32Exception(1726),
        };
        native.Set(plan.MachineRoot, WindowsMachineRootState.EmptyOrdinaryDirectory);
        native.Set(plan.ServiceDataRoot, WindowsMachineRootState.EmptyOrdinaryDirectory);
        var cleanup = new WindowsMachineRootCleanup(native);

        cleanup.RemoveAndVerify(plan, CancellationToken.None);

        Assert.Equal(2, native.DeleteCalls);
    }

    [Fact]
    public void DeleteWithoutObservableEffectEndsUncertain()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeCleanupNative(plan)
        {
            ApplyDelete = false,
            DeleteFailure = new Win32Exception(5),
        };
        native.Set(plan.MachineRoot, WindowsMachineRootState.EmptyOrdinaryDirectory);
        var cleanup = new WindowsMachineRootCleanup(native);

        InstallerStateUncertainException exception =
            Assert.Throws<InstallerStateUncertainException>(() =>
                cleanup.RemoveAndVerify(plan, CancellationToken.None));

        Assert.Equal("installer.machine.root_cleanup_uncertain", exception.DiagnosticCode);
    }

    [Fact]
    public void PreCancellationMakesNoNativeCalls()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeCleanupNative(plan);
        var cleanup = new WindowsMachineRootCleanup(native);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            cleanup.RemoveAndVerify(plan, cancellation.Token));

        Assert.Equal(0, native.InspectCalls);
        Assert.Equal(0, native.DeleteCalls);
    }

    [Fact]
    public void WindowsNativeDeletesOnlyTheTwoEmptyFixtureLeafRoots()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        Directory.CreateDirectory(plan.MachineRoot);
        Directory.CreateDirectory(plan.ServiceDataRoot);
        var cleanup = new WindowsMachineRootCleanup();

        cleanup.RemoveAndVerify(plan, CancellationToken.None);

        Assert.False(Directory.Exists(plan.MachineRoot));
        Assert.False(Directory.Exists(plan.ServiceDataRoot));
        Assert.True(Directory.Exists(Path.GetDirectoryName(plan.MachineRoot)));
        Assert.True(Directory.Exists(Path.GetDirectoryName(plan.ServiceDataRoot)));
    }

    [Fact]
    public void DeletionHandlePinsTheExactDirectoryAgainstRename()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        Directory.CreateDirectory(plan.MachineRoot);
        string moved = string.Concat(plan.MachineRoot, ".moved");

        using (WindowsFileSystemNative.OpenOrdinaryDirectoryForDeletion(plan.MachineRoot))
        {
            Exception? failure = Record.Exception(() =>
                Directory.Move(plan.MachineRoot, moved));

            Assert.NotNull(failure);
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.True(Directory.Exists(plan.MachineRoot));
            Assert.False(Directory.Exists(moved));
        }
    }

    private static WindowsPayloadFixture Fixture() =>
        new(
            createPayload: false,
            removeCurrentUserCertificateOnDispose: false);

    private static WindowsMachineDeploymentPlan Plan(WindowsPayloadFixture fixture) =>
        WindowsMachineDeploymentPlan.Create(
            fixture.Request(targetSid: TargetSid),
            fixture.Manifest,
            InstallerMachineAssociation.Create(TargetSid, Token),
            Path.Combine(fixture.RootDirectory, "Program Files"),
            Path.Combine(fixture.RootDirectory, "ProgramData"),
            Path.Combine(fixture.RootDirectory, "Users", "owner"));

    private sealed class FakeCleanupNative : IWindowsMachineRootCleanupNative
    {
        private readonly Dictionary<string, WindowsMachineRootState> _states;

        internal FakeCleanupNative(WindowsMachineDeploymentPlan plan)
        {
            _states = new(StringComparer.OrdinalIgnoreCase)
            {
                [plan.MachineRoot] = WindowsMachineRootState.Missing,
                [plan.ServiceDataRoot] = WindowsMachineRootState.Missing,
            };
        }

        internal bool ApplyDelete { get; init; } = true;

        internal Exception? DeleteFailure { get; init; }

        internal int InspectCalls { get; private set; }

        internal int DeleteCalls { get; private set; }

        internal void Set(string path, WindowsMachineRootState state) =>
            _states[path] = state;

        public WindowsMachineRootState Inspect(string path)
        {
            InspectCalls++;
            return _states[path];
        }

        public void DeleteEmpty(string path)
        {
            DeleteCalls++;
            if (ApplyDelete)
            {
                _states[path] = WindowsMachineRootState.Missing;
            }

            if (DeleteFailure is not null)
            {
                throw DeleteFailure;
            }
        }
    }
}
