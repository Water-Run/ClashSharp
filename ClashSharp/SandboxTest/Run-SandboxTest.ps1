[CmdletBinding()]
param(
    [switch]$Launch,
    [string]$Configuration = "Debug",
    [string]$Scenario = "install-only",
    [string]$PayloadPath,
    [int]$TimeoutSeconds = 900
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $PSCommandPath
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..\..")
$manifestPath = Join-Path $scriptRoot "Cargo.toml"
$sandboxScript = Join-Path $scriptRoot "scripts\Run-InSandbox.ps1"
$sandboxRoot = Join-Path $scriptRoot ".sandbox"
$runId = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ")

function Resolve-ScenarioSelection {
    param([string]$Selection)

    $defaultScenarios = @(
        "install-only",
        "launch-no-proxy",
        "startup-with-proxy-config",
        "cleanup-uninstall"
    )
    $allScenarios = $defaultScenarios + @("real-proxy-optional")

    if ([string]::IsNullOrWhiteSpace($Selection)) {
        return @("install-only")
    }

    if ($Selection.Trim().Equals("all", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $defaultScenarios
    }

    $selected = $Selection.Split(",") |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($name in $selected) {
        if ($allScenarios -notcontains $name) {
            throw "Unknown SandboxTest scenario '$name'. Valid scenarios: $($allScenarios -join ', '), all."
        }
    }

    return @($selected)
}

function Resolve-PayloadSource {
    param([string]$ExplicitPayloadPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPayloadPath)) {
        $candidates += $ExplicitPayloadPath
    }

    $candidates += @(
        (Join-Path $repoRoot.Path "ClashSharp\Installer\target\release\payload"),
        (Join-Path $repoRoot.Path "ClashSharp\Installer\payload"),
        (Join-Path $repoRoot.Path "artifacts")
    )

    foreach ($candidate in $candidates) {
        if (-not (Test-Path $candidate)) {
            continue
        }

        $resolved = Resolve-Path $candidate
        $package = Get-ChildItem -LiteralPath $resolved.Path -File -Recurse |
            Where-Object { $_.Extension -in ".msix", ".msixbundle" -and $_.Name -like "ClashSharp_*" } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        $certificate = Get-ChildItem -LiteralPath $resolved.Path -File -Recurse -Filter "*.cer" |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if ($null -ne $package -and $null -ne $certificate) {
            return $resolved.Path
        }
    }

    throw "No usable Clash# MSIX payload was found. Build the installer or pass -PayloadPath."
}

function Copy-Payload {
    param(
        [string]$Source,
        [string]$Destination
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
        }
}

function Wait-SandboxReport {
    param(
        [string]$ReportPath,
        [int]$Timeout
    )

    $deadline = (Get-Date).AddSeconds($Timeout)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $ReportPath) {
            return $true
        }

        Start-Sleep -Seconds 2
    }

    return $false
}

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw "Rust cargo was not found on PATH. Install Rust before running SandboxTest."
}

$planArgs = @(
    "run",
    "--quiet",
    "--manifest-path",
    $manifestPath,
    "--",
    "plan",
    "--repo-root",
    $repoRoot.Path
)
& cargo @planArgs

$scenarios = Resolve-ScenarioSelection -Selection $Scenario
$payloadSource = Resolve-PayloadSource -ExplicitPayloadPath $PayloadPath
$preparedRuns = @()

foreach ($scenarioName in $scenarios) {
    $runDir = Join-Path $sandboxRoot "runs\$runId\$scenarioName"
    $sharedDir = Join-Path $runDir "shared"
    $sharedScriptsDir = Join-Path $sharedDir "scripts"
    $payloadTarget = Join-Path $sharedDir "payload"
    $reportsDir = Join-Path $sharedDir "reports"
    $sandboxScriptTarget = Join-Path $sharedScriptsDir "Run-InSandbox.ps1"
    $scenarioPlanPath = Join-Path $sharedDir "scenario-plan.json"
    $wsbPath = Join-Path $runDir "ClashSharpSandbox-$scenarioName.wsb"
    $reportPath = Join-Path $reportsDir "result.json"

    New-Item -ItemType Directory -Force -Path $sharedScriptsDir, $reportsDir | Out-Null
    Copy-Item -Force -Path $sandboxScript -Destination $sandboxScriptTarget
    Copy-Payload -Source $payloadSource -Destination $payloadTarget

    $scenarioPlan = [ordered]@{
        schemaVersion = 1
        generatedAt = (Get-Date).ToUniversalTime().ToString("o")
        configuration = $Configuration
        scenario = $scenarioName
        runId = $runId
        repoRoot = $repoRoot.Path
        payloadSource = $payloadSource
        paths = [ordered]@{
            root = "C:\Users\WDAGUtilityAccount\Desktop\ClashSharpSandbox"
            payloadPath = "C:\Users\WDAGUtilityAccount\Desktop\ClashSharpSandbox\payload"
            reportsPath = "C:\Users\WDAGUtilityAccount\Desktop\ClashSharpSandbox\reports"
        }
    }

    $scenarioPlan | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -Path $scenarioPlanPath

    $emitArgs = @(
        "run",
        "--quiet",
        "--manifest-path",
        $manifestPath,
        "--",
        "emit-wsb",
        "--repo-root",
        $repoRoot.Path,
        "--shared-dir",
        $sharedDir,
        "--wsb-file",
        $wsbPath
    )
    $emittedWsbPath = (& cargo @emitArgs | Select-Object -Last 1)

    $preparedRuns += [pscustomobject]@{
        Scenario = $scenarioName
        WsbPath = $emittedWsbPath
        SharedDir = $sharedDir
        PlanPath = $scenarioPlanPath
        ReportPath = $reportPath
    }
}

Write-Host ""
Write-Host "Prepared Sandbox scenario files:"
foreach ($run in $preparedRuns) {
    Write-Host "  Scenario: $($run.Scenario)"
    Write-Host "    WSB: $($run.WsbPath)"
    Write-Host "    Shared: $($run.SharedDir)"
    Write-Host "    Plan: $($run.PlanPath)"
}

if (-not $Launch) {
    Write-Host "Dry run complete. Re-run with -Launch to open Windows Sandbox."
    return
}

foreach ($run in $preparedRuns) {
    if (-not (Test-Path $run.WsbPath)) {
        throw "Expected WSB file was not created: $($run.WsbPath)"
    }

    Start-Process -FilePath $run.WsbPath
    Write-Host "Windows Sandbox launch requested for scenario '$($run.Scenario)'."

    if (Wait-SandboxReport -ReportPath $run.ReportPath -Timeout $TimeoutSeconds) {
        $report = Get-Content -Raw -Path $run.ReportPath | ConvertFrom-Json
        Write-Host "Scenario '$($run.Scenario)' completed with status '$($report.status)'."
        if ($report.status -eq "failed" -or $report.status -eq "timedOut") {
            throw "Scenario '$($run.Scenario)' failed. Report: $($run.ReportPath)"
        }
    } else {
        throw "Timed out waiting for scenario '$($run.Scenario)' report: $($run.ReportPath)"
    }
}
