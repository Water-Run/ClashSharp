#Requires -Version 5.1
<#
.SYNOPSIS
    Executes one offline MSIX smoke scenario inside a newly owned Windows Sandbox guest.
.DESCRIPTION
    Refuses host paths and accounts before importing helpers or creating output. Identity and
    read-only mapping checks precede every mutation. Cleanup owns only resources absent at entry.
.PARAMETER ExpectedPlanSha256
    Original host plan digest, supplied through the Sandbox logon command.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedPlanSha256)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSCommandPath -ine 'C:\ClashSharpTestInput\Run-InSandbox.ps1' -or
    $env:USERNAME -cne 'WDAGUtilityAccount') { throw 'sandbox.guest.required' }
Import-Module 'C:\ClashSharpTestInput\SandboxInputContract.psm1' -Force -ErrorAction Stop
$planPath = 'C:\ClashSharpTestInput\scenario-plan.json'
if ((Get-SandboxFileSha256 $planPath) -cne $ExpectedPlanSha256) { throw 'sandbox.plan.changed' }
$plan = Read-SandboxJson $planPath
$environment = [pscustomobject]@{
    computerName = $env:COMPUTERNAME
    machineId = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Cryptography').MachineGuid
    userName = $env:USERNAME; profile = $env:USERPROFILE
    productType = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\ProductOptions').ProductType
    osBuild = [string](Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber
    architecture = $env:PROCESSOR_ARCHITECTURE; scriptPath = $PSCommandPath
}
Assert-SandboxGuestIdentity $plan $environment
$candidate = $plan.candidate
$startedAt = [DateTime]::UtcNow
$steps = [Collections.Generic.List[object]]::new()
$checks = [ordered]@{}
$failure = $null
$stage = 'C:\ClashSharpTestPayload'
$stageOwned = $false
$certificateOwned = $false
$packageAttempted = $false
$installedPackage = $null
$launched = $null
$dependencyBefore = @()
$dependenciesAttempted = $false
$proxyBefore = $null
$servicesBefore = $null
$certificatePath = 'Cert:\LocalMachine\TrustedPeople\' + $candidate.certificateThumbprint

function Invoke-SandboxStep {
    <#
    .SYNOPSIS
        Records one actually executed step using stable, non-sensitive error diagnostics.
    .DESCRIPTION
        Records elapsed time and terminal status, retains the first failure, and propagates errors
        after replacing raw exception text with a bounded type and HRESULT summary.
    .PARAMETER Name
        Contract-defined step name.
    .PARAMETER Action
        Synchronous operation whose output is not forwarded to the host.
    #>
    param([string]$Name, [scriptblock]$Action)
    $begin = [DateTime]::UtcNow
    $errorCode = $null
    try { $null = & $Action }
    catch {
        $errorCode = '{0}:{1}:0x{2:X8}' -f $Name, $_.Exception.GetType().Name, $_.Exception.HResult
        $deploymentCodes = @([regex]::Matches($_.Exception.Message, '(?i)\b0x[0-9a-f]{8}\b') |
            ForEach-Object { $_.Value.ToUpperInvariant() } | Sort-Object -Unique | Select-Object -First 4)
        if ($deploymentCodes.Count -gt 0) { $errorCode += ':' + ($deploymentCodes -join ':') }
        if ($null -eq $script:failure) { $script:failure = $errorCode }
        throw
    } finally {
        $finish = [DateTime]::UtcNow
        $status = 'passed'
        if ($null -ne $errorCode) { $status = 'failed' }
        $steps.Add([ordered]@{ name = $Name; status = $status; startedAt = $begin.ToString('o')
            finishedAt = $finish.ToString('o'); durationMs = [long][Math]::Round(($finish - $begin).TotalMilliseconds)
            error = $errorCode })
    }
}

function Get-SandboxServices {
    <#
    .SYNOPSIS
        Reads only ClashSharp service identities and states in the isolated guest.
    .DESCRIPTION
        Returns a stable sorted before/after observation without changing services.
    #>
    param()
    return (@(Get-Service | Where-Object { $_.Name -like 'ClashSharp*' } |
        Sort-Object Name | ForEach-Object { '{0}:{1}' -f $_.Name, $_.Status }) -join '|')
}

function Remove-SandboxStage {
    <#
    .SYNOPSIS
        Deletes the exact guest-owned staging tree after rejecting all reparse entries.
    .DESCRIPTION
        Requires the previously created fixed root, walks ordinary descendants without following
        reparse entries, and only then removes the contained test tree.
    #>
    param()
    if (-not $script:stageOwned -or -not (Test-Path -LiteralPath $stage)) { return }
    $resolved = (Resolve-Path -LiteralPath $stage).ProviderPath
    if ($resolved -ine 'C:\ClashSharpTestPayload') { throw 'sandbox.cleanup.path' }
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($resolved)
    while ($pending.Count -gt 0) {
        $path = $pending.Dequeue()
        $item = Get-Item -LiteralPath $path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'sandbox.cleanup.reparse' }
        if ($item.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $path -Force) {
                if (-not $child.FullName.StartsWith($resolved + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'sandbox.cleanup.escape'
                }
                $pending.Enqueue($child.FullName)
            }
        }
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop
}

try {
    Invoke-SandboxStep 'verify-isolation' {
        $readOnly = $false
        try {
            $writeProbe = [IO.File]::Open($planPath, 'Open', 'Write', 'Read')
            $writeProbe.Dispose()
        } catch [UnauthorizedAccessException] { $readOnly = $true }
        if (-not $readOnly) { throw 'sandbox.input.writable' }
        $adapterCount = @([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
            Where-Object { $_.OperationalStatus -eq 'Up' -and $_.NetworkInterfaceType -ne 'Loopback' }).Count
        if ($adapterCount -ne 0) { throw 'sandbox.network.enabled' }
        if (@(Get-AppxPackage -Name $candidate.packageName).Count -ne 0 -or
            (Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $certificatePath)) {
            throw 'sandbox.preexisting.resource'
        }
        $proxyKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
            'Software\Microsoft\Windows\CurrentVersion\Internet Settings', $false)
        try {
            if ($null -ne $proxyKey -and [int]$proxyKey.GetValue('ProxyEnable', 0) -ne 0) {
                throw 'sandbox.proxy.preexisting'
            }
        } finally { if ($null -ne $proxyKey) { $proxyKey.Dispose() } }
        $script:proxyBefore = Get-SandboxProxyFingerprint
        $script:servicesBefore = Get-SandboxServices
        $script:dependencyBefore = @(Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.8' |
            Select-Object -ExpandProperty PackageFullName | Sort-Object)
        $checks.isolation = [ordered]@{ inputReadOnly = $true; externalAdapterCount = $adapterCount; payloadFilesVerified = 0 }
    }
    Invoke-SandboxStep 'verify-payload' {
        Assert-SandboxPayload $plan 'C:\ClashSharpTestInput\payload'
        $checks.isolation.payloadFilesVerified = 4
    }
    Invoke-SandboxStep 'copy-payload' {
        $null = New-Item -ItemType Directory -Path $stage -ErrorAction Stop
        $script:stageOwned = $true
        foreach ($file in $candidate.files) {
            $target = Join-Path $stage $file.path
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.File]::Copy((Join-Path 'C:\ClashSharpTestInput\payload' $file.path), $target, $false)
        }
        Assert-SandboxPayload $plan $stage
    }
    Invoke-SandboxStep 'import-certificate' {
        $script:certificateOwned = $true
        $cert = Import-Certificate -FilePath (Join-Path $stage 'ClashSharp_TemporaryKey.cer') -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'
        if ($cert.Thumbprint -cne $candidate.certificateThumbprint) { throw 'sandbox.certificate.identity' }
    }
    Invoke-SandboxStep 'install-dependencies' {
        $script:dependenciesAttempted = $true
        Add-AppxPackage -Path (Join-Path $stage 'Dependencies\x64\Microsoft.WindowsAppRuntime.1.8.msix')
    }
    Invoke-SandboxStep 'install-package' {
        $script:packageAttempted = $true
        Add-AppxPackage -Path (Join-Path $stage 'ClashSharp_1.0.0.0_x64.msix')
    }
    Invoke-SandboxStep 'verify-package' {
        $packages = @(Get-AppxPackage -Name $candidate.packageName)
        if ($packages.Count -ne 1 -or $packages[0].PackageFullName -cne $candidate.fullName -or
            $packages[0].PackageFamilyName -cne $candidate.familyName -or
            [string]$packages[0].Version -cne $candidate.version -or $packages[0].Status -ne 'Ok') {
            throw 'sandbox.package.registration'
        }
        $script:installedPackage = $packages[0]
        $checks.package = [ordered]@{ installed = $true; name = $candidate.packageName
            fullName = $packages[0].PackageFullName; version = [string]$packages[0].Version
            source = 'ClashSharp_1.0.0.0_x64.msix'; dependencyCount = 1; certificateStore = 'LocalMachine/TrustedPeople' }
    }
    if ($plan.scenario -ceq 'launch-no-proxy') {
        Invoke-SandboxStep 'launch-package' {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
public static class SandboxProcessIdentity {
    [DllImport(@"C:\Windows\System32\kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport(@"C:\Windows\System32\kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(SafeProcessHandle process, ref uint length, StringBuilder name);
    public static string Read(uint pid) {
        using (var process = OpenProcess(0x1000, false, pid)) {
            if (process.IsInvalid) { throw new InvalidOperationException("sandbox.process.open"); }
            uint length = 0;
            if (GetPackageFullName(process, ref length, null) != 122 || length > 4096) {
                throw new InvalidOperationException("sandbox.process.unpackaged");
            }
            var text = new StringBuilder((int)length);
            if (GetPackageFullName(process, ref length, text) != 0) {
                throw new InvalidOperationException("sandbox.process.identity");
            }
            return text.ToString();
        }
    }
}
'@
            $executable = Join-Path $installedPackage.InstallLocation $candidate.executable
            if ((Get-SandboxFileSha256 $executable) -cne $candidate.executableSha256) { throw 'sandbox.executable.changed' }
            $startInfo = [Diagnostics.ProcessStartInfo]::new($executable)
            $startInfo.UseShellExecute = $false
            $startInfo.WorkingDirectory = $installedPackage.InstallLocation
            $launchStartedAt = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            $script:launched = [Diagnostics.Process]::Start($startInfo)
            if ([SandboxProcessIdentity]::Read([uint32]$launched.Id) -cne $candidate.fullName) {
                throw 'sandbox.process.wrong_package'
            }
            $timer = [Diagnostics.Stopwatch]::StartNew()
            while ($timer.Elapsed.TotalSeconds -lt 60) {
                $launched.Refresh()
                if ($launched.HasExited) { throw 'sandbox.process.early_exit' }
                if ($launched.MainWindowHandle -ne [IntPtr]::Zero) { break }
                Start-Sleep -Milliseconds 200
            }
            if ($launched.MainWindowHandle -eq [IntPtr]::Zero) { throw 'sandbox.window.missing' }
            $stable = [Diagnostics.Stopwatch]::StartNew()
            while ($stable.Elapsed.TotalMilliseconds -lt 30000) {
                Start-Sleep -Milliseconds 200
                $launched.Refresh()
                if ($launched.HasExited -or $launched.MainWindowHandle -eq [IntPtr]::Zero) {
                    throw 'sandbox.window.unstable'
                }
            }
            Import-Module 'C:\ClashSharpTestInput\SandboxStartupEvidence.psm1' -Force -ErrorAction Stop
            $logPath = Join-Path $env:LOCALAPPDATA ('Packages\' + $candidate.familyName + '\LocalState\ClashSharpLogs.sqlite3')
            $startup = Get-SandboxStartupEvidence -LiteralPath $logPath -StartedAtUnixTime $launchStartedAt
            $launched.Refresh()
            if ($launched.HasExited -or $launched.MainWindowHandle -eq [IntPtr]::Zero) {
                throw 'sandbox.window.unstable'
            }
            $checks.launch = [ordered]@{ processId = $launched.Id; packageFullName = $candidate.fullName
                executableSha256 = (Get-SandboxFileSha256 $executable); mainWindowObserved = $true
                stabilizationMs = [long]$stable.Elapsed.TotalMilliseconds; termination = 'owned-process'; startup = $startup }
        }
    }
} catch {
    if ($null -eq $failure) { $failure = 'sandbox.scenario.failed' }
} finally {
    foreach ($cleanup in @(
        @{ Name = 'cleanup-package'; Action = {
            if ($null -ne $script:launched) {
                try {
                    if (-not $script:launched.HasExited) { $script:launched.Kill() }
                    if (-not $script:launched.WaitForExit(10000)) { throw 'sandbox.process.cleanup_timeout' }
                } finally { $script:launched.Dispose(); $script:launched = $null }
            }
            if ($script:packageAttempted) {
                $owned = @(Get-AppxPackage -Name $candidate.packageName |
                    Where-Object PackageFullName -CEQ $candidate.fullName)
                foreach ($package in $owned) { Remove-AppxPackage -Package $package.PackageFullName }
            }
            if ($script:dependenciesAttempted) {
                foreach ($dependency in @(Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.8')) {
                    if ($script:dependencyBefore -cnotcontains $dependency.PackageFullName) {
                        Remove-AppxPackage -Package $dependency.PackageFullName
                    }
                }
            }
        } },
        @{ Name = 'cleanup-certificate'; Action = {
            if ($script:certificateOwned -and (Test-Path -LiteralPath $certificatePath)) {
                Remove-Item -LiteralPath $certificatePath -ErrorAction Stop
            }
        } },
        @{ Name = 'cleanup-payload'; Action = { Remove-SandboxStage } },
        @{ Name = 'verify-cleanup'; Action = {
            $dependencyAfter = @(Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.8' |
                Select-Object -ExpandProperty PackageFullName | Sort-Object)
            $proxyAfter = Get-SandboxProxyFingerprint
            $checks.proxy = [ordered]@{ beforeSha256 = $proxyBefore; afterSha256 = $proxyAfter
                enabledBefore = $false; unchanged = ($proxyBefore -ceq $proxyAfter) }
            $processes = @([Diagnostics.Process]::GetProcesses())
            try {
                $checks.cleanup = [ordered]@{
                    packageAbsent = (@(Get-AppxPackage -Name $candidate.packageName).Count -eq 0)
                    certificateAbsent = (-not (Test-Path -LiteralPath $certificatePath))
                    stagingAbsent = (-not (Test-Path -LiteralPath $stage))
                    ownedProcessesAbsent = (@($processes | Where-Object { $_.ProcessName -like 'ClashSharp*' }).Count -eq 0)
                    dependenciesRestored = (($dependencyBefore -join '|') -ceq ($dependencyAfter -join '|'))
                    servicesUnchanged = ($servicesBefore -ceq (Get-SandboxServices))
                    proxyUnchanged = ($proxyBefore -ceq $proxyAfter)
                }
            } finally { foreach ($process in $processes) { $process.Dispose() } }
            if (@($checks.cleanup.Values | Where-Object { -not $_ }).Count -ne 0) {
                throw 'sandbox.cleanup.postcondition'
            }
        } }
    )) {
        try { Invoke-SandboxStep $cleanup.Name $cleanup.Action } catch { }
    }
    $status = 'passed'
    if ($null -ne $failure) { $status = 'failed' }
    $report = [ordered]@{ schemaVersion = 2; scenario = $plan.scenario; runId = $plan.runId
        sandboxId = $plan.sandboxId; planSha256 = $ExpectedPlanSha256; status = $status
        startedAt = $startedAt.ToString('o'); finishedAt = [DateTime]::UtcNow.ToString('o')
        environment = $environment; steps = @($steps.ToArray()); checks = $checks; failure = $failure }
    $temporary = 'C:\ClashSharpTestResults\result.json.tmp'
    $stream = [IO.File]::Open($temporary, 'CreateNew', 'Write', 'None')
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($report | ConvertTo-Json -Depth 12))
        if ($bytes.Length -gt 1048576) { throw 'sandbox.report.length' }
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    [IO.File]::Move($temporary, 'C:\ClashSharpTestResults\result.json')
}
if ($null -ne $failure) { exit 1 }
