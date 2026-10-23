#Requires -Version 7.0
<#
.SYNOPSIS
    Reads fresh GitHub observations and verifies one full product CI run's execution metadata.
.DESCRIPTION
    Uses the existing github.com gh authentication without exporting credentials. Review-only successful runs
    are rejected. Does not download or certify artifact bytes, sign, deploy, or publish anything.
.PARAMETER RunId
    Exact completed run id.
.PARAMETER SourceCommit
    Exact mainline source commit selected for subsequent candidate preparation.
.PARAMETER OutputPath
    Optional new JSON descriptor. Existing evidence is never overwritten.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long] $RunId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}\z')][string] $SourceCommit,
    [string] $OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../eng/ProductCiRun.psm1') -Force
$gh = (Get-Command gh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
function Read-GitHubApi {
    <#
    .SYNOPSIS
        Reads one fixed repository API endpoint through authenticated gh.
    .DESCRIPTION
        Retains failures only as a stable diagnostic; authentication output and HTTP headers are
        neither returned nor copied into the descriptor.
    .PARAMETER Endpoint
        Fixed relative API endpoint assembled by this script.
    #>
    [CmdletBinding()]
    param([string] $Endpoint)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $lines = @(& $gh api --hostname github.com $Endpoint 2>&1); $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) { throw 'product-ci.api-unavailable' }
    return ($lines -join "`n" | ConvertFrom-Json -Depth 30)
}
$prefix = 'repos/Water-Run/ClashSharp/actions/'
$run = Read-GitHubApi ($prefix + 'runs/' + $RunId)
$jobs = Read-GitHubApi ($prefix + 'runs/' + $RunId + '/jobs?per_page=100')
$artifacts = Read-GitHubApi ($prefix + 'runs/' + $RunId + '/artifacts?per_page=100')
$workflow = Read-GitHubApi ($prefix + 'workflows/ci.yml')
$result = Get-ClashSharpProductCiRun -Run $run -Jobs $jobs -Artifacts $artifacts -Workflow $workflow `
    -ExpectedRunId $RunId -ExpectedCommit $SourceCommit
$again = Read-GitHubApi ($prefix + 'runs/' + $RunId)
if ($again.run_attempt -ne $run.run_attempt -or $again.head_sha -cne $SourceCommit -or
    $again.status -cne 'completed' -or $again.conclusion -cne 'success') {
    throw 'product-ci.run-changed-during-observation'
}
if ($OutputPath) {
    $path = [IO.Path]::GetFullPath($OutputPath)
    $null = [IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($result | ConvertTo-Json -Depth 8) + "`n")
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}
$result
