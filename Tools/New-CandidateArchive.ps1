#Requires -Version 7.4
<#
.SYNOPSIS
    Seals an existing trusted signed candidate for transfer without rebuilding or signing.
.DESCRIPTION
    Requires its independently pinned identity, preserves all ten original files, and returns
    the exact transport digest. Existing outputs or partial attempts are never overwritten.
    Does not import trust, execute the product, install, or authorize publication.
.PARAMETER IdentityPath
    Canonical identity produced by the controlled signing operation.
.PARAMETER ExpectedIdentitySha256
    Independent SHA256 returned by that operation.
.PARAMETER ReleaseDirectory
    Exact ten-file signed candidate directory.
.PARAMETER OutputPath
    New absolute ZIP path outside the candidate directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $IdentityPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}\z')][string] $ExpectedIdentitySha256,
    [Parameter(Mandatory)][string] $ReleaseDirectory,
    [Parameter(Mandatory)][string] $OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '../ClashSharp/Installer/CandidateArchive.psm1') -Force
New-ClashSharpCandidateArchive @PSBoundParameters
