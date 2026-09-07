#Requires -Version 7.4
<#
.SYNOPSIS
    Runs bound offline package smoke tests in owned Windows Sandbox sessions.
.DESCRIPTION
    Requires the Sandbox CLI. Copies only an explicit verified payload, maps inputs read-only,
    accepts complete candidate-bound reports, and always stops the exact newly reserved guest ID.
    No package, certificate, service, or proxy mutations run in the host operating system.
.PARAMETER Launch
    Starts the prepared guest and waits for terminal evidence; omitted means preparation only.
.PARAMETER Scenario
    Comma-separated implemented scenarios. The unfinished full matrix is deliberately rejected.
.PARAMETER PayloadPath
    Exact generated payload directory containing payload-provenance.json.
.PARAMETER TimeoutSeconds
    Maximum guest execution time, excluding bounded CLI startup and cleanup.
.PARAMETER AllowSelfSignedCandidate
    Allows the selected development signer without adding host trust. Guest AppX verifies deployment.
#>
[CmdletBinding()]
param([switch]$Launch, [string]$Scenario = 'install-only',
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$PayloadPath,
    [ValidateRange(30, 1800)][int]$TimeoutSeconds = 900, [switch]$AllowSelfSignedCandidate)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SandboxHost.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'SandboxInputContract.psm1')
Import-Module (Join-Path $PSScriptRoot 'SandboxReportContract.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..\Installer\PackagingContract.psm1')
$scenarios = @($Scenario.Split(',') | ForEach-Object { $_.Trim() })
if ($scenarios.Count -eq 0 -or @($scenarios | Sort-Object -Unique).Count -ne $scenarios.Count) {
    throw 'sandbox.scenarios.invalid'
}
foreach ($name in $scenarios) {
    if ($name -cnotin @('install-only', 'launch-no-proxy')) {
        throw 'Only install-only and launch-no-proxy are implemented. The full installer/recovery matrix has not passed.'
    }
}
$mutex = [Threading.Mutex]::new($false, 'Local\ClashSharp.SandboxTest.Runner')
$acquired = $false
try {
    try { $acquired = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (-not $acquired) { throw 'Another ClashSharp Sandbox runner is active.' }
    $cli = $null
    if ($Launch) { $cli = (Get-Command wsb.exe -CommandType Application -ErrorAction Stop).Source }
    foreach ($name in $scenarios) {
        $plan = New-SandboxCandidatePlan -PayloadPath $PayloadPath -Scenario $name -AllowSelfSignedCandidate:$AllowSelfSignedCandidate
        $runPath = Join-Path $PSScriptRoot ('.sandbox\runs\' + $plan.runId)
        $null = Assert-ClashSharpOrdinaryPath $runPath -AllowMissing
        if (Test-Path -LiteralPath $runPath) { throw 'sandbox.run.exists' }
        $inputPath = Join-Path $runPath 'inputs'
        $outputPath = Join-Path $runPath 'reports'
        $null = [IO.Directory]::CreateDirectory($inputPath)
        $null = [IO.Directory]::CreateDirectory($outputPath)
        foreach ($source in @('scripts\Run-InSandbox.ps1', 'SandboxInputContract.psm1')) {
            $scriptSource = Assert-ClashSharpOrdinaryPath (Join-Path $PSScriptRoot $source) -RequireFile
            Copy-Item -LiteralPath $scriptSource -Destination (Join-Path $inputPath ([IO.Path]::GetFileName($source)))
        }
        $payloadTarget = Join-Path $inputPath 'payload'
        foreach ($file in $plan.candidate.files) {
            $source = Assert-ClashSharpOrdinaryPath (Join-Path $PayloadPath $file.path) -RequireFile
            $target = Join-Path $payloadTarget $file.path
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.File]::Copy($source, $target, $false)
        }
        Assert-SandboxPayload $plan $payloadTarget
        $planPath = Join-Path $inputPath 'scenario-plan.json'
        [IO.File]::WriteAllText($planPath, ($plan | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
        $planHash = Get-SandboxFileSha256 $planPath
        $inputContract = Get-ClashSharpDirectoryContract -LiteralPath $inputPath
        $configuration = New-SandboxConfiguration $inputPath $outputPath $planHash
        [IO.File]::WriteAllText((Join-Path $runPath 'scenario.wsb'), $configuration, [Text.UTF8Encoding]::new($false))
        Write-Host "Prepared $name; run $($plan.runId). Evidence: $outputPath"
        if (-not $Launch) { continue }
        $existing = Invoke-SandboxCli $cli @('list', '--raw') | ConvertFrom-Json
        if (@($existing.WindowsSandboxEnvironments | Where-Object Id -eq $plan.sandboxId).Count -ne 0) {
            throw 'sandbox.id.already_owned'
        }
        $hostProxyBefore = Get-SandboxProxyFingerprint
        $hostReceipt = [ordered]@{ schemaVersion = 1; runId = $plan.runId; sandboxId = $plan.sandboxId
            guestReportAccepted = $false; sandboxAbsent = $false; inputsUnchanged = $false
            hostProxyUnchanged = $false; finishedAt = $null }
        try {
            $started = Invoke-SandboxCli $cli @('start', '--id', $plan.sandboxId, '--config', $configuration, '--raw') | ConvertFrom-Json
            if ($started.Id -cne $plan.sandboxId) { throw 'sandbox.cli.unexpected_id' }
            Invoke-SandboxCli $cli @('connect', '--id', $plan.sandboxId, '--raw') -NoCapture
            $reportPath = Join-Path $outputPath 'result.json'
            $timer = [Diagnostics.Stopwatch]::StartNew()
            while (-not (Test-Path -LiteralPath $reportPath)) {
                if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw 'sandbox.report.timed_out' }
                Start-Sleep -Milliseconds 500
            }
            $null = Assert-ClashSharpOrdinaryPath $reportPath -RequireFile
            $report = Read-SandboxJson $reportPath
            Assert-SandboxScenarioReport $report $plan $planHash
            $hostReceipt.guestReportAccepted = $true
        } finally {
            try {
                $null = Invoke-SandboxCli $cli @('stop', '--id', $plan.sandboxId, '--raw')
                $remaining = Invoke-SandboxCli $cli @('list', '--raw') | ConvertFrom-Json
                $hostReceipt.sandboxAbsent = @($remaining.WindowsSandboxEnvironments |
                    Where-Object Id -eq $plan.sandboxId).Count -eq 0
                $currentContract = Get-ClashSharpDirectoryContract -LiteralPath $inputPath
                Compare-ClashSharpDirectoryContract -Expected $inputContract -Actual $currentContract
                $hostReceipt.inputsUnchanged = $true
                $hostReceipt.hostProxyUnchanged = (Get-SandboxProxyFingerprint) -ceq $hostProxyBefore
            } finally {
                $hostReceipt.finishedAt = [DateTime]::UtcNow.ToString('o')
                [IO.File]::WriteAllText((Join-Path $runPath 'host-result.json'),
                    ($hostReceipt | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
            }
            if (-not $hostReceipt.sandboxAbsent -or -not $hostReceipt.inputsUnchanged -or
                -not $hostReceipt.hostProxyUnchanged) { throw 'sandbox.host.postcondition_failed' }
        }
        Write-Host "Passed $name; exact candidate verified and owned Sandbox stopped."
    }
} finally {
    if ($acquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
