Set-StrictMode -Version Latest

function Get-ClashSharpInternalCandidateSource {
    <#
    .SYNOPSIS
        Admits one immutable artifact from an actually successful internal producer job.
    .DESCRIPTION
        Separates producer success from a later collector failure. This pure metadata policy
        does not authenticate caller-supplied JSON, verify bytes or grant execution approval.
        The CLI obtains fresh API observations; consumers retain independent candidate hashes.
    .PARAMETER Run
        Exact source workflow run API response.
    .PARAMETER Jobs
        Complete jobs API response for that run.
    .PARAMETER Artifact
        Exact single artifact API response.
    .PARAMETER Workflow
        Current internal-candidate.yml API identity.
    .PARAMETER ExpectedRunId
        Independently selected source run.
    .PARAMETER ExpectedArtifactId
        Independently selected producer artifact.
    #>
    [CmdletBinding()]
    param($Run,$Jobs,$Artifact,$Workflow,[long]$ExpectedRunId,[long]$ExpectedArtifactId)
    if($ExpectedRunId -le 0 -or $ExpectedArtifactId -le 0 -or $Run.id -ne $ExpectedRunId -or $Artifact.id -ne $ExpectedArtifactId){throw 'internal-source.identity'}
    if($Run.repository.full_name -cne 'Water-Run/ClashSharp' -or $Run.head_repository.full_name -cne 'Water-Run/ClashSharp' -or
        $Run.event -cne 'workflow_dispatch' -or $Run.head_branch -cne 'main' -or $Run.head_sha -cnotmatch '^[0-9a-f]{40}\z' -or $Run.head_sha -ceq ('0'*40)){
        throw 'internal-source.mainline'
    }
    if($Run.status -cne 'completed' -or $Run.conclusion -cnotin @('success','failure') -or $Run.run_attempt -lt 1){throw 'internal-source.incomplete'}
    if($Run.path -cne '.github/workflows/internal-candidate.yml' -or $Workflow.path -cne $Run.path -or $Workflow.state -cne 'active' -or
        $Workflow.id -le 0 -or $Run.workflow_id -ne $Workflow.id){throw 'internal-source.workflow'}
    if($Jobs.total_count -ne 2 -or @($Jobs.jobs).Count -ne 2){throw 'internal-source.job-inventory'}
    $producer=@($Jobs.jobs|Where-Object name -CEQ 'Build internal self-signed candidate')
    if($producer.Count -ne 1 -or $producer[0].status -cne 'completed' -or $producer[0].conclusion -cne 'success' -or
        $producer[0].run_id -ne $ExpectedRunId -or $producer[0].head_sha -cne $Run.head_sha -or $producer[0].id -le 0){throw 'internal-source.producer-incomplete'}
    $required=@('Require mainline manual dispatch','Check out trusted controller','Verify exact product CI bytes','Verify actual controller composition',
        'Check out exact product source','Set up pinned source SDK','Restore exact source dependencies','Acquire pinned source inputs',
        'Sign and seal internal candidate','Upload public internal-candidate files')
    foreach($name in $required){
        $step=@($producer[0].steps|Where-Object name -CEQ $name)
        if($step.Count -ne 1 -or $step[0].status -cne 'completed' -or $step[0].conclusion -cne 'success'){throw ('internal-source.required-step:'+ $name)}
    }
    $expectedName='internal-signed-candidate-'+$ExpectedRunId+'-'+$Run.run_attempt
    if($Artifact.name -cne $expectedName -or $Artifact.expired -isnot [bool] -or $Artifact.expired -or
        $Artifact.size_in_bytes -le 0 -or $Artifact.size_in_bytes -gt 1073741824 -or $Artifact.digest -cnotmatch '^sha256:[0-9a-f]{64}\z' -or
        $Artifact.workflow_run.id -ne $ExpectedRunId -or $Artifact.workflow_run.head_sha -cne $Run.head_sha){throw 'internal-source.artifact-origin'}
    [pscustomobject][ordered]@{
        schemaVersion=1;kind='internal-candidate-producer-reference';repository='Water-Run/ClashSharp'
        runId=$ExpectedRunId;runAttempt=$Run.run_attempt;controllerCommit=$Run.head_sha;producerJobId=$producer[0].id
        artifactId=$ExpectedArtifactId;artifactName=$expectedName;artifactLength=$Artifact.size_in_bytes;artifactDigest=$Artifact.digest
        sourceWorkflowConclusion=$Run.conclusion;producerExecutionVerified=$true
        artifactBytesVerified=$false;acceptanceVerified=$false;publicReleaseAuthorized=$false;promotionAuthorized=$false
    }
}

Export-ModuleMember -Function Get-ClashSharpInternalCandidateSource
