#Requires -Version 5.1

<#
.SYNOPSIS
Exercises the coverage floor gate with independent Cobertura fixtures.
.DESCRIPTION
Checks that a report at or above every floor passes, that one area below its floor fails by name, that an
area missing from the report fails, that lines are merged per file across assemblies by the highest hit
count, that rooted and relative source names group the same way, and that files outside the solution are
ignored. Uses only a temporary owned directory and no test runner.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:checks = 0
$script:gate = Join-Path $PSScriptRoot 'Test-CoverageFloor.ps1'
$script:root = Join-Path ([IO.Path]::GetTempPath()) ('coverage-floor-contract-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $script:root

<#
.SYNOPSIS
Builds a class element with one line per hit count.
.DESCRIPTION
Each entry of Hits becomes a line numbered from one, so the covered fraction is exactly the share of non-zero entries.
.PARAMETER File
Source name recorded on the class, relative to the solution folder or rooted.
.PARAMETER Hits
Hit count of each line; zero marks an uncovered line.
#>
function New-ClassXml {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $File, [Parameter(Mandatory)][int[]] $Hits)

    $lines = for ($i = 0; $i -lt $Hits.Count; $i++) { '<line number="{0}" hits="{1}" branch="False" />' -f ($i + 1), $Hits[$i] }
    '<class name="C" filename="{0}"><methods /><lines>{1}</lines></class>' -f $File, ($lines -join '')
}

<#
.SYNOPSIS
Writes a report whose packages hold the supplied class elements.
.DESCRIPTION
Each class element goes into its own package, so one source file can be seen by several assemblies as in a real report.
.PARAMETER Name
File name of the report inside the temporary directory, without extension.
.PARAMETER PackageClasses
Class elements to place, one per package.
#>
function Write-Report {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][string[]] $PackageClasses)

    $packages = $PackageClasses | ForEach-Object { '<package name="P"><classes>{0}</classes></package>' -f $_ }
    $path = Join-Path $script:root ($Name + '.xml')
    Set-Content -LiteralPath $path -Encoding UTF8 -Value ('<?xml version="1.0"?><coverage><sources /><packages>{0}</packages></coverage>' -f ($packages -join ''))
    $path
}

<#
.SYNOPSIS
Runs the gate and returns whether it passed and what it said.
.DESCRIPTION
Calls the gate with the fixture solution folder and reports a pass, or the rejection message when the gate throws.
.PARAMETER Report
Path of the Cobertura fixture to judge.
.PARAMETER Floors
Path of the floors file to judge it against.
#>
function Invoke-Gate {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Report, [Parameter(Mandatory)][string] $Floors)

    try {
        $output = & $script:gate -ReportPath $Report -FloorsPath $Floors -SolutionRoot $script:solution 2>&1 | Out-String
        [pscustomobject]@{ Passed = $true; Output = $output }
    } catch {
        [pscustomobject]@{ Passed = $false; Output = $_.Exception.Message }
    }
}

<#
.SYNOPSIS
Fails the contract with a message unless the condition holds.
.DESCRIPTION
Counts every check so the final summary shows how many conditions were exercised.
.PARAMETER Condition
Condition that must hold.
.PARAMETER Message
Description of the expected behavior, used in the failure.
#>
function Assert-Check {
    [CmdletBinding()]
    param([Parameter(Mandatory)][bool] $Condition, [Parameter(Mandatory)][string] $Message)

    $script:checks++
    if (-not $Condition) { throw "Coverage floor contract failed: $Message" }
}

try {
    $script:solution = Join-Path $script:root 'solution'
    $null = New-Item -ItemType Directory -Path $script:solution
    $floors = Join-Path $script:root 'floors.json'
    Set-Content -LiteralPath $floors -Encoding UTF8 -Value '{"floors":{"ClashSharp/Service":50,"ClashSharp.Core":75}}'

    # Four lines, two covered: exactly 50 percent. Core: four lines, three covered: exactly 75 percent.
    $service = New-ClassXml 'ClashSharp/Service/A.cs' @(1, 0, 5, 0)
    $core = New-ClassXml 'ClashSharp.Core/B.cs' @(1, 2, 3, 0)

    $exact = Invoke-Gate (Write-Report 'exact' @($service, $core)) $floors
    Assert-Check $exact.Passed 'a report exactly on every floor must pass'

    $below = Invoke-Gate (Write-Report 'below' @((New-ClassXml 'ClashSharp/Service/A.cs' @(1, 0, 0, 0)), $core)) $floors
    Assert-Check (-not $below.Passed) 'one area under its floor must fail'
    Assert-Check ($below.Output -match 'ClashSharp/Service' -and $below.Output -notmatch 'ClashSharp\.Core: line') 'only the area under its floor is named'

    $missing = Invoke-Gate (Write-Report 'missing' @($service)) $floors
    Assert-Check (-not $missing.Passed -and $missing.Output -match 'ClashSharp\.Core: the report has no lines') 'an area without lines must fail, not pass silently'

    # The same file seen by two assemblies: the covering one wins, so a linked copy that never ran cannot hide it.
    $merged = Invoke-Gate (Write-Report 'merged' @((New-ClassXml 'ClashSharp/Service/A.cs' @(0, 0, 0, 0)), $service, $core)) $floors
    Assert-Check $merged.Passed 'lines are merged per file by the highest hit count'

    # Rooted names inside the solution group like relative ones; rooted names outside it are ignored.
    $rooted = ($script:solution -replace '\\', '/') + '/ClashSharp/Service/A.cs'
    $inside = Invoke-Gate (Write-Report 'rooted' @((New-ClassXml $rooted @(1, 0, 5, 0)), $core)) $floors
    Assert-Check $inside.Passed 'a rooted source name inside the solution groups like a relative one'
    $outside = Invoke-Gate (Write-Report 'outside' @((New-ClassXml 'C:/elsewhere/ClashSharp/Service/Z.cs' @(0, 0, 0, 0)), $service, $core)) $floors
    Assert-Check $outside.Passed 'files outside the solution never count'

    # Another application folder is its own area and never lifts Service.
    $other = Invoke-Gate (Write-Report 'other' @((New-ClassXml 'ClashSharp/View/V.cs' @(1, 1, 1, 1)), (New-ClassXml 'ClashSharp/Service/A.cs' @(1, 0, 0, 0)), $core)) $floors
    Assert-Check (-not $other.Passed) 'a well covered sibling folder must not raise Service'

    "Coverage floor contract passed ($script:checks checks)."
} finally {
    Remove-Item -LiteralPath $script:root -Recurse -Force -ErrorAction SilentlyContinue
}
