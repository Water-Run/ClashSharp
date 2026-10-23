#Requires -Version 5.1
<#
.SYNOPSIS
    Tests the product-CI metadata gate, including successful review-only impostors.
.DESCRIPTION
    Uses API-shaped offline fixtures. Does not contact GitHub or execute the product. The positive
    fixture is also compared with the maintained workflow so required product gates cannot drift.
.PARAMETER ModulePath
    Product-CI module under review.
#>
[CmdletBinding()]
param([string] $ModulePath = (Join-Path $PSScriptRoot 'ProductCiRun.psm1'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module $ModulePath -Force
$script:checks = 0
function Assert-Condition([bool] $Condition, [string] $Message) {
    $script:checks++
    if (-not $Condition) { throw $Message }
}
function New-Fixture() {
    $commit = 'a' * 40
    $id = 12345678901L
    $policy = Get-ClashSharpRequiredProductCiSteps
    $jobId = 10L
    $jobs = @(foreach ($entry in $policy.GetEnumerator()) {
        $jobId++
        [ordered]@{ id = $jobId; name = $entry.Key; run_id = $id; head_sha = $commit;
            status = 'completed'; conclusion = 'success'; steps = @($entry.Value | ForEach-Object {
                [ordered]@{ name = $_; status = 'completed'; conclusion = 'success' }
            }) }
    })
    $artifactId = 20L
    $artifacts = @(foreach ($name in @('ci-scope-dotnet', 'ci-scope-installer', 'dotnet-test-results', 'ClashSharp-1.0.0-development-package')) {
        $artifactId++
        [ordered]@{ id = $artifactId; name = $name; expired = $false; size_in_bytes = 123;
            digest = 'sha256:' + ('b' * 64); workflow_run = @{ id = $id; head_sha = $commit } }
    })
    [ordered]@{
        Run = @{ id = $id; head_sha = $commit; event = 'push'; head_branch = 'main'; path = '.github/workflows/ci.yml';
            workflow_id = 42; run_attempt = 1; status = 'completed'; conclusion = 'success';
            repository = @{ full_name = 'Water-Run/ClashSharp' }; head_repository = @{ full_name = 'Water-Run/ClashSharp' } }
        Jobs = @{ total_count = 2; jobs = $jobs }
        Artifacts = @{ total_count = 4; artifacts = $artifacts }
        Workflow = @{ id = 42; path = '.github/workflows/ci.yml'; state = 'active' }
    } | ConvertTo-Json -Depth 20 | ConvertFrom-Json
}
function Invoke-Fixture($Value) {
    Get-ClashSharpProductCiRun -Run $Value.Run -Jobs $Value.Jobs -Artifacts $Value.Artifacts -Workflow $Value.Workflow `
        -ExpectedRunId 12345678901 -ExpectedCommit ('a' * 40)
}
function Assert-Rejected([scriptblock] $Mutation, [string] $Code) {
    $fixture = New-Fixture
    & $Mutation $fixture
    $refused = $false
    try { $null = Invoke-Fixture $fixture } catch {
        if (-not $_.Exception.Message.StartsWith($Code, [StringComparison]::Ordinal)) { throw }
        $refused = $true
    }
    Assert-Condition $refused ('Expected rejection: ' + $Code)
}
$fixture = New-Fixture
$result = Invoke-Fixture $fixture
Assert-Condition ($result.jobExecutionVerified -and $result.jobs.Count -eq 2 -and $result.artifacts.Count -eq 4) 'Complete execution metadata must be accepted.'
Assert-Condition (-not $result.artifactBytesVerified -and -not $result.rawTestReportsVerified -and -not $result.promotionAuthorized) 'Metadata must not certify bytes, test counters or promotion.'
Assert-Rejected { param($f) $f.Run.id++ } 'product-ci.run-identity'
Assert-Rejected { param($f) $f.Run.head_sha = 'c' * 40 } 'product-ci.run-identity'
Assert-Rejected { param($f) $f.Run.head_repository.full_name = 'fork/ClashSharp' } 'product-ci.mainline-required'
Assert-Rejected { param($f) $f.Run.repository.full_name = 'other/ClashSharp' } 'product-ci.mainline-required'
Assert-Rejected { param($f) $f.Run.event = 'pull_request' } 'product-ci.mainline-required'
Assert-Rejected { param($f) $f.Run.head_branch = 'unreviewed' } 'product-ci.mainline-required'
Assert-Rejected { param($f) $f.Run.workflow_id++ } 'product-ci.workflow-identity'
Assert-Rejected { param($f) $f.Workflow.id = 0; $f.Run.workflow_id = 0 } 'product-ci.workflow-identity'
Assert-Rejected { param($f) $f.Workflow.state = 'disabled_manually' } 'product-ci.workflow-identity'
Assert-Rejected { param($f) $f.Run.status = 'in_progress' } 'product-ci.run-incomplete'
Assert-Rejected { param($f) $f.Run.conclusion = 'failure' } 'product-ci.run-incomplete'
Assert-Rejected { param($f) $f.Jobs.total_count = 3 } 'product-ci.job-inventory'
Assert-Rejected { param($f) $f.Jobs.jobs[1].name = $f.Jobs.jobs[0].name } 'product-ci.job-identity'
Assert-Rejected { param($f) $f.Jobs.jobs[1].id = $f.Jobs.jobs[0].id } 'product-ci.duplicate-job-id'
Assert-Rejected { param($f) $f.Jobs.jobs[0].conclusion = 'cancelled' } 'product-ci.job-incomplete'
Assert-Rejected { param($f) $f.Jobs.jobs[0].head_sha = 'c' * 40 } 'product-ci.job-incomplete'
Assert-Rejected { param($f) $f.Jobs.jobs[0].steps[0].conclusion = 'skipped' } 'product-ci.required-step:'
Assert-Rejected { param($f) $f.Jobs.jobs[0].steps += $f.Jobs.jobs[0].steps[0] } 'product-ci.required-step:'
# Even green jobs and plausible artifact names cannot replace actually executed product steps.
Assert-Rejected { param($f) foreach($job in $f.Jobs.jobs){foreach($step in $job.steps){if($step.name -in @('Run tests','Build complete development Installer payload')){$step.conclusion='skipped'}}} } 'product-ci.required-step:'
Assert-Rejected { param($f) $f.Artifacts.total_count = 5 } 'product-ci.artifact-inventory'
Assert-Rejected { param($f) $f.Artifacts.artifacts[2].name = $f.Artifacts.artifacts[0].name } 'product-ci.artifact-identity'
Assert-Rejected { param($f) $f.Artifacts.artifacts[0].expired = $true } 'product-ci.artifact-origin-or-digest'
Assert-Rejected { param($f) $f.Artifacts.artifacts[0].digest = 'sha256:arbitrary' } 'product-ci.artifact-origin-or-digest'
Assert-Rejected { param($f) $f.Artifacts.artifacts[0].size_in_bytes = 0 } 'product-ci.artifact-origin-or-digest'
Assert-Rejected { param($f) $f.Artifacts.artifacts[0].workflow_run.head_sha = 'c' * 40 } 'product-ci.artifact-origin-or-digest'
Assert-Rejected { param($f) $f.Artifacts.artifacts[0].workflow_run.id++ } 'product-ci.artifact-origin-or-digest'
Assert-Rejected { param($f) $f.Artifacts.artifacts[1].id = $f.Artifacts.artifacts[0].id } 'product-ci.duplicate-artifact-id'

$workflowPath = Join-Path (Split-Path -Parent (Split-Path -Parent $ModulePath)) '.github/workflows/ci.yml'
$yaml = Get-Content -LiteralPath $workflowPath -Raw
foreach ($entry in (Get-ClashSharpRequiredProductCiSteps).GetEnumerator()) {
    Assert-Condition ($yaml.Contains('name: ' + $entry.Key)) 'Required job missing from workflow.'
    foreach ($step in $entry.Value) {
        Assert-Condition ($yaml.Contains('- name: ' + $step)) ('Required step missing from workflow: ' + $step)
    }
}
# A caller cannot weaken the next invocation by editing a previously returned policy.
$mutable = Get-ClashSharpRequiredProductCiSteps
$mutable.Clear()
Assert-Condition ((Get-ClashSharpRequiredProductCiSteps).Count -eq 2) 'Required policy must not be shared mutable state.'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $ModulePath)
$audit = Get-Content (Join-Path $repositoryRoot '.github/workflows/product-ci-input.yml') -Raw
Assert-Condition ($audit.Contains("if: github.ref == 'refs/heads/main'")) 'Input auditing must use mainline workflow code.'
Assert-Condition ($audit.Contains('contents: read') -and $audit.Contains('actions: read')) 'Input auditing needs only read permissions.'
Assert-Condition ($audit -notmatch '(?m)^\s+(contents|actions|id-token|attestations):\s*write') 'Input auditing must not request publication or signing permissions.'
Assert-Condition ($audit.Contains('PRODUCT_RUN_ID:') -and $audit.Contains('PRODUCT_SOURCE_COMMIT:') -and $audit.Contains('-RunId $env:PRODUCT_RUN_ID')) 'Dispatch inputs must be separate environment values, not shell code.'
Assert-Condition ($audit.Contains('./Tools/Get-ProductCiRun.ps1') -and $audit.Contains('product-ci-execution-metadata.json')) 'The audit workflow must invoke the actual verifier and upload its descriptor.'
$cli = Get-Content (Join-Path $repositoryRoot 'Tools/Get-ProductCiRun.ps1') -Raw
Assert-Condition ($cli.Contains('api --hostname github.com')) 'Authenticated metadata requests must pin the GitHub host.'
[pscustomobject]@{ success = $true; checks = $script:checks; networkUsed = $false; productExecuted = $false }
