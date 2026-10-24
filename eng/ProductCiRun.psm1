Set-StrictMode -Version Latest

function Get-ClashSharpRequiredProductCiSteps {
    <#
    .SYNOPSIS
        Returns the minimum executed-step policy for a complete product CI run.
    .DESCRIPTION
        Job success alone is insufficient because review-only runs retain the required job names.
        Each call creates a new policy; consumers cannot mutate a shared gate definition.
    #>
    [CmdletBinding()]
    param()
    $common = @('Check out repository', 'Verify CI scope contract (Windows PowerShell 5.1)',
        'Verify CI scope contract (PowerShell 7)', 'Determine product CI scope',
        'Set up pinned .NET SDK', 'Restore locked dependencies', 'Upload CI scope')
    [ordered]@{
        '.NET build, format, and test' = $common + @(
            'Verify PowerShell syntax (Windows PowerShell 5.1)', 'Verify PowerShell syntax (PowerShell 7)',
            'Verify .NET evidence contract (Windows PowerShell 5.1)', 'Verify .NET evidence contract (PowerShell 7)',
            'Verify installer vector icon bytes', 'Verify offline .NET package contract',
            'Verify bounded Authenticode signing contract',
            'Verify candidate archive transport contract',
            'Verify internal candidate workflow contract',
            'Verify installer build profiles', 'Verify third-party notices and final MSIX contract',
            'Verify pinned source-materials contract', 'Verify formatting',
            'Verify Sandbox report contract (Windows PowerShell 5.1)', 'Verify Sandbox report contract (PowerShell 7)',
            'Build Release', 'Verify green runtime component publishes', 'Run tests',
            'Run Installer Core tests', 'Run Installer presentation tests', 'Run Installer Windows adapter tests',
            'Verify actual .NET test results and save evidence', 'Enforce Installer Core coverage floor',
            'Upload test results')
        'Offline Installer package' = $common + @(
            'Acquire and stage pinned offline data', 'Build complete development Installer payload',
            'Upload development package')
    }
}

function Get-ClashSharpCiField {
    <#
    .SYNOPSIS
        Reads one required API field without silently treating an absent field as success.
    .DESCRIPTION
        Accepts deserialized API objects and rejects missing fields before interpreting their values.
    .PARAMETER Value
        Deserialized API object.
    .PARAMETER Name
        Fixed field name requested by the policy.
    #>
    [CmdletBinding()]
    param($Value, [string] $Name)
    if ($null -eq $Value -or $null -eq $Value.PSObject.Properties[$Name]) {
        throw ('product-ci.missing-field:' + $Name)
    }
    return $Value.PSObject.Properties[$Name].Value
}

