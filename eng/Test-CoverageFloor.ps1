#Requires -Version 5.1

<#
.SYNOPSIS
Enforces per-area line-coverage floors on the main test project's Cobertura report.
.DESCRIPTION
The main test project compiles many application sources into the test assembly, so the report must be
produced with eng/coverage.runsettings (it includes the test assembly and excludes the tests' own files).
Lines are merged per source file across assemblies by taking the highest hit count, then grouped by
project folder; the application project is split by its first sub-folder (ClashSharp/Service and so on).
Every area in the floors file must have lines in the report and must reach its floor.
.PARAMETER ReportPath
Cobertura report. Defaults to the newest report under ClashSharp/ClashSharp.Tests/TestResults.
.PARAMETER FloorsPath
JSON file with a "floors" object of area name to minimum line coverage percent.
.PARAMETER SolutionRoot
Folder that contains the project folders. Defaults to the repository's ClashSharp folder.
.EXAMPLE
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj --settings eng/coverage.runsettings
./eng/Test-CoverageFloor.ps1
#>
[CmdletBinding()]
param(
    [string] $ReportPath,
    [string] $FloorsPath,
    [string] $SolutionRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (-not $PSBoundParameters.ContainsKey('FloorsPath')) { $FloorsPath = Join-Path $PSScriptRoot 'coverage-floors.json' }
if (-not $PSBoundParameters.ContainsKey('SolutionRoot')) { $SolutionRoot = Join-Path $repository 'ClashSharp' }
if (-not $PSBoundParameters.ContainsKey('ReportPath')) {
    $reports = @(Get-ChildItem -LiteralPath (Join-Path $SolutionRoot 'ClashSharp.Tests/TestResults') -Filter 'coverage.cobertura.xml' -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/]In[\\/]' } | Sort-Object LastWriteTimeUtc -Descending)
    if ($reports.Count -eq 0) { throw 'The main test project produced no Cobertura report. Run it with --settings eng/coverage.runsettings.' }
    $ReportPath = $reports[0].FullName
}

$floors = (Get-Content -LiteralPath $FloorsPath -Raw | ConvertFrom-Json).floors
$prefix = ([IO.Path]::GetFullPath($SolutionRoot).TrimEnd('\', '/') -replace '\\', '/') + '/'

$document = New-Object System.Xml.XmlDocument
$document.Load($ReportPath)
$files = @{}
foreach ($class in $document.SelectNodes('//class')) {
    # Cobertura names sources relative to the report's source root (the solution folder); rooted names come from outside it or from older collectors.
    $name = ([string]$class.GetAttribute('filename')) -replace '\\', '/'
    if ([IO.Path]::IsPathRooted($name)) {
        if (-not $name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $name = $name.Substring($prefix.Length)
    }
    if (-not $files.ContainsKey($name)) { $files[$name] = @{} }
    $lines = $files[$name]
    foreach ($line in $class.SelectNodes('lines/line')) {
        $number = [int]$line.GetAttribute('number')
        $hits = [int]$line.GetAttribute('hits')
        if (-not $lines.ContainsKey($number) -or $lines[$number] -lt $hits) { $lines[$number] = $hits }
    }
}

$covered = @{}
$total = @{}
foreach ($name in $files.Keys) {
    $parts = $name.Split('/')
    $area = if ($parts[0] -ceq 'ClashSharp' -and $parts.Count -gt 2) { 'ClashSharp/' + $parts[1] } else { $parts[0] }
    if (-not $total.ContainsKey($area)) { $total[$area] = 0; $covered[$area] = 0 }
    foreach ($hits in $files[$name].Values) {
        $total[$area]++
        if ($hits -gt 0) { $covered[$area]++ }
    }
}

$failures = New-Object System.Collections.Generic.List[string]
foreach ($property in $floors.PSObject.Properties) {
    $area = $property.Name
    $floor = [double]$property.Value
    if (-not $total.ContainsKey($area) -or $total[$area] -eq 0) {
        $failures.Add("${area}: the report has no lines for this area.")
        continue
    }
    $percent = 100.0 * $covered[$area] / $total[$area]
    $status = if ($percent -ge $floor) { 'ok' } else { 'BELOW' }
    '{0,-34} {1,6:N1}%  floor {2,5:N1}%  ({3}/{4})  {5}' -f $area, $percent, $floor, $covered[$area], $total[$area], $status
    if ($percent -lt $floor) { $failures.Add(('{0}: line coverage {1:N1}% is below the floor {2:N1}%.' -f $area, $percent, $floor)) }
}
if ($failures.Count -gt 0) { throw ("Coverage floors failed:`n" + ($failures -join "`n")) }
'All coverage floors hold.'
