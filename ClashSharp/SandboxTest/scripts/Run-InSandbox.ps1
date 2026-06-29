[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$sandboxRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$planPath = Join-Path $sandboxRoot "host-plan.json"
$reportDir = Join-Path $sandboxRoot "reports"
$reportPath = Join-Path $reportDir "latest.json"

New-Item -ItemType Directory -Force -Path $reportDir | Out-Null

$inputPlan = $null
if (Test-Path $planPath) {
    $inputPlan = Get-Content -Raw -Path $planPath | ConvertFrom-Json
}

$report = [ordered]@{
    startedAt = (Get-Date).ToUniversalTime().ToString("o")
    computerName = $env:COMPUTERNAME
    userName = $env:USERNAME
    status = "framework-ready"
    input = $inputPlan
    nextSteps = @(
        "Copy or locate a ClashSharp installer artifact.",
        "Install ClashSharp in Windows Sandbox.",
        "Run smoke checks against the UI, service, proxy state, logs, and cleanup behavior.",
        "Write structured results back to the mapped reports directory."
    )
}

$report | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -Path $reportPath

Write-Host "ClashSharp SandboxTest placeholder completed."
Write-Host "Report: $reportPath"
