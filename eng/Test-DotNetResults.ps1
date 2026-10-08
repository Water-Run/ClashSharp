#Requires -Version 5.1

<#
.SYNOPSIS
Verifies the actual .NET test evidence and optionally saves its SHA-256 summary.
.DESCRIPTION
Defaults to the four complete reports produced by CI. Local callers can supply explicit reports
from scoped runs; excluded tests remain outside that evidence. Summary output is created only
after every report succeeds and never overwrites an existing file. Raw test output is not exported.
.PARAMETER ResultPath
Explicit TRX paths. Defaults to the main, Installer Core, Presentation and Windows CI reports.
.PARAMETER SummaryPath
Optional new JSON file for validated counters, run identities and report hashes.
.EXAMPLE
./eng/Test-DotNetResults.ps1 -SummaryPath ./artifacts/validation/dotnet-test-evidence.json
#>
[CmdletBinding()]
param(
    [ValidateCount(1, 16)][string[]] $ResultPath,
    [ValidateNotNullOrEmpty()][ValidatePattern('\.json$')][string] $SummaryPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module -Name (Join-Path $PSScriptRoot 'TestResultEvidence.psm1') -Force
if (-not $PSBoundParameters.ContainsKey('ResultPath')) {
    $repository = Split-Path -Parent $PSScriptRoot
    $ResultPath = @(
        'ClashSharp/ClashSharp.Tests/TestResults/tests.trx',
        'ClashSharp/ClashSharp.Installer.Tests/TestResults/installer-core-tests.trx',
        'ClashSharp/ClashSharp.Installer.Presentation.Tests/TestResults/installer-presentation-tests.trx',
        'ClashSharp/ClashSharp.Installer.Windows.Tests/TestResults/installer-windows-tests.trx'
    ) | ForEach-Object { Join-Path $repository $_ }
}
$summary = Get-ClashSharpTestResultSummary -LiteralPath $ResultPath
if ($PSBoundParameters.ContainsKey('SummaryPath')) {
    $destination = [IO.Path]::GetFullPath($SummaryPath)
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($summary | ConvertTo-Json -Depth 5) + "`n")
    $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $output.Write($bytes, 0, $bytes.Length) } finally { $output.Dispose() }
}
Write-Output $summary
