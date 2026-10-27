#Requires -Version 5.1
<#
.SYNOPSIS
    Exercises the release-protection policy and the non-publishing workflow wiring.
.DESCRIPTION
    Uses API-shaped offline fixtures to reject absent reviewers, bots, widened deployment
    policies and coerced metadata. Does not contact GitHub, execute the product or publish.
.PARAMETER RepositoryRoot
    Repository checkout containing the exact policy and workflow under review.
#>
[CmdletBinding()]
param([string] $RepositoryRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $PSBoundParameters.ContainsKey('RepositoryRoot')) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
Import-Module (Join-Path $RepositoryRoot 'eng/ReleaseEnvironment.psm1') -Force
$script:checks = 0

function Assert-ReleaseCondition {
    <#
    .SYNOPSIS
        Requires one release-contract condition.
    .DESCRIPTION
        Counts the assertion and stops at the first false policy or wiring observation.
    .PARAMETER Value
        Condition evaluated from the actual policy output or maintained workflow text.
    .PARAMETER Message
        Non-sensitive failure description.
    #>
    [CmdletBinding()]
    param([bool] $Value, [string] $Message)
    $script:checks++
    if (-not $Value) { throw $Message }
}

function New-ReleaseFixture {
    <#
    .SYNOPSIS
        Creates fresh GitHub-shaped protection observations.
    .DESCRIPTION
        Returns an independent mutable fixture for each negative case; it is never live approval.
    #>
    [CmdletBinding()]
    param()
    @{
        environment = @{
            id = 1001; name = 'release'; url = 'https://api.github.com/repos/Water-Run/ClashSharp/environments/release'
            deployment_branch_policy = @{ protected_branches = $true; custom_branch_policies = $false }
            protection_rules = @(@{ id = 2001; type = 'required_reviewers'; prevent_self_review = $false
                reviewers = @(@{ type = 'User'; reviewer = @{ id = 3001; login = 'release-owner'; type = 'User' } }) })
        }
        branch = @{ name = 'main'; protected = $true; commit = @{ sha = ('a' * 40) } }
    } | ConvertTo-Json -Depth 15 | ConvertFrom-Json
}

$fixture = New-ReleaseFixture
$valid = Get-ClashSharpReleaseEnvironment $fixture.environment $fixture.branch
Assert-ReleaseCondition ($valid.environmentConfigurationVerified -and $valid.protectedMain) 'Configured protection was not observed.'
Assert-ReleaseCondition ($valid.reviewers.Count -eq 1 -and $valid.reviewers[0].type -ceq 'User') 'Human reviewer identity was lost.'
Assert-ReleaseCondition (-not $valid.publicationAuthorized -and -not $valid.deploymentApprovalVerified -and
    -not $valid.formalSigningVerified -and -not $valid.manualProductAcceptanceVerified) 'Configuration cannot grant publication.'
Assert-ReleaseCondition (-not $valid.preventSelfReview) 'Single-owner review remains representable without inventing another reviewer.'
Assert-ReleaseCondition (-not $valid.administratorBypassPolicyVerified) 'Absent bypass evidence must remain unverified.'
$fixture = New-ReleaseFixture
$fixture.environment.protection_rules[0].prevent_self_review = $true
$fixture.environment.protection_rules[0].reviewers = @(@{ type = 'Team'; reviewer = @{ id = 4001; slug = 'release-team' } })
$team = Get-ClashSharpReleaseEnvironment $fixture.environment $fixture.branch
Assert-ReleaseCondition ($team.preventSelfReview -and $team.reviewers[0].type -ceq 'Team') 'Configured team review must be retained.'

