#Requires -Version 5.1
<#
.SYNOPSIS
    Exercises Sandbox input, isolation, payload, and terminal evidence without guest mutations.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SandboxReportContract.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'SandboxInputContract.psm1')
$assertions = 0
$packageName = '67dc1dc3-13fd-46c5-84f4-2932d94b566f'
$fullName = $packageName + '_1.0.0.0_x64__abcde12345xyz'
$validPlan = [ordered]@{
    schemaVersion = 2; scenario = 'install-only'; runId = ('a' * 32)
    sandboxId = '11111111-1111-1111-1111-111111111111'
    host = @{ computerName = 'TEST-HOST'; machineId = '22222222-2222-2222-2222-222222222222' }
    candidate = @{
        packageName = $packageName; fullName = $fullName; familyName = $packageName + '_abcde12345xyz'
        version = '1.0.0.0'; applicationId = 'App'; executable = 'ClashSharp.exe'
        executableSha256 = ('a' * 64); certificateThumbprint = ('A' * 40)
        files = @(
            @{ role = 'package'; path = 'ClashSharp_1.0.0.0_x64.msix'; length = 1; sha256 = ('a' * 64) },
            @{ role = 'certificate'; path = 'ClashSharp_TemporaryKey.cer'; length = 1; sha256 = ('a' * 64) },
            @{ role = 'dependency'; path = 'Dependencies/x64/Microsoft.WindowsAppRuntime.1.8.msix'; length = 1; sha256 = ('a' * 64) },
            @{ role = 'provenance'; path = 'payload-provenance.json'; length = 1; sha256 = ('a' * 64) })
    }
} | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$validEnvironment = [pscustomobject]@{
    computerName = 'TEST-GUEST'; machineId = '33333333-3333-3333-3333-333333333333'
    userName = 'WDAGUtilityAccount'; profile = 'C:\Users\WDAGUtilityAccount'
    productType = 'WinNT'; osBuild = '26100'; architecture = 'AMD64'
    scriptPath = 'C:\ClashSharpTestInput\Run-InSandbox.ps1'
}
$planHash = 'b' * 64

function Copy-SandboxFixture {
    <#
    .SYNOPSIS
        Copies a synthetic JSON fixture without sharing mutable properties.
    .DESCRIPTION
        Round-trips fixture data through JSON while retaining timestamp strings.
    .PARAMETER Value
        Fixture to copy.
    #>
    param([object]$Value)
    $options = @{}
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $options.DateKind = 'String' }
    return ($Value | ConvertTo-Json -Depth 15 | ConvertFrom-Json @options)
}

function New-SandboxReportFixture {
    <#
    .SYNOPSIS
        Builds complete synthetic executed evidence for one supported scenario.
    .DESCRIPTION
        Supplies exact step order, consistent timestamps, package and proxy checks, and cleanup evidence.
    .PARAMETER Scenario
        Report scenario name.
    #>
    param([string]$Scenario)
    $names = @('verify-isolation', 'verify-payload', 'copy-payload', 'import-certificate',
        'install-dependencies', 'install-package', 'verify-package')
    if ($Scenario -ceq 'launch-no-proxy') { $names += 'launch-package' }
    $names += @('cleanup-package', 'cleanup-certificate', 'cleanup-payload', 'verify-cleanup')
    $start = [DateTimeOffset]::Parse('2026-09-07T00:00:00+00:00')
    $stepList = @()
    for ($index = 0; $index -lt $names.Count; $index++) {
        $stepList += @{ name = $names[$index]; status = 'passed'
            startedAt = $start.AddSeconds($index).ToString('o')
            finishedAt = $start.AddSeconds($index + 1).ToString('o'); durationMs = 1000; error = $null }
    }
    $checks = [ordered]@{
        isolation = @{ inputReadOnly = $true; externalAdapterCount = 0; payloadFilesVerified = 4 }
        package = @{ installed = $true; name = $packageName; fullName = $fullName
            version = '1.0.0.0'; source = 'ClashSharp_1.0.0.0_x64.msix'; dependencyCount = 1; certificateStore = 'LocalMachine/TrustedPeople' }
        proxy = @{ beforeSha256 = ('c' * 64); afterSha256 = ('c' * 64); enabledBefore = $false; unchanged = $true }
        cleanup = @{ packageAbsent = $true; certificateAbsent = $true; stagingAbsent = $true
            ownedProcessesAbsent = $true; dependenciesRestored = $true; servicesUnchanged = $true; proxyUnchanged = $true }
    }
    if ($Scenario -ceq 'launch-no-proxy') {
        $checks.launch = @{ processId = 123; packageFullName = $fullName; executableSha256 = ('a' * 64)
            mainWindowObserved = $true; stabilizationMs = 30000; termination = 'owned-process' }
    }
    return (Copy-SandboxFixture ([ordered]@{ schemaVersion = 2; scenario = $Scenario
        runId = $validPlan.runId; sandboxId = $validPlan.sandboxId; planSha256 = $planHash
        status = 'passed'; startedAt = $start.ToString('o'); finishedAt = $start.AddSeconds($names.Count).ToString('o')
        environment = $validEnvironment; steps = $stepList; checks = $checks; failure = $null }))
}

