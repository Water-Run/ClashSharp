Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'SandboxStartupEvidence.psm1') -Force
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SandboxInputContract.psm1')

function Assert-SandboxScenarioReport {
    <#
    .SYNOPSIS
        Requires complete executed evidence for the exact immutable Sandbox candidate.
    .DESCRIPTION
        Validates typed fields, guest isolation, ordered executed steps, timestamps, package and
        launch identity, unchanged proxy state, and all required cleanup postconditions.
    .PARAMETER Report
        Bounded terminal JSON report from the untrusted writable result mapping.
    .PARAMETER ExpectedPlan
        Original host plan; never read back from a writable guest mapping.
    .PARAMETER ExpectedPlanSha256
        Original input plan digest supplied to the guest entry point.
    #>
    param([Parameter(Mandatory)][object]$Report, [Parameter(Mandatory)][object]$ExpectedPlan,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedPlanSha256)
    Assert-SandboxPlan $ExpectedPlan
    Assert-SandboxObject $Report @('schemaVersion', 'scenario', 'runId', 'sandboxId', 'planSha256',
        'status', 'startedAt', 'finishedAt', 'environment', 'steps', 'checks', 'failure')
    Assert-SandboxStringFields $Report @('scenario', 'runId', 'sandboxId', 'planSha256', 'status', 'startedAt', 'finishedAt')
    if (($Report.schemaVersion -isnot [int] -and $Report.schemaVersion -isnot [long]) -or
        $Report.schemaVersion -ne 2 -or $Report.scenario -cne $ExpectedPlan.scenario -or
        $Report.runId -cne $ExpectedPlan.runId -or $Report.sandboxId -cne $ExpectedPlan.sandboxId -or
        $Report.planSha256 -cne $ExpectedPlanSha256 -or $Report.status -cne 'passed' -or
        $null -ne $Report.failure) { throw 'sandbox.report.identity_or_status' }
    Assert-SandboxGuestIdentity $ExpectedPlan $Report.environment
    $started = [DateTimeOffset]::ParseExact($Report.startedAt, 'o', [Globalization.CultureInfo]::InvariantCulture)
    $finished = [DateTimeOffset]::ParseExact($Report.finishedAt, 'o', [Globalization.CultureInfo]::InvariantCulture)
    if ($started.Offset -ne [TimeSpan]::Zero -or $finished.Offset -ne [TimeSpan]::Zero -or
        $finished -lt $started -or ($finished - $started).TotalMinutes -gt 30) { throw 'sandbox.report.timestamps' }
    $expectedSteps = @('verify-isolation', 'verify-payload', 'copy-payload', 'import-certificate',
        'install-dependencies', 'install-package', 'verify-package')
    $checkNames = @('isolation', 'package', 'proxy', 'cleanup')
    if ($ExpectedPlan.scenario -ceq 'launch-no-proxy') {
        $expectedSteps += 'launch-package'
        $checkNames += 'launch'
    }
    $expectedSteps += @('cleanup-package', 'cleanup-certificate', 'cleanup-payload', 'verify-cleanup')
    if ($Report.steps -isnot [array] -or $Report.steps.Count -ne $expectedSteps.Count) {
        throw 'sandbox.report.steps'
    }
    $previousFinish = $started
    for ($index = 0; $index -lt $expectedSteps.Count; $index++) {
        $step = $Report.steps[$index]
        Assert-SandboxObject $step @('name', 'status', 'startedAt', 'finishedAt', 'durationMs', 'error')
        Assert-SandboxStringFields $step @('name', 'status', 'startedAt', 'finishedAt')
        $stepStart = [DateTimeOffset]::ParseExact($step.startedAt, 'o', [Globalization.CultureInfo]::InvariantCulture)
        $stepFinish = [DateTimeOffset]::ParseExact($step.finishedAt, 'o', [Globalization.CultureInfo]::InvariantCulture)
        if ($step.name -cne $expectedSteps[$index] -or $step.status -cne 'passed' -or $null -ne $step.error -or
            $stepStart.Offset -ne [TimeSpan]::Zero -or $stepFinish.Offset -ne [TimeSpan]::Zero -or
            $stepStart -lt $previousFinish -or $stepFinish -lt $stepStart -or $stepFinish -gt $finished -or
            ($step.durationMs -isnot [int] -and $step.durationMs -isnot [long]) -or $step.durationMs -lt 0 -or
            [Math]::Abs(($stepFinish - $stepStart).TotalMilliseconds - $step.durationMs) -gt 2) {
            throw 'sandbox.report.step_evidence'
        }
        $previousFinish = $stepFinish
    }
    Assert-SandboxObject $Report.checks $checkNames
    $isolation = $Report.checks.isolation
    Assert-SandboxObject $isolation @('inputReadOnly', 'externalAdapterCount', 'payloadFilesVerified')
    if ($isolation.inputReadOnly -isnot [bool] -or -not $isolation.inputReadOnly -or
        ($isolation.externalAdapterCount -isnot [int] -and $isolation.externalAdapterCount -isnot [long]) -or
        $isolation.externalAdapterCount -ne 0 -or
        ($isolation.payloadFilesVerified -isnot [int] -and $isolation.payloadFilesVerified -isnot [long]) -or
        $isolation.payloadFilesVerified -ne 4) { throw 'sandbox.report.isolation' }
    $package = $Report.checks.package
    $candidate = $ExpectedPlan.candidate
    Assert-SandboxObject $package @('installed', 'name', 'fullName', 'version', 'source', 'dependencyCount', 'certificateStore')
    Assert-SandboxStringFields $package @('name', 'fullName', 'version', 'source', 'certificateStore')
    if ($package.installed -isnot [bool] -or -not $package.installed -or
        $package.name -cne $candidate.packageName -or $package.fullName -cne $candidate.fullName -or
        $package.version -cne $candidate.version -or $package.source -cne 'ClashSharp_1.0.0.0_x64.msix' -or
        ($package.dependencyCount -isnot [int] -and $package.dependencyCount -isnot [long]) -or
        $package.dependencyCount -ne 1 -or $package.certificateStore -cne 'LocalMachine/TrustedPeople') { throw 'sandbox.report.package' }
    $proxy = $Report.checks.proxy
    Assert-SandboxObject $proxy @('beforeSha256', 'afterSha256', 'enabledBefore', 'unchanged')
    Assert-SandboxStringFields $proxy @('beforeSha256', 'afterSha256')
    if ($proxy.beforeSha256 -isnot [string] -or $proxy.beforeSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $proxy.afterSha256 -cne $proxy.beforeSha256 -or $proxy.enabledBefore -isnot [bool] -or
        $proxy.enabledBefore -or $proxy.unchanged -isnot [bool] -or -not $proxy.unchanged) {
        throw 'sandbox.report.proxy'
    }
    $cleanupNames = @('packageAbsent', 'certificateAbsent', 'stagingAbsent', 'ownedProcessesAbsent',
        'dependenciesRestored', 'servicesUnchanged', 'proxyUnchanged')
    Assert-SandboxObject $Report.checks.cleanup $cleanupNames
    foreach ($name in $cleanupNames) {
        if ($Report.checks.cleanup.$name -isnot [bool] -or -not $Report.checks.cleanup.$name) {
            throw 'sandbox.report.cleanup'
        }
    }
    if ($ExpectedPlan.scenario -ceq 'launch-no-proxy') {
        $launch = $Report.checks.launch
        Assert-SandboxObject $launch @('processId', 'packageFullName', 'executableSha256',
            'mainWindowObserved', 'stabilizationMs', 'termination', 'startup')
        Assert-SandboxStringFields $launch @('packageFullName', 'executableSha256', 'termination')
        $launchStep = @($Report.steps | Where-Object { $_.name -ceq 'launch-package' })[0]
        Assert-SandboxStartupEvidence $launch.startup
        $launchStart = [DateTimeOffset]::ParseExact($launchStep.startedAt, 'o', [Globalization.CultureInfo]::InvariantCulture)
        $launchFinish = [DateTimeOffset]::ParseExact($launchStep.finishedAt, 'o', [Globalization.CultureInfo]::InvariantCulture)
        if ($launch.startup.startedAtUnixTime -lt $launchStart.ToUnixTimeSeconds() -or
            $launch.startup.startedAtUnixTime -gt $launchFinish.ToUnixTimeSeconds()) { throw 'sandbox.report.startup_time' }
        if (($launch.processId -isnot [int] -and $launch.processId -isnot [long]) -or $launch.processId -le 0 -or
            $launch.packageFullName -cne $candidate.fullName -or
            $launch.executableSha256 -cne $candidate.executableSha256 -or
            $launch.mainWindowObserved -isnot [bool] -or -not $launch.mainWindowObserved -or
            ($launch.stabilizationMs -isnot [int] -and $launch.stabilizationMs -isnot [long]) -or
            $launch.stabilizationMs -lt 30000 -or $launch.stabilizationMs -gt $launchStep.durationMs -or
            $launch.termination -cne 'owned-process') {
            throw 'sandbox.report.launch'
        }
    }
}

Export-ModuleMember -Function Assert-SandboxScenarioReport
