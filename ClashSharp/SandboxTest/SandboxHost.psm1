#Requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SandboxInputContract.psm1')
Import-Module (Join-Path $PSScriptRoot '..\Installer\PackagingContract.psm1')

function New-SandboxCandidatePlan {
    <#
    .SYNOPSIS
        Binds one explicit release payload to its actual MSIX, signer, certificate, and files.
    .DESCRIPTION
        Reads the exact provenance allowlist, checks package identities and bytes, and emits a
        fresh guest plan. Development trust is evaluated without modifying host certificate stores.
    .PARAMETER PayloadPath
        Exact payload directory containing the generated provenance and four payload files.
    .PARAMETER Scenario
        Implemented scenario selected by the caller.
    .PARAMETER AllowSelfSignedCandidate
        Allows an explicitly selected development signer that is not trusted by the host.
        AppX deployment in the guest still verifies the actual package signature.
    #>
    param([Parameter(Mandatory)][string]$PayloadPath,
        [Parameter(Mandatory)][ValidateSet('install-only', 'launch-no-proxy')][string]$Scenario,
        [switch]$AllowSelfSignedCandidate)
    $root = Assert-ClashSharpOrdinaryPath -LiteralPath $PayloadPath -RequireDirectory
    $provenancePath = Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $root 'payload-provenance.json') -RequireFile
    $provenance = Read-SandboxJson $provenancePath
    if ($provenance.schemaVersion -ne 1 -or @($provenance.dependencies).Count -ne 1) {
        throw 'sandbox.provenance.unsupported'
    }
    $files = [Collections.Generic.List[object]]::new()
    $entries = @(
        @{ Role = 'package'; Value = $provenance.primary; Path = 'ClashSharp_1.0.0.0_x64.msix' },
        @{ Role = 'certificate'; Value = $provenance.certificate; Path = 'ClashSharp_TemporaryKey.cer' },
        @{ Role = 'dependency'; Value = $provenance.dependencies[0]; Path = 'Dependencies/x64/Microsoft.WindowsAppRuntime.1.8.msix' })
    foreach ($entry in $entries) {
        $metadata = $entry.Value
        if ($metadata.path -cne $entry.Path -or $metadata.sha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw 'sandbox.provenance.path'
        }
        $path = Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $root $entry.Path) -RequireFile
        $item = Get-Item -LiteralPath $path -Force
        $sha256 = Get-SandboxFileSha256 $path
        if ($item.Length -ne $metadata.length -or $sha256 -cne $metadata.sha256) {
            throw 'sandbox.provenance.changed'
        }
        $files.Add([ordered]@{ role = $entry.Role; path = $entry.Path; length = $item.Length; sha256 = $sha256 })
    }
    $files.Add([ordered]@{ role = 'provenance'; path = 'payload-provenance.json'
        length = (Get-Item -LiteralPath $provenancePath).Length; sha256 = (Get-SandboxFileSha256 $provenancePath) })
    $packagePath = Join-Path $root $entries[0].Path
    $identity = Get-ClashSharpMsixIdentity -LiteralPath $packagePath
    foreach ($name in @('name', 'publisher', 'version', 'architecture')) {
        if ($identity.$name -cne $provenance.primary.$name) { throw 'sandbox.provenance.identity' }
    }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $root $entries[1].Path))
    try {
        if ($certificate.HasPrivateKey -or $certificate.Thumbprint -cne $provenance.certificate.thumbprint -or
            $certificate.Subject -cne $identity.Publisher -or
            $certificate.Thumbprint -cne $provenance.primary.signerThumbprint) { throw 'sandbox.provenance.certificate' }
        $signature = Get-ClashSharpPackageSignature -LiteralPath $packagePath -ExpectedSubject $identity.Publisher -ExpectedThumbprint $certificate.Thumbprint
        if ($signature.Status -cne 'Valid') {
            if (-not $AllowSelfSignedCandidate -or $signature.Status -cnotin @('NotTrusted', 'UnknownError') -or
                $certificate.Subject -cne $certificate.Issuer) { throw 'sandbox.package.signature' }
            $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
            try {
                $chain.ChainPolicy.TrustMode = [Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
                $null = $chain.ChainPolicy.CustomTrustStore.Add($certificate)
                $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
                if (-not $chain.Build($certificate)) { throw 'sandbox.development_certificate.invalid' }
            } finally { $chain.Dispose() }
        }
    } finally { $certificate.Dispose() }
    $dependencyPath = Join-Path $root $entries[2].Path
    $dependencyIdentity = Get-ClashSharpMsixIdentity -LiteralPath $dependencyPath
    foreach ($name in @('name', 'publisher', 'version', 'architecture')) {
        if ($dependencyIdentity.$name -cne $provenance.dependencies[0].$name) { throw 'sandbox.provenance.dependency' }
    }
    if (-not $dependencyIdentity.IsFramework -or $dependencyIdentity.Name -cne 'Microsoft.WindowsAppRuntime.1.8') {
        throw 'sandbox.dependency.identity'
    }
    $null = Get-ClashSharpPackageSignature -LiteralPath $dependencyPath -ExpectedSubject $dependencyIdentity.Publisher -ExpectedThumbprint $provenance.dependencies[0].signerThumbprint -RequireTrusted -RequireTimestamp
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $executables = @($archive.Entries | Where-Object { $_.FullName -ieq 'ClashSharp.exe' })
        if ($executables.Count -ne 1 -or $executables[0].FullName -cne 'ClashSharp.exe' -or
            $executables[0].Length -lt 1 -or $executables[0].Length -gt 16777216) { throw 'sandbox.executable.invalid' }
        $stream = $executables[0].Open()
        $hash = [Security.Cryptography.SHA256]::Create()
        try { $executableSha256 = ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $hash.Dispose(); $stream.Dispose() }
    } finally { $archive.Dispose() }
    $plan = [ordered]@{
        schemaVersion = 2; scenario = $Scenario; runId = [Guid]::NewGuid().ToString('N')
        sandboxId = [Guid]::NewGuid().ToString()
        host = [ordered]@{ computerName = $env:COMPUTERNAME
            machineId = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Cryptography').MachineGuid }
        candidate = [ordered]@{
            packageName = $identity.Name; fullName = $identity.PackageFullName; familyName = $identity.PackageFamilyName
            version = $identity.Version; applicationId = $identity.ApplicationId
            executable = $identity.ApplicationExecutable; executableSha256 = $executableSha256
            certificateThumbprint = $provenance.certificate.thumbprint; files = @($files.ToArray())
        }
    } | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    Assert-SandboxPlan $plan
    Assert-SandboxPayload $plan $root
    return $plan
}

