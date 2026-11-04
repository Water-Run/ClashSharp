#Requires -Version 7.4

<#
.SYNOPSIS
Scans the exact pinned Mihomo release binary against the Go vulnerability database.
.DESCRIPTION
Reads and holds the bundled executable without running it. A failed scan or a finding
blocks the preflight. The report records its binary identity and scanner result.
.PARAMETER OutputDirectory
New absolute directory beneath an existing ordinary directory for the scan evidence.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $OutputDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $root 'ClashSharp/Installer/PackagingContract.psm1') -Force
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'release.security.absolute-output-required' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$null = Assert-ClashSharpOrdinaryPath -LiteralPath (Split-Path -Parent $output) -RequireDirectory
if (Test-Path -LiteralPath $output) { throw 'release.security.output-exists' }

$inputRoot = Join-Path $root 'ClashSharp/ClashSharp/Binaries'
$manifestPath = Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $inputRoot 'mihomo-manifest.json') -RequireFile
if ((Get-Item -LiteralPath $manifestPath).Length -gt 16384) { throw 'release.security.manifest-size' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
if ($manifest.schemaVersion -ne 1 -or $manifest.version -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+\z' -or
    $manifest.sourceReleaseUrl -cne ('https://github.com/MetaCubeX/mihomo/releases/tag/' + $manifest.version) -or
    $manifest.sha256 -cnotmatch '^[0-9a-f]{64}\z' -or $manifest.length -lt 1 -or $manifest.length -gt 268435456) {
    throw 'release.security.manifest-invalid'
}

$binary = Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $inputRoot 'mihomo.exe') -RequireFile
$lease = [IO.File]::Open($binary, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    if ($lease.Length -ne $manifest.length -or
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($lease)).ToLowerInvariant() -cne $manifest.sha256) {
        throw 'release.security.binary-pin-mismatch'
    }
    $go = (Get-Command go -CommandType Application | Select-Object -First 1).Source
    $null = [IO.Directory]::CreateDirectory($output)
    $report = Join-Path $output 'govulncheck.txt'
    $scanner = 'golang.org/x/vuln/cmd/govulncheck@v1.9.0'
    $database = 'https://vuln.go.dev'
    $previousGoWork = $env:GOWORK
    try {
        $env:GOWORK = 'off'
        & $go run $scanner -mode=binary -db $database $binary *> $report
        $scanExitCode = $LASTEXITCODE
    } finally {
        $env:GOWORK = $previousGoWork
    }

    $passed = $scanExitCode -eq 0 -and (Get-Item -LiteralPath $report).Length -gt 0
    $receipt = [ordered]@{
        schemaVersion = 1; status = if ($passed) { 'passed' } else { 'failed' }
        version = $manifest.version; binarySha256 = $manifest.sha256
        scanner = $scanner; database = $database; scannerExitCode = $scanExitCode
        reportSha256 = (Get-FileHash -LiteralPath $report).Hash.ToLowerInvariant()
        publicationAuthorized = $false
    }
    $receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'mihomo-security.json') -Encoding utf8NoBOM
    if (-not $passed) { throw 'release.security.mihomo-scan-failed' }
    [pscustomobject]$receipt
} finally {
    $lease.Dispose()
}
