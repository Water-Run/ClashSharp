using System.Security.Cryptography;
using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsServicePreparationBaselineTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string Credential = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void RoundTripPreservesConfigurationButDoesNotTreatAPidAsRecoveryIdentity()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        InstallerTransactionJournal intent = InstallerTransactionJournal.Create(plan.Request);
        WindowsServiceSnapshot service = new(plan.Service, WindowsServiceRuntimeState.Running,
            WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(Owner), ProcessId: 1234);
        WindowsServicePreparationBaseline original = WindowsServicePreparationBaseline.Capture(plan, intent, service);
        byte[] bytes = original.Serialize();
        try
        {
            WindowsServicePreparationBaseline read = WindowsServicePreparationBaseline.Parse(bytes);
            read.RequireBoundary(plan, intent.TransitionTo(InstallerTransactionPhase.MachineReserved));
            Assert.Equal(0u, read.Service.ProcessId);
            Assert.Equal(WindowsServiceRuntimeState.Running, read.Service.RuntimeState);
            Assert.True(WindowsServiceConfigurationVerifier.ConfigurationMatches(plan.Service, read.Service.Configuration));
            Assert.Equal(bytes, read.Serialize());
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Theory]
    [InlineData("foreign-transaction")]
    [InlineData("committed-package")]
    [InlineData("foreign-release")]
    public void ChangedTransactionOrCommittedPackageCannotAuthorizeRestoration(string scenario)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        InstallerTransactionJournal intent = InstallerTransactionJournal.Create(plan.Request);
        WindowsServicePreparationBaseline baseline = WindowsServicePreparationBaseline.Capture(plan, intent, Service(plan));
        InstallerTransactionJournal current = scenario switch
        {
            "foreign-transaction" => intent with { TransactionId = new string('1', 64) },
            "committed-package" => intent.TransitionTo(InstallerTransactionPhase.MachineReserved).TransitionTo(InstallerTransactionPhase.PackageCommitted),
            _ => intent with { InstallerPayloadSha256 = new string('2', 64) },
        };

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() => baseline.RequireBoundary(plan, current));

        Assert.Equal("installer.recovery.service_baseline_mismatch", failure.DiagnosticCode);
    }

    [Theory]
    [InlineData("fenced")]
    [InlineData("pending")]
    [InlineData("foreign-dacl")]
    [InlineData("foreign-binary")]
    [InlineData("install")]
    public void UnsafeOrAlreadyPreparedObservationCannotBecomeAnOriginalBaseline(string scenario)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        InstallerTransactionJournal intent = InstallerTransactionJournal.Create(plan.Request);
        WindowsServiceSnapshot service = Service(plan);
        if (scenario == "fenced")
        {
            service = service with
            {
                Configuration = service.Configuration with { StartMode = WindowsServiceStartMode.Disabled },
                RuntimeState = WindowsServiceRuntimeState.Stopped,
                DaclSddl = WindowsServiceConfigurationVerifier.BuildMutationFenceDaclSddl()
            };
        }
        else if (scenario == "pending") { service = service with { RuntimeState = WindowsServiceRuntimeState.StopPending }; }
        else if (scenario == "foreign-dacl") { service = service with { DaclSddl = "D:(A;;GA;;;WD)" }; }
        else if (scenario == "foreign-binary") { service = service with { Configuration = service.Configuration with { BinaryPath = "C:\\foreign.exe" } }; }
        else { intent = InstallerTransactionJournal.Create(plan.Request with { Operation = InstallerOperation.Install }); }

        Assert.Throws<InstallerProtocolException>(() => WindowsServicePreparationBaseline.Capture(plan, intent, service));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("whitespace")]
    [InlineData("missing")]
    public void NoncanonicalOrIncompletePrivateEvidenceIsRejected(string scenario)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsServicePreparationBaseline baseline = WindowsServicePreparationBaseline.Capture(plan,
            InstallerTransactionJournal.Create(plan.Request), Service(plan));
        byte[] original = baseline.Serialize();
        string json = Encoding.UTF8.GetString(original);
        string altered = scenario switch
        {
            "unknown" => json.Insert(1, "\"extra\":true,"),
            "duplicate" => json.Insert(1, "\"schema\":1,"),
            "whitespace" => " " + json,
            _ => json.Replace("\"schema\":1,", string.Empty, StringComparison.Ordinal),
        };
        byte[] bytes = Encoding.UTF8.GetBytes(altered);
        try { Assert.Throws<InstallerProtocolException>(() => WindowsServicePreparationBaseline.Parse(bytes)); }
        finally { CryptographicOperations.ZeroMemory(original); CryptographicOperations.ZeroMemory(bytes); }
    }

    private static WindowsPayloadFixture Fixture() => new(createPayload: false, removeCurrentUserCertificateOnDispose: false);
    private static WindowsMachineDeploymentPlan Plan(WindowsPayloadFixture fixture) => WindowsMachineDeploymentPlan.Create(
        fixture.Request(InstallerOperation.Repair, Owner), fixture.Manifest, InstallerMachineAssociation.Create(Owner, Credential),
        @"C:\Program Files", @"C:\ProgramData", @"C:\Users\owner");
    private static WindowsServiceSnapshot Service(WindowsMachineDeploymentPlan plan) => new(plan.Service,
        WindowsServiceRuntimeState.Running, WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(Owner));
}
