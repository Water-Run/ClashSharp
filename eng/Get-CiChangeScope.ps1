#Requires -Version 5.1
<#
.SYNOPSIS
    Records CI scope and emits a conservative product flag for the existing required jobs.
.PARAMETER Repository
    Checkout root. Defaults to this repository.
.PARAMETER BaseCommit
    Push before or pull-request base SHA.
.PARAMETER HeadCommit
    Expected checkout SHA.
.PARAMETER RecordPath
    New JSON evidence file. Existing evidence is never overwritten.
.PARAMETER GitHubOutputPath
    Runner-managed output file for the fixed product and reason keys.
#>
[CmdletBinding()]
param(
    [string] $Repository = (Split-Path -Parent $PSScriptRoot),
    [AllowNull()][AllowEmptyString()][string] $BaseCommit,
    [AllowNull()][AllowEmptyString()][string] $HeadCommit,
    [Parameter(Mandatory)][string] $RecordPath,
    [string] $GitHubOutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CiChangeScope.psm1') -Force
$scope = Get-ClashSharpCiRepositoryScope -Repository $Repository -BaseCommit $BaseCommit -HeadCommit $HeadCommit -FetchBase
$destination = [IO.Path]::GetFullPath($RecordPath)
$null = [IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($scope | ConvertTo-Json -Depth 4) + "`n")
$stream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
if ($GitHubOutputPath) {
    $flag = if ($scope.product) { 'true' } else { 'false' }
    [IO.File]::AppendAllText([IO.Path]::GetFullPath($GitHubOutputPath),
        "product=$flag`nreason=$($scope.reason)`n", [Text.UTF8Encoding]::new($false))
}
$scope
