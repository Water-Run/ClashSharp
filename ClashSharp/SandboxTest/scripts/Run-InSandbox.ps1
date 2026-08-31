[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$packageName = "67dc1dc3-13fd-46c5-84f4-2932d94b566f"
$sandboxRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$planPath = Join-Path $sandboxRoot "scenario-plan.json"
$reportDir = Join-Path $sandboxRoot "reports"
$reportPath = Join-Path $reportDir "result.json"
$startedAt = (Get-Date).ToUniversalTime()
$steps = New-Object System.Collections.Generic.List[object]
$failed = $false

New-Item -ItemType Directory -Force -Path $reportDir | Out-Null

<#
.SYNOPSIS
Reads the Windows build number recorded in the guest operating system registry.
.DESCRIPTION
Returns the CurrentBuildNumber value used only as evidence in the scenario report.
#>
function Get-WindowsBuildNumber {
    $property = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
    return [string]$property.CurrentBuildNumber
}

<#
.SYNOPSIS
Appends one bounded scenario-step result to the in-memory report.
.DESCRIPTION
Records status, UTC timestamps, elapsed milliseconds, and the sanitized step failure message.
.PARAMETER Name
Stable scenario step name.
.PARAMETER Status
Terminal step status.
.PARAMETER StartedAt
UTC start time captured before the step ran.
.PARAMETER ErrorMessage
Failure detail, or null for a successful step.
#>
function Add-StepResult {
    param(
        [string]$Name,
        [string]$Status,
        [datetime]$StartedAt,
        [string]$ErrorMessage
    )

    $finishedAt = (Get-Date).ToUniversalTime()
    $steps.Add([ordered]@{
        name = $Name
        status = $Status
        startedAt = $StartedAt.ToString("o")
        finishedAt = $finishedAt.ToString("o")
        durationMs = [int][Math]::Round(($finishedAt - $StartedAt).TotalMilliseconds)
        error = $ErrorMessage
    }) | Out-Null
}

<#
.SYNOPSIS
Runs one scenario action and records its terminal result.
.DESCRIPTION
Marks the overall scenario failed and rethrows when the action fails so later mutation steps do
not continue after an unmet prerequisite.
.PARAMETER Name
Stable name written to the step report.
.PARAMETER Action
Scenario action to execute synchronously.
#>
function Invoke-ScenarioStep {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    $stepStartedAt = (Get-Date).ToUniversalTime()
    try {
        & $Action
        Add-StepResult -Name $Name -Status "passed" -StartedAt $stepStartedAt -ErrorMessage $null
    } catch {
        $script:failed = $true
        Add-StepResult -Name $Name -Status "failed" -StartedAt $stepStartedAt -ErrorMessage $_.Exception.Message
        throw
    }
}

<#
.SYNOPSIS
Selects the newest top-level ClashSharp MSIX package from the mapped payload.
.DESCRIPTION
Rejects the scenario when no matching MSIX or bundle is available at the expected root.
.PARAMETER PayloadPath
Mapped guest payload directory to inspect.
#>
function Find-PayloadPackage {
    param([string]$PayloadPath)

    $package = Get-ChildItem -LiteralPath $PayloadPath -File |
        Where-Object { $_.Extension -in ".msix", ".msixbundle" -and $_.Name -like "ClashSharp_*" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $package) {
        throw "No top-level Clash# MSIX package was found under $PayloadPath."
    }

    return $package
}

<#
.SYNOPSIS
Selects the newest certificate file from the mapped payload tree.
.DESCRIPTION
Rejects the scenario when the payload has no certificate available for the install-only probe.
.PARAMETER PayloadPath
Mapped guest payload directory to inspect recursively.
#>
function Find-PayloadCertificate {
    param([string]$PayloadPath)

    $certificate = Get-ChildItem -LiteralPath $PayloadPath -File -Recurse -Filter "*.cer" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $certificate) {
        throw "No package certificate was found under $PayloadPath."
    }

    return $certificate
}

