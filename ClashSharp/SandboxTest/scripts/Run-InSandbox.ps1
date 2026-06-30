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

function Get-WindowsBuildNumber {
    $property = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
    return [string]$property.CurrentBuildNumber
}

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

function Get-DependencyPackages {
    param([string]$PayloadPath)

    $dependencyRoot = Join-Path $PayloadPath "Dependencies"
    if (-not (Test-Path $dependencyRoot)) {
        return @()
    }

    return @(Get-ChildItem -LiteralPath $dependencyRoot -File -Recurse -Filter "*.msix" |
        Sort-Object FullName)
}

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

function New-SkippedChecks {
    param([string]$Reason)

    return [ordered]@{
        reason = $Reason
    }
}

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

    $report | ConvertTo-Json -Depth 12 | Set-Content -Encoding UTF8 -Path $reportPath
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
            $checks.skipped = New-SkippedChecks -Reason "launch-no-proxy is planned but not implemented in this increment."
            Add-StepResult -Name "scenario-skipped" -Status "skipped" -StartedAt (Get-Date).ToUniversalTime() -ErrorMessage $null
            Write-ScenarioReport -Plan $plan -Status "skipped" -Checks $checks -FailureMessage $null
            return
        }
        "startup-with-proxy-config" {
            $checks.skipped = New-SkippedChecks -Reason "startup-with-proxy-config is planned but not implemented in this increment."
            Add-StepResult -Name "scenario-skipped" -Status "skipped" -StartedAt (Get-Date).ToUniversalTime() -ErrorMessage $null
            Write-ScenarioReport -Plan $plan -Status "skipped" -Checks $checks -FailureMessage $null
            return
        }
        "cleanup-uninstall" {
            $checks.skipped = New-SkippedChecks -Reason "cleanup-uninstall is planned but not implemented in this increment."
            Add-StepResult -Name "scenario-skipped" -Status "skipped" -StartedAt (Get-Date).ToUniversalTime() -ErrorMessage $null
            Write-ScenarioReport -Plan $plan -Status "skipped" -Checks $checks -FailureMessage $null
            return
        }
        "real-proxy-optional" {
            $checks.skipped = New-SkippedChecks -Reason "real-proxy-optional requires explicit proxy inputs and is not enabled by default."
            Add-StepResult -Name "scenario-skipped" -Status "skipped" -StartedAt (Get-Date).ToUniversalTime() -ErrorMessage $null
            Write-ScenarioReport -Plan $plan -Status "skipped" -Checks $checks -FailureMessage $null
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
