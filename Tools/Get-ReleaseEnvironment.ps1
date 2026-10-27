#Requires -Version 7.4
<#
.SYNOPSIS
    Reads existing release protection without creating an environment or authorizing publication.
.DESCRIPTION
    Uses the existing opaque gh authentication, fixed repository endpoints and repeated observations.
    Missing, incomplete or changing protection fails closed and leaves a non-authorizing receipt.
    Does not read secret values, alter protection, approve deployment, sign or publish anything.
.PARAMETER OutputPath
    New absolute JSON receipt under an existing ordinary directory, including a failed observation.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string] $OutputPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../eng/ReleaseEnvironment.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '../ClashSharp/Installer/PackagingContract.psm1') -Force
if (-not [IO.Path]::IsPathFullyQualified($OutputPath)) { throw 'release.environment.absolute-output-required' }
$output = [IO.Path]::GetFullPath($OutputPath)
$null = Assert-ClashSharpOrdinaryPath -LiteralPath (Split-Path -Parent $output) -RequireDirectory
if (Test-Path -LiteralPath $output) { throw 'release.environment.observe-existing-output' }
$gh = (Get-Command gh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source

function Read-ReleaseApi {
    <#
    .SYNOPSIS
        Reads one fixed repository metadata endpoint without exporting credentials.
    .DESCRIPTION
        API failures remain stable codes; headers, authentication and raw stderr are never emitted.
    .PARAMETER Endpoint
        One of the two fixed read-only endpoint suffixes used by this collector.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateSet('environments?per_page=100', 'branches/main')][string] $Endpoint)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $lines = @(& $gh api --hostname github.com ('repos/Water-Run/ClashSharp/' + $Endpoint) 2>&1); $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) { throw 'release.environment.api-unavailable' }
    try { return ($lines -join "`n" | ConvertFrom-Json -Depth 30) }
    catch { throw 'release.environment.api-json-invalid' }
}

function Read-ReleaseProtection {
    <#
    .SYNOPSIS
        Observes a complete environment inventory and the protected main branch.
    .DESCRIPTION
        Refuses incomplete pagination and missing or duplicate release entries before running policy.
    #>
    [CmdletBinding()]
    param()
    $inventory = Read-ReleaseApi 'environments?per_page=100'
    $items = @($inventory.environments)
    if (($inventory.total_count -isnot [int] -and $inventory.total_count -isnot [long]) -or
        $inventory.total_count -lt 0 -or $inventory.total_count -gt 100 -or $items.Count -ne $inventory.total_count) {
        throw 'release.environment.inventory-incomplete'
    }
    $environment = @($items | Where-Object name -CEQ 'release')
    if ($environment.Count -eq 0) { throw 'release.environment.missing' }
    if ($environment.Count -ne 1) { throw 'release.environment.identity' }
    $branch = Read-ReleaseApi 'branches/main'
    return Get-ClashSharpReleaseEnvironment -EnvironmentObservation $environment[0] -MainBranchObservation $branch
}

$receipt = $null
$failed = $false
try {
    $first = Read-ReleaseProtection
    $again = Read-ReleaseProtection
    if (($first | ConvertTo-Json -Depth 8 -Compress) -cne ($again | ConvertTo-Json -Depth 8 -Compress)) {
        throw 'release.environment.changed-during-read'
    }
    $receipt = [ordered]@{ status = 'passed'; observation = $first; publicationAuthorized = $false }
} catch {
    $failed = $true
    $diagnostic = if ($_.Exception.Message -cmatch '^release\.environment\.[a-z-]+\z') {
        $_.Exception.Message
    } else { 'release.environment.observation-invalid' }
    $receipt = [ordered]@{ status = 'failed'; repository = 'Water-Run/ClashSharp';
        scope = 'existing-release-protection-read-only'; code = $diagnostic; publicationAuthorized = $false }
}
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($receipt | ConvertTo-Json -Depth 10) + "`n")
$stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
if ($failed) { throw $receipt.code }
[pscustomobject]$receipt
