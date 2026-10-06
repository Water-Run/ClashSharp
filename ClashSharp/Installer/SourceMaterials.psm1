#Requires -Version 7.0
Set-StrictMode -Version Latest
Import-Module -Name (Join-Path $PSScriptRoot 'PackagingContract.psm1')

function Get-ClashSharpSourceHash {
    param([byte[]] $Bytes)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Read-ClashSharpSourceDocument {
    param([string] $LiteralPath, [int] $MaximumLength = 65536)
    $path = Assert-ClashSharpOrdinaryPath -LiteralPath $LiteralPath -RequireFile
    $stream = [IO.File]::Open($path, 'Open', 'Read', 'Read')
    try {
        if ($stream.Length -lt 1 -or $stream.Length -gt $MaximumLength) { throw 'Source document exceeds its byte contract.' }
        $bytes = [byte[]]::new([int]$stream.Length)
        $stream.ReadExactly($bytes)
        return ,$bytes
    } finally { $stream.Dispose() }
}

function Get-ClashSharpSourceStreamHash {
    param([IO.Stream] $Stream, [long] $ExpectedLength)
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $buffer = [byte[]]::new(65536)
        $length = 0L
        while (($count = $Stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $length += $count
            if ($length -gt $ExpectedLength) { throw 'Source input exceeded its byte budget.' }
            $hash.AppendData($buffer, 0, $count)
        }
        if ($length -ne $ExpectedLength) { throw 'Source input length mismatch.' }
        return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    } finally { $hash.Dispose() }
}

function Get-ClashSharpMihomoSourceManifest {
    <#
    .SYNOPSIS
        Loads fixed source inputs and binds them to the repository's bundled core manifest.
    .DESCRIPTION
        Reads bounded ordinary files, verifies the complete role set and public origins, and
        rejects a different binary/version or an unsafe cache/archive name. No download occurs.
    .PARAMETER RepositoryRoot
        Repository containing the maintained Installer and bundled-core manifests.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $RepositoryRoot)
    $root = Assert-ClashSharpOrdinaryPath -LiteralPath $RepositoryRoot -RequireDirectory
    $bytes = Read-ClashSharpSourceDocument -LiteralPath (Join-Path $root 'ClashSharp/Installer/source-materials.json')
    $manifest = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -AsHashtable -Depth 8
    $binaryBytes = Read-ClashSharpSourceDocument -LiteralPath (Join-Path $root 'ClashSharp/ClashSharp/Binaries/mihomo-manifest.json')
    $binary = [Text.Encoding]::UTF8.GetString($binaryBytes) | ConvertFrom-Json -AsHashtable -Depth 4
    if ($manifest.schemaVersion -ne 1 -or $manifest.productVersion -cne '1.0.0' -or
        $manifest.repository -cne 'MetaCubeX/mihomo' -or $manifest.version -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+$' -or
        $manifest.goVersion -cnotmatch '^go[0-9]+\.[0-9]+\.[0-9]+$' -or $manifest.sourceCommit -cnotmatch '^[0-9a-f]{40}$' -or
        $manifest.bundledBinary.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $manifest.bundledBinary.length -lt 1 -or
        $manifest.bundledBinary.length -gt 268435456 -or $manifest.bundledBinary.vcsModified -isnot [bool] -or
        $manifest.bundledBinary.releaseAssetId -lt 1 -or $manifest.bundledBinary.releaseAssetSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $binary.schemaVersion -ne 1 -or $binary.version -cne $manifest.version -or
        $binary.sha256 -cne $manifest.bundledBinary.sha256 -or $binary.length -ne $manifest.bundledBinary.length -or
        $manifest.files.Count -ne 4) { throw 'Source materials are not bound to the selected bundled core.' }
    $expected = @{ source = 'source.tar.gz'; vendor = 'vendor.tar.gz'; toolchain = 'toolchain.tar.gz'; 'public-ca' = 'ca-certificates.crt' }
    $roles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        if ($file.role -cnotin @('source', 'vendor', 'toolchain', 'public-ca') -or -not $roles.Add($file.role) -or
            $file.archivePath -cne $expected[$file.role] -or $file.file -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{1,100}$' -or
            -not $names.Add($file.file) -or ($file.length -isnot [long] -and $file.length -isnot [int]) -or
            $file.length -lt 1 -or $file.length -gt 268435456 -or $file.sha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw 'Source manifest contains an invalid or duplicate input.'
        }
        if ($file.role -ceq 'public-ca') {
            if (($file.binaryOffset -isnot [long] -and $file.binaryOffset -isnot [int]) -or $file.binaryOffset -lt 0 -or
                $file.length -gt 1048576 -or $file.binaryOffset -gt ($manifest.bundledBinary.length - $file.length)) {
                throw 'Observed public CA range is outside the fixed binary.'
            }
        } else {
            $expectedUrl = if ($file.role -ceq 'source') { 'https://codeload.github.com/MetaCubeX/mihomo/tar.gz/' + $manifest.sourceCommit }
                else { 'https://github.com/MetaCubeX/mihomo/releases/download/' + $manifest.version + '/' + $file.archivePath }
            if ($file.sourceUrl -cne $expectedUrl) { throw 'Source input origin is outside the fixed upstream contract.' }
        }
    }
    if (-not $roles.SetEquals([string[]]@('source', 'vendor', 'toolchain', 'public-ca'))) { throw 'Source manifest must contain all four canonical input roles.' }
    return [pscustomobject]@{ Root = $root; Manifest = $manifest; ManifestSha256 = Get-ClashSharpSourceHash $bytes }
}

