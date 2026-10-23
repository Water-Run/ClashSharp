#Requires -Version 5.1
<#
.SYNOPSIS
    Exercises review-only classification and real Git discovery without running the product.
.PARAMETER ModulePath
    Scope module under review. Defaults to the adjacent module.
#>
[CmdletBinding()]
param([string] $ModulePath = (Join-Path $PSScriptRoot 'CiChangeScope.psm1'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module $ModulePath -Force
$script:checks = 0
function Assert-Condition([bool] $Value, [string] $Message) {
    $script:checks++
    if (-not $Value) { throw $Message }
}
$cases = @(
    @{ paths = @('docs/reviews/a.md'); product = $false },
    @{ paths = @('docs/reviews/a.md', 'docs/reviews/2026/one-two.md'); product = $false },
    @{ paths = @('README.md'); product = $true },
    @{ paths = @('docs/guides/agent.md'); product = $true },
    @{ paths = @('docs/reviews/a.ps1'); product = $true },
    @{ paths = @('docs/reviews/a.md', 'ClashSharp/App.xaml'); product = $true },
    @{ paths = @('docs/reviews/a.md', '.github/workflows/ci.yml'); product = $true },
    @{ paths = @('docs/reviews/a.md', 'eng/CiChangeScope.psm1'); product = $true },
    @{ paths = @('docs/reviews/../README.md'); product = $true },
    @{ paths = @('docs/reviews//a.md'); product = $true },
    @{ paths = @('docs/reviews/./a.md'); product = $true },
    @{ paths = @('docs/reviews/a.MD'); product = $true },
    @{ paths = @('Docs/reviews/a.md'); product = $true },
    @{ paths = @('docs\reviews\a.md'); product = $true },
    @{ paths = @('docs/reviews/a.md' + "`n" + 'source.cs'); product = $true },
    @{ paths = @('"docs/reviews/quoted.md"'); product = $true },
    @{ paths = @(); product = $true }
)
foreach ($case in $cases) {
    $result = Get-ClashSharpCiChangeScope -ChangedPath $case.paths -Complete $true
    Assert-Condition ($result.product -eq $case.product) ('Wrong path classification: ' + ($case.paths -join ', '))
}
$incomplete = Get-ClashSharpCiChangeScope -ChangedPath @('docs/reviews/a.md')
Assert-Condition ($incomplete.product -and -not $incomplete.complete) 'Incomplete discovery must require product CI.'

$workflow = Join-Path (Split-Path -Parent (Split-Path -Parent $ModulePath)) '.github/workflows/ci.yml'
$yaml = Get-Content -LiteralPath $workflow -Raw
Assert-Condition ($yaml.Contains('name: .NET build, format, and test') -and $yaml.Contains('name: Offline Installer package')) 'Required job names must remain stable.'
$unconditional = @('Check out repository', 'Verify CI scope contract (Windows PowerShell 5.1)',
    'Verify CI scope contract (PowerShell 7)', 'Determine product CI scope', 'Upload CI scope')
$steps = [regex]::Matches($yaml, '(?ms)^      - name: ([^\r\n]+)\r?\n(.*?)(?=^      - name: |\z)')
foreach ($step in $steps) {
    $name = $step.Groups[1].Value
    if ($name -in $unconditional) { continue }
    $expected = if ($name -eq 'Explain review-only CI') { "if: steps.scope.outputs.product == 'false'" }
        elseif ($name -eq 'Upload test results') { "if: always() && steps.scope.outputs.product != 'false'" }
        else { "if: steps.scope.outputs.product != 'false'" }
    Assert-Condition ($step.Groups[2].Value.Contains($expected)) ('Unsafe workflow condition on ' + $name)
}

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('clashsharp-ci-scope-' + [Guid]::NewGuid().ToString('N'))
$root = [IO.Path]::GetFullPath($fixture)
$git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
function Invoke-FixtureGit([string] $At, [string[]] $Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = @(& $git -C $At @Arguments 2>&1); $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) { throw 'Fixture Git operation failed.' }
    return $output
}
function Commit-Fixture([string] $At, [string] $Message) {
    $null = Invoke-FixtureGit $At @('add', '--all')
    $null = Invoke-FixtureGit $At @('commit', '--quiet', '-m', $Message)
    return [string](Invoke-FixtureGit $At @('rev-parse', 'HEAD'))
}
try {
    $null = [IO.Directory]::CreateDirectory($root)
    $author = Join-Path $root 'author with spaces'
    $origin = Join-Path $root 'origin.git'
    $clone = Join-Path $root 'shallow'
    $null = [IO.Directory]::CreateDirectory($author)
    $null = Invoke-FixtureGit $author @('init', '--quiet', '-b', 'main')
    $null = Invoke-FixtureGit $author @('config', 'user.name', 'CI Scope Fixture')
    $null = Invoke-FixtureGit $author @('config', 'user.email', 'scope@example.invalid')
    $null = Invoke-FixtureGit $author @('config', 'core.autocrlf', 'false')
    $null = Invoke-FixtureGit $author @('config', 'commit.gpgsign', 'false')
    $null = Invoke-FixtureGit $author @('config', 'core.hooksPath', (Join-Path $root 'no-hooks'))
    $null = [IO.Directory]::CreateDirectory((Join-Path $author 'docs/reviews'))
    [IO.File]::WriteAllText((Join-Path $author 'source.cs'), 'original product input')
    [IO.File]::WriteAllText((Join-Path $author 'docs/reviews/review.md'), 'first review')
    $base = Commit-Fixture $author 'baseline'
    [IO.File]::WriteAllText((Join-Path $author 'docs/reviews/review.md'), 'updated review')
    $review = Commit-Fixture $author 'review only'
    $scope = Get-ClashSharpCiRepositoryScope -Repository $author -BaseCommit $base -HeadCommit $review
    Assert-Condition (-not $scope.product -and $scope.complete) 'Real review-only diff must be complete.'
    $reporter = Join-Path (Split-Path -Parent $ModulePath) 'Get-CiChangeScope.ps1'
    $lightRecord = Join-Path $root 'review-scope.json'
    $lightOutputs = Join-Path $root 'review-github-output.txt'
    $null = & $reporter -Repository $author -BaseCommit $base -HeadCommit $review -RecordPath $lightRecord -GitHubOutputPath $lightOutputs
    $light = Get-Content -LiteralPath $lightRecord -Raw | ConvertFrom-Json
    Assert-Condition (-not $light.product -and -not $light.productTestsExecuted -and (Get-Content $lightOutputs -Raw).Contains('product=false')) 'Review-only output must identify that no product tests were executed.'
    $previousPath = $env:PATH
    try {
        $front = Join-Path $root 'git-front'
        $second = Join-Path $root 'git-second'
        $null = [IO.Directory]::CreateDirectory($front)
        $null = [IO.Directory]::CreateDirectory($second)
        # Keep the real executable first. Later candidates deliberately fail if selected;
        # batch wrappers are not equivalent to git.exe when cmd consumes revision carets.
        $shim = "@echo off`r`nexit /b 123`r`n"
        foreach ($directory in @($front, $second)) {
            [IO.File]::WriteAllText((Join-Path $directory 'git.cmd'), $shim, [Text.Encoding]::ASCII)
        }
        $env:PATH = (Split-Path -Parent $git) + [IO.Path]::PathSeparator + $front +
            [IO.Path]::PathSeparator + $second + [IO.Path]::PathSeparator + $previousPath
        Assert-Condition (@(Get-Command git -CommandType Application).Count -ge 2) 'Multi-Git fixture must expose multiple applications.'
        $scope = Get-ClashSharpCiRepositoryScope -Repository $author -BaseCommit $base -HeadCommit $review
        Assert-Condition (-not $scope.product -and $scope.complete) 'Discovery must use the first Git application instead of joining all paths.'
    } finally {
        $env:PATH = $previousPath
    }
    $scope = Get-ClashSharpCiRepositoryScope -Repository $author -BaseCommit $base -HeadCommit $base
    Assert-Condition ($scope.product -and $scope.reason -eq 'checkout-identity-unverified') 'Wrong checkout must require product CI.'
    $scope = Get-ClashSharpCiRepositoryScope -Repository $author -BaseCommit ('0' * 40) -HeadCommit $review -FetchBase
    Assert-Condition ($scope.product -and $scope.reason -eq 'unknown-commit') 'New-branch zero base must require product CI.'
    $scope = Get-ClashSharpCiRepositoryScope -Repository $author -BaseCommit ('f' * 40) -HeadCommit $review
    Assert-Condition ($scope.product -and $scope.reason -eq 'base-unavailable') 'Missing base must require product CI.'
    $scope = Get-ClashSharpCiRepositoryScope -Repository $author -BaseCommit ('f' * 40) -HeadCommit $review -FetchBase
    Assert-Condition ($scope.product -and $scope.reason -eq 'base-unavailable') 'A failed fetch must never turn into review-only scope.'

    # File transport forces a real shallow clone; cloning a local path would ignore --depth.
    $null = Invoke-FixtureGit $root @('clone', '--quiet', '--bare', $author, $origin)
    $originUri = ([Uri]::new([IO.Path]::GetFullPath($origin))).AbsoluteUri
    $null = Invoke-FixtureGit $root @('clone', '--quiet', '--depth=1', $originUri, $clone)
    $shallow = [string](Invoke-FixtureGit $clone @('rev-parse', '--is-shallow-repository'))
    Assert-Condition ($shallow -eq 'true') 'Fixture must really be shallow.'
    $scope = Get-ClashSharpCiRepositoryScope -Repository $clone -BaseCommit $base -HeadCommit $review -FetchBase
    Assert-Condition (-not $scope.product -and $scope.complete) 'A fetched exact shallow base must support review-only discovery.'

    $null = Invoke-FixtureGit $author @('mv', 'source.cs', 'docs/reviews/renamed.md')
    $renamed = Commit-Fixture $author 'rename product into reviews'
    $scope = Get-ClashSharpCiRepositoryScope -Repository $author -BaseCommit $review -HeadCommit $renamed
    Assert-Condition ($scope.product -and $scope.changedPaths -contains 'source.cs') 'Rename must retain the deleted product path.'
    Remove-Item -LiteralPath (Join-Path $author 'docs/reviews/review.md')
    $deleted = Commit-Fixture $author 'delete review'
    $scope = Get-ClashSharpCiRepositoryScope -Repository $author -BaseCommit $renamed -HeadCommit $deleted
    Assert-Condition (-not $scope.product) 'Deleted review Markdown alone may use the light path.'
    [IO.File]::WriteAllText((Join-Path $author 'source.cs'), 'new product input')
    $product = Commit-Fixture $author 'product change'
    $scope = Get-ClashSharpCiRepositoryScope -Repository $author -BaseCommit $deleted -HeadCommit $product
    Assert-Condition ($scope.product) 'A real product edit must require the full jobs.'
    $scope = Get-ClashSharpCiRepositoryScope -Repository $author -BaseCommit $product -HeadCommit $product
    Assert-Condition ($scope.product -and $scope.reason -eq 'empty-diff') 'An empty tree diff is not evidence to omit product checks.'

    # Exercise the reporter's actual output and its no-overwrite contract.
    $record = Join-Path $root 'scope.json'
    $outputs = Join-Path $root 'github-output.txt'
    $null = & $reporter -Repository $author -BaseCommit $deleted -HeadCommit $product -RecordPath $record -GitHubOutputPath $outputs
    $saved = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
    Assert-Condition ($saved.product -and (Get-Content $outputs -Raw).Contains('product=true')) 'Runner output must match saved product scope.'
    $before = (Get-FileHash -LiteralPath $record).Hash
    $refused = $false
    try { $null = & $reporter -Repository $author -BaseCommit $deleted -HeadCommit $product -RecordPath $record -GitHubOutputPath $outputs } catch { $refused = $true }
    Assert-Condition ($refused -and (Get-FileHash $record).Hash -eq $before) 'Reporter must preserve an existing record.'
    [pscustomobject]@{ success = $true; checks = $script:checks; realShallowFetch = $true; productExecuted = $false }
} finally {
    if (Test-Path -LiteralPath $root) {
        $resolved = (Get-Item -LiteralPath $root -Force).FullName
        $tempRoot = (Get-Item -LiteralPath ([IO.Path]::GetTempPath()) -Force).FullName.TrimEnd([char[]]@('\', '/'))
        $entries = @((Get-Item -LiteralPath $root -Force)) + @(Get-ChildItem -LiteralPath $root -Force -Recurse)
        if (-not $resolved.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolved) -cnotmatch '^clashsharp-ci-scope-[0-9a-f]{32}$' -or
            @($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) {
            throw 'Fixture cleanup refused an unexpected or reparse path.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