<#
.SYNOPSIS
Returns the deterministic dependency-package list below the mapped payload.
.DESCRIPTION
Returns an empty array when no Dependencies directory exists; otherwise returns MSIX files sorted
by full path so installation order is reproducible.
.PARAMETER PayloadPath
Mapped guest payload directory containing the optional Dependencies tree.
#>
function Get-DependencyPackages {
    param([string]$PayloadPath)

    $dependencyRoot = Join-Path $PayloadPath "Dependencies"
    if (-not (Test-Path $dependencyRoot)) {
        return @()
    }

    return @(Get-ChildItem -LiteralPath $dependencyRoot -File -Recurse -Filter "*.msix" |
        Sort-Object FullName)
}

<#
.SYNOPSIS
Executes the sandbox install-only package probe and records package identity evidence.
.DESCRIPTION
Resolves the payload, imports its certificate, installs dependencies and the primary package, and
requires the expected package registration before reporting success.
.PARAMETER Plan
Parsed immutable scenario plan for the current run.
.PARAMETER Checks
Mutable report dictionary that receives verified package evidence.
#>
function Invoke-InstallOnlyScenario {
    param(
        [object]$Plan,
        [System.Collections.IDictionary]$Checks
    )

    $payloadPath = Join-Path $sandboxRoot "payload"
    $package = $null
    $certificate = $null
    $dependencies = @()

    Invoke-ScenarioStep -Name "resolve-payload" -Action {
        if (-not (Test-Path $payloadPath)) {
            throw "Payload path does not exist: $payloadPath"
        }

        $script:package = Find-PayloadPackage -PayloadPath $payloadPath
        $script:certificate = Find-PayloadCertificate -PayloadPath $payloadPath
        $script:dependencies = Get-DependencyPackages -PayloadPath $payloadPath
    }

    Invoke-ScenarioStep -Name "import-certificate" -Action {
        Import-Certificate -FilePath $script:certificate.FullName -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
    }

    Invoke-ScenarioStep -Name "install-dependencies" -Action {
        foreach ($dependency in $script:dependencies) {
            Add-AppxPackage -Path $dependency.FullName -ForceApplicationShutdown
        }
    }

    Invoke-ScenarioStep -Name "install-package" -Action {
        Add-AppxPackage -Path $script:package.FullName -ForceApplicationShutdown
    }

    Invoke-ScenarioStep -Name "verify-package" -Action {
        $installedPackage = Get-AppxPackage -Name $packageName
        if ($null -eq $installedPackage) {
            throw "Clash# package was not found after Add-AppxPackage."
        }

        $Checks.package = [ordered]@{
            installed = $true
            name = $installedPackage.Name
            fullName = $installedPackage.PackageFullName
            version = [string]$installedPackage.Version
            source = $script:package.Name
            dependencyCount = @($script:dependencies).Count
        }
    }
}

<#
.SYNOPSIS
Writes the terminal sandbox scenario report as JSON.
.DESCRIPTION
Combines the immutable plan, guest environment, ordered step evidence, checks, and optional failure
into a temporary file and atomically publishes it at the fixed path consumed by the host harness.
.PARAMETER Plan
Parsed scenario plan that supplies identity and scenario name.
.PARAMETER Status
Terminal scenario status.
.PARAMETER Checks
Evidence dictionary produced by executed checks.
.PARAMETER FailureMessage
Terminal failure detail, or null when no failure occurred.
#>
function Write-ScenarioReport {
    param(
        [object]$Plan,
        [string]$Status,
        [System.Collections.IDictionary]$Checks,
        [string]$FailureMessage
    )

    $finishedAt = (Get-Date).ToUniversalTime()
    $report = [ordered]@{
        schemaVersion = 1
        scenario = [string]$Plan.scenario
        runId = [string]$Plan.runId
        status = $Status
        startedAt = $startedAt.ToString("o")
        finishedAt = $finishedAt.ToString("o")
        environment = [ordered]@{
            computerName = $env:COMPUTERNAME
            userName = $env:USERNAME
            osBuild = (Get-WindowsBuildNumber)
            architecture = $env:PROCESSOR_ARCHITECTURE
        }
        steps = $steps.ToArray()
        checks = $Checks
        failure = $FailureMessage
    }

    $temporaryReportPath = "$reportPath.tmp"
    try {
        $report | ConvertTo-Json -Depth 12 |
            Set-Content -Encoding UTF8 -LiteralPath $temporaryReportPath
        Move-Item -LiteralPath $temporaryReportPath -Destination $reportPath -Force
    } finally {
        if (Test-Path -LiteralPath $temporaryReportPath) {
            Remove-Item -LiteralPath $temporaryReportPath -Force
        }
    }
}

