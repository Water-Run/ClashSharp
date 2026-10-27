#Requires -Version 7.4
<#
.SYNOPSIS
    Seals one clean checkout, verified CI input and trusted signed candidate file set.
.DESCRIPTION
    Runs inside a controlled signing context while its trust is already available. Never imports
    certificates, signs or executes a package, changes source, or grants acceptance/publication.
    The output belongs outside the ten-file release directory and is always a new file.
.PARAMETER RepositoryRoot
    Clean Git checkout actually used by the controlled build.
.PARAMETER ReleaseDirectory
    Eight formal-shaped output files produced by that build.
.PARAMETER CiEvidencePath
    Verified artifact/TRX snapshot from the same source commit.
.PARAMETER ExpectedCiEvidenceSha256
    Independent digest of the CI evidence supplied by the trusted producer.
.PARAMETER AuthenticodeSubject
    Exact expected Installer signing subject from signing policy.
.PARAMETER AuthenticodeThumbprint
    Exact expected Installer signing thumbprint from signing policy.
.PARAMETER MsixThumbprint
    Exact expected main package signing thumbprint from signing policy.
.PARAMETER OutputPath
    New candidate identity JSON outside ReleaseDirectory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RepositoryRoot,
    [Parameter(Mandatory)][string] $ReleaseDirectory,
    [Parameter(Mandatory)][string] $CiEvidencePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}\z')][string] $ExpectedCiEvidenceSha256,
    [Parameter(Mandatory)][string] $AuthenticodeSubject,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-F]{40}\z')][string] $AuthenticodeThumbprint,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-F]{40}\z')][string] $MsixThumbprint,
    [Parameter(Mandatory)][string] $OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$installerRoot=Join-Path $PSScriptRoot '../ClashSharp/Installer'
