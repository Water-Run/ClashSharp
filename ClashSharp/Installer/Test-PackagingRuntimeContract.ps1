#Requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module -Name (Join-Path $PSScriptRoot 'PackagingContract.psm1') -Force

<#
.SYNOPSIS
Creates a small ZIP fixture with an independently described offline runtime payload.
.DESCRIPTION
Writes only the supplied byte entries to a new file; no package registration or signing occurs.
.PARAMETER LiteralPath
Previously absent fixture ZIP path in the test-owned directory.
.PARAMETER Entries
Map of ZIP entry names to fixture bytes.
#>
function New-RuntimePackageFixture {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $LiteralPath,
        [Parameter(Mandatory)]
        [Collections.IDictionary] $Entries
    )

    $archive = [IO.Compression.ZipFile]::Open($LiteralPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries.Keys) {
            $stream = $archive.CreateEntry($name).Open()
            try {
                $bytes = [byte[]]$Entries[$name]
                $stream.Write($bytes, 0, $bytes.Length)
            } finally {
                $stream.Dispose()
            }
        }
    } finally {
        $archive.Dispose()
    }
}

$testParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $testParent ('clashsharp-runtime-contract-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
try {
    $native = [byte[]]::new(70)
    $native[0] = 0x4D
    $native[1] = 0x5A
    $native[60] = 64
    $native[64] = 0x50
    $native[65] = 0x45
    $native[68] = 0x64
    $native[69] = 0x86
    $runtimeJson = '{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.5"}]}}'
    $depsJson = '{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/win-x64"}}'
    $valid = @{
        'ClashSharp.exe' = $native
        'ClashSharp.dll' = [byte[]]@(1)
        'ClashSharp.runtimeconfig.json' = [Text.Encoding]::UTF8.GetBytes($runtimeJson)
        'ClashSharp.deps.json' = [Text.Encoding]::UTF8.GetBytes($depsJson)
        'coreclr.dll' = $native
        'hostfxr.dll' = $native
        'hostpolicy.dll' = $native
        'System.Private.CoreLib.dll' = [byte[]]@(1)
    }
    $validPath = Join-Path $testRoot 'valid.msix'
    New-RuntimePackageFixture -LiteralPath $validPath -Entries $valid
    $contract = Get-ClashSharpMsixDotNetRuntimeContract -LiteralPath $validPath
    if ($contract.version -cne '10.0.5' -or $contract.runtimeIdentifier -cne 'win-x64' -or
        -not $contract.selfContained -or $contract.requiredFiles.Count -ne 8) {
        throw 'Valid self-contained package returned an incorrect runtime receipt.'
    }

    $cases = [Collections.Generic.List[object]]::new()
    foreach ($missingName in $valid.Keys) {
        $entries = $valid.Clone()
        $entries.Remove($missingName)
        $cases.Add(@{ Name = "missing $missingName"; Entries = $entries })
    }
    foreach ($sharedProperty in @('framework', 'frameworks')) {
        $entries = $valid.Clone()
        $options = $runtimeJson | ConvertFrom-Json -AsHashtable
        $options.runtimeOptions[$sharedProperty] = @{}
        $entries['ClashSharp.runtimeconfig.json'] = [Text.Encoding]::UTF8.GetBytes(
            ($options | ConvertTo-Json -Depth 6 -Compress))
        $cases.Add(@{ Name = "shared $sharedProperty"; Entries = $entries })
    }
    foreach ($invalidRuntime in @(
        '{"runtimeOptions":{"tfm":"net10.0"}}',
        $runtimeJson.Replace('10.0.5', '9.0.1'),
        $runtimeJson.Replace('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App'),
        '{'
    )) {
        $entries = $valid.Clone()
        $entries['ClashSharp.runtimeconfig.json'] = [Text.Encoding]::UTF8.GetBytes($invalidRuntime)
        $cases.Add(@{ Name = 'invalid runtime metadata'; Entries = $entries })
    }
    $wrongRid = $valid.Clone()
    $wrongRid['ClashSharp.deps.json'] = [Text.Encoding]::UTF8.GetBytes($depsJson.Replace('win-x64', 'win-arm64'))
    $cases.Add(@{ Name = 'wrong RID'; Entries = $wrongRid })

    $wrongNative = $valid.Clone()
    $arm64 = [byte[]]$native.Clone()
    $arm64[68] = 0x64
    $arm64[69] = 0xAA
    $wrongNative['coreclr.dll'] = $arm64
    $cases.Add(@{ Name = 'ARM64 CLR with x64 metadata'; Entries = $wrongNative })
    $truncated = $valid.Clone()
    $truncated['hostfxr.dll'] = [byte[]]@(1)
    $cases.Add(@{ Name = 'truncated native host'; Entries = $truncated })
    $oversized = $valid.Clone()
    $oversized['ClashSharp.runtimeconfig.json'] = [byte[]]::new(4194305)
    $cases.Add(@{ Name = 'oversized runtime metadata'; Entries = $oversized })

    $colliding = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::Ordinal)
    foreach ($name in $valid.Keys) { $colliding.Add($name, $valid[$name]) }
    $colliding.Add('CORECLR.dll', $native)
    $cases.Add(@{ Name = 'case-colliding runtime file'; Entries = $colliding })

    $index = 0
    foreach ($case in $cases) {
        $path = Join-Path $testRoot ("invalid-$index.msix")
        New-RuntimePackageFixture -LiteralPath $path -Entries $case.Entries
        $rejected = $false
        try { $null = Get-ClashSharpMsixDotNetRuntimeContract -LiteralPath $path }
        catch { $rejected = $true }
        if (-not $rejected) { throw "Runtime contract accepted $($case.Name)." }
        $index++
    }
    Write-Output "Packaging runtime contract passed: 1 valid package and $index rejected packages."
} finally {
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    if (-not [IO.Path]::GetDirectoryName($resolvedRoot).Equals(
            $testParent.TrimEnd([IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolvedRoot).StartsWith('clashsharp-runtime-contract-', [StringComparison]::Ordinal)) {
        throw 'Refusing test cleanup outside the owned temporary directory.'
    }
    Get-ChildItem -LiteralPath $resolvedRoot -File | Remove-Item -Force
    Remove-Item -LiteralPath $resolvedRoot
}