<#
.SYNOPSIS
Publishes explicit failed evidence for a required scenario that did not execute.
.DESCRIPTION
Records a failed step and terminal failed report so an unimplemented required scenario can never be
mistaken for a pass by the host evidence validator.
.PARAMETER Plan
Parsed immutable scenario plan for the current run.
.PARAMETER Checks
Mutable report dictionary that receives the not-executed reason.
.PARAMETER Reason
Human-readable explanation recorded as both failed step evidence and terminal failure.
#>
function Write-NotExecutedScenarioReport {
    param(
        [object]$Plan,
        [System.Collections.IDictionary]$Checks,
        [string]$Reason
    )

    $Checks.notExecuted = [ordered]@{
        reason = $Reason
    }
    Add-StepResult `
        -Name "scenario-not-executed" `
        -Status "failed" `
        -StartedAt (Get-Date).ToUniversalTime() `
        -ErrorMessage $Reason
    Write-ScenarioReport `
        -Plan $Plan `
        -Status "failed" `
        -Checks $Checks `
        -FailureMessage $Reason
}

$plan = $null
$checks = [ordered]@{}
$failureMessage = $null

try {
    if (-not (Test-Path $planPath)) {
        throw "Scenario plan was not found: $planPath"
    }

    $plan = Get-Content -Raw -Path $planPath | ConvertFrom-Json

    switch ([string]$plan.scenario) {
        "install-only" {
            Invoke-InstallOnlyScenario -Plan $plan -Checks $checks
        }
        "launch-no-proxy" {
            Write-NotExecutedScenarioReport `
                -Plan $plan `
                -Checks $checks `
                -Reason "launch-no-proxy is required but not implemented."
            return
        }
        "startup-with-proxy-config" {
            Write-NotExecutedScenarioReport `
                -Plan $plan `
                -Checks $checks `
                -Reason "startup-with-proxy-config is required but not implemented."
            return
        }
        "cleanup-uninstall" {
            Write-NotExecutedScenarioReport `
                -Plan $plan `
                -Checks $checks `
                -Reason "cleanup-uninstall is required but not implemented."
            return
        }
        "real-proxy-optional" {
            Write-NotExecutedScenarioReport `
                -Plan $plan `
                -Checks $checks `
                -Reason "real-proxy-optional requires explicit proxy inputs that were not supplied."
            return
        }
        default {
            throw "Unknown scenario in plan: $($plan.scenario)"
        }
    }
} catch {
    $failed = $true
    $failureMessage = $_.Exception.Message
} finally {
    if ($null -eq $plan) {
        $plan = [pscustomobject]@{
            scenario = "unknown"
            runId = "unknown"
        }
    }

    if ($failed) {
        Write-ScenarioReport -Plan $plan -Status "failed" -Checks $checks -FailureMessage $failureMessage
    } elseif (([string]$plan.scenario) -eq "install-only") {
        Write-ScenarioReport -Plan $plan -Status "passed" -Checks $checks -FailureMessage $null
    }
}

Write-Host "ClashSharp SandboxTest scenario completed."
Write-Host "Report: $reportPath"
