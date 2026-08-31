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
$sandboxScript = Join-Path $scriptRoot "scripts\Run-InSandbox.ps1"
$sandboxRoot = Join-Path $scriptRoot ".sandbox"
$runId = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ")

<#
.SYNOPSIS
Expands and validates the requested Windows Sandbox scenario selection.
.DESCRIPTION
Maps an empty selection to install-only, expands all to the default matrix, and rejects unknown
scenario names before any sandbox files are created.
.PARAMETER Selection
Comma-separated scenario names or the all keyword.
#>
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

<#
.SYNOPSIS
Finds the first candidate tree containing both a ClashSharp package and certificate.
.DESCRIPTION
Prefers an explicit input and then checks fixed generated locations, returning only a resolved
directory that contains a matching MSIX or bundle and a certificate.
.PARAMETER ExplicitPayloadPath
Optional caller-selected payload root to inspect before the fixed candidates.
#>
function Resolve-PayloadSource {
    param([string]$ExplicitPayloadPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPayloadPath)) {
        $candidates += $ExplicitPayloadPath
    }

    $candidates += @(
        (Join-Path $repoRoot.Path "artifacts\installer\release\payload"),
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

<#
.SYNOPSIS
Copies a selected payload tree into one isolated sandbox shared directory.
.DESCRIPTION
Creates the destination and recursively copies every top-level payload entry for the current run.
.PARAMETER Source
Resolved payload directory to copy.
.PARAMETER Destination
Isolated run directory that receives the payload snapshot.
#>
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

<#
.SYNOPSIS
Waits for the sandbox guest to publish its scenario report.
.DESCRIPTION
Polls the exact report path until it appears or the caller-supplied timeout expires.
.PARAMETER ReportPath
Expected result JSON path in the mapped reports directory.
.PARAMETER Timeout
Maximum number of seconds to wait.
#>
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

<#
.SYNOPSIS
Writes one escaped Windows Sandbox configuration for an isolated scenario run.
.DESCRIPTION
Maps the resolved shared directory and emits the fixed guest logon command without interpolating
unescaped XML content.
.PARAMETER SharedDirectory
Host directory mapped into the sandbox.
.PARAMETER Destination
Literal path of the WSB configuration to create.
#>
function Write-SandboxConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SharedDirectory,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    $resolvedSharedDirectory = (Resolve-Path -LiteralPath $SharedDirectory).Path
    $escapedSharedDirectory = [Security.SecurityElement]::Escape($resolvedSharedDirectory)
    $sandboxDirectory = "C:\Users\WDAGUtilityAccount\Desktop\ClashSharpSandbox"
    $logonCommand = "powershell.exe -ExecutionPolicy Bypass -File $sandboxDirectory\scripts\Run-InSandbox.ps1"
    $escapedLogonCommand = [Security.SecurityElement]::Escape($logonCommand)
    $configuration = @"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$escapedSharedDirectory</HostFolder>
      <SandboxFolder>$sandboxDirectory</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>$escapedLogonCommand</Command>
  </LogonCommand>
</Configuration>
"@
    Set-Content -LiteralPath $Destination -Value $configuration -Encoding utf8NoBOM
    return (Resolve-Path -LiteralPath $Destination).Path
}

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

    $emittedWsbPath = Write-SandboxConfiguration `
        -SharedDirectory $sharedDir `
        -Destination $wsbPath

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
