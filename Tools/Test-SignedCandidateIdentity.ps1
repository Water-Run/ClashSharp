#Requires -Version 7.4
<#
.SYNOPSIS
    Rechecks a pinned candidate identity and its ten output bytes without executing them.
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
Invoke-ClashSharpCandidateIdentity -IdentityPath $IdentityPath -ExpectedIdentitySha256 $ExpectedIdentitySha256 -ReleaseDirectory $ReleaseDirectory -Consume {
  param($value)
  [pscustomobject]@{kind='candidate-identity-recheck';identitySha256=$ExpectedIdentitySha256;sourceCommit=$value.source.commit;
    fileBytesMatchedIdentity=$true;signatureTrustRechecked=$false;acceptanceBound=$false;promotionAuthorized=$false}
}
