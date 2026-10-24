#Requires -Version 7.4
<#
.SYNOPSIS
    Builds and archives a self-signed internal candidate from exact verified product CI.
.DESCRIPTION
    Runs only in this repository's disposable hosted CI context. Uses fresh short-lived keys
    and narrowly owned temporary machine trust, preserving normal signing and payload gates,
    and removes owned trust, keys and encrypted signing copies before returning any references.
    Only public output files are exported. This does not authorize public release or installation.
.PARAMETER RepositoryRoot
    Clean source checkout actually used for compilation, with its pinned offline inputs staged.
.PARAMETER SourceCommit
    Exact source identity of the complete product CI run.
.PARAMETER CiEvidencePath
    Verified artifact and original TRX evidence from that trusted upstream operation.
.PARAMETER ExpectedCiEvidenceSha256
    Independently retained digest from that operation.
.PARAMETER OutputDirectory
    New absolute operation directory outside the source checkout.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RepositoryRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}\z')][string]$SourceCommit,
    [Parameter(Mandatory)][string]$CiEvidencePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}\z')][string]$ExpectedCiEvidenceSha256,
    [Parameter(Mandatory)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_REPOSITORY -cne 'Water-Run/ClashSharp' -or
    $env:GITHUB_REF -cne 'refs/heads/main' -or $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or
    $env:GITHUB_WORKFLOW_REF -cne 'Water-Run/ClashSharp/.github/workflows/internal-candidate.yml@refs/heads/main' -or
    $env:INTERNAL_DISPOSABLE_RUNNER -cne 'github-hosted'){
    throw 'internal-candidate.disposable-mainline-ci-required'
}
$controllerRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module "$controllerRoot/ClashSharp/Installer/PackagingContract.psm1" -Force
Import-Module "$controllerRoot/ClashSharp/Installer/InternalSigning.psm1" -Force
$source=Assert-ClashSharpOrdinaryPath -LiteralPath $RepositoryRoot -RequireDirectory
$git=(Get-Command git -CommandType Application|Select-Object -First 1).Source
$toolCommit=[string](& $git -C $controllerRoot rev-parse HEAD)
if($LASTEXITCODE -or $toolCommit -cnotmatch '^[0-9a-f]{40}\z'){throw 'internal-candidate.controller-identity'}
$trackedController=@(& $git -C $controllerRoot ls-files --error-unmatch -- 'Tools/New-InternalCandidate.ps1')
if($LASTEXITCODE -or $trackedController.Count -ne 1){throw 'internal-candidate.tracked-controller-required'}
$controllerDirty=@(& $git -C $controllerRoot status --porcelain --untracked-files=no)
if($LASTEXITCODE -or $controllerDirty.Count){throw 'internal-candidate.clean-controller-required'}
$sourceHead=[string](& $git -C $source rev-parse HEAD)
if($LASTEXITCODE -or $sourceHead -cne $SourceCommit){throw 'internal-candidate.source-identity'}
$sourceDirty=@(& $git -C $source status --porcelain --untracked-files=no)
if($LASTEXITCODE -or $sourceDirty.Count){throw 'internal-candidate.clean-source-required'}
if(Test-Path (Join-Path $source 'artifacts/installer/release')){throw 'internal-candidate.observe-existing-build'}
if(-not [IO.Path]::IsPathFullyQualified($OutputDirectory)){throw 'internal-candidate.absolute-output-required'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
$null=Assert-ClashSharpOrdinaryPath -LiteralPath (Split-Path -Parent $output) -RequireDirectory
if((Test-Path -LiteralPath $output) -or $output.StartsWith($source.TrimEnd([char[]]@('\','/'))+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'internal-candidate.new-external-output-required'}
$ciPath=Assert-ClashSharpOrdinaryPath -LiteralPath $CiEvidencePath -RequireFile
$ciLease=[IO.File]::Open($ciPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
$signingLease=$null;$cleanup=$null;$failure=$null;$result=$null
$variables=@('CLASHSHARP_AUTHENTICODE_CERTIFICATE_THUMBPRINT','CLASHSHARP_MSIX_CERTIFICATE_THUMBPRINT','CLASHSHARP_AUTHENTICODE_TIMESTAMP_URL','CLASHSHARP_CERTIFICATE_PASSWORD')
$beforeEnvironment=@{};foreach($name in $variables){$beforeEnvironment[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try{
    if($ciLease.Length -le 0 -or $ciLease.Length -gt 1048576){throw 'internal-candidate.ci-budget'}
    $ciHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($ciLease)).ToLowerInvariant()
    if($ciHash -cne $ExpectedCiEvidenceSha256){throw 'internal-candidate.ci-reference'}
    $ciLease.Position=0;$reader=[IO.StreamReader]::new($ciLease,[Text.UTF8Encoding]::new($false,$true),$false,1024,$true)
    try{$ci=$reader.ReadToEnd()|ConvertFrom-Json -Depth 20}finally{$reader.Dispose()}
    if($ci.kind -cne 'verified-product-ci-artifact-snapshots' -or $ci.repository -cne 'Water-Run/ClashSharp' -or $ci.sourceCommit -cne $SourceCommit -or
        $ci.artifactBytesVerified -isnot [bool] -or -not $ci.artifactBytesVerified -or $ci.rawTestReportsVerified -isnot [bool] -or -not $ci.rawTestReportsVerified -or
        $ci.tests.reportCount -ne 4 -or $ci.tests.passed -le 0 -or $ci.tests.failed -ne 0 -or $ci.tests.skipped -ne 0){throw 'internal-candidate.complete-product-ci-required'}
    $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null
    $xmlReader=[Xml.XmlReader]::Create((Join-Path $source 'ClashSharp/ClashSharp/Package.appxmanifest'),$settings)
    try{$manifest=[Xml.XmlDocument]::new();$manifest.XmlResolver=$null;$manifest.Load($xmlReader)}finally{$xmlReader.Dispose()}
    $identity=@($manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Identity']"))
    if($identity.Count -ne 1){throw 'internal-candidate.package-publisher'}
    $null=[IO.Directory]::CreateDirectory($output)
    $public=Join-Path $output 'public';$null=[IO.Directory]::CreateDirectory($public)
    $signingLease=New-ClashSharpInternalSigningLease -Publisher $identity[0].GetAttribute('Publisher') -SigningDirectory (Join-Path $source 'ClashSharp/Installer/signing')
    $signers=Get-ClashSharpInternalSigningLeaseInfo -Lease $signingLease
    [Environment]::SetEnvironmentVariable('CLASHSHARP_AUTHENTICODE_CERTIFICATE_THUMBPRINT',$signers.authenticodeThumbprint,'Process')
    [Environment]::SetEnvironmentVariable('CLASHSHARP_MSIX_CERTIFICATE_THUMBPRINT',$signers.msixThumbprint,'Process')
    [Environment]::SetEnvironmentVariable('CLASHSHARP_AUTHENTICODE_TIMESTAMP_URL','http://timestamp.digicert.com','Process')
    [Environment]::SetEnvironmentVariable('CLASHSHARP_CERTIFICATE_PASSWORD',$null,'Process')
    Push-Location -LiteralPath $source
    try{& "$source/ClashSharp/Installer/build.ps1" *> "$output/build.log"}finally{Pop-Location}
    $identityReceipt=& "$PSScriptRoot/New-SignedCandidateIdentity.ps1" -RepositoryRoot $source -ReleaseDirectory "$source/artifacts/installer/release" -CiEvidencePath $ciPath -ExpectedCiEvidenceSha256 $ExpectedCiEvidenceSha256 -AuthenticodeSubject $signers.authenticodeSubject -AuthenticodeThumbprint $signers.authenticodeThumbprint -MsixThumbprint $signers.msixThumbprint -OutputPath "$public/candidate-identity.json"
    $archive=& "$PSScriptRoot/New-CandidateArchive.ps1" -IdentityPath "$public/candidate-identity.json" -ExpectedIdentitySha256 $identityReceipt.identitySha256 -ReleaseDirectory "$source/artifacts/installer/release" -OutputPath "$public/internal-candidate.zip"
    $result=[ordered]@{
        schemaVersion=1;kind='clashsharp-internal-candidate';scope='internal-self-signed';toolCommit=$toolCommit
        sourceCommit=$SourceCommit;ciRunId=$ci.runId;ciEvidenceSha256=$ExpectedCiEvidenceSha256
        candidateIdentitySha256=$identityReceipt.identitySha256;archiveSha256=$archive.archiveSha256;archiveLength=$archive.archiveLength
        authenticodeThumbprint=$signers.authenticodeThumbprint;msixThumbprint=$signers.msixThumbprint
        temporarySigningMaterialRemoved=$false;publicReleaseAuthorized=$false;promotionAuthorized=$false
    }
    [IO.File]::Copy($ciPath,"$public/ci-evidence.json",$false)
}catch{$failure=$_}
finally{
    try{
        if($null -ne $signingLease){$cleanup=Close-ClashSharpInternalSigningLease -Lease $signingLease}
    }catch{$failure=$_}
    foreach($name in $variables){[Environment]::SetEnvironmentVariable($name,$beforeEnvironment[$name],'Process')}
    $ciLease.Dispose()
}
if($null -ne $failure){throw $failure}
if($null -eq $result -or $null -eq $cleanup -or -not $cleanup.success){throw 'internal-candidate.cleanup-required'}
$result.temporarySigningMaterialRemoved=$true;$result.cleanup=$cleanup
$allowed=@('internal-candidate.zip','candidate-identity.json','ci-evidence.json')
if(@(Get-ChildItem -LiteralPath $public -Force|Where-Object {$_.PSIsContainer -or $_.Name -cnotin $allowed}).Count){throw 'internal-candidate.public-output-inventory'}
$result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath "$public/internal-candidate-result.json" -Encoding utf8NoBOM
[pscustomobject]$result
