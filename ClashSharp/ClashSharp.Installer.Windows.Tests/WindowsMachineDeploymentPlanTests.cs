using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMachineDeploymentPlanTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string Token =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string ProgramFilesRoot = @"C:\Program Files";
    private const string ProgramDataRoot = @"C:\ProgramData";
    private const string ProfileRoot = @"C:\Users\owner";

    [Fact]
    public void PlanDerivesOnlyFixedMachineProfileAndServicePaths()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);

        Assert.Equal(@"C:\Program Files\ClashSharp\Service", plan.MachineRoot);
        Assert.Equal(@"C:\Program Files\ClashSharp\Service\current", plan.CurrentRoot);
        Assert.Equal(@"C:\Program Files\ClashSharp\Service\staging", plan.StagingRoot);
        Assert.Equal(@"C:\Program Files\ClashSharp\Service\previous", plan.PreviousRoot);
        Assert.Equal(
            @"C:\Program Files\ClashSharp\Service\current\Host\ClashSharp.MihomoService.exe",
            plan.ServiceHostPath);
        Assert.Equal(
            @"C:\Program Files\ClashSharp\Service\current\mihomo.exe",
            plan.MihomoPath);
        Assert.Equal(
            @"C:\ProgramData\ClashSharp\MihomoService\association.json",
            plan.AssociationPath);
        Assert.Equal(
            $@"C:\Users\owner\AppData\Local\Packages\{fixture.Manifest.PackageIdentity.PackageFamilyName}\LocalState\mihomo\config.yaml",
            plan.ConfigPath);
        Assert.DoesNotContain("WindowsApps", plan.Service.BinaryPath, StringComparison.OrdinalIgnoreCase);
        plan.Validate();
    }

    [Fact]
    public void FixedMachineRootsCanBeDerivedWithoutTargetProfile()
    {
        WindowsMachineDeploymentRoots roots = WindowsMachineDeploymentRoots.Create(
            ProgramFilesRoot,
            ProgramDataRoot);

        Assert.Equal(ProgramFilesRoot, roots.ProgramFilesRoot);
        Assert.Equal(ProgramDataRoot, roots.CommonApplicationDataRoot);
        Assert.Equal(@"C:\Program Files\ClashSharp\Service", roots.MachineRoot);
        Assert.Equal(
            @"C:\ProgramData\ClashSharp\MihomoService",
            roots.ServiceDataRoot);
        roots.Validate();
    }

    [Fact]
    public void ProfileIndependentRemovalPlanKeepsFixedRootsWithoutARealProfile()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        InstallerMachineAssociation association = InstallerMachineAssociation.Create(
            TargetSid,
            Token);
        var backend = new WindowsMachineHelperMachineBackend(
            ProgramFilesRoot,
            ProgramDataRoot);

        WindowsMachineDeploymentPlan plan =
            backend.CreateProfileIndependentRemovalPlan(
                request,
                fixture.Manifest,
                association);

        Assert.Equal(@"C:\Program Files\ClashSharp\Service", plan.MachineRoot);
        Assert.Equal(
            @"C:\ProgramData\ClashSharp\MihomoService",
            plan.ServiceDataRoot);
        Assert.Equal(
            @"C:\ClashSharp.UnavailableTargetProfile",
            plan.TargetProfileRoot);
        Assert.NotEqual(ProfileRoot, plan.TargetProfileRoot);
        Assert.False(plan.TargetProfileRoot.StartsWith(
            plan.MachineRoot,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(plan.TargetProfileRoot.StartsWith(
            plan.ServiceDataRoot,
            StringComparison.OrdinalIgnoreCase));
        plan.Validate();
    }

    [Fact]
    public void FixedMachineRootsRejectDerivedPathTampering()
    {
        WindowsMachineDeploymentRoots roots = WindowsMachineDeploymentRoots.Create(
            ProgramFilesRoot,
            ProgramDataRoot);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            (roots with { MachineRoot = @"C:\Program Files\ClashSharp\Other" }).Validate());

        Assert.Equal(
            "installer.machine.deployment_roots_invalid",
            exception.DiagnosticCode);
    }

    [Fact]
    public void EverySignedMachineEntryHasOneCanonicalDestination()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);

        Assert.Equal(fixture.Manifest.MachineFiles.Count, plan.PayloadTargets.Count);
        Assert.Equal(
            @"mihomo.exe",
            Target(plan, "binaries/mihomo.exe").RelativeTargetPath);
        Assert.Equal(
            @"Host\ClashSharp.MihomoService.exe",
            Target(
                plan,
                "binaries/service/clashsharp.mihomoservice.exe").RelativeTargetPath,
            ignoreCase: true);
        Assert.Equal(
            @"GeoData\ASN.mmdb",
            Target(plan, "binaries/geodata/asn.mmdb").RelativeTargetPath,
            ignoreCase: true);
        Assert.All(plan.PayloadTargets, target =>
            Assert.StartsWith(
                plan.CurrentRoot + Path.DirectorySeparatorChar,
                target.DestinationPath,
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            plan.PayloadTargets.Count,
            plan.PayloadTargets.Select(static target => target.DestinationPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ScmTupleIsExactOwnProcessDelayedAutoLocalSystem()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        string pipeName = plan.Association.BuildServicePipeName();
        string expected =
            $"\"{plan.ServiceHostPath}\" --mihomo \"{plan.MihomoPath}\" "
            + $"--config \"{plan.ConfigPath}\" --pipe-name \"{pipeName}\" "
            + $"--ipc-token \"{Token}\" --allowed-sid \"{TargetSid}\"";

        Assert.Equal(WindowsMachineDeploymentPlan.ServiceName, plan.Service.ServiceName);
        Assert.Equal(
            WindowsMachineDeploymentPlan.ServiceDisplayName,
            plan.Service.DisplayName);
        Assert.Equal(
            WindowsMachineDeploymentPlan.ServiceDescription,
            plan.Service.Description);
        Assert.Equal(WindowsServiceProcessType.OwnProcess, plan.Service.ProcessType);
        Assert.Equal(WindowsServiceStartMode.Automatic, plan.Service.StartMode);
        Assert.Equal(WindowsServiceErrorMode.Normal, plan.Service.ErrorMode);
        Assert.True(plan.Service.DelayedAutoStart);
        Assert.Equal("LocalSystem", plan.Service.AccountName);
        Assert.Empty(plan.Service.Dependencies);
        Assert.Equal(expected, plan.Service.BinaryPath);
        Assert.Equal(5, plan.Service.BinaryPath.Split(" --", StringSplitOptions.None).Length - 1);
        plan.Service.ValidateExpected();
    }

    [Fact]
    public void RequestReleaseAndAssociationMustBeTheSameIdentity()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        InstallerMachineAssociation association = InstallerMachineAssociation.Create(
            TargetSid,
            Token);

        InstallerProtocolException owner = Assert.Throws<InstallerProtocolException>(() =>
            WindowsMachineDeploymentPlan.Create(
                request,
                fixture.Manifest,
                association with { OwnerSid = "S-1-5-21-100-200-300-1002" },
                ProgramFilesRoot,
                ProgramDataRoot,
                ProfileRoot));
        InstallerProtocolException release = Assert.Throws<InstallerProtocolException>(() =>
            WindowsMachineDeploymentPlan.Create(
                request with { ExpectedPackageVersion = "1.2.3.5" },
                fixture.Manifest,
                association,
                ProgramFilesRoot,
                ProgramDataRoot,
                ProfileRoot));

        Assert.Equal("installer.machine.association_owner_mismatch", owner.DiagnosticCode);
        Assert.Equal("installer.release.identity_mismatch", release.DiagnosticCode);
    }

    [Fact]
    public void UninstallCannotConstructAnApplyPlan()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            WindowsMachineDeploymentPlan.Create(
                request,
                fixture.Manifest,
                InstallerMachineAssociation.Create(TargetSid, Token),
                ProgramFilesRoot,
                ProgramDataRoot,
                ProfileRoot));

        Assert.Equal(
            "installer.machine.deployment_plan_operation_invalid",
            exception.DiagnosticCode);

        WindowsMachineDeploymentPlan removal =
            WindowsMachineDeploymentPlan.CreateForRemoval(
                request,
                fixture.Manifest,
                InstallerMachineAssociation.Create(TargetSid, Token),
                ProgramFilesRoot,
                ProgramDataRoot,
                ProfileRoot);
        Assert.Equal(InstallerOperation.Uninstall, removal.Request.Operation);
        removal.Validate();
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\Program Files")]
    [InlineData(@"C:\Program Files\..\Windows")]
    [InlineData("C:/Program Files")]
    [InlineData("C:\\Program\"Files")]
    [InlineData("C:\\ProgramData:stream")]
    public void UntrustedProgramFilesRootsAreRejected(string root)
    {
        using var fixture = Fixture();

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            WindowsMachineDeploymentPlan.Create(
                fixture.Request(targetSid: TargetSid),
                fixture.Manifest,
                InstallerMachineAssociation.Create(TargetSid, Token),
                root,
                ProgramDataRoot,
                ProfileRoot));

        Assert.Equal("installer.machine.program_files_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public void WellKnownAndProfileRootsMustRemainDistinct()
    {
        using var fixture = Fixture();

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            WindowsMachineDeploymentPlan.Create(
                fixture.Request(targetSid: TargetSid),
                fixture.Manifest,
                InstallerMachineAssociation.Create(TargetSid, Token),
                ProgramFilesRoot,
                ProgramFilesRoot,
                ProfileRoot));

        Assert.Equal("installer.machine.root_identity_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public void ServiceConfigurationRejectsAnyTupleDrift()
    {
        using var fixture = Fixture();
        WindowsServiceConfiguration expected = Plan(fixture).Service;
        WindowsServiceConfiguration[] drifted =
        [
            expected with { ServiceName = "Other" },
            expected with { DisplayName = "Other" },
            expected with { Description = "Other" },
            expected with { ProcessType = (WindowsServiceProcessType)99 },
            expected with { StartMode = (WindowsServiceStartMode)99 },
            expected with { ErrorMode = (WindowsServiceErrorMode)99 },
            expected with { DelayedAutoStart = false },
            expected with { AccountName = "NT AUTHORITY\\LocalService" },
            expected with { BinaryPath = string.Empty },
            expected with { Dependencies = ["Tcpip"] },
        ];

        foreach (WindowsServiceConfiguration configuration in drifted)
        {
            InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(
                configuration.ValidateExpected);
            Assert.Equal(
                "installer.machine.service_configuration_invalid",
                exception.DiagnosticCode);
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
            ProgramFilesRoot,
            ProgramDataRoot,
            ProfileRoot);

    private static WindowsMachinePayloadTarget Target(
        WindowsMachineDeploymentPlan plan,
        string sourcePath) =>
        plan.PayloadTargets.Single(target =>
            string.Equals(target.Source.Path, sourcePath, StringComparison.Ordinal));
}
