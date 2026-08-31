using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerRuntimeInspectionTests
{
    [Fact]
    public void CanonicalSupportedAndUnsupportedSnapshotsValidate()
    {
        Inspection(
            isSupported: true,
            installedVersion: null,
            isApplicationRunning: false,
            blockingDiagnosticCode: null).Validate();
        Inspection(
            isSupported: false,
            installedVersion: Version,
            isApplicationRunning: false,
            blockingDiagnosticCode:
                "installer.environment.windows_11_required").Validate();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunningApplicationRequiresAnInstalledPackageOnEveryPlatform(
        bool isSupported)
    {
        InstallerRuntimeInspection inspection = Inspection(
            isSupported,
            installedVersion: null,
            isApplicationRunning: true,
            blockingDiagnosticCode: isSupported
                ? null
                : "installer.environment.windows_11_required");

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(
            inspection.Validate);

        Assert.Equal(
            "installer.runtime.inspection_result_invalid",
            exception.DiagnosticCode);
    }

    [Fact]
    public void PlatformSupportAndBlockingDiagnosticMustBeMutuallyExclusive()
    {
        InstallerRuntimeInspection supportedWithBlock = Inspection(
            isSupported: true,
            installedVersion: null,
            isApplicationRunning: false,
            blockingDiagnosticCode:
                "installer.environment.windows_11_required");
        InstallerRuntimeInspection unsupportedWithoutBlock = Inspection(
            isSupported: false,
            installedVersion: null,
            isApplicationRunning: false,
            blockingDiagnosticCode: null);

        Assert.Equal(
            "installer.runtime.inspection_result_invalid",
            Assert.Throws<InstallerProtocolException>(
                supportedWithBlock.Validate).DiagnosticCode);
        Assert.Equal(
            "installer.runtime.inspection_result_invalid",
            Assert.Throws<InstallerProtocolException>(
                unsupportedWithoutBlock.Validate).DiagnosticCode);
    }

    [Fact]
    public void RawBlockingDiagnosticIsRejected()
    {
        InstallerRuntimeInspection inspection = Inspection(
            isSupported: false,
            installedVersion: null,
            isApplicationRunning: false,
            blockingDiagnosticCode: "Windows is unsupported");

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(
            inspection.Validate);

        Assert.Equal(
            "installer.runtime.inspection_result_invalid",
            exception.DiagnosticCode);
    }

    [Fact]
    public void DurableTransactionMustBelongToTheInspectedRelease()
    {
        InstallerTransactionSnapshot durable = InstallerTransactionSnapshot.Create(
            InstallerTransactionJournal.Create(new InstallerRequest(
                InstallerOperation.Repair,
                TargetSid,
                AllowReassociation: false,
                "9.9.9.9",
                PayloadHash)));
        var inspection = new InstallerRuntimeInspection(
            new InstallerEnvironmentSnapshot(
                IsSupported: true,
                InstalledPackageVersion: Version,
                IsApplicationRunning: false,
                BlockingDiagnosticCode: null),
            durable,
            Version);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(
            inspection.Validate);

        Assert.Equal(
            "installer.runtime.inspection_result_invalid",
            exception.DiagnosticCode);
    }

    private const string Version = "1.2.3.4";
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string PayloadHash =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    private static InstallerRuntimeInspection Inspection(
        bool isSupported,
        string? installedVersion,
        bool isApplicationRunning,
        string? blockingDiagnosticCode) =>
        new(
            new InstallerEnvironmentSnapshot(
                isSupported,
                installedVersion,
                isApplicationRunning,
                blockingDiagnosticCode),
            DurableTransaction: null,
            Version);
}
