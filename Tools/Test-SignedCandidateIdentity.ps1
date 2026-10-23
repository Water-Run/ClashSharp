#Requires -Version 7.4
<#
.SYNOPSIS
    Rechecks a pinned candidate identity and its eight output bytes without executing them.
.DESCRIPTION
    Requires an independently obtained manifest SHA256 and canonical manifest bytes. This proves
    that the current file set matches that identity; it does not authenticate a supplied manifest,
    recheck certificate trust/revocation, bind an acceptance result or authorize publication.
.PARAMETER IdentityPath
    Canonical manifest produced by the controlled signing operation.
.PARAMETER ExpectedIdentitySha256
    Independent digest from that operation, not a digest selected from the same untrusted file.
.PARAMETER ReleaseDirectory
    Ordinary candidate directory to compare against the pinned snapshot.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $IdentityPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}\z')][string] $ExpectedIdentitySha256,
    [Parameter(Mandatory)][string] $ReleaseDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$installerRoot=Join-Path $PSScriptRoot '../ClashSharp/Installer'
Import-Module (Join-Path $installerRoot 'CandidateIdentity.psm1') -Force
Import-Module (Join-Path $installerRoot 'PackagingContract.psm1') -Force
$path=Assert-ClashSharpOrdinaryPath -LiteralPath $IdentityPath -RequireFile
$lease=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{
 if($lease.Length -le 0 -or $lease.Length -gt 65536){throw 'candidate.manifest-budget'}
 $digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($lease)).ToLowerInvariant()
 if($digest -cne $ExpectedIdentitySha256){throw 'candidate.manifest-digest'}
 $lease.Position=0;$reader=[IO.StreamReader]::new($lease,[Text.UTF8Encoding]::new($false,$true),$false,1024,$true)
 try{$text=$reader.ReadToEnd()}finally{$reader.Dispose()}
 try{$value=$text|ConvertFrom-Json -Depth 20}catch{throw 'candidate.manifest-json'}
 if($value.schemaVersion -ne 1 -or $value.kind -cne 'clashsharp-signed-candidate-identity' -or
    $value.repository -cne 'Water-Run/ClashSharp' -or $value.acceptanceBound -isnot [bool] -or $value.acceptanceBound -or
    $value.promotionAuthorized -isnot [bool] -or $value.promotionAuthorized){throw 'candidate.manifest-state'}
 $canonical=Get-ClashSharpCandidateDocument -SourceCommit $value.source.commit -SourceTree $value.source.tree `
    -CiRunId $value.ci.runId -CiEvidenceSha256 $value.ci.artifactEvidenceSha256 -Files $value.files -Signatures $value.signatures
 if($canonical.identitySha256 -cne $ExpectedIdentitySha256 -or $canonical.canonicalJson -cne $text){throw 'candidate.manifest-noncanonical'}
 Get-ClashSharpCandidateFileSnapshot -ReleaseDirectory $ReleaseDirectory -Consume {
  param($files)
  foreach($expected in $canonical.document.files){$actual=@($files|Where-Object path -CEQ $expected.path)
   if($actual.Count -ne 1 -or $actual[0].length -ne $expected.length -or $actual[0].sha256 -cne $expected.sha256){throw 'candidate.file-changed'}
  }
  [pscustomobject]@{kind='candidate-identity-recheck';identitySha256=$ExpectedIdentitySha256;sourceCommit=$value.source.commit;
    fileBytesMatchedIdentity=$true;signatureTrustRechecked=$false;acceptanceBound=$false;promotionAuthorized=$false}
 }
}finally{$lease.Dispose()}