function Get-ClashSharpSourceTemplates {
    param([string] $RepositoryRoot)
    $root = Join-Path $RepositoryRoot 'ClashSharp/Installer'
    return [ordered]@{
        'BUILD-OFFLINE.py' = Read-ClashSharpSourceDocument -LiteralPath (Join-Path $root 'SourceMaterials/BUILD-OFFLINE.py')
        'LICENSE' = Read-ClashSharpSourceDocument -LiteralPath (Join-Path $RepositoryRoot 'ClashSharp/ClashSharp/Binaries/mihomo-LICENSE.txt')
        'README.md' = Read-ClashSharpSourceDocument -LiteralPath (Join-Path $root 'SourceMaterials/README.md')
    }
}

function New-ClashSharpMihomoSourceMaterials {
    <#
    .SYNOPSIS
        Generates a deterministic pinned source-materials ZIP from an offline input cache.
    .DESCRIPTION
        Holds all four verified input files read-locked while streaming them into a fresh ZIP.
        Validates the completed temporary archive before an atomic, non-overwriting promotion.
        Does not extract archives, execute inputs, download, sign, or change certificates.
    .PARAMETER RepositoryRoot
        Repository whose source and binary pins define the release contract.
    .PARAMETER InputRoot
        Ordinary offline cache with the exact four pinned files.
    .PARAMETER OutputPath
        New ZIP destination. An existing file is preserved and rejected.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $InputRoot, [Parameter(Mandatory)][string] $OutputPath)
    $pins = Get-ClashSharpMihomoSourceManifest -RepositoryRoot $RepositoryRoot
    $cache = Assert-ClashSharpOrdinaryPath -LiteralPath $InputRoot -RequireDirectory
    $destination = Assert-ClashSharpOrdinaryPath -LiteralPath $OutputPath -AllowMissing
    if (Test-Path -LiteralPath $destination) { throw 'Source-materials output already exists; inspect it instead of replacing it.' }
    $parent = Assert-ClashSharpOrdinaryPath -LiteralPath (Split-Path -Parent $destination) -AllowMissing
    $null = New-Item -ItemType Directory -Path $parent -Force
    $null = Assert-ClashSharpOrdinaryPath -LiteralPath $parent -RequireDirectory
    $temporary = Join-Path $parent ('.source-materials-' + [Guid]::NewGuid().ToString('N') + '.zip')
    $streams = [Collections.Generic.Dictionary[string, IO.Stream]]::new([StringComparer]::Ordinal)
    $documents = Get-ClashSharpSourceTemplates -RepositoryRoot $pins.Root
    $inventory = [Collections.Generic.List[object]]::new()
    try {
        foreach ($file in $pins.Manifest.files) {
            $path = Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $cache $file.file) -RequireFile
            $stream = [IO.File]::Open($path, 'Open', 'Read', 'Read')
            $streams.Add($file.archivePath, $stream)
            if ($stream.Length -ne $file.length -or (Get-ClashSharpSourceStreamHash $stream $file.length) -cne $file.sha256) {
                throw ('Pinned source input mismatch: ' + $file.file)
            }
            $stream.Position = 0
            $inventory.Add([ordered]@{ path = $file.archivePath; role = $file.role; length = $file.length; sha256 = $file.sha256 })
        }
        foreach ($name in $documents.Keys) {
            $inventory.Add([ordered]@{ path = $name; role = 'support'; length = $documents[$name].Length; sha256 = Get-ClashSharpSourceHash $documents[$name] })
        }
        $metadata = [ordered]@{
            schemaVersion = 1; productVersion = '1.0.0'; repository = $pins.Manifest.repository
            version = $pins.Manifest.version; sourceCommit = $pins.Manifest.sourceCommit; goVersion = $pins.Manifest.goVersion
            sourceManifestSha256 = $pins.ManifestSha256
            bundledBinary = [ordered]@{ length = $pins.Manifest.bundledBinary.length; sha256 = $pins.Manifest.bundledBinary.sha256
                releaseAssetId = $pins.Manifest.bundledBinary.releaseAssetId; releaseAssetSha256 = $pins.Manifest.bundledBinary.releaseAssetSha256
                vcsModified = $pins.Manifest.bundledBinary.vcsModified }
            scope = 'Pinned offline compilation inputs; byte-identical reproduction is not claimed.'
            files = @($inventory)
        }
        $documents['SOURCE-MATERIALS.json'] = [Text.UTF8Encoding]::new($false).GetBytes(($metadata | ConvertTo-Json -Depth 8) + [char]10)
        [string[]]$names = @($streams.Keys) + @($documents.Keys)
        [Array]::Sort($names, [StringComparer]::Ordinal)
        $output = [IO.File]::Open($temporary, 'CreateNew', 'ReadWrite', 'None')
        try {
            $zip = [IO.Compression.ZipArchive]::new($output, [IO.Compression.ZipArchiveMode]::Create, $true)
            try {
                foreach ($name in $names) {
                    $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::NoCompression)
                    $entry.LastWriteTime = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                    $entry.ExternalAttributes = 0
                    $target = $entry.Open()
                    try {
                        if ($streams.ContainsKey($name)) { $streams[$name].CopyTo($target) }
                        else { $target.Write($documents[$name], 0, $documents[$name].Length) }
                    } finally { $target.Dispose() }
                }
            } finally { $zip.Dispose() }
        } finally { $output.Dispose() }
        $validated = Get-ClashSharpMihomoSourceMaterialsContract -RepositoryRoot $pins.Root -LiteralPath $temporary
        $null = Assert-ClashSharpOrdinaryPath -LiteralPath $destination -AllowMissing
        [IO.File]::Move($temporary, $destination)
        return [pscustomobject]@{ Path = $destination; Length = $validated.Length; Sha256 = $validated.Sha256
            SourceCommit = $validated.SourceCommit; BinarySha256 = $validated.BinarySha256; FileCount = $validated.FileCount }
    } finally {
        foreach ($stream in $streams.Values) { $stream.Dispose() }
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Get-ClashSharpMihomoSourceMaterialsContract {
    <#
    .SYNOPSIS
        Independently verifies a ZIP against the exact maintained source inputs and support files.
    .DESCRIPTION
        Refuses extra/duplicate/case-colliding entries, changed metadata, oversized streams,
        incomplete material sets and altered scripts or licenses without extracting any path.
    .PARAMETER RepositoryRoot
        Repository defining the expected source/binary pins and support-file bytes.
    .PARAMETER LiteralPath
        Ordinary ZIP archive to read under one held file handle.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $RepositoryRoot, [Parameter(Mandatory)][string] $LiteralPath)
    $pins = Get-ClashSharpMihomoSourceManifest -RepositoryRoot $RepositoryRoot
    $documents = Get-ClashSharpSourceTemplates -RepositoryRoot $pins.Root
    $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($file in $pins.Manifest.files) { $expected.Add($file.archivePath, [pscustomobject]@{ Role = $file.role; Length = $file.length; Sha256 = $file.sha256 }) }
    foreach ($name in $documents.Keys) { $expected.Add($name, [pscustomobject]@{ Role = 'support'; Length = $documents[$name].Length; Sha256 = Get-ClashSharpSourceHash $documents[$name] }) }
    $path = Assert-ClashSharpOrdinaryPath -LiteralPath $LiteralPath -RequireFile
    $stream = [IO.File]::Open($path, 'Open', 'Read', 'Read')
    try {
        if ($stream.Length -lt 1 -or $stream.Length -gt 1073741824) { throw 'Source ZIP is outside its byte budget.' }
        $length = $stream.Length
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            if ($zip.Entries.Count -ne 8) { throw 'Source ZIP contains an unexpected material count.' }
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $manifest = $null
            foreach ($entry in $zip.Entries) {
                if (-not $seen.Add($entry.FullName) -or ($entry.FullName -cne 'SOURCE-MATERIALS.json' -and -not $expected.ContainsKey($entry.FullName))) {
                    throw 'Source ZIP contains an unknown, duplicate or case-colliding path.'
                }
                $content = $entry.Open()
                try {
                    if ($entry.FullName -ceq 'SOURCE-MATERIALS.json') {
                        if ($entry.Length -lt 1 -or $entry.Length -gt 65536) { throw 'Source ZIP manifest is oversized.' }
                        $memory = [IO.MemoryStream]::new()
                        try {
                            $buffer = [byte[]]::new(4096)
                            while (($count = $content.Read($buffer, 0, $buffer.Length)) -gt 0) {
                                if ($memory.Length + $count -gt $entry.Length) { throw 'Source ZIP manifest exceeded its byte contract.' }
                                $memory.Write($buffer, 0, $count)
                            }
                            if ($memory.Length -ne $entry.Length) { throw 'Source ZIP manifest length mismatch.' }
                            $manifest = [Text.UTF8Encoding]::new($false, $true).GetString($memory.ToArray()) | ConvertFrom-Json -AsHashtable -Depth 8
                        } finally { $memory.Dispose() }
                    } else {
                        $pin = $expected[$entry.FullName]
                        if ($entry.Length -ne $pin.Length -or (Get-ClashSharpSourceStreamHash $content $pin.Length) -cne $pin.Sha256) { throw 'Source ZIP entry does not match its frozen input.' }
                    }
                } finally { $content.Dispose() }
            }
            if ($null -eq $manifest -or $manifest.schemaVersion -ne 1 -or $manifest.productVersion -cne '1.0.0' -or
                $manifest.sourceManifestSha256 -cne $pins.ManifestSha256 -or $manifest.sourceCommit -cne $pins.Manifest.sourceCommit -or
                $manifest.version -cne $pins.Manifest.version -or $manifest.goVersion -cne $pins.Manifest.goVersion -or
                $manifest.repository -cne $pins.Manifest.repository -or $manifest.bundledBinary.sha256 -cne $pins.Manifest.bundledBinary.sha256 -or
                $manifest.bundledBinary.length -ne $pins.Manifest.bundledBinary.length -or
                $manifest.bundledBinary.releaseAssetId -ne $pins.Manifest.bundledBinary.releaseAssetId -or
                $manifest.bundledBinary.releaseAssetSha256 -cne $pins.Manifest.bundledBinary.releaseAssetSha256 -or
                $manifest.bundledBinary.vcsModified -isnot [bool] -or $manifest.bundledBinary.vcsModified -ne $pins.Manifest.bundledBinary.vcsModified -or
                $manifest.scope -cne 'Pinned offline compilation inputs; byte-identical reproduction is not claimed.' -or
                $manifest.files.Count -ne 7) { throw 'Source ZIP metadata is not bound to the selected core.' }
            $references = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($file in $manifest.files) {
                if (-not $expected.ContainsKey($file.path) -or -not $references.Add($file.path) -or
                    $file.role -cne $expected[$file.path].Role -or $file.length -ne $expected[$file.path].Length -or
                    $file.sha256 -cne $expected[$file.path].Sha256) { throw 'Source ZIP inventory differs from the verified material set.' }
            }
        } finally { $zip.Dispose() }
        $stream.Position = 0
        $hash = Get-ClashSharpSourceStreamHash $stream $length
        return [pscustomobject]@{ Length = $length; Sha256 = $hash; SourceCommit = $pins.Manifest.sourceCommit
            BinarySha256 = $pins.Manifest.bundledBinary.sha256; FileCount = 8 }
    } finally { $stream.Dispose() }
}

Export-ModuleMember -Function Get-ClashSharpMihomoSourceManifest, New-ClashSharpMihomoSourceMaterials, Get-ClashSharpMihomoSourceMaterialsContract