function Assert-SandboxRejected {
    <#
    .SYNOPSIS
        Requires a contract call to reject a prepared invalid fixture.
    .DESCRIPTION
        Counts a negative assertion only when validation throws; fixture mutation occurs outside this helper.
    .PARAMETER Name
        Stable failure-case label.
    .PARAMETER Action
        Validation call only; fixture mutations happen before this call.
    #>
    param([string]$Name, [scriptblock]$Action)
    $rejected = $false
    try { $null = & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw "Accepted invalid Sandbox evidence: $Name" }
    $script:assertions++
}

foreach ($scenario in @('install-only', 'launch-no-proxy')) {
    $plan = Copy-SandboxFixture $validPlan
    $plan.scenario = $scenario
    Assert-SandboxPlan $plan
    Assert-SandboxGuestIdentity $plan $validEnvironment
    Assert-SandboxScenarioReport (New-SandboxReportFixture $scenario) $plan $planHash
    $assertions += 3
}
$reportCases = @(
    @{ Name = 'legacy schema'; Change = { param($r) $r.schemaVersion = 1 } },
    @{ Name = 'string schema'; Change = { param($r) $r.schemaVersion = '2' } },
    @{ Name = 'wrong scenario'; Change = { param($r) $r.scenario = 'cleanup-uninstall' } },
    @{ Name = 'wrong run'; Change = { param($r) $r.runId = 'b' * 32 } },
    @{ Name = 'wrong guest ID'; Change = { param($r) $r.sandboxId = '44444444-4444-4444-4444-444444444444' } },
    @{ Name = 'wrong plan'; Change = { param($r) $r.planSha256 = 'd' * 64 } },
    @{ Name = 'skipped'; Change = { param($r) $r.status = 'skipped' } },
    @{ Name = 'failed'; Change = { param($r) $r.status = 'failed' } },
    @{ Name = 'timed out'; Change = { param($r) $r.status = 'timedOut' } },
    @{ Name = 'empty failure'; Change = { param($r) $r.failure = '' } },
    @{ Name = 'failure'; Change = { param($r) $r.failure = 'failed' } },
    @{ Name = 'extra field'; Change = { param($r) $r | Add-Member unexpected $true } },
    @{ Name = 'missing field'; Change = { param($r) $r.PSObject.Properties.Remove('runId') } },
    @{ Name = 'bad timestamp'; Change = { param($r) $r.startedAt = 'invalid' } },
    @{ Name = 'reversed timestamps'; Change = { param($r) $r.finishedAt = '2020-01-01T00:00:00.0000000+00:00' } },
    @{ Name = 'overlong run'; Change = { param($r) $r.finishedAt = '2026-09-07T01:00:00.0000000+00:00' } },
    @{ Name = 'no steps'; Change = { param($r) $r.steps = @() } },
    @{ Name = 'missing step'; Change = { param($r) $r.steps = @($r.steps | Select-Object -Skip 1) } },
    @{ Name = 'duplicate step'; Change = { param($r) $r.steps[1].name = $r.steps[0].name } },
    @{ Name = 'failed step'; Change = { param($r) $r.steps[0].status = 'failed' } },
    @{ Name = 'step error'; Change = { param($r) $r.steps[0].error = '' } },
    @{ Name = 'duration mismatch'; Change = { param($r) $r.steps[0].durationMs = 1 } },
    @{ Name = 'negative duration'; Change = { param($r) $r.steps[0].durationMs = -1 } },
    @{ Name = 'duration type'; Change = { param($r) $r.steps[0].durationMs = '1000' } },
    @{ Name = 'step overlap'; Change = { param($r) $r.steps[1].startedAt = $r.steps[0].startedAt } },
    @{ Name = 'empty checks'; Change = { param($r) $r.checks = [pscustomobject]@{} } },
    @{ Name = 'scalar checks'; Change = { param($r) $r.checks = 'passed' } },
    @{ Name = 'writable inputs'; Change = { param($r) $r.checks.isolation.inputReadOnly = $false } },
    @{ Name = 'string isolation'; Change = { param($r) $r.checks.isolation.inputReadOnly = 'true' } },
    @{ Name = 'online guest'; Change = { param($r) $r.checks.isolation.externalAdapterCount = 1 } },
    @{ Name = 'string adapter count'; Change = { param($r) $r.checks.isolation.externalAdapterCount = '0' } },
    @{ Name = 'partial payload'; Change = { param($r) $r.checks.isolation.payloadFilesVerified = 3 } },
    @{ Name = 'uninstalled'; Change = { param($r) $r.checks.package.installed = $false } },
    @{ Name = 'wrong package'; Change = { param($r) $r.checks.package.name = 'other' } },
    @{ Name = 'wrong full name'; Change = { param($r) $r.checks.package.fullName += 'x' } },
    @{ Name = 'array full name'; Change = { param($r) $r.checks.package.fullName = @($r.checks.package.fullName) } },
    @{ Name = 'array status'; Change = { param($r) $r.status = @('passed') } },
    @{ Name = 'wrong version'; Change = { param($r) $r.checks.package.version = '1.2.3.4' } },
    @{ Name = 'wrong source'; Change = { param($r) $r.checks.package.source = '..\other.msix' } },
    @{ Name = 'missing dependency'; Change = { param($r) $r.checks.package.dependencyCount = 0 } },
    @{ Name = 'wrong trust scope'; Change = { param($r) $r.checks.package.certificateStore = 'CurrentUser/TrustedPeople' } },
    @{ Name = 'changed proxy'; Change = { param($r) $r.checks.proxy.afterSha256 = 'd' * 64 } },
    @{ Name = 'proxy pre-enabled'; Change = { param($r) $r.checks.proxy.enabledBefore = $true } },
    @{ Name = 'invalid proxy hash'; Change = { param($r) $r.checks.proxy.beforeSha256 = '' } },
    @{ Name = 'no process'; Change = { param($r) $r.checks.launch.processId = 0 } },
    @{ Name = 'unpackaged process'; Change = { param($r) $r.checks.launch.packageFullName = '' } },
    @{ Name = 'wrong executable'; Change = { param($r) $r.checks.launch.executableSha256 = 'd' * 64 } },
    @{ Name = 'no window'; Change = { param($r) $r.checks.launch.mainWindowObserved = $false } },
    @{ Name = 'no stabilization'; Change = { param($r) $r.checks.launch.stabilizationMs = 29999 } },
    @{ Name = 'claims graceful exit'; Change = { param($r) $r.checks.launch.termination = 'graceful' } }
)
$launchPlan = Copy-SandboxFixture $validPlan
$launchPlan.scenario = 'launch-no-proxy'
foreach ($case in $reportCases) {
    $report = New-SandboxReportFixture 'launch-no-proxy'
    & $case.Change $report
    Assert-SandboxRejected $case.Name { Assert-SandboxScenarioReport $report $launchPlan $planHash }
}
foreach ($name in @('packageAbsent', 'certificateAbsent', 'stagingAbsent', 'ownedProcessesAbsent',
        'dependenciesRestored', 'servicesUnchanged', 'proxyUnchanged')) {
    $report = New-SandboxReportFixture 'install-only'
    $report.checks.cleanup.$name = $false
    Assert-SandboxRejected "cleanup $name" { Assert-SandboxScenarioReport $report $validPlan $planHash }
}
$environmentCases = @(
    @{ Name = 'host computer'; Property = 'computerName'; Value = $validPlan.host.computerName },
    @{ Name = 'host machine'; Property = 'machineId'; Value = $validPlan.host.machineId },
    @{ Name = 'regular user'; Property = 'userName'; Value = 'Administrator' },
    @{ Name = 'host profile'; Property = 'profile'; Value = 'C:\Users\Administrator' },
    @{ Name = 'Server'; Property = 'productType'; Value = 'ServerNT' },
    @{ Name = 'Windows 10'; Property = 'osBuild'; Value = '19045' },
    @{ Name = 'wrong architecture'; Property = 'architecture'; Value = 'x86' },
    @{ Name = 'host script'; Property = 'scriptPath'; Value = $PSCommandPath }
)
foreach ($case in $environmentCases) {
    $environment = Copy-SandboxFixture $validEnvironment
    $environment.($case.Property) = $case.Value
    Assert-SandboxRejected $case.Name { Assert-SandboxGuestIdentity $validPlan $environment }
    $report = New-SandboxReportFixture 'install-only'
    $report.environment = $environment
    Assert-SandboxRejected $case.Name { Assert-SandboxScenarioReport $report $validPlan $planHash }
}
$planCases = @(
    @{ Name = 'unsupported recovery'; Change = { param($p) $p.scenario = 'startup-with-proxy-config' } },
    @{ Name = 'extra plan'; Change = { param($p) $p | Add-Member repoRoot 'C:\private' } },
    @{ Name = 'bad run ID'; Change = { param($p) $p.runId = '..\host' } },
    @{ Name = 'bad Sandbox ID'; Change = { param($p) $p.sandboxId = 'not-an-id' } },
    @{ Name = 'wrong application'; Change = { param($p) $p.candidate.applicationId = 'AnotherApp' } },
    @{ Name = 'array application'; Change = { param($p) $p.candidate.applicationId = @('App') } },
    @{ Name = 'array file'; Change = { param($p) $p.candidate.files[0].path = @($p.candidate.files[0].path) } },
    @{ Name = 'wrong executable path'; Change = { param($p) $p.candidate.executable = '..\host.exe' } },
    @{ Name = 'missing file'; Change = { param($p) $p.candidate.files = @($p.candidate.files | Select-Object -Skip 1) } },
    @{ Name = 'duplicate role'; Change = { param($p) $p.candidate.files[1] = $p.candidate.files[0] } },
    @{ Name = 'traversal'; Change = { param($p) $p.candidate.files[0].path = '..\ClashSharp_1.0.0.0_x64.msix' } },
    @{ Name = 'absolute file'; Change = { param($p) $p.candidate.files[0].path = 'C:\private.msix' } },
    @{ Name = 'uppercase digest'; Change = { param($p) $p.candidate.files[0].sha256 = 'A' * 64 } },
    @{ Name = 'zero file'; Change = { param($p) $p.candidate.files[0].length = 0 } },
    @{ Name = 'huge file'; Change = { param($p) $p.candidate.files[0].length = 1073741825 } },
    @{ Name = 'string length'; Change = { param($p) $p.candidate.files[0].length = '1' } }
)
foreach ($case in $planCases) {
    $plan = Copy-SandboxFixture $validPlan
    & $case.Change $plan
    Assert-SandboxRejected $case.Name { Assert-SandboxPlan $plan }
}
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('..\..\artifacts\validation\sandbox-contract-' + [Guid]::NewGuid().ToString('N'))))
$validationRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\artifacts\validation'))
if (-not $fixtureRoot.StartsWith($validationRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $fixtureRoot)) { throw 'Invalid fixture scope.' }
try {
    $plan = Copy-SandboxFixture $validPlan
    foreach ($file in $plan.candidate.files) {
        $path = Join-Path $fixtureRoot $file.path
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        [IO.File]::WriteAllBytes($path, [byte[]]@(42))
        $file.sha256 = Get-SandboxFileSha256 $path
    }
    Assert-SandboxPayload $plan $fixtureRoot
    $assertions++
    $primary = Join-Path $fixtureRoot $plan.candidate.files[0].path
    [IO.File]::WriteAllBytes($primary, [byte[]]@(43))
    Assert-SandboxRejected 'same-size modified payload' { Assert-SandboxPayload $plan $fixtureRoot }
    [IO.File]::WriteAllBytes($primary, [byte[]]@(42, 43))
    Assert-SandboxRejected 'changed length' { Assert-SandboxPayload $plan $fixtureRoot }
    Remove-Item -LiteralPath $primary
    Assert-SandboxRejected 'missing payload' { Assert-SandboxPayload $plan $fixtureRoot }
    $jsonPath = Join-Path $fixtureRoot 'fixture.json'
    [IO.File]::WriteAllText($jsonPath, '{"incomplete":')
    Assert-SandboxRejected 'partial JSON' { Read-SandboxJson $jsonPath }
    [IO.File]::WriteAllBytes($jsonPath, [byte[]]::new(1048577))
    Assert-SandboxRejected 'oversize JSON' { Read-SandboxJson $jsonPath }
} finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        $resolved = (Resolve-Path -LiteralPath $fixtureRoot).ProviderPath
        $entries = @((Get-Item -LiteralPath $resolved)) + @(Get-ChildItem -LiteralPath $resolved -Recurse -Force)
        if ($resolved -cne $fixtureRoot -or
            -not $resolved.StartsWith($validationRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
            @($entries |
                Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -ne 0) {
            throw 'Refused unsafe fixture cleanup.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
$guestPath = Join-Path $PSScriptRoot 'scripts\Run-InSandbox.ps1'
$hostRefused = $false
try { & $guestPath -ExpectedPlanSha256 ('a' * 64) }
catch { if ($_.Exception.Message -ceq 'sandbox.guest.required') { $hostRefused = $true } else { throw } }
if (-not $hostRefused) { throw 'Guest script admitted host execution.' }
$assertions++
Write-Host "Sandbox contracts passed: $assertions assertions; no package, certificate, service, or proxy mutations."