function Get-ClashSharpProductCiRun {
    <#
    .SYNOPSIS
        Verifies product-job execution and artifact metadata for one exact mainline CI run.
    .DESCRIPTION
        Requires complete API lists, canonical identities, successful required steps and four
        distinct artifact roles. It does not authenticate caller-supplied JSON, verify downloaded
        bytes, certify TRX counters, or authorize signing or publication. The CLI obtains fresh
        API observations; later candidate gates must verify the recorded digests and raw reports.
    .PARAMETER Run
        GitHub actions/runs response obtained over the authenticated API.
    .PARAMETER Jobs
        Complete actions/runs/jobs response for this attempt.
    .PARAMETER Artifacts
        Complete actions/runs/artifacts response.
    .PARAMETER Workflow
        Current ci.yml workflow API identity.
    .PARAMETER ExpectedRunId
        Exact requested run id.
    .PARAMETER ExpectedCommit
        Exact source commit selected by the caller's trusted candidate policy.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Run,
        [Parameter(Mandatory)] $Jobs,
        [Parameter(Mandatory)] $Artifacts,
        [Parameter(Mandatory)] $Workflow,
        [Parameter(Mandatory)][long] $ExpectedRunId,
        [Parameter(Mandatory)][string] $ExpectedCommit
    )
    $repository = 'Water-Run/ClashSharp'
    if ($ExpectedRunId -le 0 -or $ExpectedCommit -cnotmatch '^[0-9a-f]{40}\z' -or
        $ExpectedCommit -ceq ('0' * 40) -or
        (Get-ClashSharpCiField $Run 'id') -ne $ExpectedRunId -or
        (Get-ClashSharpCiField $Run 'head_sha') -cne $ExpectedCommit) {
        throw 'product-ci.run-identity'
    }
    if ((Get-ClashSharpCiField (Get-ClashSharpCiField $Run 'repository') 'full_name') -cne $repository -or
        (Get-ClashSharpCiField (Get-ClashSharpCiField $Run 'head_repository') 'full_name') -cne $repository -or
        (Get-ClashSharpCiField $Run 'event') -cne 'push' -or
        (Get-ClashSharpCiField $Run 'head_branch') -cne 'main') {
        throw 'product-ci.mainline-required'
    }
    if ((Get-ClashSharpCiField $Run 'path') -cne '.github/workflows/ci.yml' -or
        (Get-ClashSharpCiField $Workflow 'path') -cne '.github/workflows/ci.yml' -or
        (Get-ClashSharpCiField $Workflow 'state') -cne 'active' -or
        (Get-ClashSharpCiField $Workflow 'id') -le 0 -or
        (Get-ClashSharpCiField $Run 'workflow_id') -le 0 -or
        (Get-ClashSharpCiField $Run 'workflow_id') -ne (Get-ClashSharpCiField $Workflow 'id')) {
        throw 'product-ci.workflow-identity'
    }
    if ((Get-ClashSharpCiField $Run 'status') -cne 'completed' -or
        (Get-ClashSharpCiField $Run 'conclusion') -cne 'success' -or
        (Get-ClashSharpCiField $Run 'run_attempt') -lt 1) {
        throw 'product-ci.run-incomplete'
    }
    $policy = Get-ClashSharpRequiredProductCiSteps
    $jobList = @(Get-ClashSharpCiField $Jobs 'jobs')
    if ((Get-ClashSharpCiField $Jobs 'total_count') -ne $jobList.Count -or $jobList.Count -ne $policy.Count) {
        throw 'product-ci.job-inventory'
    }
    $observedJobs = @(foreach ($entry in $policy.GetEnumerator()) {
        $matchingJobs = @($jobList | Where-Object { (Get-ClashSharpCiField $_ 'name') -ceq $entry.Key })
        if ($matchingJobs.Count -ne 1) { throw 'product-ci.job-identity' }
        $job = $matchingJobs[0]
        if ((Get-ClashSharpCiField $job 'status') -cne 'completed' -or
            (Get-ClashSharpCiField $job 'conclusion') -cne 'success' -or
            (Get-ClashSharpCiField $job 'run_id') -ne $ExpectedRunId -or
            (Get-ClashSharpCiField $job 'head_sha') -cne $ExpectedCommit -or
            (Get-ClashSharpCiField $job 'id') -le 0) { throw 'product-ci.job-incomplete' }
        $steps = @(Get-ClashSharpCiField $job 'steps')
        foreach ($name in $entry.Value) {
            $step = @($steps | Where-Object { (Get-ClashSharpCiField $_ 'name') -ceq $name })
            if ($step.Count -ne 1 -or (Get-ClashSharpCiField $step[0] 'status') -cne 'completed' -or
                (Get-ClashSharpCiField $step[0] 'conclusion') -cne 'success') {
                throw ('product-ci.required-step:' + $name)
            }
        }
        [pscustomobject]@{ name = $entry.Key; id = $job.id; requiredStepsExecuted = $entry.Value.Count }
    })
    if (@($observedJobs.id | Select-Object -Unique).Count -ne $observedJobs.Count) {
        throw 'product-ci.duplicate-job-id'
    }
    $artifactList = @(Get-ClashSharpCiField $Artifacts 'artifacts')
    $names = @('ci-scope-dotnet', 'ci-scope-installer', 'dotnet-test-results', 'ClashSharp-1.0.0-development-package')
    if ((Get-ClashSharpCiField $Artifacts 'total_count') -ne $artifactList.Count -or $artifactList.Count -ne $names.Count) {
        throw 'product-ci.artifact-inventory'
    }
    $observedArtifacts = @(foreach ($name in $names) {
        $matchingArtifacts = @($artifactList | Where-Object { (Get-ClashSharpCiField $_ 'name') -ceq $name })
        if ($matchingArtifacts.Count -ne 1) { throw 'product-ci.artifact-identity' }
        $artifact = $matchingArtifacts[0]
        $origin = Get-ClashSharpCiField $artifact 'workflow_run'
        if ((Get-ClashSharpCiField $artifact 'id') -le 0 -or
            (Get-ClashSharpCiField $artifact 'expired') -ne $false -or
            (Get-ClashSharpCiField $artifact 'size_in_bytes') -le 0 -or
            (Get-ClashSharpCiField $artifact 'digest') -cnotmatch '^sha256:[0-9a-f]{64}\z' -or
            (Get-ClashSharpCiField $origin 'id') -ne $ExpectedRunId -or
            (Get-ClashSharpCiField $origin 'head_sha') -cne $ExpectedCommit) {
            throw 'product-ci.artifact-origin-or-digest'
        }
        [pscustomobject]@{ name = $name; id = $artifact.id; length = $artifact.size_in_bytes; digest = $artifact.digest }
    })
    if (@($observedArtifacts.id | Select-Object -Unique).Count -ne $observedArtifacts.Count) {
        throw 'product-ci.duplicate-artifact-id'
    }
    [pscustomobject][ordered]@{
        schemaVersion = 1
        kind = 'product-ci-execution-metadata'
        repository = $repository
        sourceCommit = $ExpectedCommit
        runId = $ExpectedRunId
        runAttempt = $Run.run_attempt
        jobExecutionVerified = $true
        artifacts = $observedArtifacts
        jobs = $observedJobs
        artifactBytesVerified = $false
        rawTestReportsVerified = $false
        promotionAuthorized = $false
    }
}

Export-ModuleMember -Function Get-ClashSharpRequiredProductCiSteps, Get-ClashSharpProductCiRun
