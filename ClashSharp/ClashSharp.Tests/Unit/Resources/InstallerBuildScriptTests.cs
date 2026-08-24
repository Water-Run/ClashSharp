namespace ClashSharp.Tests.Unit.Resources;

/// <summary>Tests the installer build script source-level contract.</summary>
public sealed class InstallerBuildScriptTests
{
    /// <summary>Verifies the native Installer is the only packaging entry and release inputs fail closed.</summary>
    [Fact]
    public void BuildInstallerScript_UsesOnlySignedPinnedNativeReleasePath()
    {
        string scriptPath = FindSourceFile("ClashSharp", "Installer", "build.ps1");
        string repositoryRoot = FindRepositoryRoot();

        string script = File.ReadAllText(scriptPath);

        Assert.False(File.Exists(Path.Combine(repositoryRoot, "Tools", "build_installer.py")));
        Assert.Contains("cargo build --release", script, StringComparison.Ordinal);
        Assert.Contains(
            "cargo build --release --frozen --target $rustTarget --target-dir $cargoStagingRoot",
            script,
            StringComparison.Ordinal);
        Assert.Contains("$rustTarget = \"x86_64-pc-windows-msvc\"", script, StringComparison.Ordinal);
        Assert.Contains("dotnet publish $appProject", script, StringComparison.Ordinal);
        Assert.Contains("--no-restore", script, StringComparison.Ordinal);
        Assert.Contains("CLASHSHARP_INSTALLER_PACKAGING_MODE", script, StringComparison.Ordinal);
        Assert.Contains("mihomo-manifest.json", script, StringComparison.Ordinal);
        Assert.Contains("$manifest.files.Count -ne 4", script, StringComparison.Ordinal);
        Assert.Contains("CLASHSHARP_MSIX_CERTIFICATE_THUMBPRINT", script, StringComparison.Ordinal);
        Assert.Contains(
            "CLASHSHARP_AUTHENTICODE_CERTIFICATE_THUMBPRINT",
            script,
            StringComparison.Ordinal);
        Assert.Contains("CLASHSHARP_AUTHENTICODE_TIMESTAMP_URL", script, StringComparison.Ordinal);
        Assert.Contains("signtool.exe", script, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", script, StringComparison.Ordinal);
        Assert.Contains("CLASHSHARP_WINDOWS_SDK_VERSION", script, StringComparison.Ordinal);
        Assert.Contains("O=Microsoft Corporation", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Command signtool.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TimeStamperCertificate", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$signToolSignature.SignerCertificate.NotAfter",
            script,
            StringComparison.Ordinal);
        Assert.Contains("Remove-Item Env:\\CLASHSHARP_CERTIFICATE_PASSWORD", script, StringComparison.Ordinal);
        Assert.Contains("packaging-staging", script, StringComparison.Ordinal);
        Assert.Contains("release-artifacts", script, StringComparison.Ordinal);
        Assert.Contains("$stagedInstallerExecutable", script, StringComparison.Ordinal);
        Assert.Contains("Remove-GeneratedDirectory -LiteralPath $releaseDir", script, StringComparison.Ordinal);
        Assert.Contains("Installer artifact SHA-256", script, StringComparison.Ordinal);
        Assert.Contains("ClashSharp-Installer-Development-Unsigned.exe", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Update-Mihomo.ps1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-WebRequest", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-RestMethod", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CLASHSHARP_ALLOW_MISSING_GEODATA", script, StringComparison.Ordinal);

        int clearPassword = script.IndexOf(
            "Remove-Item Env:\\CLASHSHARP_CERTIFICATE_PASSWORD",
            StringComparison.Ordinal);
        int publish = script.IndexOf("dotnet publish $appProject", StringComparison.Ordinal);
        int signStaged = script.IndexOf("& $signToolPath sign", StringComparison.Ordinal);
        int publishOfficialName = script.IndexOf(
            "Move-Item -LiteralPath $stagedInstallerExecutable -Destination $installerExecutable",
            StringComparison.Ordinal);
        Assert.True(clearPassword >= 0 && clearPassword < publish);
        Assert.True(signStaged >= 0 && publishOfficialName > signStaged);
    }

    /// <summary>Verifies the native Rust installer embeds a Windows executable icon.</summary>
    [Fact]
    public void NativeInstallerBuild_EmbedsWindowsExecutableIcon()
    {
        string cargoPath = FindSourceFile("ClashSharp", "Installer", "Cargo.toml");
        string buildScriptPath = FindSourceFile("ClashSharp", "Installer", "build.rs");
        string iconPath = FindSourceFile("ClashSharp", "Installer", "LogoInstaller.ico");

        string cargo = File.ReadAllText(cargoPath);
        string buildScript = File.ReadAllText(buildScriptPath);

        Assert.Contains("winresource", cargo, StringComparison.Ordinal);
        Assert.Contains("WindowsResource::new()", buildScript, StringComparison.Ordinal);
        Assert.Contains("set_icon(\"LogoInstaller.ico\")", buildScript, StringComparison.Ordinal);
        Assert.Contains("CLASHSHARP_INSTALLER_PACKAGING_MODE", buildScript, StringComparison.Ordinal);
        Assert.Contains("release Installer builds must run through build.ps1", buildScript, StringComparison.Ordinal);
        Assert.EndsWith("LogoInstaller.ico", iconPath, StringComparison.Ordinal);
    }

    /// <summary>Verifies the final MSIX GeoData set and manifest content are bound into the Installer.</summary>
    [Fact]
    public void NativeInstallerBuild_BindsCompleteGeoDataManifestFromFinalMsix()
    {
        string cargoPath = FindSourceFile("ClashSharp", "Installer", "Cargo.toml");
        string buildScriptPath = FindSourceFile("ClashSharp", "Installer", "build.rs");

        string cargo = File.ReadAllText(cargoPath);
        string buildScript = File.ReadAllText(buildScriptPath);

        Assert.Contains("serde_json = \"1\"", cargo, StringComparison.Ordinal);
        Assert.Contains("serde_json::from_slice", buildScript, StringComparison.Ordinal);
        Assert.Contains("validate_geodata_manifest", buildScript, StringComparison.Ordinal);
        Assert.Contains("#[serde(deny_unknown_fields)]", buildScript, StringComparison.Ordinal);
        Assert.Contains("Binaries/GeoData/manifest.json", buildScript, StringComparison.Ordinal);
        Assert.Contains("Binaries/GeoData/Country.mmdb", buildScript, StringComparison.Ordinal);
        Assert.Contains("Binaries/GeoData/GeoIP.dat", buildScript, StringComparison.Ordinal);
        Assert.Contains("Binaries/GeoData/GeoSite.dat", buildScript, StringComparison.Ordinal);
        Assert.Contains("Binaries/GeoData/ASN.mmdb", buildScript, StringComparison.Ordinal);
        Assert.Contains("actual_length != asset.length", buildScript, StringComparison.Ordinal);
        Assert.Contains("actual_sha256 != &asset.sha256", buildScript, StringComparison.Ordinal);
    }

    /// <summary>Verifies the final MSIX is the sole generated package-identity source.</summary>
    [Fact]
    public void NativeInstallerBuild_GeneratesCompleteIdentityFromFinalMsix()
    {
        string buildScriptPath = FindSourceFile("ClashSharp", "Installer", "build.rs");
        string servicePlanPath = FindSourceFile(
            "ClashSharp",
            "Installer",
            "src",
            "service_plan.rs");

        string buildScript = File.ReadAllText(buildScriptPath);
        string servicePlan = File.ReadAllText(servicePlanPath);

        Assert.Contains("extract_trusted_package_manifest", buildScript, StringComparison.Ordinal);
        Assert.Contains("parse_final_appx_identity", buildScript, StringComparison.Ordinal);
        Assert.Contains("ProcessorArchitecture", buildScript, StringComparison.Ordinal);
        Assert.Contains("Application Executable", buildScript, StringComparison.Ordinal);
        Assert.Contains("TRUSTED_PACKAGE_IDENTITY_NAME", buildScript, StringComparison.Ordinal);
        Assert.Contains("TRUSTED_PACKAGE_PUBLISHER", buildScript, StringComparison.Ordinal);
        Assert.Contains("TRUSTED_PACKAGE_PUBLISHER_ID", buildScript, StringComparison.Ordinal);
        Assert.Contains("TRUSTED_PACKAGE_FAMILY_NAME", buildScript, StringComparison.Ordinal);
        Assert.Contains("TRUSTED_PACKAGE_VERSION", buildScript, StringComparison.Ordinal);
        Assert.Contains("TRUSTED_PACKAGE_ARCHITECTURE", buildScript, StringComparison.Ordinal);
        Assert.Contains("TRUSTED_APPLICATION_ID", buildScript, StringComparison.Ordinal);
        Assert.Contains("TRUSTED_APPLICATION_EXECUTABLE", buildScript, StringComparison.Ordinal);
        Assert.Contains("PackageFamilyNameFromId", buildScript, StringComparison.Ordinal);
        Assert.Contains("derive_publisher_id", buildScript, StringComparison.Ordinal);

        Assert.Contains("pub use crate::trust_anchor", servicePlan, StringComparison.Ordinal);
        Assert.DoesNotContain("pub const PACKAGE_IDENTITY_NAME: &str =", servicePlan, StringComparison.Ordinal);
        Assert.DoesNotContain("pub const PACKAGE_PUBLISHER: &str =", servicePlan, StringComparison.Ordinal);
        Assert.DoesNotContain("pub const PACKAGE_PUBLISHER_ID: &str =", servicePlan, StringComparison.Ordinal);
        Assert.DoesNotContain("pub const PACKAGE_FAMILY_NAME: &str =", servicePlan, StringComparison.Ordinal);
    }

    /// <summary>Verifies ordinary Repair cannot opt into package downgrade semantics.</summary>
    [Fact]
    public void NativeInstallerRepair_RejectsDowngradeByDefault()
    {
        string installerPath = FindSourceFile("ClashSharp", "Installer", "src", "main.rs");
        string identityPath = FindSourceFile(
            "ClashSharp",
            "Installer",
            "src",
            "package_identity.rs");

        string installer = File.ReadAllText(installerPath);
        string identity = File.ReadAllText(identityPath);

        Assert.Contains("query_current_user_package_registration", installer, StringComparison.Ordinal);
        Assert.Contains("installer.package.downgrade_rejected", installer, StringComparison.Ordinal);
        Assert.Contains("classify_deployment_version", installer, StringComparison.Ordinal);
        Assert.Contains("PackageVersion", identity, StringComparison.Ordinal);
        Assert.Contains("DeploymentVersionChange::Downgrade", identity, StringComparison.Ordinal);
        Assert.Contains(" -Update -RetainFilesOnFailure", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("ForceUpdateFromAnyVersion", installer, StringComparison.Ordinal);
    }

    /// <summary>Verifies MSIX signing identity is derived from and bound to the manifest Publisher.</summary>
    [Fact]
    public void NativeInstallerBuild_BindsSigningCertificateToManifestPublisher()
    {
        string buildScriptPath = FindSourceFile("ClashSharp", "Installer", "build.ps1");
        string manifestPath = FindSourceFile("ClashSharp", "ClashSharp", "Package.appxmanifest");

        string buildScript = File.ReadAllText(buildScriptPath);
        System.Xml.XmlDocument manifest = new();
        manifest.Load(manifestPath);
        System.Xml.XmlElement identity = Assert.IsType<System.Xml.XmlElement>(
            manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']"));
        string publisher = identity.GetAttribute("Publisher");

        Assert.False(string.IsNullOrWhiteSpace(publisher));
        Assert.Contains("$appManifest", buildScript, StringComparison.Ordinal);
        Assert.Contains(
            "$manifestPublisher = [string]$manifestIdentityNodes[0].GetAttribute(\"Publisher\")",
            buildScript,
            StringComparison.Ordinal);
        Assert.Contains("$certificateSubject = $manifestPublisher", buildScript, StringComparison.Ordinal);
        Assert.Contains(
            "CLASHSHARP_CERTIFICATE_SUBJECT does not exactly match Package.appxmanifest Identity Publisher",
            buildScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$payloadCertificate.Subject.Equals($manifestPublisher, [System.StringComparison]::Ordinal)",
            buildScript,
            StringComparison.Ordinal);
        Assert.Contains("$_.Thumbprint -ceq $payloadCertificate.Thumbprint", buildScript, StringComparison.Ordinal);
        Assert.DoesNotContain("CN=ClashSharp Development", buildScript, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{publisher}\"", buildScript, StringComparison.Ordinal);
    }

    /// <summary>Verifies formal packaging uses fresh exact content, dependency, signer, and promotion contracts.</summary>
    [Fact]
    public void NativeInstallerBuild_UsesExactIsolatedPayloadAndSignatureContract()
    {
        string scriptPath = FindSourceFile("ClashSharp", "Installer", "build.ps1");
        string modulePath = FindSourceFile(
            "ClashSharp",
            "Installer",
            "PackagingContract.psm1");
        string buildScriptPath = FindSourceFile("ClashSharp", "Installer", "build.rs");
        string projectPath = FindSourceFile("ClashSharp", "ClashSharp", "ClashSharp.csproj");

        string script = File.ReadAllText(scriptPath);
        string module = File.ReadAllText(modulePath);
        string buildScript = File.ReadAllText(buildScriptPath);
        string project = File.ReadAllText(projectPath);

        Assert.Contains("[Guid]::NewGuid().ToString('N')", script, StringComparison.Ordinal);
        Assert.Contains("Copy-ClashSharpComponentPayload", script, StringComparison.Ordinal);
        Assert.Contains("Get-ClashSharpDirectoryContract", script, StringComparison.Ordinal);
        Assert.Contains("-p:AppxPackageDir=$appPackageStagingRoot", script, StringComparison.Ordinal);
        Assert.Contains("Get-ClashSharpMsixIdentity", script, StringComparison.Ordinal);
        Assert.Contains("Get-ClashSharpMainPackageDependency", script, StringComparison.Ordinal);
        Assert.Contains("CLASHSHARP_WINDOWS_APP_RUNTIME_SIGNER_THUMBPRINT", script, StringComparison.Ordinal);
        Assert.Contains("-ExpectedThumbprint $expectedDependencyThumbprint", script, StringComparison.Ordinal);
        Assert.Contains("-RequireTrusted", script, StringComparison.Ordinal);
        Assert.Contains("-RequireTimestamp", script, StringComparison.Ordinal);
        Assert.Contains("payload-provenance.json", script, StringComparison.Ordinal);
        Assert.Contains("CLASHSHARP_INSTALLER_PAYLOAD_DIR", script, StringComparison.Ordinal);
        Assert.Contains("Copy-ClashSharpVerifiedDirectory", script, StringComparison.Ordinal);
        Assert.Contains("Compare-ClashSharpDirectoryContract", script, StringComparison.Ordinal);
        Assert.DoesNotContain("LastWriteTimeUtc", script, StringComparison.Ordinal);
        Assert.DoesNotContain("latestPackageDirectory", script, StringComparison.Ordinal);

        Assert.Contains("Assert-ClashSharpOrdinaryPath", module, StringComparison.Ordinal);
        Assert.Contains("FileAttributes]::ReparsePoint", module, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", module, StringComparison.Ordinal);
        Assert.Contains("TimeStamperCertificate", module, StringComparison.Ordinal);
        Assert.Contains("RelativePath", module, StringComparison.Ordinal);
        Assert.Contains("Sha256", module, StringComparison.Ordinal);

        Assert.Contains("ClashSharpInstallerServiceRoot", project, StringComparison.Ordinal);
        Assert.Contains("ClashSharpInstallerWatchdogRoot", project, StringComparison.Ordinal);
        Assert.Contains("Binaries\\Service", project, StringComparison.Ordinal);
        Assert.Contains("ClashSharp.RecoveryWatchdog.runtimeconfig.json", project, StringComparison.Ordinal);
        Assert.Contains("Binaries\\GeoData\\ASN.mmdb", project, StringComparison.Ordinal);

        Assert.Contains("validate_final_msix_file_contract", buildScript, StringComparison.Ordinal);
        Assert.Contains("REQUIRED_PACKAGE_EXECUTABLES", buildScript, StringComparison.Ordinal);
        Assert.Contains("parse_dependency_package_identity", buildScript, StringComparison.Ordinal);
        Assert.Contains("payload provenance", buildScript, StringComparison.Ordinal);
        Assert.Contains("ensure_payload_root_is_ordinary", buildScript, StringComparison.Ordinal);
        Assert.Contains("unexpected release payload directory", buildScript, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Web.WebView2.Core.winmd", buildScript, StringComparison.Ordinal);
    }

    /// <summary>Verifies the explicit Mihomo maintenance tool requires exact release and archive/binary hashes.</summary>
    [Fact]
    public void MihomoMaintenanceTool_RejectsLatestAndWritesPinnedManifest()
    {
        string scriptPath = FindSourceFile("Tools", "Update-Mihomo.ps1");
        string script = File.ReadAllText(scriptPath);

        Assert.Contains("ExpectedArchiveSha256", script, StringComparison.Ordinal);
        Assert.Contains("ExpectedBinarySha256", script, StringComparison.Ordinal);
        Assert.Contains("latest is not accepted", script, StringComparison.Ordinal);
        Assert.Contains("mihomo-manifest.json", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", script, StringComparison.Ordinal);
        Assert.Contains("browser_download_url -cne", script, StringComparison.Ordinal);
        Assert.DoesNotContain("releases/latest", script, StringComparison.Ordinal);
    }

    /// <summary>Verifies uninstall crosses the narrow owner-checked helper before CurrentUser cleanup.</summary>
    [Fact]
    public void InstallerUninstall_UsesOwnerCheckedMachineHelperBeforeCurrentUserCleanup()
    {
        string installerPath = FindSourceFile("ClashSharp", "Installer", "src", "main.rs");
        string servicePlanPath = FindSourceFile("ClashSharp", "Installer", "src", "service_plan.rs");

        string installer = File.ReadAllText(installerPath);
        string servicePlan = File.ReadAllText(servicePlanPath);

        Assert.Contains("uninstall_machine_resources_if_owner(&target_sid)?", installer, StringComparison.Ordinal);
        Assert.Contains("--machine-uninstall", installer, StringComparison.Ordinal);
        Assert.Contains("--target-sid", installer, StringComparison.Ordinal);
        Assert.Contains("uninstall_startup_restore_fallback(", installer, StringComparison.Ordinal);
        Assert.Contains("package_mutation_locks.installer_mutation_path()", installer, StringComparison.Ordinal);
        Assert.Contains("ClashSharp.ProxyRestoreFallback", installer, StringComparison.Ordinal);
        Assert.Contains("Remove-ItemProperty", installer, StringComparison.Ordinal);
        Assert.Contains("Remove-AppxPackage", installer, StringComparison.Ordinal);

        Assert.Contains("MachineHelperInvocation::Uninstall { target_sid }", installer, StringComparison.Ordinal);
        Assert.Contains("may_uninstall_machine(target_sid, &association)?", installer, StringComparison.Ordinal);
        Assert.Contains("return Ok(0);", installer, StringComparison.Ordinal);
        Assert.Contains("render_uninstall_script(target_sid)", installer, StringComparison.Ordinal);
        Assert.Contains("owner association does not authorize this uninstall", servicePlan, StringComparison.Ordinal);
        Assert.Contains("$scExe = Join-Path ([Environment]::SystemDirectory) 'sc.exe'", servicePlan, StringComparison.Ordinal);
        Assert.Contains("& $scExe delete $serviceName", servicePlan, StringComparison.Ordinal);
        Assert.Contains("& $scExe query $serviceName", servicePlan, StringComparison.Ordinal);
        Assert.Contains(
            "installer.machine.service_delete_pending_reboot",
            servicePlan,
            StringComparison.Ordinal);
        Assert.Contains("ClashSharpMihomo", servicePlan, StringComparison.Ordinal);

        int removeMachine = servicePlan.IndexOf(
            "OperationStep::RemoveMachineResourcesIfOwner,",
            StringComparison.Ordinal);
        int removeFallback = servicePlan.IndexOf(
            "OperationStep::RemoveCurrentUserStartupFallback,",
            removeMachine + 1,
            StringComparison.Ordinal);
        int removePackage = servicePlan.IndexOf(
            "OperationStep::RemoveCurrentUserPackageIfPresent,",
            removeFallback + 1,
            StringComparison.Ordinal);
        Assert.True(
            removeMachine >= 0 && removeFallback > removeMachine && removePackage > removeFallback,
            "Owner-checked machine cleanup must precede CurrentUser fallback and package removal.");
    }

    private static string FindSourceFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Source file was not found.", Path.Combine(segments));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
