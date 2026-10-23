Set-StrictMode -Version Latest

function Get-ClashSharpCiChangeScope {
    <#
    .SYNOPSIS
        Classifies a complete diff, allowing only ordinary review Markdown to omit product builds.
    .DESCRIPTION
        Unknown, empty, incomplete and non-review inputs require the full product jobs. This
        decision describes scope discovery only and does not certify product test execution.
    .PARAMETER ChangedPath
        Every added, modified and deleted path, with renames expanded into both paths.
    .PARAMETER Complete
        Whether discovery established the complete diff against the expected checkout.
    #>
    [CmdletBinding()]
    param([AllowEmptyCollection()][string[]] $ChangedPath = @(), [bool] $Complete = $false)

    $paths = @($ChangedPath)
    $reason = 'incomplete-diff'
    $product = $true
    if ($Complete -and $paths.Count -gt 0) {
        $reviewOnly = $true
        foreach ($path in $paths) {
            if ([string]::IsNullOrEmpty($path) -or
                $path -cnotmatch '^docs/reviews/[A-Za-z0-9][A-Za-z0-9._/-]*\.md\z' -or
                @($path.Split('/') | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count -gt 0) {
                $reviewOnly = $false
                break
            }
        }
        $product = -not $reviewOnly
        $reason = if ($reviewOnly) { 'review-markdown-only' } else { 'product-or-other-path' }
    } elseif ($Complete) {
        $reason = 'empty-diff'
    }
    [pscustomobject][ordered]@{
        schemaVersion = 1
        product = $product
        reason = $reason
        complete = $Complete
        changedPaths = $paths
        productTestsExecuted = $false
    }
}

function Invoke-ClashSharpCiGit {
    <#
    .SYNOPSIS
        Invokes the first Git application with separate arguments and captures its exit status.
    .DESCRIPTION
        Resolves one application even on runners with several Git installations. Native stderr
        and a missing executable cannot be mistaken for a successful empty diff. Fetch output
        is retained only in memory and is not copied into CI scope records.
    .PARAMETER Repository
        Checkout or isolated fixture directory supplied as Git's separate -C argument.
    .PARAMETER Argument
        Fixed Git operation and its already validated revision or path arguments.
    #>
    [CmdletBinding()]
    param([string] $Repository, [string[]] $Argument)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $executable = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        $output = @(& $executable -C $Repository @Argument 2>&1)
        $code = $LASTEXITCODE
    } catch {
        $output = @()
        $code = -1
    } finally {
        $ErrorActionPreference = $previous
    }
    [pscustomobject]@{ exitCode = $code; lines = @($output | ForEach-Object { [string] $_ }) }
}

function Get-ClashSharpCiRepositoryScope {
    <#
    .SYNOPSIS
        Establishes a complete Git tree diff, falling back to full product CI on any uncertainty.
    .DESCRIPTION
        Verifies checkout identity before discovery. A missing base can be fetched by its validated
        commit id; no ancestry walk or GitHub path-list truncation is used. Native Git errors are
        not interpreted as an empty diff. No repository or system configuration is changed.
    .PARAMETER Repository
        Checkout to inspect.
    .PARAMETER BaseCommit
        Push before or pull-request base SHA; absent and zero ids require product CI.
    .PARAMETER HeadCommit
        Expected checked-out commit, normally GITHUB_SHA.
    .PARAMETER FetchBase
        Allows fetching only a missing validated base commit from origin.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Repository,
        [AllowNull()][AllowEmptyString()][string] $BaseCommit,
        [AllowNull()][AllowEmptyString()][string] $HeadCommit,
        [switch] $FetchBase
    )

    $fallback = Get-ClashSharpCiChangeScope
    $fallback | Add-Member -NotePropertyName baseCommit -NotePropertyValue $BaseCommit
    $fallback | Add-Member -NotePropertyName headCommit -NotePropertyValue $HeadCommit
    if ($BaseCommit -cnotmatch '^[0-9a-f]{40}\z' -or $BaseCommit -ceq ('0' * 40) -or
        $HeadCommit -cnotmatch '^[0-9a-f]{40}\z' -or $HeadCommit -ceq ('0' * 40)) {
        $fallback.reason = 'unknown-commit'
        return $fallback
    }
    $head = Invoke-ClashSharpCiGit -Repository $Repository -Argument @('rev-parse', '--verify', 'HEAD')
    if ($head.exitCode -ne 0 -or $head.lines.Count -ne 1 -or $head.lines[0] -cne $HeadCommit) {
        $fallback.reason = 'checkout-identity-unverified'
        return $fallback
    }
    $base = Invoke-ClashSharpCiGit -Repository $Repository -Argument @('cat-file', '-e', ($BaseCommit + '^{commit}'))
    if ($base.exitCode -ne 0 -and $FetchBase) {
        $fetch = Invoke-ClashSharpCiGit -Repository $Repository -Argument @('fetch', '--no-tags', '--depth=1', 'origin', $BaseCommit)
        if ($fetch.exitCode -eq 0) {
            $base = Invoke-ClashSharpCiGit -Repository $Repository -Argument @('cat-file', '-e', ($BaseCommit + '^{commit}'))
        }
    }
    if ($base.exitCode -ne 0) {
        $fallback.reason = 'base-unavailable'
        return $fallback
    }
    $diff = Invoke-ClashSharpCiGit -Repository $Repository -Argument @(
        'diff', '--no-ext-diff', '--no-textconv', '--name-only', '--no-renames', $BaseCommit, $HeadCommit, '--')
    if ($diff.exitCode -ne 0) {
        $fallback.reason = 'diff-unavailable'
        return $fallback
    }
    $result = Get-ClashSharpCiChangeScope -ChangedPath $diff.lines -Complete $true
    $result | Add-Member -NotePropertyName baseCommit -NotePropertyValue $BaseCommit
    $result | Add-Member -NotePropertyName headCommit -NotePropertyValue $HeadCommit
    return $result
}

Export-ModuleMember -Function Get-ClashSharpCiChangeScope, Get-ClashSharpCiRepositoryScope
