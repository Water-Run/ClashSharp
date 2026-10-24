#Requires -Version 5.1
<#
.SYNOPSIS
    Exercises immutable source provenance independently of subsequent acceptance results.
.DESCRIPTION
    Uses synthetic API observations only. Does not authenticate JSON, contact GitHub, download
    files, execute candidates, change trust or certify an installation.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'InternalCandidateSource.psm1') -Force
$script:checks=0
function Assert-Condition([bool]$Value,[string]$Message){$script:checks++;if(-not $Value){throw $Message}}
function New-Fixture(){
 $steps=@('Require mainline manual dispatch','Check out trusted controller','Verify exact product CI bytes','Verify actual controller composition',
 'Check out exact product source','Set up pinned source SDK','Restore exact source dependencies','Acquire pinned source inputs','Sign and seal internal candidate','Upload public internal-candidate files')
 [ordered]@{
  Run=@{id=123;repository=@{full_name='Water-Run/ClashSharp'};head_repository=@{full_name='Water-Run/ClashSharp'};event='workflow_dispatch';head_branch='main';head_sha=('a'*40);
   status='completed';conclusion='failure';run_attempt=1;path='.github/workflows/internal-candidate.yml';workflow_id=456}
  Jobs=@{total_count=2;jobs=@(
   @{id=789;name='Build internal self-signed candidate';status='completed';conclusion='success';run_id=123;head_sha=('a'*40);steps=@($steps|ForEach-Object {@{name=$_;status='completed';conclusion='success'}})},
   @{id=790;name='Independent internal payload check (windows-server)';status='completed';conclusion='failure'}
  )}
  Artifact=@{id=987;name='internal-signed-candidate-123-1';expired=$false;size_in_bytes=1000;digest=('sha256:'+('b'*64));workflow_run=@{id=123;head_sha=('a'*40)}}
  Workflow=@{id=456;path='.github/workflows/internal-candidate.yml';state='active'}
 }|ConvertTo-Json -Depth 15|ConvertFrom-Json
}
function Invoke-Fixture($Value){
 Get-ClashSharpInternalCandidateSource -Run $Value.Run -Jobs $Value.Jobs -Artifact $Value.Artifact -Workflow $Value.Workflow -ExpectedRunId 123 -ExpectedArtifactId 987
}
function Assert-Rejected([scriptblock]$Mutation,[string]$Code){
 $fixture=New-Fixture;& $Mutation $fixture;$refused=$false
 try{$null=Invoke-Fixture $fixture}catch{if(-not $_.Exception.Message.StartsWith($Code,[StringComparison]::Ordinal)){throw};$refused=$true}
 Assert-Condition $refused ('Expected refusal: '+$Code)
}
$fixture=New-Fixture;$result=Invoke-Fixture $fixture
Assert-Condition ($result.producerExecutionVerified -and $result.sourceWorkflowConclusion -ceq 'failure' -and -not $result.acceptanceVerified -and -not $result.artifactBytesVerified -and -not $result.promotionAuthorized) 'A retained successful producer must not claim its failed collector passed.'
$fixture.Run.conclusion='success';Assert-Condition ((Invoke-Fixture $fixture).producerExecutionVerified) 'A successful complete source may also be consumed.'
Assert-Rejected {param($f)$f.Run.id=124} 'internal-source.identity'
Assert-Rejected {param($f)$f.Artifact.id=988} 'internal-source.identity'
Assert-Rejected {param($f)$f.Run.repository.full_name='other/repo'} 'internal-source.mainline'
Assert-Rejected {param($f)$f.Run.head_repository.full_name='other/repo'} 'internal-source.mainline'
Assert-Rejected {param($f)$f.Run.head_branch='other'} 'internal-source.mainline'
Assert-Rejected {param($f)$f.Run.event='push'} 'internal-source.mainline'
Assert-Rejected {param($f)$f.Run.head_sha='0'*40} 'internal-source.mainline'
Assert-Rejected {param($f)$f.Run.head_sha+=[char]10} 'internal-source.mainline'
Assert-Rejected {param($f)$f.Run.status='in_progress'} 'internal-source.incomplete'
Assert-Rejected {param($f)$f.Run.conclusion='cancelled'} 'internal-source.incomplete'
Assert-Rejected {param($f)$f.Run.run_attempt=0} 'internal-source.incomplete'
Assert-Rejected {param($f)$f.Run.path='.github/workflows/ci.yml'} 'internal-source.workflow'
Assert-Rejected {param($f)$f.Run.workflow_id=457} 'internal-source.workflow'
Assert-Rejected {param($f)$f.Workflow.state='disabled_manually'} 'internal-source.workflow'
Assert-Rejected {param($f)$f.Jobs.total_count=3} 'internal-source.job-inventory'
Assert-Rejected {param($f)$f.Jobs.jobs[1].name='Build internal self-signed candidate'} 'internal-source.producer-incomplete'
Assert-Rejected {param($f)$f.Jobs.jobs[0].conclusion='failure'} 'internal-source.producer-incomplete'
Assert-Rejected {param($f)$f.Jobs.jobs[0].head_sha='c'*40} 'internal-source.producer-incomplete'
Assert-Rejected {param($f)$f.Jobs.jobs[0].run_id=124} 'internal-source.producer-incomplete'
foreach($name in @('Verify exact product CI bytes','Sign and seal internal candidate','Upload public internal-candidate files')){
 $fixture=New-Fixture;($fixture.Jobs.jobs[0].steps|Where-Object name -CEQ $name).conclusion='skipped'
 $refused=$false;try{$null=Invoke-Fixture $fixture}catch{$refused=$_.Exception.Message.StartsWith('internal-source.required-step:',[StringComparison]::Ordinal)}
 Assert-Condition $refused ('Producer step must execute: '+$name)
}
Assert-Rejected {param($f)$f.Artifact.name='internal-signed-candidate-123-2'} 'internal-source.artifact-origin'
Assert-Rejected {param($f)$f.Artifact.expired=$true} 'internal-source.artifact-origin'
Assert-Rejected {param($f)$f.Artifact.expired='false'} 'internal-source.artifact-origin'
Assert-Rejected {param($f)$f.Artifact.size_in_bytes=0} 'internal-source.artifact-origin'
Assert-Rejected {param($f)$f.Artifact.digest='sha256:bad'} 'internal-source.artifact-origin'
Assert-Rejected {param($f)$f.Artifact.workflow_run.id=124} 'internal-source.artifact-origin'
Assert-Rejected {param($f)$f.Artifact.workflow_run.head_sha='c'*40} 'internal-source.artifact-origin'
$repository=Split-Path -Parent $PSScriptRoot
$yaml=Get-Content "$repository/.github/workflows/internal-payload.yml" -Raw
Assert-Condition ($yaml.Contains('workflow_dispatch:') -and $yaml.Contains('$env:GITHUB_REF -cne') -and $yaml.Contains('contents: read') -and $yaml.Contains('actions: read')) 'Independent recheck must retain explicit mainline read-only dispatch.'
Assert-Condition ($yaml.Contains('Get-InternalCandidateSource.ps1') -and $yaml.Contains('artifact-ids: ${{ steps.source.outputs.artifact_id }}') -and $yaml.Contains('run-id: ${{ steps.source.outputs.run_id }}')) 'Recheck must bind the exact successful producer artifact and source run.'
Assert-Condition ($yaml.Contains('artifacts/validation') -and $yaml.IndexOf('[IO.Directory]::CreateDirectory') -lt $yaml.IndexOf('./Tools/Get-InternalCandidateSource.ps1')) 'Fresh collector must prepare its metadata and audit parent.'
Assert-Condition ($yaml -notmatch 'New-SelfSignedCertificate|New-InternalCandidate\.ps1|dotnet (build|publish)|secrets\.|(?m)^\s+(contents|actions): write') 'Rechecking an existing candidate may not rebuild, sign or acquire release authority.'
foreach($action in [regex]::Matches($yaml,'(?m)^\s+uses: ([^\r\n]+)')){Assert-Condition ($action.Groups[1].Value -cmatch '^actions/[a-z-]+@[0-9a-f]{40}\z') 'Recheck actions must remain pinned.'}
[pscustomobject]@{success=$true;checks=$script:checks;syntheticObservations=$true;networkUsed=$false;packagesExecuted=$false;promotionAuthorized=$false}