$mutations = @(
    { param($f) $f.environment.id = '1001' },
    { param($f) $f.environment.id = $true },
    { param($f) $f.environment.id = 0 },
    { param($f) $f.environment.name = 'Release' },
    { param($f) $f.environment.url = 'https://api.github.com/repos/other/ClashSharp/environments/release' },
    { param($f) $f.branch.name = 'feature' },
    { param($f) $f.branch.protected = $false },
    { param($f) $f.branch.protected = 'true' },
    { param($f) $f.branch.commit.sha = ('0' * 40) },
    { param($f) $f.branch.commit.sha = ('A' * 40) },
    { param($f) $f.environment.deployment_branch_policy = $null },
    { param($f) $f.environment.deployment_branch_policy.protected_branches = $false },
    { param($f) $f.environment.deployment_branch_policy.protected_branches = 'true' },
    { param($f) $f.environment.deployment_branch_policy.custom_branch_policies = $true },
    { param($f) $f.environment.protection_rules = @() },
    { param($f) $f.environment.protection_rules[0].type = 'wait_timer' },
    { param($f) $f.environment.protection_rules += $f.environment.protection_rules[0] },
    { param($f) $f.environment.protection_rules[0].prevent_self_review = 'false' },
    { param($f) $f.environment.protection_rules[0].reviewers = @() },
    { param($f) $f.environment.protection_rules[0].reviewers[0].reviewer.type = 'Bot' },
    { param($f) $f.environment.protection_rules[0].reviewers[0].reviewer.login = 'automation[bot]' },
    { param($f) $f.environment.protection_rules[0].reviewers[0].reviewer.id = '3001' },
    { param($f) $f.environment.protection_rules[0].reviewers[0].type = 'App' },
    { param($f) $f.environment.protection_rules[0].reviewers += $f.environment.protection_rules[0].reviewers[0] }
)
foreach ($mutation in $mutations) {
    $fixture = New-ReleaseFixture
    & $mutation $fixture
    $refused = $false
    try { $null = Get-ClashSharpReleaseEnvironment $fixture.environment $fixture.branch }
    catch { $refused = $_.Exception.Message -cmatch '^release\.environment\.' }
    Assert-ReleaseCondition $refused 'An incomplete, widened or non-human protection fixture was accepted.'
}

$workflow = [IO.File]::ReadAllText((Join-Path $RepositoryRoot '.github/workflows/release-preflight.yml'))
Assert-ReleaseCondition ($workflow.Contains('workflow_dispatch:') -and -not ($workflow -match '(?m)^  (push|pull_request|schedule|workflow_run):')) 'Release preflight must be manually dispatched.'
Assert-ReleaseCondition (-not ($workflow -match '(?m)^\s+environment:')) 'Preflight must not implicitly create or enter an environment.'
Assert-ReleaseCondition ($workflow.Contains('contents: read') -and $workflow.Contains('actions: read') -and
    -not ($workflow -match '(?m)^\s+(contents|actions|deployments|id-token): write')) 'Preflight cannot acquire publishing or signing permissions.'
Assert-ReleaseCondition ($workflow.Contains("`$env:GITHUB_REF -cne 'refs/heads/main'") -and
    $workflow.Contains("`$env:GITHUB_REPOSITORY -cne 'Water-Run/ClashSharp'")) 'Manual preflight must enforce the repository and main ref.'
Assert-ReleaseCondition ($workflow.Contains('./Tools/Get-ReleaseEnvironment.ps1') -and
    $workflow.Contains('if: always()') -and $workflow.Contains('artifacts/release-preflight/environment.json')) 'Actual observations and negative receipts must be retained.'
Assert-ReleaseCondition (-not ($workflow -match '(?i)gh release|signtool|New-InternalCandidate|secrets\.CLASHSHARP')) 'Preflight cannot sign or publish a candidate.'
$cli = [IO.File]::ReadAllText((Join-Path $RepositoryRoot 'Tools/Get-ReleaseEnvironment.ps1'))
Assert-ReleaseCondition ($cli.Contains('Read-ReleaseProtection') -and
    -not ($cli -match '(?i)--method\s+(POST|PUT|PATCH|DELETE)|gh auth token|GITHUB_OUTPUT')) 'Collector must only read protection and not produce job authorization.'
[pscustomobject]@{ checks = $script:checks; negativeFixtures = $mutations.Count; status = 'passed'; publicationAuthorized = $false }
