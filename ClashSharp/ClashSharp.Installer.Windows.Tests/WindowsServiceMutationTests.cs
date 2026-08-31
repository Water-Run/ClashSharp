using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsServiceMutationTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string Token =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task StopDisableAndFenceReachesExactPreparedState()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeMutationNative(Installed(plan));
        var delay = new RecordingDelay();
        WindowsServiceMutation mutation = Mutation(native, delay);

        await mutation.StopDisableAndFenceAsync(plan, CancellationToken.None);

        WindowsServiceSnapshot snapshot = Assert.IsType<WindowsServiceSnapshot>(native.Snapshot);
        Assert.Equal(WindowsServiceRuntimeState.Stopped, snapshot.RuntimeState);
        Assert.Equal(WindowsServiceStartMode.Disabled, snapshot.Configuration.StartMode);
        Assert.Equal(
            WindowsServiceConfigurationVerifier.BuildMutationFenceDaclSddl(),
            snapshot.DaclSddl);
        Assert.Equal(1, native.StopFenceCalls);
        Assert.Equal(0, delay.Calls);
    }

    [Fact]
    public async Task MissingServiceIsAnIdempotentPreparePostcondition()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeMutationNative(snapshot: null);
        WindowsServiceMutation mutation = Mutation(native);

        await mutation.StopDisableAndFenceAsync(plan, CancellationToken.None);

        Assert.Null(native.Snapshot);
        Assert.Equal(1, native.StopFenceCalls);
        Assert.Equal(2, native.InspectCalls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnsafeExistingServiceIsRejectedBeforeEveryMutation(
        bool sharedProcess,
        bool foreignAccount)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsServiceSnapshot unsafeSnapshot = Installed(plan) with
        {
            Configuration = plan.Service with
            {
                ProcessType = sharedProcess
                    ? WindowsServiceProcessType.SharedProcess
                    : WindowsServiceProcessType.OwnProcess,
                AccountName = foreignAccount
                    ? "NT AUTHORITY\\LocalService"
                    : "LocalSystem",
            },
        };

        foreach (Func<WindowsServiceMutation, Task> action in Actions(plan))
        {
            var native = new FakeMutationNative(unsafeSnapshot);
            WindowsServiceMutation mutation = Mutation(native);

            InstallerProtocolException exception =
                await Assert.ThrowsAsync<InstallerProtocolException>(() => action(mutation));

            Assert.Equal("installer.machine.existing_service_unsafe", exception.DiagnosticCode);
            Assert.Equal(0, native.MutationCalls);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SafeButForeignServiceCannotBeStoppedOrFenced(
        bool foreignBinary,
        bool foreignDacl)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsServiceSnapshot snapshot = Installed(plan) with
        {
            Configuration = foreignBinary
                ? plan.Service with
                {
                    BinaryPath = "\"C:\\Other\\service.exe\" --foreign",
                }
                : plan.Service,
            DaclSddl = foreignDacl
                ? WindowsServiceConfigurationVerifier.NormalizeDacl(
                    "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)")
                : WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(TargetSid),
        };
        var native = new FakeMutationNative(snapshot);
        WindowsServiceMutation mutation = Mutation(native);

        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                mutation.StopDisableAndFenceAsync(plan, CancellationToken.None));

        Assert.Equal("installer.machine.existing_service_not_owned", exception.DiagnosticCode);
        Assert.Equal(0, native.StopFenceCalls);
    }

    [Fact]
    public async Task ConfigureAndStartReachesExactRunningState()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeMutationNative(snapshot: null);
        WindowsServiceMutation mutation = Mutation(native);

        await mutation.ConfigureStartAndVerifyAsync(plan, CancellationToken.None);

        WindowsServiceSnapshot snapshot = Assert.IsType<WindowsServiceSnapshot>(native.Snapshot);
        Assert.True(WindowsServiceConfigurationVerifier.ConfigurationMatches(
            snapshot.Configuration,
            plan.Service));
        Assert.Equal(WindowsServiceRuntimeState.Running, snapshot.RuntimeState);
        Assert.Equal(
            WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(TargetSid),
            snapshot.DaclSddl);
        Assert.Equal(1, native.EnsureCalls);
        Assert.Equal(1, native.StartCalls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LostConfigurationOrStartAcknowledgementIsReconciled(
        bool loseConfigurationAcknowledgement,
        bool loseStartAcknowledgement)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeMutationNative(snapshot: null)
        {
            EnsureFailure = loseConfigurationAcknowledgement
                ? new Win32Exception(1726)
                : null,
            StartFailure = loseStartAcknowledgement
                ? new Win32Exception(1726)
                : null,
        };
        WindowsServiceMutation mutation = Mutation(native);

        await mutation.ConfigureStartAndVerifyAsync(plan, CancellationToken.None);

        Assert.Equal(WindowsServiceRuntimeState.Running, native.Snapshot?.RuntimeState);
        Assert.Equal(1, native.EnsureCalls);
        Assert.Equal(1, native.StartCalls);
    }

    [Fact]
    public async Task FailedMutationWithoutObservableEffectEndsUncertain()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeMutationNative(new WindowsServiceSnapshot(
            plan.Service with
            {
                DisplayName = "Previous service",
                StartMode = WindowsServiceStartMode.Disabled,
            },
            WindowsServiceRuntimeState.Stopped,
            WindowsServiceConfigurationVerifier.BuildMutationFenceDaclSddl()))
        {
            ApplyEnsure = false,
            EnsureFailure = new Win32Exception(5),
        };
        var delay = new RecordingDelay();
        WindowsServiceMutation mutation = Mutation(native, delay, maximumPolls: 2);

        InstallerStateUncertainException exception =
            await Assert.ThrowsAsync<InstallerStateUncertainException>(() =>
                mutation.ConfigureStartAndVerifyAsync(plan, CancellationToken.None));

        Assert.Equal("installer.machine.service_state_uncertain", exception.DiagnosticCode);
        Assert.Equal(1, native.EnsureCalls);
        Assert.Equal(0, native.StartCalls);
        Assert.Equal(1, delay.Calls);
    }

    [Fact]
    public async Task ExactConfiguredReplaySkipsConfigurationAndOnlyEnsuresRunning()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeMutationNative(Installed(plan) with
        {
            RuntimeState = WindowsServiceRuntimeState.Stopped,
        });
        WindowsServiceMutation mutation = Mutation(native);

        await mutation.ConfigureStartAndVerifyAsync(plan, CancellationToken.None);

        Assert.Equal(0, native.EnsureCalls);
        Assert.Equal(1, native.StartCalls);
        Assert.Equal(WindowsServiceRuntimeState.Running, native.Snapshot?.RuntimeState);
    }

    [Fact]
    public async Task UnfencedConfigurationDriftIsRejectedBeforeScmWrite()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeMutationNative(Installed(plan) with
        {
            Configuration = plan.Service with { DisplayName = "Previous service" },
            RuntimeState = WindowsServiceRuntimeState.Stopped,
        });
        WindowsServiceMutation mutation = Mutation(native);

        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                mutation.ConfigureStartAndVerifyAsync(plan, CancellationToken.None));

        Assert.Equal(
            "installer.machine.existing_service_not_prepared",
            exception.DiagnosticCode);
        Assert.Equal(0, native.EnsureCalls);
        Assert.Equal(0, native.StartCalls);
    }

    [Fact]
    public async Task LostDeleteAcknowledgementIsAcceptedOnlyAfterAbsenceIsObserved()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeMutationNative(Installed(plan))
        {
            DeleteFailure = new Win32Exception(1726),
        };
        WindowsServiceMutation mutation = Mutation(native);

        await mutation.StopDeleteAndVerifyAsync(plan, CancellationToken.None);

        Assert.Null(native.Snapshot);
        Assert.Equal(1, native.DeleteCalls);
    }

    [Fact]
    public async Task MissingServiceSkipsDeleteMutation()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeMutationNative(snapshot: null);
        WindowsServiceMutation mutation = Mutation(native);

        await mutation.StopDeleteAndVerifyAsync(plan, CancellationToken.None);

        Assert.Equal(0, native.DeleteCalls);
        Assert.Equal(1, native.InspectCalls);
    }

    [Fact]
    public async Task SafeButForeignServiceTupleCannotBeDeleted()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeMutationNative(Installed(plan) with
        {
            Configuration = plan.Service with
            {
                BinaryPath = "\"C:\\Other\\service.exe\" --foreign",
            },
        });
        WindowsServiceMutation mutation = Mutation(native);

        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                mutation.StopDeleteAndVerifyAsync(plan, CancellationToken.None));

        Assert.Equal("installer.machine.existing_service_not_owned", exception.DiagnosticCode);
        Assert.Equal(0, native.DeleteCalls);
    }

    [Fact]
    public async Task InspectionFailureIsSanitizedBeforeMutation()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeMutationNative(snapshot: null)
        {
            InspectFailure = new Win32Exception(5),
        };
        WindowsServiceMutation mutation = Mutation(native);

        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                mutation.StopDisableAndFenceAsync(plan, CancellationToken.None));

        Assert.Equal("installer.machine.service_inspection_failed", exception.DiagnosticCode);
        Assert.Equal(0, native.MutationCalls);
    }

    [Fact]
    public async Task PreCancellationMakesNoNativeOrDelayCalls()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        foreach (Func<WindowsServiceMutation, Task> action in Actions(plan, cancellation.Token))
        {
            var native = new FakeMutationNative(Installed(plan));
            var delay = new RecordingDelay();
            WindowsServiceMutation mutation = Mutation(native, delay);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action(mutation));

            Assert.Equal(0, native.InspectCalls);
            Assert.Equal(0, native.MutationCalls);
            Assert.Equal(0, delay.Calls);
        }
    }

    private static IEnumerable<Func<WindowsServiceMutation, Task>> Actions(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken = default)
    {
        yield return mutation => mutation.StopDisableAndFenceAsync(plan, cancellationToken);
        yield return mutation => mutation.ConfigureStartAndVerifyAsync(plan, cancellationToken);
        yield return mutation => mutation.StopDeleteAndVerifyAsync(plan, cancellationToken);
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
            @"C:\Program Files",
            @"C:\ProgramData",
            @"C:\Users\owner");

    private static WindowsServiceSnapshot Installed(WindowsMachineDeploymentPlan plan) =>
        new(
            plan.Service,
            WindowsServiceRuntimeState.Running,
            WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(TargetSid));

    private static WindowsServiceMutation Mutation(
        FakeMutationNative native,
        RecordingDelay? delay = null,
        int maximumPolls = 3) =>
        new(
            native,
            delay ?? new RecordingDelay(),
            new WindowsServiceMutationLimits(
                maximumPolls,
                TimeSpan.FromMilliseconds(1)));

    private sealed class RecordingDelay : IWindowsServiceMutationDelay
    {
        internal int Calls { get; private set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMutationNative : IWindowsServiceMutationNative
    {
        internal FakeMutationNative(WindowsServiceSnapshot? snapshot)
        {
            Snapshot = snapshot;
        }

        internal WindowsServiceSnapshot? Snapshot { get; private set; }

        internal Exception? InspectFailure { get; init; }

        internal Exception? EnsureFailure { get; init; }

        internal Exception? StartFailure { get; init; }

        internal Exception? DeleteFailure { get; init; }

        internal bool ApplyEnsure { get; init; } = true;

        internal int InspectCalls { get; private set; }

        internal int StopFenceCalls { get; private set; }

        internal int EnsureCalls { get; private set; }

        internal int StartCalls { get; private set; }

        internal int DeleteCalls { get; private set; }

        internal int MutationCalls => StopFenceCalls + EnsureCalls + StartCalls + DeleteCalls;

        public WindowsServiceSnapshot? Inspect(string serviceName)
        {
            Assert.Equal(WindowsMachineDeploymentPlan.ServiceName, serviceName);
            InspectCalls++;
            if (InspectFailure is not null)
            {
                throw InspectFailure;
            }

            return Snapshot;
        }

        public void StopDisableAndFence(string serviceName, string fenceDaclSddl)
        {
            Assert.Equal(WindowsMachineDeploymentPlan.ServiceName, serviceName);
            StopFenceCalls++;
            if (Snapshot is not null)
            {
                Snapshot = Snapshot with
                {
                    Configuration = Snapshot.Configuration with
                    {
                        StartMode = WindowsServiceStartMode.Disabled,
                    },
                    RuntimeState = WindowsServiceRuntimeState.Stopped,
                    DaclSddl = fenceDaclSddl,
                };
            }
        }

        public void EnsureConfigured(
            WindowsServiceConfiguration configuration,
            string expectedDaclSddl)
        {
            EnsureCalls++;
            if (ApplyEnsure)
            {
                Snapshot = new WindowsServiceSnapshot(
                    configuration,
                    WindowsServiceRuntimeState.Stopped,
                    expectedDaclSddl);
            }

            if (EnsureFailure is not null)
            {
                throw EnsureFailure;
            }
        }

        public void Start(string serviceName)
        {
            Assert.Equal(WindowsMachineDeploymentPlan.ServiceName, serviceName);
            StartCalls++;
            if (Snapshot is not null)
            {
                Snapshot = Snapshot with
                {
                    RuntimeState = WindowsServiceRuntimeState.Running,
                };
            }

            if (StartFailure is not null)
            {
                throw StartFailure;
            }
        }

        public void StopAndDelete(string serviceName)
        {
            Assert.Equal(WindowsMachineDeploymentPlan.ServiceName, serviceName);
            DeleteCalls++;
            Snapshot = null;
            if (DeleteFailure is not null)
            {
                throw DeleteFailure;
            }
        }
    }
}
