#Requires -Version 7.4
<#
.SYNOPSIS
    Reads fresh GitHub provenance for an existing successful internal producer artifact.
.DESCRIPTION
    Allows a completed source workflow whose producer succeeded and later collector failed.
    Does not execute, download or certify artifact bytes, import trust, sign or publish.
.PARAMETER RunId
    Exact internal-candidate.yml source run.
.PARAMETER ArtifactId
    Exact immutable producer artifact returned by the trusted operation.
.PARAMETER OutputPath
    New JSON metadata file under an existing ordinary directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1,[long]::MaxValue)][long]$RunId,
    [Parameter(Mandatory)][ValidateRange(1,[long]::MaxValue)][long]$ArtifactId,
    [Parameter(Mandatory)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '../eng/InternalCandidateSource.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '../ClashSharp/Installer/PackagingContract.psm1') -Force
$output=[IO.Path]::GetFullPath($OutputPath)
$null=Assert-ClashSharpOrdinaryPath -LiteralPath (Split-Path -Parent $output) -RequireDirectory
if(Test-Path -LiteralPath $output){throw 'internal-source.observe-existing-output'}
$gh=(Get-Command gh -CommandType Application|Select-Object -First 1).Source
function Read-InternalCandidateApi {
    <#
    .SYNOPSIS
        Reads one fixed repository API endpoint without exporting credentials.
    .DESCRIPTION
        Keeps failed API output private and returns only a stable failure code.
    .PARAMETER Endpoint
        Fixed relative endpoint assembled from validated numeric IDs.
    #>
    [CmdletBinding()]
    param([string]$Endpoint)
    $previous=$ErrorActionPreference;$ErrorActionPreference='Continue'
    try{$lines=@(& $gh api --hostname github.com $Endpoint 2>&1);$code=$LASTEXITCODE}finally{$ErrorActionPreference=$previous}
    if($code){throw 'internal-source.api-unavailable'}
    $lines -join [Environment]::NewLine|ConvertFrom-Json -Depth 20
}
$prefix='repos/Water-Run/ClashSharp/actions/'
$run=Read-InternalCandidateApi ($prefix+'runs/'+$RunId)
$jobs=Read-InternalCandidateApi ($prefix+'runs/'+$RunId+'/jobs?per_page=100')
$artifact=Read-InternalCandidateApi ($prefix+'artifacts/'+$ArtifactId)
$workflow=Read-InternalCandidateApi ($prefix+'workflows/internal-candidate.yml')
$reference=Get-ClashSharpInternalCandidateSource -Run $run -Jobs $jobs -Artifact $artifact -Workflow $workflow -ExpectedRunId $RunId -ExpectedArtifactId $ArtifactId
$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($reference|ConvertTo-Json -Depth 8)+[char]10)
$writer=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$writer.Write($bytes);$writer.Flush($true)}finally{$writer.Dispose()}
$reference
