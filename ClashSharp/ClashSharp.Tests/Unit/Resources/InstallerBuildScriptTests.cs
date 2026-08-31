namespace ClashSharp.Tests.Unit.Resources;

/// <summary>Tests the installer build script source-level contract.</summary>
public sealed class InstallerBuildScriptTests
{
    /// <summary>Verifies the WPF Installer is the only packaging entry and release inputs fail closed.</summary>
    [Fact]
    public void BuildInstallerScript_UsesOnlySignedPinnedWpfReleasePath()
    {
        string scriptPath = FindSourceFile("ClashSharp", "Installer", "build.ps1");
        string repositoryRoot = FindRepositoryRoot();

        string script = File.ReadAllText(scriptPath);

        Assert.False(File.Exists(Path.Combine(repositoryRoot, "Tools", "build_installer.py")));
        Assert.Contains("dotnet publish $installerProject", script, StringComparison.Ordinal);
        Assert.Contains("ClashSharp.Installer.csproj", script, StringComparison.Ordinal);
        Assert.Contains("dotnet publish $appProject", script, StringComparison.Ordinal);
        Assert.Contains("--no-restore", script, StringComparison.Ordinal);
        Assert.Contains("-p:ClashSharpFormalInstallerBuild=true", script, StringComparison.Ordinal);
        Assert.Contains("New-ClashSharpInstallerReleaseManifest", script, StringComparison.Ordinal);
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
        Assert.Contains("artifacts\\installer", script, StringComparison.Ordinal);
        Assert.Contains("$releaseDir = Join-Path $installerTargetRoot \"release\"", script, StringComparison.Ordinal);
        Assert.Contains("$stagedInstallerExecutable", script, StringComparison.Ordinal);
        Assert.Contains("Remove-GeneratedDirectory -LiteralPath $releaseDir", script, StringComparison.Ordinal);
        Assert.Contains("Installer artifact SHA-256", script, StringComparison.Ordinal);
        Assert.Contains("ClashSharp-Installer-Development-Unsigned.exe", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Update-Mihomo.ps1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-WebRequest", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-RestMethod", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CLASHSHARP_ALLOW_MISSING_GEODATA", script, StringComparison.Ordinal);
        Assert.DoesNotMatch("(?i)\\b(cargo|rustc|rustup)\\b", script);
        Assert.DoesNotContain("CSharpInstallerCandidate", script, StringComparison.Ordinal);

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

    /// <summary>Verifies the WPF Installer embeds a generated manifest and is the promoted executable.</summary>
    [Fact]
    public void BuildInstallerScript_HasFormalWpfInstallerContract()
    {
        string scriptPath = FindSourceFile("ClashSharp", "Installer", "build.ps1");
        string modulePath = FindSourceFile(
            "ClashSharp",
            "Installer",
            "PackagingContract.psm1");
        string installerProjectPath = FindSourceFile(
            "ClashSharp",
            "ClashSharp.Installer",
            "ClashSharp.Installer.csproj");

        string script = File.ReadAllText(scriptPath);
        string module = File.ReadAllText(modulePath);
        string installerProject = File.ReadAllText(installerProjectPath);

        Assert.DoesNotContain("CSharpInstallerCandidate", script, StringComparison.Ordinal);
        Assert.Contains("New-ClashSharpInstallerReleaseManifest", script, StringComparison.Ordinal);
        Assert.Contains("dotnet publish $installerProject", script, StringComparison.Ordinal);
        Assert.Contains("-p:ClashSharpFormalInstallerBuild=true", script, StringComparison.Ordinal);
        Assert.Contains("ClashSharpInstallerReleaseManifestPath", script, StringComparison.Ordinal);
        Assert.Contains(
            "-AuthenticodeCertificateThumbprint $authenticodeThumbprint",
            script,
            StringComparison.Ordinal);
        Assert.Contains("must publish as one self-contained executable", script, StringComparison.Ordinal);
        Assert.Contains(
            "$stagedInstallerExecutable = $installerEntries[0].FullName",
            script,
            StringComparison.Ordinal);

        Assert.Contains("Get-ClashSharpPublisherId", module, StringComparison.Ordinal);
        Assert.Contains("Get-ClashSharpMsixMachineFileContract", module, StringComparison.Ordinal);
        Assert.Contains("[Security.Cryptography.SHA256]::HashData", module, StringComparison.Ordinal);
        Assert.Contains("[Security.Cryptography.IncrementalHash]::CreateHash", module, StringComparison.Ordinal);
        Assert.Contains("New-ClashSharpInstallerReleaseManifest", module, StringComparison.Ordinal);
        Assert.Contains("authenticodeCertificateThumbprint", module, StringComparison.Ordinal);
        Assert.Contains("machineFiles                  = @($machineFiles)", module, StringComparison.Ordinal);
        Assert.Contains("binaries/mihomo.exe", module, StringComparison.Ordinal);
        Assert.Contains(
            "binaries/service/clashsharp.mihomoservice.exe",
            module,
            StringComparison.Ordinal);
        Assert.Contains("[Array]::Sort($sortedPaths, [StringComparer]::Ordinal)", module, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-Json -Depth 8 -Compress", module, StringComparison.Ordinal);

        Assert.Contains("<EmbeddedResource", installerProject, StringComparison.Ordinal);
        Assert.Contains("ClashSharp.Installer.ReleaseManifest.json", installerProject, StringComparison.Ordinal);
        Assert.Contains("ValidateClashSharpFormalInstallerManifest", installerProject, StringComparison.Ordinal);
    }

    /// <summary>Verifies the WPF Installer embeds the Windows executable icon.</summary>
    [Fact]
    public void WpfInstallerBuild_EmbedsWindowsExecutableIcon()
    {
        string installerProjectPath = FindSourceFile(
            "ClashSharp",
            "ClashSharp.Installer",
            "ClashSharp.Installer.csproj");
        string iconPath = FindSourceFile("ClashSharp", "Installer", "LogoInstaller.ico");

        string installerProject = File.ReadAllText(installerProjectPath);

        Assert.Contains(
            "<ApplicationIcon>..\\Installer\\LogoInstaller.ico</ApplicationIcon>",
            installerProject,
            StringComparison.Ordinal);
        Assert.EndsWith("LogoInstaller.ico", iconPath, StringComparison.Ordinal);
    }

    /// <summary>Verifies the final MSIX GeoData set and manifest content are bound into the WPF Installer.</summary>
    [Fact]
    public void WpfInstallerBuild_BindsCompleteGeoDataManifestFromFinalMsix()
    {
        string scriptPath = FindSourceFile("ClashSharp", "Installer", "build.ps1");
        string modulePath = FindSourceFile("ClashSharp", "Installer", "PackagingContract.psm1");

        string script = File.ReadAllText(scriptPath);
        string module = File.ReadAllText(modulePath);

        Assert.Contains("$manifest.files.Count -ne 4", script, StringComparison.Ordinal);
        Assert.Contains("Country.mmdb", script, StringComparison.Ordinal);
        Assert.Contains("GeoIP.dat", script, StringComparison.Ordinal);
        Assert.Contains("GeoSite.dat", script, StringComparison.Ordinal);
        Assert.Contains("ASN.mmdb", script, StringComparison.Ordinal);
        Assert.Contains("GeoData asset length mismatch", script, StringComparison.Ordinal);
        Assert.Contains("GeoData asset SHA-256 mismatch", script, StringComparison.Ordinal);
        Assert.Contains("machineFiles                  = @($machineFiles)", module, StringComparison.Ordinal);
    }

    /// <summary>Verifies the final MSIX is the sole generated package-identity source.</summary>
    [Fact]
    public void WpfInstallerBuild_GeneratesCompleteIdentityFromFinalMsix()
    {
        string scriptPath = FindSourceFile("ClashSharp", "Installer", "build.ps1");
        string modulePath = FindSourceFile("ClashSharp", "Installer", "PackagingContract.psm1");
        string manifestPath = FindSourceFile(
            "ClashSharp",
            "ClashSharp.Installer.Core",
            "Payloads",
            "InstallerReleaseManifest.cs");

        string script = File.ReadAllText(scriptPath);
        string module = File.ReadAllText(modulePath);
        string manifest = File.ReadAllText(manifestPath);

        Assert.Contains("Get-ClashSharpMsixIdentity", script, StringComparison.Ordinal);
        Assert.Contains("Get-ClashSharpPublisherId", module, StringComparison.Ordinal);
        Assert.Contains("New-ClashSharpInstallerReleaseManifest", module, StringComparison.Ordinal);
        Assert.Contains("expectedPackageVersion       = [string]$PrimaryIdentity.Version", module, StringComparison.Ordinal);
        Assert.Matches("packageIdentity\\s+= \\[ordered\\]@\\{", module);
        Assert.Contains("public InstallerPackageIdentity PackageIdentity", manifest, StringComparison.Ordinal);
        Assert.Contains("PackageIdentity.Validate(ExpectedPackageVersion)", manifest, StringComparison.Ordinal);
    }

    /// <summary>Verifies ordinary Repair cannot opt into package downgrade semantics.</summary>
    [Fact]
    public void WpfInstallerRepair_RejectsDowngradeByDefault()
    {
        string coordinatorPath = FindSourceFile(
            "ClashSharp",
            "ClashSharp.Installer.Core",
            "Execution",
            "InstallerCoordinator.cs");
        string packageAdapterPath = FindSourceFile(
            "ClashSharp",
            "ClashSharp.Installer.Windows",
            "Packages",
            "WindowsCurrentUserPackageStoreAdapter.cs");

        string coordinator = File.ReadAllText(coordinatorPath);
        string packageAdapter = File.ReadAllText(packageAdapterPath);

        Assert.Contains("environment.InstalledPackageVersion", coordinator, StringComparison.Ordinal);
        Assert.Contains("installed > requested", coordinator, StringComparison.Ordinal);
        Assert.Contains("installer.package.downgrade_rejected", coordinator, StringComparison.Ordinal);
        Assert.Contains("ForceUpdateFromAnyVersion: false", packageAdapter, StringComparison.Ordinal);
    }

    /// <summary>Verifies MSIX signing identity is derived from and bound to the manifest Publisher.</summary>
    [Fact]
    public void WpfInstallerBuild_BindsSigningCertificateToManifestPublisher()
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
    public void WpfInstallerBuild_UsesExactIsolatedPayloadAndSignatureContract()
    {
        string scriptPath = FindSourceFile("ClashSharp", "Installer", "build.ps1");
        string modulePath = FindSourceFile(
            "ClashSharp",
            "Installer",
            "PackagingContract.psm1");
        string projectPath = FindSourceFile("ClashSharp", "ClashSharp", "ClashSharp.csproj");
        string serviceProjectPath = FindSourceFile(
            "ClashSharp",
            "ClashSharp.MihomoService",
            "ClashSharp.MihomoService.csproj");
        string watchdogProjectPath = FindSourceFile(
            "ClashSharp",
            "ClashSharp.RecoveryWatchdog",
            "ClashSharp.RecoveryWatchdog.csproj");

        string script = File.ReadAllText(scriptPath);
        string module = File.ReadAllText(modulePath);
        string project = File.ReadAllText(projectPath);
        string serviceProject = File.ReadAllText(serviceProjectPath);
        string watchdogProject = File.ReadAllText(watchdogProjectPath);

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
        Assert.Contains("dotnet publish $installerProject", script, StringComparison.Ordinal);
        Assert.Contains("$stagedInstallerExecutable = $installerEntries[0].FullName", script, StringComparison.Ordinal);
        Assert.Contains("Copy-ClashSharpVerifiedDirectory", script, StringComparison.Ordinal);
        Assert.Contains("Compare-ClashSharpDirectoryContract", script, StringComparison.Ordinal);
        Assert.Contains("--self-contained true", script, StringComparison.Ordinal);
        Assert.Contains("-p:ClashSharpFormalInstallerComponent=true", script, StringComparison.Ordinal);
        Assert.Contains("-p:PublishSingleFile=true", script, StringComparison.Ordinal);
        Assert.Contains("-p:PublishTrimmed=false", script, StringComparison.Ordinal);
        Assert.Contains("-p:PublishReadyToRun=true", script, StringComparison.Ordinal);
        Assert.Contains("-p:IncludeNativeLibrariesForSelfExtract=true", script, StringComparison.Ordinal);
        Assert.Equal(
            3,
            script.Split("-p:PublishDocumentationFiles=false", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("--self-contained false", script, StringComparison.Ordinal);
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
        Assert.Contains("ClashSharp.RecoveryWatchdog.exe", project, StringComparison.Ordinal);
        Assert.DoesNotContain("ClashSharp.RecoveryWatchdog.runtimeconfig.json", project, StringComparison.Ordinal);
        Assert.Contains("Binaries\\GeoData\\ASN.mmdb", project, StringComparison.Ordinal);

        Assert.Contains("<PublishDocumentationFiles>false", serviceProject, StringComparison.Ordinal);
        Assert.Contains("<Content Update=\"packages.lock.json\"", serviceProject, StringComparison.Ordinal);
        Assert.Contains("CopyToPublishDirectory=\"Never\"", serviceProject, StringComparison.Ordinal);
        Assert.Contains("<PublishDocumentationFiles>false", watchdogProject, StringComparison.Ordinal);

        Assert.Contains("New-ClashSharpInstallerReleaseManifest", module, StringComparison.Ordinal);
        Assert.Contains("machineFiles                  = @($machineFiles)", module, StringComparison.Ordinal);
        Assert.Contains("files                         = @($files)", module, StringComparison.Ordinal);
        Assert.Contains("installerPayloadSha256", module, StringComparison.Ordinal);
    }

    /// <summary>Verifies verified payload bytes stay locked through consumers and registration gets a full-file check.</summary>
    [Fact]
    public void WpfInstaller_ClosesPayloadConsumptionRaceAndVerifiesPackageIntegrity()
    {
        string lockedFilePath = FindSourceFile(
            "ClashSharp",
            "ClashSharp.Installer.Windows",
            "Files",
            "WindowsLockedPayloadFile.cs");
        string releaseLeasePath = FindSourceFile(
            "ClashSharp",
            "ClashSharp.Installer.Windows",
            "Files",
            "WindowsInstallerReleaseLease.cs");
        string packageVerifierPath = FindSourceFile(
            "ClashSharp",
            "ClashSharp.Installer.Core",
            "Payloads",
            "InstallerMsixPackageVerifier.cs");
        string packageManifestPath = FindSourceFile(
            "ClashSharp",
            "ClashSharp",
            "Package.appxmanifest");

        string lockedFile = File.ReadAllText(lockedFilePath);
        string releaseLease = File.ReadAllText(releaseLeasePath);
        string packageVerifier = File.ReadAllText(packageVerifierPath);
        string packageManifest = File.ReadAllText(packageManifestPath);

        Assert.Contains("SafeFileHandle handle", lockedFile, StringComparison.Ordinal);
        Assert.Contains("WindowsFileSystemNative.GetOrdinaryFileIdentity", lockedFile, StringComparison.Ordinal);
        Assert.Contains("RandomAccess.GetLength", lockedFile, StringComparison.Ordinal);
        Assert.Contains("IncrementalHash.CreateHash(HashAlgorithmName.SHA256)", lockedFile, StringComparison.Ordinal);
        Assert.Contains("internal void Reverify", lockedFile, StringComparison.Ordinal);
        Assert.Contains("WindowsInstallerPayloadLocker.VerifyExactShape", releaseLease, StringComparison.Ordinal);
        Assert.Contains("file.Reverify(cancellationToken)", releaseLease, StringComparison.Ordinal);
        Assert.Contains("AppxBlockMap.xml", packageVerifier, StringComparison.Ordinal);
        Assert.Contains("Uap10Namespace + \"PackageIntegrity\"", packageVerifier, StringComparison.Ordinal);
        Assert.Contains("uap10:PackageIntegrity", packageManifest, StringComparison.Ordinal);
        Assert.Contains("uap10:Content Enforcement=\"on\"", packageManifest, StringComparison.Ordinal);
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
        string coordinatorPath = FindSourceFile(
            "ClashSharp",
            "ClashSharp.Installer.Core",
            "Execution",
            "InstallerCoordinator.cs");
        string machineAdapterPath = FindSourceFile(
            "ClashSharp",
            "ClashSharp.Installer.Windows",
            "Machines",
            "WindowsElevatedMachineAdapter.cs");

        string coordinator = File.ReadAllText(coordinatorPath);
        string machineAdapter = File.ReadAllText(machineAdapterPath);

        Assert.Contains("InstallerTransactionPhase.MachineRemovalAuthorized", coordinator, StringComparison.Ordinal);
        Assert.Contains("InstallerMachineHelperVerb.Remove", machineAdapter, StringComparison.Ordinal);
        Assert.Contains("InstallerMachineHelperInvocation.Create(verb, durableState)", machineAdapter, StringComparison.Ordinal);
        Assert.Contains("result.ValidateAgainst(command)", machineAdapter, StringComparison.Ordinal);
        Assert.Contains("installer.machine.target_user_mismatch", machineAdapter, StringComparison.Ordinal);

        int removeMachine = coordinator.IndexOf(
            ".ApplyAsync(request, release, durable, cancellationToken)",
            coordinator.IndexOf("ExecuteUninstallAsync", StringComparison.Ordinal),
            StringComparison.Ordinal);
        int removePackage = coordinator.IndexOf(
            "_packageMutation.ApplyAsync(request, release, cancellationToken)",
            removeMachine + 1,
            StringComparison.Ordinal);
        Assert.True(
            removeMachine >= 0 && removePackage > removeMachine,
            "Owner-checked helper cleanup must precede CurrentUser package removal.");
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
