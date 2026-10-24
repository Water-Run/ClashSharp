#Requires -Version 7.4
<#
.SYNOPSIS
    Receives an independently pinned candidate archive and verifies its original file identity.
.DESCRIPTION
    Keeps the archive against replacement, accepts only its fixed nine-entry layout and bounded
    bytes, and preserves failed destinations. References must come from the trusted producer,
    not from an adjacent untrusted receipt. Never executes or installs extracted files.
.PARAMETER ArchivePath
    Ordinary candidate ZIP supplied by the trusted producer.
.PARAMETER ExpectedArchiveSha256
    Independently retained digest of that exact ZIP.
.PARAMETER ExpectedIdentitySha256
    Independently retained identity of the original signed candidate.
.PARAMETER Destination
    New absolute directory under an existing ordinary parent.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ArchivePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}\z')][string] $ExpectedArchiveSha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}\z')][string] $ExpectedIdentitySha256,
    [Parameter(Mandatory)][string] $Destination
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '../ClashSharp/Installer/CandidateArchive.psm1') -Force
Expand-ClashSharpCandidateArchive @PSBoundParameters
