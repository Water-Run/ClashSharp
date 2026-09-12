#Requires -Version 7.6

<#
.SYNOPSIS
    Exports one default candidate from an already built ClashSharp assembly for isolated mihomo -t validation.
.DESCRIPTION
    Calls the real runtime configuration builder with a fixed test credential. Writes only a newly
    created probe directory; does not open application settings or start mihomo, the app, or a service.
.PARAMETER AssemblyPath
    Absolute or relative path to a built ClashSharp.dll with its dependencies beside it.
.PARAMETER OutputRoot
    Existing directory under which a unique probe directory is created. Defaults to the system temp directory.
.EXAMPLE
    ./tools/New-DefaultConfigurationProbe.ps1 -AssemblyPath ./ClashSharp/ClashSharp/bin/x64/Release/net10.0-windows10.0.22000.0/win-x64/ClashSharp.dll
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$AssemblyPath,

    [ValidateNotNullOrEmpty()]
    [string]$OutputRoot = [System.IO.Path]::GetTempPath(),

    [ValidateSet('Disabled', 'Standby', 'RuleTakeover', 'FullTakeover')]
    [string]$Mode = 'Disabled',

    [ValidateRange(1, 65535)]
    [int]$MixedPort = 10000,

    [switch]$TransparentProxyEnabled
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedAssembly = (Get-Item -LiteralPath $AssemblyPath -ErrorAction Stop).FullName
$resolvedOutputRoot = (Get-Item -LiteralPath $OutputRoot -ErrorAction Stop).FullName
if (-not [System.IO.Directory]::Exists($resolvedOutputRoot)) {
    throw 'OutputRoot must be an existing directory.'
}

$builderAssembly = [System.Reflection.Assembly]::LoadFrom($resolvedAssembly)
$builderType = $builderAssembly.GetType('ClashSharp.Service.MihomoRuntimeConfigurationBuilder', $true)
$builderMethod = $builderType.GetMethod('BuildDefaultConfiguration', [System.Reflection.BindingFlags]'Public, Static')
if ($null -eq $builderMethod) {
    throw 'The specified assembly does not expose the expected runtime configuration builder.'
}

$modeType = $builderMethod.GetParameters()[1].ParameterType
$modeValue = [System.Enum]::Parse($modeType, $Mode)
$testCredential = '1' * 64
$candidate = [string]$builderMethod.Invoke($null, [object[]]@(
    $MixedPort, $modeValue, [bool]$TransparentProxyEnabled, $testCredential
))

$probeDirectory = Join-Path $resolvedOutputRoot ('clashsharp-default-probe-' + [System.Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $probeDirectory -ErrorAction Stop
$candidatePath = Join-Path $probeDirectory 'config.yaml'
[System.IO.File]::WriteAllText($candidatePath, $candidate, [System.Text.UTF8Encoding]::new($false))
$manifest = [ordered]@{
    schemaVersion = 1
    assemblyPath = $resolvedAssembly
    assemblySha256 = (Get-FileHash -LiteralPath $resolvedAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
    mode = $Mode
    mixedPort = $MixedPort
    transparentProxyEnabled = [bool]$TransparentProxyEnabled
    usesFixedTestCredential = $true
    candidateSha256 = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash.ToLowerInvariant()
    createdAtUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
}
$manifestPath = Join-Path $probeDirectory 'probe.json'
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    ProbeDirectory = $probeDirectory
    CandidatePath = $candidatePath
    ManifestPath = $manifestPath
    Mode = $Mode
    UsesFixedTestCredential = $true
}
