#Requires -Version 5.1
Set-StrictMode -Version Latest

function Test-ClashSharpReleaseInteger {
    <#
    .SYNOPSIS
        Tests a positive integral GitHub observation without coercing strings or booleans.
    .DESCRIPTION
        API identifiers must retain their JSON integer representation at this policy boundary.
    .PARAMETER Value
        Observed identifier to classify.
    #>
    [CmdletBinding()]
    param([AllowNull()] $Value)
    return ($Value -is [int] -or $Value -is [long]) -and $Value -gt 0
}

function Get-ClashSharpReleaseEnvironment {
    <#
    .SYNOPSIS
        Checks an existing release environment and a protected main branch.
    .DESCRIPTION
        Canonicalizes independently read GitHub observations. Required reviewers and protected
        branches are necessary configuration, not an approval receipt or signing/publication
        authority. This policy never creates an environment, approves a job or publishes assets.
    .PARAMETER EnvironmentObservation
        Actual release environment observation from this repository's API endpoint.
    .PARAMETER MainBranchObservation
        Actual main-branch observation from the same repository.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $EnvironmentObservation, [Parameter(Mandatory)] $MainBranchObservation)
    try {
        $environment = $EnvironmentObservation
        $branch = $MainBranchObservation
        if (-not (Test-ClashSharpReleaseInteger $environment.id) -or $environment.name -cne 'release' -or
            $environment.url -cne 'https://api.github.com/repos/Water-Run/ClashSharp/environments/release') {
            throw 'release.environment.identity'
        }
        if ($branch.name -cne 'main' -or $branch.protected -isnot [bool] -or -not $branch.protected -or
            $branch.commit.sha -isnot [string] -or $branch.commit.sha -cnotmatch '^[0-9a-f]{40}\z' -or
            $branch.commit.sha -ceq ('0' * 40)) { throw 'release.environment.protected-main-required' }

        $policy = $environment.deployment_branch_policy
        if ($null -eq $policy -or $policy.protected_branches -isnot [bool] -or -not $policy.protected_branches -or
            $policy.custom_branch_policies -isnot [bool] -or $policy.custom_branch_policies) {
            throw 'release.environment.protected-branches-required'
        }
        $reviewRules = @($environment.protection_rules | Where-Object type -CEQ 'required_reviewers')
        if ($reviewRules.Count -ne 1 -or -not (Test-ClashSharpReleaseInteger $reviewRules[0].id) -or
            $reviewRules[0].prevent_self_review -isnot [bool]) { throw 'release.environment.reviewer-rule-required' }
        $reviewers = @($reviewRules[0].reviewers)
        if ($reviewers.Count -lt 1 -or $reviewers.Count -gt 100) { throw 'release.environment.reviewers-required' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $canonicalReviewers = @(foreach ($item in $reviewers) {
            if ($item.type -cnotin @('User', 'Team') -or -not (Test-ClashSharpReleaseInteger $item.reviewer.id)) {
                throw 'release.environment.reviewer-identity'
            }
            if ($item.type -ceq 'User') {
                if ($item.reviewer.type -cne 'User') { throw 'release.environment.human-reviewer-required' }
                $name = $item.reviewer.login
                if ($name -isnot [string] -or $name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,38}\z') {
                    throw 'release.environment.reviewer-name'
                }
            } else {
                $name = $item.reviewer.slug
                if ($name -isnot [string] -or $name -cnotmatch '^[a-z0-9][a-z0-9-]{0,99}\z') {
                    throw 'release.environment.reviewer-name'
                }
            }
            if (-not $seen.Add($item.type + ':' + [string]$item.reviewer.id)) { throw 'release.environment.duplicate-reviewer' }
            [pscustomobject][ordered]@{ type = [string]$item.type; id = [long]$item.reviewer.id; name = [string]$name }
        })
        [pscustomobject][ordered]@{
            schemaVersion = 1
            kind = 'clashsharp-release-environment-observation'
            repository = 'Water-Run/ClashSharp'
            scope = 'existing-release-protection-read-only'
            environmentId = [long]$environment.id
            environmentName = 'release'
            mainCommit = [string]$branch.commit.sha
            protectedMain = $true
            protectedBranchesOnly = $true
            reviewerRuleId = [long]$reviewRules[0].id
            preventSelfReview = [bool]$reviewRules[0].prevent_self_review
            reviewers = @($canonicalReviewers | Sort-Object type, id)
            environmentConfigurationVerified = $true
            deploymentApprovalVerified = $false
            administratorBypassPolicyVerified = $false
            formalSigningVerified = $false
            manualProductAcceptanceVerified = $false
            publicationAuthorized = $false
        }
    } catch {
        if ($_.Exception.Message -cmatch '^release\.environment\.[a-z-]+\z') { throw }
        throw 'release.environment.observation-invalid'
    }
}

Export-ModuleMember -Function Get-ClashSharpReleaseEnvironment
