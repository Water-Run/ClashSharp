#Requires -Version 7.4
<#
.SYNOPSIS
    Independently compares a target user's existing installation with one pinned candidate.
.DESCRIPTION
    Observes package registration, all candidate package content, exact machine file inventory,
    private SCM association and active service process. Never installs, repairs, elevates, runs
    the product or changes trust, user data or network settings. A passing snapshot does not
    certify installation actions, recovery, UI, dependency servicing or public promotion.
.PARAMETER IdentityPath
    Canonical candidate identity from the controlled producer.
.PARAMETER ExpectedIdentitySha256
    Independent identity digest obtained from that producer.
.PARAMETER ReleaseDirectory
    Exact eight-file signed candidate output.
.PARAMETER TargetUserSid
    Independent expected installed owner SID; the association cannot select its own owner.
.PARAMETER OutputDirectory
    New external evidence directory; previous observations and failures are never overwritten.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $IdentityPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}\z')][string] $ExpectedIdentitySha256,
    [Parameter(Mandatory)][string] $ReleaseDirectory,
    [Parameter(Mandatory)][ValidatePattern('^S-1-[0-9-]{3,178}\z')][string] $TargetUserSid,
    [Parameter(Mandatory)][string] $OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -cne 'X64') {
    throw 'installation.windows-x64-required'
}
$installerRoot = Join-Path $PSScriptRoot '../ClashSharp/Installer'
Import-Module (Join-Path $installerRoot 'CandidateAcceptance.psm1') -Force
Import-Module (Join-Path $installerRoot 'CandidateInstallation.psm1') -Force
Import-Module (Join-Path $installerRoot 'CandidateIdentity.psm1') -Force
Import-Module (Join-Path $installerRoot 'PackagingContract.psm1') -Force
Assert-ClashSharpInstallationTargetSid -TargetUserSid $TargetUserSid
$release = Assert-ClashSharpOrdinaryPath -LiteralPath $ReleaseDirectory -RequireDirectory
$output = [IO.Path]::GetFullPath($OutputDirectory)
$null = Assert-ClashSharpOrdinaryPath -LiteralPath (Split-Path -Parent $output) -RequireDirectory
if ((Test-Path -LiteralPath $output) -or $output.Equals($release,[StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($release.TrimEnd([char[]]@('\','/'))+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'installation.new-external-output-required'
}
$null = [IO.Directory]::CreateDirectory($output)
$started = [DateTimeOffset]::UtcNow.ToString('o')
$runId = [Guid]::NewGuid().ToString('D')
$observation = [pscustomobject]@{stage='candidate'}
try {
    $document = Invoke-ClashSharpCandidateIdentity -IdentityPath $IdentityPath -ExpectedIdentitySha256 $ExpectedIdentitySha256 -ReleaseDirectory $release -Consume {
        param($identity)
        $observation.stage = 'registration'
        $msix = Join-Path $release 'payload/ClashSharp_1.0.0.0_x64.msix'
        $expected = Get-ClashSharpMsixIdentity -LiteralPath $msix
        $packages = @(Get-AppxPackage -User $TargetUserSid -Name $expected.Name -ErrorAction Stop)
        if ($packages.Count -ne 1) { throw 'installation.package-registration-count' }
        $package = $packages[0]
        $actual = [ordered]@{}
        foreach ($name in @('Name','Publisher','PublisherId','Version','ResourceId','PackageFullName','PackageFamilyName')) {
            $actual[$name] = [string]$package.$name
        }
        $actual.Architecture = if ([int]$package.Architecture -eq 9) { 'x64' } else { 'unsupported' }
        foreach ($name in @('IsBundle','IsDevelopmentMode','IsFramework','IsResourcePackage','IsPartiallyStaged')) {
            $actual[$name] = $package.$name
        }
        $actual.Status = [int]$package.Status
        Assert-ClashSharpInstallationRegistration -Expected $expected -Actual $actual
        $profiles = @(Get-CimInstance Win32_UserProfile -Filter ("SID='"+$TargetUserSid+"'") -Property SID,LocalPath)
        if ($profiles.Count -ne 1 -or $profiles[0].SID -cne $TargetUserSid) { throw 'installation.profile-count' }
        $profile = Assert-ClashSharpOrdinaryPath -LiteralPath $profiles[0].LocalPath -RequireDirectory
        $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
        $programData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
        $machine = Join-Path $programFiles 'ClashSharp/Service/current'
        $associationPath = Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $programData 'ClashSharp/MihomoService/association.json') -RequireFile
        $associationLease = [IO.File]::Open($associationPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try {
            if ($associationLease.Length -le 0 -or $associationLease.Length -gt 4096) { throw 'installation.association-budget' }
            $reader = [IO.StreamReader]::new($associationLease,[Text.UTF8Encoding]::new($false,$true),$false,1024,$true)
            try { $associationText = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $observation.stage = 'content'
            $files = @(Get-ClashSharpInstallationContentContract -LiteralPath $msix)
            Invoke-ClashSharpInstalledContentSnapshot -PackageRoot $package.InstallLocation -MachineRoot $machine -ExpectedFiles $files -Consume {
                $observation.stage = 'service'
                $services = @(Get-CimInstance Win32_Service -Filter "Name='ClashSharpMihomo'")
                if ($services.Count -ne 1) { throw 'installation.service-count' }
                $service = $services[0]
                if ($service.StartName -cne 'LocalSystem' -or $service.StartMode -cne 'Auto' -or
                    $service.State -cne 'Running' -or $service.ProcessId -le 0) { throw 'installation.service-state' }
                Assert-ClashSharpInstallationServiceCommand -CommandLine $service.PathName -AssociationJson $associationText -TargetUserSid $TargetUserSid -ProfileRoot $profile -MachineRoot $machine -PackageFamilyName $expected.PackageFamilyName
                $processes = @(Get-CimInstance Win32_Process -Filter ('ProcessId='+$service.ProcessId))
                if ($processes.Count -ne 1 -or -not [IO.Path]::GetFullPath([string]$processes[0].ExecutablePath).Equals(
                    [IO.Path]::GetFullPath((Join-Path $machine 'Host/ClashSharp.MihomoService.exe')),[StringComparison]::OrdinalIgnoreCase)) {
                    throw 'installation.service-process'
                }
                Assert-ClashSharpInstallationServiceCommand -CommandLine $processes[0].CommandLine -AssociationJson $associationText -TargetUserSid $TargetUserSid -ProfileRoot $profile -MachineRoot $machine -PackageFamilyName $expected.PackageFamilyName
                $processOwner = Invoke-CimMethod -InputObject $processes[0] -MethodName GetOwnerSid
                if ($processOwner.ReturnValue -ne 0 -or $processOwner.Sid -cne 'S-1-5-18') { throw 'installation.service-process-owner' }
                $os = @(Get-CimInstance Win32_OperatingSystem -Property Version,BuildNumber,ProductType)
                if ($os.Count -ne 1) { throw 'installation.platform-count' }
                $platform = Get-ClashSharpWindowsAcceptancePlatform -Observation ([pscustomobject]@{
                    version=[string]$os[0].Version;build=[int]$os[0].BuildNumber;productType=[int]$os[0].ProductType;architecture='X64'
                })
                # Re-observe registration and SCM after hashing. Do not let a moving installation pass.
                $afterPackages = @(Get-AppxPackage -User $TargetUserSid -Name $expected.Name -ErrorAction Stop)
                $afterService = @(Get-CimInstance Win32_Service -Filter "Name='ClashSharpMihomo'")
                $afterProcess = @(Get-CimInstance Win32_Process -Filter ('ProcessId='+$service.ProcessId))
                if ($afterPackages.Count -ne 1 -or $afterService.Count -ne 1 -or
                    $afterProcess.Count -ne 1 -or $afterProcess[0].CreationDate -ne $processes[0].CreationDate -or
                    $afterProcess[0].ExecutablePath -cne $processes[0].ExecutablePath -or
                    [string]$afterPackages[0].InstallLocation -cne [string]$package.InstallLocation -or
                    [string]$afterPackages[0].PackageFullName -cne $expected.PackageFullName -or [int]$afterPackages[0].Status -ne 0 -or
                    $afterService[0].ProcessId -ne $service.ProcessId -or $afterService[0].PathName -cne $service.PathName -or
                    $afterService[0].State -cne 'Running' -or $afterService[0].StartMode -cne 'Auto' -or $afterService[0].StartName -cne 'LocalSystem') {
                    throw 'installation.observation-changed'
                }
                [ordered]@{
                    schemaVersion=1;kind='clashsharp-installed-candidate-content';repository='Water-Run/ClashSharp'
                    scope='existing-installation-read-only';status='passed';runId=$runId;startedAt=$started;finishedAt=[DateTimeOffset]::UtcNow.ToString('o')
                    candidateIdentitySha256=$ExpectedIdentitySha256;sourceCommit=$identity.source.commit;platform=$platform
                    targetUserSidSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($TargetUserSid))).ToLowerInvariant()
                    package=[ordered]@{fullName=$expected.PackageFullName;familyName=$expected.PackageFamilyName;version=$expected.Version;contentFiles=@($files|Where-Object scope -CEQ 'package').Count}
                    machine=[ordered]@{files=@($files|Where-Object scope -CEQ 'machine').Count;serviceName='ClashSharpMihomo';processId=[int]$service.ProcessId;processCreatedAt=$processes[0].CreationDate.ToUniversalTime().ToString('o');account='LocalSystem';state='Running';startMode='Auto';privateCommandMatched=$true}
                    installedContentMatched=$true;machineInventoryMatched=$true;registrationMatched=$true
                    signatureTrustRechecked=$false;dependencyServicingVerified=$false;installationActionVerified=$false
                    recoveryVerified=$false;win11AcceptanceSatisfied=$false;promotionAuthorized=$false
                }
            }
        } finally { $associationLease.Dispose() }
    }
    $json = ($document | ConvertTo-Json -Depth 10 -Compress)+[char]10
    [IO.File]::WriteAllText((Join-Path $output 'installation-content.json'),$json,[Text.UTF8Encoding]::new($false))
    [pscustomobject]@{status='passed';candidateIdentitySha256=$ExpectedIdentitySha256;receiptSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json))).ToLowerInvariant();installedContentMatched=$true;installationActionVerified=$false;promotionAuthorized=$false}
} catch {
    # Never serialize ErrorRecord, invocation text, local paths, SCM command or association input.
    $code = if ($_.Exception.Message -cmatch '^(installation\.[a-z-]+)(:|\z)') { $Matches[1] } else { 'installation.observation-refused' }
    [ordered]@{status='failed';scope='existing-installation-read-only';runId=$runId;stage=$observation.stage;code=$code;candidateIdentitySha256=$ExpectedIdentitySha256;promotionAuthorized=$false} |
        ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $output 'failure.json') -Encoding utf8NoBOM
    throw 'installation.audit-failed; inspect the retained stage and private host locally'
}
