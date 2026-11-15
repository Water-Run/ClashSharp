#Requires -Version 7.4
<#
.SYNOPSIS
Runs the linked native WinUI probe on an unselected private Windows desktop.
.DESCRIPTION
Verifies the exact developer-only executable and linked assembly, checks the actual
WinUI thread's desktop assignment, saves bound results, and releases only the owned process.
It does not install software, switch desktops, or inject keyboard or mouse input.
.PARAMETER ProbeDirectory
Absolute output directory produced by building ClashSharp.DialogProbe for win-x64.
.PARAMETER TimeoutSeconds
Maximum completion time before the owned test process is stopped.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ProbeDirectory,
    [ValidateRange(30, 900)][int]$TimeoutSeconds = 120
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../Installer/PackagingContract.psm1') -Force
$probe = Assert-ClashSharpOrdinaryPath -LiteralPath $ProbeDirectory -RequireDirectory
$executable = Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $probe 'ClashSharp.DialogProbe.exe') -RequireFile
$assembly = Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $probe 'ClashSharp.DialogProbe.dll') -RequireFile
$before = Get-ClashSharpDirectoryContract -LiteralPath $probe
$runId = [Guid]::NewGuid().ToString('N')
$run = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('../../artifacts/validation/dialog-probes/' + $runId)))
$null = New-Item -ItemType Directory -Path $run
$resultPath = Join-Path $run 'native-result.json'
$receipt = [ordered]@{
    runId = $runId
    executableSha256 = (Get-FileHash -LiteralPath $executable).Hash.ToLowerInvariant()
    assemblySha256 = (Get-FileHash -LiteralPath $assembly).Hash.ToLowerInvariant()
    privateDesktopName = $null
    processId = $null
    probePassed = $false
    processExited = $false
    exitCode = $null
    inputsUnchanged = $false
    inputInjected = $false
}
$ownedProbe = $null
try {
    if (-not ('ClashSharp.SandboxValidation.PrivateDesktopProbe' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'PrivateDesktopProbe.cs')
    }
    $ownedProbe = [ClashSharp.SandboxValidation.PrivateDesktopProbe]::Start($executable, $resultPath)
    $receipt.privateDesktopName = $ownedProbe.DesktopName
    $receipt.processId = $ownedProbe.ProcessId
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $ownedProbe.HasExited) {
        if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw 'dialog.probe.timeout' }
        Start-Sleep -Milliseconds 200
    }
    $receipt.processExited = $true
    $receipt.exitCode = $ownedProbe.ExitCode
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($result.status -cne 'passed' -or $result.inputInjected -ne $false -or
        $result.uiThreadDesktop -cne $ownedProbe.DesktopName -or $receipt.exitCode -ne 0) {
        throw 'dialog.probe.failed'
    }
    $receipt.probePassed = $true
} finally {
    if ($null -ne $ownedProbe) { $ownedProbe.Dispose() }
    $after = Get-ClashSharpDirectoryContract -LiteralPath $probe
    Compare-ClashSharpDirectoryContract -Expected $before -Actual $after
    $receipt.inputsUnchanged = $true
    $receipt | ConvertTo-Json | Set-Content (Join-Path $run 'host-result.json') -Encoding utf8NoBOM
}
Write-Output $run
Get-Content -LiteralPath $resultPath -Raw | Write-Output
