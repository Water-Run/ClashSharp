#Requires -Version 7.4

<#
.SYNOPSIS
    Verifies actual MSBuild evaluation for preview, development, and signed installer profiles.
.DESCRIPTION
    Evaluates the production WPF project and executes only its activation validation target.
    It never builds, launches, signs, installs, or imports a certificate. Invalid activation
    combinations must fail before a compiler or any installer runtime can execute.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PackagingContract.psm1') -Force
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\ClashSharp.Installer\ClashSharp.Installer.csproj'))
$dotnet = (Get-Command dotnet -CommandType Application).Source

function Invoke-InstallerProfileBuild {
    <#
    .SYNOPSIS
        Evaluates one bounded MSBuild request and drains its output.
    .DESCRIPTION
        Passes a native argument vector without shell interpolation, returns the exit code and
        stdout, and terminates only the owned process tree if the validation deadline expires.
    .PARAMETER Arguments
        Additional property-evaluation or validation-target arguments for the fixed WPF project.
    #>
    param([Parameter(Mandatory)][string[]] $Arguments)

    $start = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in (@('msbuild', $project, '-nologo', '-nr:false', '-p:Platform=x64', '-p:Configuration=Release') + $Arguments)) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $cancellation = [Threading.CancellationTokenSource]::new(45000)
    $started = $false
    $stdout = $null
    $stderr = $null
    try {
        $started = $process.Start()
        if (-not $started) { throw 'MSBuild profile validation did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync($cancellation.Token)
        $stderr = $process.StandardError.ReadToEndAsync($cancellation.Token)
        if (-not $process.WaitForExit(40000)) { throw 'MSBuild profile validation timed out.' }
        return [pscustomobject]@{ exitCode = $process.ExitCode; output = $stdout.GetAwaiter().GetResult(); error = $stderr.GetAwaiter().GetResult() }
    } finally {
        $cancellation.Cancel()
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            if (-not $process.WaitForExit(5000)) { throw 'MSBuild profile process did not terminate.' }
        }
        foreach ($read in @($stdout, $stderr)) {
            if ($null -ne $read) { try { $null = $read.GetAwaiter().GetResult() } catch { } }
        }
        $process.Dispose()
        $cancellation.Dispose()
    }
}

$testParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$testRoot = Join-Path $testParent ('clashsharp-installer-profiles-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
$manifest = Join-Path $testRoot 'manifest.json'
try {
    # Only manifest presence is evaluated here; payload and signature validity have separate gates.
    [IO.File]::WriteAllText($manifest, '{}', [Text.UTF8Encoding]::new($false))
    $formal = '-p:ClashSharpFormalInstallerBuild=true'
    $manifestProperty = "-p:ClashSharpInstallerReleaseManifestPath=$manifest"
    $enabled = Get-ClashSharpInstallerMutationRuntimeProperty
    $disabled = Get-ClashSharpInstallerMutationRuntimeProperty -Development
    $profiles = @(
        @{ name = 'preview'; enabled = $false; arguments = @() },
        @{ name = 'development'; enabled = $false; arguments = @($formal, $manifestProperty, $disabled) },
        @{ name = 'signed'; enabled = $true; arguments = @($formal, $manifestProperty, $enabled) }
    )
    foreach ($profile in $profiles) {
        $result = Invoke-InstallerProfileBuild -Arguments (@('-getProperty:ClashSharpEnableInstallerMutationRuntime,DefineConstants,SelfContained,PublishSingleFile') + $profile.arguments)
        if ($result.exitCode -ne 0) { throw "MSBuild profile $($profile.name) failed: $($result.output)$($result.error)" }
        $properties = ($result.output | ConvertFrom-Json).Properties
        $expected = if ($profile.enabled) { 'true' } else { 'false' }
        if ($properties.ClashSharpEnableInstallerMutationRuntime -cne $expected -or
            (($properties.DefineConstants -split ';') -ccontains 'CLASHSHARP_INSTALLER_MUTATION_RUNTIME') -ne $profile.enabled -or
            $properties.SelfContained -cne 'true' -or $properties.PublishSingleFile -cne 'true') {
            throw "MSBuild profile $($profile.name) selected an incorrect runtime or deployment model."
        }
    }
    $invalid = @(
        @{ arguments = @($enabled, $manifestProperty); diagnostic = 'requires ClashSharpFormalInstallerBuild=true' },
        @{ arguments = @($enabled, $formal); diagnostic = 'requires an embedded release manifest' },
        @{ arguments = @($enabled, $formal, "-p:ClashSharpInstallerReleaseManifestPath=$manifest.missing"); diagnostic = 'requires an embedded release manifest' }
    )
    foreach ($case in $invalid) {
        $result = Invoke-InstallerProfileBuild -Arguments (@('-t:ValidateClashSharpInstallerMutationRuntime') + $case.arguments)
        if ($result.exitCode -eq 0 -or -not $result.output.Contains($case.diagnostic, [StringComparison]::Ordinal)) {
            throw 'MSBuild admitted an incomplete installer activation profile or failed for an unrelated reason.'
        }
    }
    Write-Output 'Installer build profiles passed: 3 evaluated profiles and 3 rejected activation combinations.'
} finally {
    $resolved = Assert-ClashSharpOrdinaryPath -LiteralPath $testRoot -RequireDirectory
    if ([IO.Path]::GetDirectoryName($resolved) -cne $testParent -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('clashsharp-installer-profiles-', [StringComparison]::Ordinal)) {
        throw 'Refusing profile fixture cleanup outside the owned temporary directory.'
    }
    if (Test-Path -LiteralPath $manifest) {
        $null = Assert-ClashSharpOrdinaryPath -LiteralPath $manifest -RequireFile
        Remove-Item -LiteralPath $manifest -Force
    }
    if (@(Get-ChildItem -LiteralPath $resolved -Force).Count -ne 0) {
        throw 'Unexpected files remain in the profile fixture directory.'
    }
    Remove-Item -LiteralPath $resolved
}