Import-Module (Join-Path $installerRoot 'CandidateIdentity.psm1') -Force
Import-Module (Join-Path $installerRoot 'PackagingContract.psm1') -Force
$repository=Assert-ClashSharpOrdinaryPath -LiteralPath $RepositoryRoot -RequireDirectory
$release=Assert-ClashSharpOrdinaryPath -LiteralPath $ReleaseDirectory -RequireDirectory
$resultPath=[IO.Path]::GetFullPath($OutputPath)
if($resultPath.StartsWith($release.TrimEnd([char[]]@('\','/'))+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $resultPath)){throw 'candidate.new-external-output-required'}
$git=(Get-Command git -CommandType Application -ErrorAction Stop|Select-Object -First 1).Source
$commit=[string](& $git -C $repository rev-parse --verify HEAD);if($LASTEXITCODE -or $commit -cnotmatch '^[0-9a-f]{40}\z'){throw 'candidate.source-commit'}
$tree=[string](& $git -C $repository rev-parse --verify 'HEAD^{tree}');if($LASTEXITCODE -or $tree -cnotmatch '^[0-9a-f]{40}\z'){throw 'candidate.source-tree'}
$dirty=@(& $git -C $repository status --porcelain --untracked-files=no)
if($LASTEXITCODE -or $dirty.Count){throw 'candidate.dirty-checkout'}
$ciPath=Assert-ClashSharpOrdinaryPath -LiteralPath $CiEvidencePath -RequireFile
$ciLease=[IO.File]::Open($ciPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{
 if($ciLease.Length -le 0 -or $ciLease.Length -gt 1048576){throw 'candidate.ci-evidence-budget'}
 $ciHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($ciLease)).ToLowerInvariant()
 if($ciHash -cne $ExpectedCiEvidenceSha256){throw 'candidate.ci-evidence-digest'}
 $ciLease.Position=0;$reader=[IO.StreamReader]::new($ciLease,[Text.Encoding]::UTF8,$true,1024,$true)
 try{$ci=$reader.ReadToEnd()|ConvertFrom-Json -Depth 20}finally{$reader.Dispose()}
 if($ci.kind -cne 'verified-product-ci-artifact-snapshots' -or $ci.repository -cne 'Water-Run/ClashSharp' -or
    $ci.sourceCommit -cne $commit -or $ci.artifactBytesVerified -isnot [bool] -or $ci.artifactBytesVerified -ne $true -or
    $ci.rawTestReportsVerified -isnot [bool] -or $ci.rawTestReportsVerified -ne $true -or
    $ci.tests.reportCount -ne 4 -or $ci.tests.passed -le 0 -or $ci.tests.failed -ne 0 -or $ci.tests.skipped -ne 0 -or
    $ci.runId -le 0 -or $ci.runAttempt -le 0){throw 'candidate.ci-source-or-state'}
 $sourceManifest=Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $repository 'ClashSharp/ClashSharp/Package.appxmanifest') -RequireFile
 $xmlSettings=[Xml.XmlReaderSettings]::new();$xmlSettings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$xmlSettings.XmlResolver=$null
 $xmlReader=[Xml.XmlReader]::Create($sourceManifest,$xmlSettings)
 try{$manifest=[Xml.XmlDocument]::new();$manifest.XmlResolver=$null;$manifest.Load($xmlReader)}finally{$xmlReader.Dispose()}
 $sourceIdentities=@($manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Identity']"))
 if($sourceIdentities.Count -ne 1){throw 'candidate.source-package-identity'}
 $sourceIdentity=$sourceIdentities[0]
 $pins=Get-Content -LiteralPath (Join-Path $repository 'ClashSharp/Installer/release-inputs.json') -Raw|ConvertFrom-Json
 $snapshot=Get-ClashSharpCandidateFileSnapshot -ReleaseDirectory $release -Consume {
  param($files)
  $primary=Join-Path $release 'payload/ClashSharp_1.0.0.0_x64.msix'
  $identity=Get-ClashSharpMsixIdentity -LiteralPath $primary
  if($identity.Name -cne $sourceIdentity.GetAttribute('Name') -or $identity.Publisher -cne $sourceIdentity.GetAttribute('Publisher') -or
     $identity.Version -cne $sourceIdentity.GetAttribute('Version') -or $identity.Architecture -cne 'x64'){throw 'candidate.package-source-mismatch'}
  $runtime=Join-Path $release 'payload/Dependencies/x64/Microsoft.WindowsAppRuntime.1.8.msix'
  $runtimeFile=@($files|Where-Object path -CEQ 'payload/Dependencies/x64/Microsoft.WindowsAppRuntime.1.8.msix')[0]
  if($runtimeFile.sha256 -cne $pins.windowsAppRuntime.sha256 -or $runtimeFile.length -ne $pins.windowsAppRuntime.length){throw 'candidate.runtime-pin'}
  $dependencyIdentity=Get-ClashSharpMsixIdentity -LiteralPath $runtime
  $signatures=[pscustomobject]@{
   installer=Get-ClashSharpPackageSignature -LiteralPath (Join-Path $release 'Setup.exe') -ExpectedSubject $AuthenticodeSubject -ExpectedThumbprint $AuthenticodeThumbprint -RequireTrusted -RequireTimestamp
   primary=Get-ClashSharpPackageSignature -LiteralPath $primary -ExpectedSubject $identity.Publisher -ExpectedThumbprint $MsixThumbprint -RequireTrusted
   dependency=Get-ClashSharpPackageSignature -LiteralPath $runtime -ExpectedSubject $dependencyIdentity.Publisher -ExpectedThumbprint $pins.windowsAppRuntime.signerThumbprint -RequireTrusted -RequireTimestamp
  }
  Get-ClashSharpCandidateDocument -SourceCommit $commit -SourceTree $tree -CiRunId $ci.runId -CiEvidenceSha256 $ciHash -Files $files -Signatures $signatures
 }
 $headAgain=[string](& $git -C $repository rev-parse --verify HEAD)
 $dirtyAgain=@(& $git -C $repository status --porcelain --untracked-files=no)
 if($LASTEXITCODE -or $headAgain -cne $commit -or $dirtyAgain.Count){throw 'candidate.source-changed-during-observation'}
 $null=[IO.Directory]::CreateDirectory((Split-Path -Parent $resultPath))
 $bytes=[Text.UTF8Encoding]::new($false).GetBytes($snapshot.canonicalJson)
 $output=[IO.File]::Open($resultPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
 try{$output.Write($bytes,0,$bytes.Length)}finally{$output.Dispose()}
 [pscustomobject]@{kind='clashsharp-signed-candidate-identity';sourceCommit=$commit;identitySha256=$snapshot.identitySha256;documentSha256=(Get-FileHash -LiteralPath $resultPath).Hash.ToLowerInvariant();acceptanceBound=$false;promotionAuthorized=$false}
}finally{$ciLease.Dispose()}
