#Requires -Version 7.4
<#
.SYNOPSIS
    Downloads and verifies exact product CI artifact snapshots and their original test evidence.
.DESCRIPTION
    Uses fresh authenticated metadata and byte-preserving native redirection. Existing matching
    downloads are reusable; a partial or changed file is preserved and rejected, never overwritten.
    No archive is executed and no candidate, signature, environment or release is created.
.PARAMETER RunId
    Exact successful mainline CI run.
.PARAMETER SourceCommit
    Exact source commit selected by the trusted caller.
.PARAMETER Destination
    Ordinary directory to retain artifact-ID.zip inputs for diagnosis and subsequent evidence.
.PARAMETER OutputPath
    New verification JSON file. Existing evidence cannot be overwritten.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long] $RunId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}\z')][string] $SourceCommit,
    [Parameter(Mandatory)][string] $Destination,
    [Parameter(Mandatory)][string] $OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$resultPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $resultPath) { throw 'ci-artifacts.observe-existing-output' }
$metadataTool = Join-Path $PSScriptRoot 'Get-ProductCiRun.ps1'
$metadata = & $metadataTool -RunId $RunId -SourceCommit $SourceCommit
Import-Module (Join-Path $PSScriptRoot '../eng/ProductCiArtifacts.psm1') -Force
$directory = [IO.Path]::GetFullPath($Destination)
$null = [IO.Directory]::CreateDirectory($directory)
$current = $directory
while ($current) {
    if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'ci-artifacts.reparse-path'
    }
    $current = [IO.Path]::GetDirectoryName($current)
}
$gh = (Get-Command gh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
foreach ($artifact in $metadata.artifacts) {
    $path = Join-Path $directory ('artifact-' + $artifact.id + '.zip')
    $existing = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    if ($null -ne $existing -and ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'ci-artifacts.reparse-path'
    }
    if (-not (Test-Path -LiteralPath $path)) {
        $endpoint = 'repos/Water-Run/ClashSharp/actions/artifacts/' + $artifact.id + '/zip'
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            # Do not merge stderr into stdout: this must remain an unmodified binary stream.
            & $gh api --hostname github.com $endpoint > $path
            $code = $LASTEXITCODE
        } finally { $ErrorActionPreference = $previous }
        if ($code -ne 0) { throw 'ci-artifacts.download-failed-inspect-retained-bytes' }
    }
}
$evidence = Get-ClashSharpProductCiArtifactEvidence -Metadata $metadata -ArtifactDirectory $directory
$again = & $metadataTool -RunId $RunId -SourceCommit $SourceCommit
if (($metadata | ConvertTo-Json -Depth 8 -Compress) -cne ($again | ConvertTo-Json -Depth 8 -Compress)) {
    throw 'ci-artifacts.metadata-changed-during-consumption'
}
$null = [IO.Directory]::CreateDirectory((Split-Path -Parent $resultPath))
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($evidence | ConvertTo-Json -Depth 10) + "`n")
$output = [IO.File]::Open($resultPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $output.Write($bytes, 0, $bytes.Length) } finally { $output.Dispose() }
$evidence
