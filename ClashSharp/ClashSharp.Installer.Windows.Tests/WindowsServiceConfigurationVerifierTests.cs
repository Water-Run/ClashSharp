using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsServiceConfigurationVerifierTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string OtherSid = "S-1-5-21-100-200-300-1002";
    private const string Token =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Theory]
    [InlineData(4u, true)]
    [InlineData(4u, false)]
    [InlineData(1u, false)]
    public void ExactTupleAndDaclPassReadOnlyVerification(
        uint runtimeStateValue,
        bool requireRunning)
    {
        var runtimeState = (WindowsServiceRuntimeState)runtimeStateValue;
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new RecordingServiceNative(Snapshot(plan, runtimeState));
        var verifier = new WindowsServiceConfigurationVerifier(native);

        verifier.VerifyInstalled(plan, requireRunning, CancellationToken.None);

        Assert.Equal(1, native.InspectCalls);
        Assert.Equal(WindowsMachineDeploymentPlan.ServiceName, native.ServiceName);
    }

    [Fact]
    public void RunningRequirementRejectsAStoppedService()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var verifier = new WindowsServiceConfigurationVerifier(
            new RecordingServiceNative(
                Snapshot(plan, WindowsServiceRuntimeState.Stopped)));

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            verifier.VerifyInstalled(
                plan,
                requireRunning: true,
                CancellationToken.None));

        Assert.Equal(
            "installer.machine.service_postcondition_failed",
            exception.DiagnosticCode);
    }

    [Fact]
    public void MissingInstalledServiceAndPresentRemovedServiceFailClosed()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var missing = new WindowsServiceConfigurationVerifier(
            new RecordingServiceNative(snapshot: null));
        var present = new WindowsServiceConfigurationVerifier(
            new RecordingServiceNative(
                Snapshot(plan, WindowsServiceRuntimeState.Stopped)));

        InstallerProtocolException missingException = Assert.Throws<InstallerProtocolException>(() =>
            missing.VerifyInstalled(plan, requireRunning: false, CancellationToken.None));
        InstallerProtocolException presentException = Assert.Throws<InstallerProtocolException>(() =>
            present.VerifyAbsent(CancellationToken.None));

        Assert.Equal("installer.machine.service_missing", missingException.DiagnosticCode);
        Assert.Equal(
            "installer.machine.service_removal_verification_failed",
            presentException.DiagnosticCode);
    }

    [Fact]
    public void MissingServiceSatisfiesRemovalPostcondition()
    {
        var native = new RecordingServiceNative(snapshot: null);
        var verifier = new WindowsServiceConfigurationVerifier(native);

        verifier.VerifyAbsent(CancellationToken.None);

        Assert.Equal(1, native.InspectCalls);
    }

    [Fact]
    public void PreparedVerificationAcceptsMissingOrExactStoppedFenceOnly()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var prepared = new WindowsServiceSnapshot(
            plan.Service with { StartMode = WindowsServiceStartMode.Disabled },
            WindowsServiceRuntimeState.Stopped,
            WindowsServiceConfigurationVerifier.BuildMutationFenceDaclSddl());
        new WindowsServiceConfigurationVerifier(
                new RecordingServiceNative(snapshot: null))
            .VerifyPrepared(CancellationToken.None);
        new WindowsServiceConfigurationVerifier(
                new RecordingServiceNative(prepared))
            .VerifyPrepared(CancellationToken.None);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            new WindowsServiceConfigurationVerifier(
                    new RecordingServiceNative(prepared with
                    {
                        RuntimeState = WindowsServiceRuntimeState.Running,
                    }))
                .VerifyPrepared(CancellationToken.None));

        Assert.Equal(
            "installer.machine.service_prepare_verification_failed",
            exception.DiagnosticCode);
    }

    [Fact]
    public void EveryScmTupleDriftIsRejected()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsServiceConfiguration expected = plan.Service;
        WindowsServiceConfiguration[] drifted =
        [
            expected with { DisplayName = "Other" },
            expected with { Description = "Other" },
            expected with { ProcessType = WindowsServiceProcessType.SharedProcess },
            expected with { StartMode = WindowsServiceStartMode.Demand },
            expected with { ErrorMode = WindowsServiceErrorMode.Severe },
            expected with { DelayedAutoStart = false },
            expected with { AccountName = "NT AUTHORITY\\LocalService" },
            expected with { BinaryPath = expected.BinaryPath + " --unexpected \"1\"" },
            expected with { Dependencies = ["Tcpip"] },
        ];

        foreach (WindowsServiceConfiguration configuration in drifted)
        {
            var snapshot = new WindowsServiceSnapshot(
                configuration,
                WindowsServiceRuntimeState.Running,
                WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(TargetSid));
            var verifier = new WindowsServiceConfigurationVerifier(
                new RecordingServiceNative(snapshot));

            InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
                verifier.VerifyInstalled(
                    plan,
                    requireRunning: true,
                    CancellationToken.None));
            Assert.Equal(
                "installer.machine.service_postcondition_failed",
                exception.DiagnosticCode);
        }
    }

    [Fact]
    public void ForeignOrExpandedServiceDaclIsRejected()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        string foreign = WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(OtherSid);
        string expanded = WindowsServiceConfigurationVerifier.NormalizeDacl(
            WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(TargetSid)
            + "(A;;RPWP;;;WD)");

        foreach (string dacl in new[] { foreign, expanded })
        {
            var snapshot = new WindowsServiceSnapshot(
                plan.Service with { Dependencies = [] },
                WindowsServiceRuntimeState.Running,
                dacl);
            var verifier = new WindowsServiceConfigurationVerifier(
                new RecordingServiceNative(snapshot));

            InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
                verifier.VerifyInstalled(
                    plan,
                    requireRunning: true,
                    CancellationToken.None));
            Assert.Equal(
                "installer.machine.service_postcondition_failed",
                exception.DiagnosticCode);
        }
    }

    [Fact]
    public void ExpectedDaclGivesTargetOnlyReadAndInterrogateRights()
    {
        string dacl = WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(TargetSid);

        Assert.Contains(";;;SY)", dacl, StringComparison.Ordinal);
        Assert.Contains(";;;BA)", dacl, StringComparison.Ordinal);
        Assert.Contains($";;;{TargetSid})", dacl, StringComparison.Ordinal);
        string targetAce = dacl[(dacl.LastIndexOf("(A;;", StringComparison.Ordinal))..];
        Assert.Contains("CC", targetAce, StringComparison.Ordinal);
        Assert.Contains("LC", targetAce, StringComparison.Ordinal);
        Assert.Contains("SW", targetAce, StringComparison.Ordinal);
        Assert.DoesNotContain("RP", targetAce, StringComparison.Ordinal);
        Assert.DoesNotContain("WP", targetAce, StringComparison.Ordinal);
        Assert.DoesNotContain("DC", targetAce, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidNativeSnapshotAndNativeFailureAreSanitized()
    {
        var invalid = new WindowsServiceSnapshot(
            new WindowsServiceConfiguration(
                WindowsMachineDeploymentPlan.ServiceName,
                WindowsMachineDeploymentPlan.ServiceDisplayName,
                WindowsMachineDeploymentPlan.ServiceDescription,
                WindowsServiceProcessType.OwnProcess,
                WindowsServiceStartMode.Automatic,
                WindowsServiceErrorMode.Normal,
                DelayedAutoStart: true,
                "LocalSystem",
                @"C:\service.exe",
                Dependencies: []),
            (WindowsServiceRuntimeState)99,
            WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(TargetSid));
        var invalidVerifier = new WindowsServiceConfigurationVerifier(
            new RecordingServiceNative(invalid));
        var failedVerifier = new WindowsServiceConfigurationVerifier(
            new RecordingServiceNative(new Win32Exception(5)));

        InstallerProtocolException invalidException = Assert.Throws<InstallerProtocolException>(() =>
            invalidVerifier.Inspect(CancellationToken.None));
        InstallerProtocolException failedException = Assert.Throws<InstallerProtocolException>(() =>
            failedVerifier.Inspect(CancellationToken.None));

        Assert.Equal(
            "installer.machine.service_snapshot_invalid",
            invalidException.DiagnosticCode);
        Assert.Equal(
            "installer.machine.service_inspection_failed",
            failedException.DiagnosticCode);
    }

    [Fact]
    public void PreCancellationFailsBeforeScmInspection()
    {
        var native = new RecordingServiceNative(snapshot: null);
        var verifier = new WindowsServiceConfigurationVerifier(native);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            verifier.VerifyAbsent(cancellation.Token));

        Assert.Equal(0, native.InspectCalls);
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

    private static WindowsServiceSnapshot Snapshot(
        WindowsMachineDeploymentPlan plan,
        WindowsServiceRuntimeState state) =>
        new(
            plan.Service with { Dependencies = [] },
            state,
            WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(TargetSid));

    private sealed class RecordingServiceNative : IWindowsServiceConfigurationNative
    {
        private readonly WindowsServiceSnapshot? _snapshot;
        private readonly Exception? _failure;

        internal RecordingServiceNative(WindowsServiceSnapshot? snapshot)
        {
            _snapshot = snapshot;
        }

        internal RecordingServiceNative(Exception failure)
        {
            _failure = failure;
        }

        internal int InspectCalls { get; private set; }

        internal string? ServiceName { get; private set; }

        public WindowsServiceSnapshot? Inspect(string serviceName)
        {
            InspectCalls++;
            ServiceName = serviceName;
            if (_failure is not null)
            {
                throw _failure;
            }

            return _snapshot;
        }
    }
}
