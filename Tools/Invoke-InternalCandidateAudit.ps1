#Requires -Version 7.4
<#
.SYNOPSIS
    Receives one independently pinned internal archive and executes its fixed read-only audit.
.DESCRIPTION
    Holds the archive against replacement, checks its producer references and actual platform,
    and retains the owned payload process evidence. No certificate import, installation, build
    or signing occurs. This narrow result cannot satisfy Win11 UI or public-release gates.
.PARAMETER ArchivePath
    Trusted producer's ordinary internal candidate ZIP.
.PARAMETER ExpectedArchiveSha256
    Independent digest returned by the producer job, not selected from downloaded JSON.
.PARAMETER ExpectedIdentitySha256
    Independent candidate identity returned by that job.
.PARAMETER SourceCommit
    Exact source expected from the producer's verified CI.
.PARAMETER AuthenticodeThumbprint
    Exact temporary Installer signer returned by that producer.
.PARAMETER MsixThumbprint
    Exact temporary primary package signer returned by that producer.
.PARAMETER ExpectedPlatform
    Platform family required by this workflow case.
.PARAMETER OutputDirectory
    New absolute evidence directory under an existing ordinary parent.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}\z')][string]$ExpectedArchiveSha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}\z')][string]$ExpectedIdentitySha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}\z')][string]$SourceCommit,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-F]{40}\z')][string]$AuthenticodeThumbprint,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-F]{40}\z')][string]$MsixThumbprint,
    [Parameter(Mandatory)][ValidateSet('windows-server','windows11')][string]$ExpectedPlatform,
    [Parameter(Mandatory)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module "$repository/ClashSharp/Installer/CandidateAcceptance.psm1" -Force
Import-Module "$repository/ClashSharp/Installer/PackagingContract.psm1" -Force
$git=(Get-Command git -CommandType Application|Select-Object -First 1).Source
$toolCommit=[string](& $git -C $repository rev-parse HEAD)
if($LASTEXITCODE -or $toolCommit -cnotmatch '^[0-9a-f]{40}\z'){throw 'internal-audit.controller-identity'}
$tracked=@(& $git -C $repository ls-files --error-unmatch -- 'Tools/Invoke-InternalCandidateAudit.ps1')
if($LASTEXITCODE -or $tracked.Count -ne 1){throw 'internal-audit.tracked-controller-required'}
$dirty=@(& $git -C $repository status --porcelain --untracked-files=no)
if($LASTEXITCODE -or $dirty.Count){throw 'internal-audit.clean-controller-required'}
$os=Get-CimInstance -ClassName Win32_OperatingSystem -Property Version,BuildNumber,ProductType
$platform=Get-ClashSharpWindowsAcceptancePlatform -Observation ([pscustomobject]@{version=[string]$os.Version;build=[int]$os.BuildNumber;productType=[int]$os.ProductType;architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()})
if($platform.family -cne $ExpectedPlatform -or $platform.architecture -cne 'X64'){throw 'internal-audit.platform-mismatch'}
if(-not [IO.Path]::IsPathFullyQualified($OutputDirectory)){throw 'internal-audit.absolute-output-required'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
$null=Assert-ClashSharpOrdinaryPath -LiteralPath (Split-Path -Parent $output) -RequireDirectory
if(Test-Path -LiteralPath $output){throw 'internal-audit.observe-existing-operation'}
$archive=Assert-ClashSharpOrdinaryPath -LiteralPath $ArchivePath -RequireFile
$lease=[IO.File]::Open($archive,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{
    if($lease.Length -le 0 -or $lease.Length -gt 4294967296){throw 'internal-audit.archive-budget'}
    if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($lease)).ToLowerInvariant() -cne $ExpectedArchiveSha256){throw 'internal-audit.archive-reference'}
    $null=[IO.Directory]::CreateDirectory($output)
    $received=& "$PSScriptRoot/Expand-CandidateArchive.ps1" -ArchivePath $archive -ExpectedArchiveSha256 $ExpectedArchiveSha256 -ExpectedIdentitySha256 $ExpectedIdentitySha256 -Destination "$output/candidate"
    $received|ConvertTo-Json -Depth 8|Set-Content "$output/archive-received.json" -Encoding utf8NoBOM
    if($received.sourceCommit -cne $SourceCommit){throw 'internal-audit.source-mismatch'}
    # The archive child reloads its dependencies in its own module scope.
    # Restore this controller's imports only after the child has returned.
    Import-Module "$repository/ClashSharp/Installer/CandidateIdentity.psm1" -Force
    Import-Module "$repository/ClashSharp/Installer/PackagingContract.psm1" -Force
    $signature=Invoke-ClashSharpCandidateIdentity -IdentityPath "$output/candidate/candidate-identity.json" -ExpectedIdentitySha256 $ExpectedIdentitySha256 -ReleaseDirectory "$output/candidate/release" -Consume {
        param($identity)
        if($identity.signatures.installer.thumbprint -cne $AuthenticodeThumbprint -or $identity.signatures.primary.thumbprint -cne $MsixThumbprint -or
            $identity.signatures.installer.subject -cnotmatch '^CN=ClashSharp Internal Candidate [0-9a-f]{32}\z'){throw 'internal-audit.internal-signer-mismatch'}
        $actual=Get-AuthenticodeSignature -LiteralPath "$output/candidate/release/Setup.exe"
        if([string]$actual.Status -cnotin @('Valid','NotTrusted','UnknownError') -or $null -eq $actual.SignerCertificate -or
            $actual.SignerCertificate.Thumbprint -cne $AuthenticodeThumbprint -or $null -eq $actual.TimeStamperCertificate){throw 'internal-audit.installer-signature-metadata'}
        [pscustomobject]@{installerSignatureMetadataMatched=$true;observedWindowsStatus=[string]$actual.Status;signatureTrustRechecked=$false}
    }
    $audit=& "$PSScriptRoot/Invoke-CandidatePayloadAudit.ps1" -IdentityPath "$output/candidate/candidate-identity.json" -ExpectedIdentitySha256 $ExpectedIdentitySha256 -ReleaseDirectory "$output/candidate/release" -OutputDirectory "$output/payload"
    $headAgain=[string](& $git -C $repository rev-parse HEAD)
    if($LASTEXITCODE -or $headAgain -cne $toolCommit){throw 'internal-audit.controller-changed'}
    $dirtyAgain=@(& $git -C $repository status --porcelain --untracked-files=no)
    if($LASTEXITCODE -or $dirtyAgain.Count){throw 'internal-audit.clean-controller-required'}
    $document=[ordered]@{
        schemaVersion=1;kind='clashsharp-internal-candidate-audit';scope='installer-read-only-payload';status='passed'
        toolCommit=$toolCommit;sourceCommit=$SourceCommit;candidateIdentitySha256=$ExpectedIdentitySha256;archiveSha256=$ExpectedArchiveSha256
        authenticodeThumbprint=$AuthenticodeThumbprint;msixThumbprint=$MsixThumbprint;platform=$platform
        installerSignatureMetadataMatched=$signature.installerSignatureMetadataMatched;observedWindowsSignatureStatus=$signature.observedWindowsStatus
        signatureTrustRechecked=$false;payloadReceiptSha256=$audit.receiptSha256
        installationVerified=$false;win11AcceptanceSatisfied=$false;publicReleaseAuthorized=$false;promotionAuthorized=$false
    }
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($document|ConvertTo-Json -Depth 10 -Compress)+[char]10)
    $writer=[IO.File]::Open("$output/internal-audit-result.json",[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$writer.Write($bytes);$writer.Flush($true)}finally{$writer.Dispose()}
    [pscustomobject]@{status='passed';scope=$document.scope;platform=$platform.family;candidateIdentitySha256=$ExpectedIdentitySha256;archiveSha256=$ExpectedArchiveSha256;receiptSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant();installationVerified=$false;win11AcceptanceSatisfied=$false;promotionAuthorized=$false}
}finally{$lease.Dispose()}