function New-SandboxConfiguration {
    <#
    .SYNOPSIS
        Generates an offline guest with separate read-only input and writable report mappings.
    .DESCRIPTION
        Escapes ordinary host paths and emits only two required mappings and the fixed guest command.
        Host peripheral sharing and guest networking are explicitly disabled.
    .PARAMETER InputPath
        Fresh host directory holding only verified candidate files and test scripts.
    .PARAMETER ReportPath
        Fresh host directory reserved for untrusted guest output.
    .PARAMETER PlanSha256
        Digest supplied independently to the fixed guest entry point.
    #>
    param([Parameter(Mandatory)][string]$InputPath, [Parameter(Mandatory)][string]$ReportPath,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$PlanSha256)
    $inputs = [Security.SecurityElement]::Escape((Assert-ClashSharpOrdinaryPath $InputPath -RequireDirectory))
    $reports = [Security.SecurityElement]::Escape((Assert-ClashSharpOrdinaryPath $ReportPath -RequireDirectory))
    return @"
<Configuration>
  <vGPU>Disable</vGPU><Networking>Disable</Networking>
  <AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection><ClipboardRedirection>Disable</ClipboardRedirection>
  <MemoryInMB>4096</MemoryInMB>
  <MappedFolders>
    <MappedFolder><HostFolder>$inputs</HostFolder><SandboxFolder>C:\ClashSharpTestInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$reports</HostFolder><SandboxFolder>C:\ClashSharpTestResults</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File C:\ClashSharpTestInput\Run-InSandbox.ps1 -ExpectedPlanSha256 $PlanSha256</Command></LogonCommand>
</Configuration>
"@
}

function Invoke-SandboxCli {
    <#
    .SYNOPSIS
        Executes and drains one bounded Sandbox CLI process without a shell.
    .DESCRIPTION
        Connect deliberately inherits no redirected pipes: its desktop child may keep those pipes
        open after the CLI exits. Other reads are cancellable and share the process deadline.
    .PARAMETER Executable
        Resolved Sandbox CLI application path.
    .PARAMETER Arguments
        Native argument vector; no shell expansion is performed.
    .PARAMETER NoCapture
        Disables redirection for the desktop connection command.
    #>
    param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$NoCapture)
    $startInfo = [Diagnostics.ProcessStartInfo]::new($Executable)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = -not $NoCapture
    $startInfo.RedirectStandardError = -not $NoCapture
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $cancellation = [Threading.CancellationTokenSource]::new(55000)
    $started = $false
    $stdout = $null
    $stderr = $null
    try {
        $started = $process.Start()
        if (-not $started) { throw 'sandbox.cli.start_failed' }
        if (-not $NoCapture) {
            $stdout = $process.StandardOutput.ReadToEndAsync($cancellation.Token)
            $stderr = $process.StandardError.ReadToEndAsync($cancellation.Token)
        }
        if (-not $process.WaitForExit(50000)) { throw 'sandbox.cli.timed_out' }
        $output = ''
        if (-not $NoCapture) {
            $output = $stdout.GetAwaiter().GetResult()
            $null = $stderr.GetAwaiter().GetResult()
        }
        if ($process.ExitCode -ne 0) { throw "sandbox.cli.exit_$($process.ExitCode)" }
        if (-not $NoCapture) { return $output }
    } finally {
        $cancellation.Cancel()
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            if (-not $process.WaitForExit(5000)) { throw 'sandbox.cli.cleanup_failed' }
        }
        foreach ($read in @($stdout, $stderr)) {
            if ($null -ne $read) { try { $null = $read.GetAwaiter().GetResult() } catch { } }
        }
        $process.Dispose()
        $cancellation.Dispose()
    }
}

Export-ModuleMember -Function New-SandboxCandidatePlan, New-SandboxConfiguration, Invoke-SandboxCli
