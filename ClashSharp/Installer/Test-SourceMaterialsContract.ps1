#Requires -Version 7.0
<#
.SYNOPSIS
    Exercises source-materials generation and release binding with isolated offline fixtures.
.DESCRIPTION
    Checks deterministic bytes, immutable input and binary pins, support-file hashes, complete
    inventories, unsafe ZIP names and forged metadata. Does not download or execute inputs.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module -Name (Join-Path $PSScriptRoot 'SourceMaterials.psm1') -Force
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testParent = Join-Path $repositoryRoot 'artifacts/installer/source-materials-contract-tests'
$runId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $testParent $runId
$null = New-Item -ItemType Directory -Path $testRoot -Force
$utf8 = [Text.UTF8Encoding]::new($false)
$script:assertions = 0
$script:scenarios = [Collections.Generic.List[string]]::new()

function Assert-Condition([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:assertions++
}
function Write-Json([string] $Path, $Value) {
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12) + [char]10, $utf8)
}
function New-Fixture([string] $Name) {
    $root = Join-Path $testRoot $Name
    $cache = Join-Path $root 'cache'
    $support = Join-Path $root 'ClashSharp/Installer/SourceMaterials'
    $binaries = Join-Path $root 'ClashSharp/ClashSharp/Binaries'
    foreach ($path in @($cache, $support, $binaries)) { $null = New-Item -ItemType Directory -Path $path -Force }
    foreach ($file in @('README.md', 'BUILD-OFFLINE.py')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('SourceMaterials/' + $file)) -Destination (Join-Path $support $file)
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'ClashSharp/ClashSharp/Binaries/mihomo-LICENSE.txt') -Destination (Join-Path $binaries 'mihomo-LICENSE.txt')
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'source-materials.json') -Raw | ConvertFrom-Json -AsHashtable
    $manifest.bundledBinary.length = 65536
    $manifest.bundledBinary.sha256 = ('a' * 64)
    foreach ($file in $manifest.files) {
        $bytes = $utf8.GetBytes('Isolated synthetic input: ' + $file.role)
        [IO.File]::WriteAllBytes((Join-Path $cache $file.file), $bytes)
        $file.length = $bytes.Length
        $file.sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        if ($file.role -ceq 'public-ca') { $file.binaryOffset = 4096 }
    }
    Write-Json (Join-Path $root 'ClashSharp/Installer/source-materials.json') $manifest
    Write-Json (Join-Path $binaries 'mihomo-manifest.json') @{ schemaVersion = 1; version = $manifest.version; length = 65536; sha256 = ('a' * 64) }
    return [pscustomobject]@{ Root = $root; Cache = $cache; Manifest = $manifest }
}
function Assert-Rejected([scriptblock] $Operation, [string] $Scenario) {
    $rejected = $false
    try { & $Operation | Out-Null } catch { $rejected = $true }
    Assert-Condition $rejected ('Invalid source-materials contract was accepted: ' + $Scenario)
    $script:scenarios.Add($Scenario)
}
function Read-Zip([string] $Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $zip.Entries) {
            $content = $entry.Open()
            $memory = [IO.MemoryStream]::new()
            try {
                $content.CopyTo($memory)
                [pscustomobject]@{ Name = $entry.FullName; Bytes = $memory.ToArray() }
            } finally { $content.Dispose(); $memory.Dispose() }
        }
    } finally { $zip.Dispose() }
}
function Write-Zip([string] $Path, [object[]] $Entries) {
    $stream = [IO.File]::Open($Path, 'CreateNew', 'ReadWrite', 'None')
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($item in $Entries) {
                $entry = $zip.CreateEntry($item.Name)
                $target = $entry.Open()
                try { $target.Write($item.Bytes, 0, $item.Bytes.Length) } finally { $target.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
}

try {
    $fixture = New-Fixture 'valid'
    $first = New-ClashSharpMihomoSourceMaterials -RepositoryRoot $fixture.Root -InputRoot $fixture.Cache -OutputPath (Join-Path $fixture.Root 'first.zip')
    $second = New-ClashSharpMihomoSourceMaterials -RepositoryRoot $fixture.Root -InputRoot $fixture.Cache -OutputPath (Join-Path $fixture.Root 'second.zip')
    Assert-Condition ($first.Sha256 -ceq $second.Sha256) 'Identical pinned inputs did not produce identical ZIP bytes.'
    Assert-Condition ($first.FileCount -eq 8) 'Source archive did not contain the complete eight-file set.'
    $contract = Get-ClashSharpMihomoSourceMaterialsContract -RepositoryRoot $fixture.Root -LiteralPath $first.Path
    Assert-Condition ($contract.Sha256 -ceq (Get-FileHash -LiteralPath $first.Path).Hash.ToLowerInvariant()) 'Independent whole-file digest did not match.'
    $script:scenarios.Add('deterministic-complete-archive')
    $entries = @(Read-Zip $first.Path)
    $sourceEntry = @($entries | Where-Object Name -CEQ 'source.tar.gz')[0]
    Assert-Condition ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceEntry.Bytes)).ToLowerInvariant() -ceq $fixture.Manifest.files[0].sha256) 'Source input bytes changed in the archive.'
    Assert-Rejected { New-ClashSharpMihomoSourceMaterials -RepositoryRoot $fixture.Root -InputRoot $fixture.Cache -OutputPath $first.Path } 'existing-output-preserved'
    Assert-Condition ((Get-FileHash -LiteralPath $first.Path).Hash.ToLowerInvariant() -ceq $first.Sha256) 'Existing output was replaced.'

    foreach ($fault in @('missing-input', 'changed-input', 'wrong-length', 'duplicate-role', 'case-changed-role', 'wrong-origin', 'unsafe-name', 'wrong-binary', 'ca-outside-binary')) {
        $bad = New-Fixture $fault
        $pins = $bad.Manifest
        switch ($fault) {
            'missing-input' { Remove-Item -LiteralPath (Join-Path $bad.Cache $pins.files[0].file) }
            'changed-input' {
                $path = Join-Path $bad.Cache $pins.files[0].file
                $bytes = [IO.File]::ReadAllBytes($path); $bytes[0] = [byte]($bytes[0] -bxor 1); [IO.File]::WriteAllBytes($path, $bytes)
            }
            'wrong-length' { $pins.files[0].length++ }
            'duplicate-role' { $pins.files[1].role = 'source' }
            'case-changed-role' { $pins.files[1].role = 'VENDOR' }
            'wrong-origin' { $pins.files[0].sourceUrl = 'https://example.invalid/untrusted-input' }
            'unsafe-name' { $pins.files[0].file = '../outside.tar.gz' }
            'wrong-binary' { $pins.bundledBinary.sha256 = ('b' * 64) }
            'ca-outside-binary' { $pins.files[3].binaryOffset = 65536 }
        }
        Write-Json (Join-Path $bad.Root 'ClashSharp/Installer/source-materials.json') $pins
        $out = Join-Path $bad.Root 'rejected.zip'
        Assert-Rejected { New-ClashSharpMihomoSourceMaterials -RepositoryRoot $bad.Root -InputRoot $bad.Cache -OutputPath $out } $fault
        Assert-Condition (-not (Test-Path -LiteralPath $out)) 'Rejected source inputs left a promoted archive.'
        Assert-Condition (@(Get-ChildItem -LiteralPath $bad.Root -File -Filter '.source-materials-*').Count -eq 0) 'Rejected source inputs left a temporary archive.'
    }

    foreach ($fault in @('changed-archive-input', 'changed-script', 'extra-entry', 'missing-entry', 'traversal-entry', 'duplicate-entry',
            'case-collision', 'wrong-source-commit', 'wrong-binary-metadata', 'hidden-vcs-modification', 'false-reproduction-claim', 'duplicate-inventory', 'oversized-manifest')) {
        $altered = @(Read-Zip $first.Path)
        $metadataEntry = @($altered | Where-Object Name -CEQ 'SOURCE-MATERIALS.json')[0]
        $metadata = $utf8.GetString($metadataEntry.Bytes) | ConvertFrom-Json -AsHashtable
        switch ($fault) {
            'changed-archive-input' { @($altered | Where-Object Name -CEQ 'source.tar.gz')[0].Bytes[0] = [byte]65 }
            'changed-script' { @($altered | Where-Object Name -CEQ 'BUILD-OFFLINE.py')[0].Bytes[0] = [byte]65 }
            'extra-entry' { $altered += [pscustomobject]@{ Name = 'extra'; Bytes = $utf8.GetBytes('unexpected') } }
            'missing-entry' { $altered = @($altered | Where-Object Name -CNE 'vendor.tar.gz') }
            'traversal-entry' { @($altered | Where-Object Name -CEQ 'vendor.tar.gz')[0].Name = '../vendor.tar.gz' }
            'duplicate-entry' { @($altered | Where-Object Name -CEQ 'vendor.tar.gz')[0].Name = 'source.tar.gz' }
            'case-collision' { @($altered | Where-Object Name -CEQ 'vendor.tar.gz')[0].Name = 'SOURCE.TAR.GZ' }
            'wrong-source-commit' { $metadata.sourceCommit = ('b' * 40) }
            'wrong-binary-metadata' { $metadata.bundledBinary.releaseAssetId++ }
            'hidden-vcs-modification' { $metadata.bundledBinary.vcsModified = $false }
            'false-reproduction-claim' { $metadata.scope = 'Guaranteed byte-identical production binary.' }
            'duplicate-inventory' { $metadata.files[1] = $metadata.files[0] }
            'oversized-manifest' { $metadata.padding = ('x' * 65536) }
        }
        $metadataEntry.Bytes = $utf8.GetBytes(($metadata | ConvertTo-Json -Depth 8) + [char]10)
        $path = Join-Path $fixture.Root ($fault + '.zip')
        Write-Zip $path $altered
        Assert-Rejected { Get-ClashSharpMihomoSourceMaterialsContract -RepositoryRoot $fixture.Root -LiteralPath $path } $fault
    }
    $summary = [ordered]@{ passed = $true; scenarios = $script:scenarios.Count; assertions = $script:assertions
        downloadsInvoked = $false; inputsExecuted = $false; signingOrInstallationInvoked = $false }
    $summary | ConvertTo-Json -Compress
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved -cne (Join-Path $testParent $runId) -or -not $resolved.StartsWith($testParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unexpected source-test cleanup root.'
    }
    foreach ($entry in @((Get-Item -LiteralPath $resolved -Force)) + @(Get-ChildItem -LiteralPath $resolved -Force -Recurse)) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to clean a fixture containing a reparse entry.' }
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
