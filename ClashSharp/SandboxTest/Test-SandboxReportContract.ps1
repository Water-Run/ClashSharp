[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $PSCommandPath
Import-Module -Name (Join-Path $scriptRoot "SandboxReportContract.psm1") -Force -ErrorAction Stop

$expectedScenario = "install-only"
$expectedRunId = "20260831T120000000Z"
$startedAt = "2026-08-31T12:00:00.0000000+00:00"
$finishedAt = "2026-08-31T12:00:01.0000000+00:00"
$validReport = [pscustomobject]@{
    schemaVersion = 1
    scenario = $expectedScenario
    runId = $expectedRunId
    status = "passed"
    startedAt = $startedAt
    finishedAt = $finishedAt
    environment = [pscustomobject]@{
        computerName = "sandbox"
        userName = "WDAGUtilityAccount"
        osBuild = "26100"
        architecture = "AMD64"
    }
    steps = @(
        [pscustomobject]@{
            name = "verify-package"
            status = "passed"
            startedAt = $startedAt
            finishedAt = $finishedAt
            durationMs = 1000
            error = $null
        }
    )
    checks = [pscustomobject]@{
        package = [pscustomobject]@{
            installed = $true
            name = "67dc1dc3-13fd-46c5-84f4-2932d94b566f"
            fullName = "67dc1dc3-13fd-46c5-84f4-2932d94b566f_1.2.3.4_x64__test"
            version = "1.2.3.4"
            source = "ClashSharp_1.2.3.4_x64.msix"
            dependencyCount = 0
        }
    }
    failure = $null
}

SandboxReportContract\Assert-SandboxScenarioReport `
    -Report $validReport `
    -ExpectedScenario $expectedScenario `
    -ExpectedRunId $expectedRunId

$invalidCases = [System.Collections.Generic.List[object]]::new()
foreach ($terminalStatus in @("skipped", "failed", "timedOut", "unknown", "")) {
    $candidate = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $candidate.status = $terminalStatus
    $invalidCases.Add([pscustomobject]@{
        Name = "terminal status '$terminalStatus'"
        Report = $candidate
    })
}

$scenarioMismatch = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$scenarioMismatch.scenario = "cleanup-uninstall"
$invalidCases.Add([pscustomobject]@{ Name = "scenario mismatch"; Report = $scenarioMismatch })

$runMismatch = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$runMismatch.runId = "another-run"
$invalidCases.Add([pscustomobject]@{ Name = "run mismatch"; Report = $runMismatch })

$emptySteps = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$emptySteps.steps = @()
$invalidCases.Add([pscustomobject]@{ Name = "empty steps"; Report = $emptySteps })

$failedStep = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$failedStep.steps[0].status = "failed"
$invalidCases.Add([pscustomobject]@{ Name = "failed step"; Report = $failedStep })

$missingRunId = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$missingRunId.PSObject.Properties.Remove("runId")
$invalidCases.Add([pscustomobject]@{ Name = "missing run ID"; Report = $missingRunId })

$contradictoryFailure = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$contradictoryFailure.failure = "unexpected failure"
$invalidCases.Add([pscustomobject]@{ Name = "contradictory failure"; Report = $contradictoryFailure })

$unknownProperty = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$unknownProperty | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
$invalidCases.Add([pscustomobject]@{ Name = "unknown property"; Report = $unknownProperty })

$invalidTimestamp = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$invalidTimestamp.finishedAt = "not-a-timestamp"
$invalidCases.Add([pscustomobject]@{ Name = "invalid timestamp"; Report = $invalidTimestamp })

$incompleteEnvironment = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$incompleteEnvironment.environment.osBuild = ""
$invalidCases.Add([pscustomobject]@{ Name = "incomplete environment"; Report = $incompleteEnvironment })

$scalarChecks = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$scalarChecks.checks = "not-evidence"
$invalidCases.Add([pscustomobject]@{ Name = "scalar checks"; Report = $scalarChecks })

$emptyChecks = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$emptyChecks.checks = [pscustomobject]@{}
$invalidCases.Add([pscustomobject]@{ Name = "empty checks"; Report = $emptyChecks })

$invalidDuration = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$invalidDuration.steps[0].durationMs = -1
$invalidCases.Add([pscustomobject]@{ Name = "invalid duration"; Report = $invalidDuration })

$invalidPackageIdentity = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$invalidPackageIdentity.checks.package.name = "another-package"
$invalidCases.Add([pscustomobject]@{ Name = "invalid package identity"; Report = $invalidPackageIdentity })

$emptyFailure = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$emptyFailure.failure = ""
$invalidCases.Add([pscustomobject]@{ Name = "non-null failure"; Report = $emptyFailure })

foreach ($invalidCase in $invalidCases) {
    $rejected = $false
    try {
        SandboxReportContract\Assert-SandboxScenarioReport `
            -Report $invalidCase.Report `
            -ExpectedScenario $expectedScenario `
            -ExpectedRunId $expectedRunId
    } catch {
        $rejected = $true
    }

    if (-not $rejected) {
        throw "Sandbox report contract accepted invalid evidence: $($invalidCase.Name)."
    }
}

$unsupportedScenario = $validReport | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$unsupportedScenario.scenario = "launch-no-proxy"
$unsupportedScenarioRejected = $false
try {
    SandboxReportContract\Assert-SandboxScenarioReport `
        -Report $unsupportedScenario `
        -ExpectedScenario $unsupportedScenario.scenario `
        -ExpectedRunId $expectedRunId
} catch {
    $unsupportedScenarioRejected = $true
}

if (-not $unsupportedScenarioRejected) {
    throw "Sandbox report contract accepted a scenario without a passing evidence contract."
}

Write-Host "Sandbox report contract tests passed."
