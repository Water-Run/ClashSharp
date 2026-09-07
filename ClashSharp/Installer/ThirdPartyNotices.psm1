#Requires -Version 7.0
Set-StrictMode -Version Latest
Import-Module -Name (Join-Path $PSScriptRoot 'PackagingContract.psm1')

function Read-ClashSharpNoticeBytes {
    <#
    .SYNOPSIS
        Reads a bounded document from an owned stream.
    .DESCRIPTION
        Rejects empty or oversized documents without extracting archive paths.
    .PARAMETER Stream
        Caller-owned readable stream.
    .PARAMETER MaximumLength
        Maximum accepted byte count.
    #>
    param([IO.Stream] $Stream, [int] $MaximumLength = 1048576)
    $memory = [IO.MemoryStream]::new()
    try {
        $buffer = [byte[]]::new(65536)
        while (($count = $Stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($memory.Length + $count -gt $MaximumLength) { throw 'Third-party document exceeds its byte budget.' }
            $memory.Write($buffer, 0, $count)
        }
        if ($memory.Length -eq 0) { throw 'Third-party document is empty.' }
        return ,$memory.ToArray()
    } finally { $memory.Dispose() }
}

function Read-ClashSharpNoticeFile {
    <#
    .SYNOPSIS
        Reads one ordinary local attribution input.
    .DESCRIPTION
        Validates every path ancestor and keeps the file read-locked until its bounded read ends.
    .PARAMETER LiteralPath
        Exact file path.
    .PARAMETER MaximumLength
        Maximum accepted byte count.
    #>
    param([string] $LiteralPath, [int] $MaximumLength = 1048576)
    $path = Assert-ClashSharpOrdinaryPath -LiteralPath $LiteralPath -RequireFile
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { return ,(Read-ClashSharpNoticeBytes -Stream $stream -MaximumLength $MaximumLength) }
    finally { $stream.Dispose() }
}

function Get-ClashSharpNoticeHash {
    <#
    .SYNOPSIS
        Computes the canonical digest of an attribution document.
    .DESCRIPTION
        Returns lowercase SHA-256 without changing the supplied bytes.
    .PARAMETER Bytes
        Complete bounded document bytes.
    #>
    param([byte[]] $Bytes)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Add-ClashSharpNoticeDocument {
    <#
    .SYNOPSIS
        Adds a content-addressed document to a notices archive.
    .DESCRIPTION
        Deduplicates identical bytes while retaining each original document name and source.
    .PARAMETER Documents
        Archive-owned path-to-bytes dictionary.
    .PARAMETER Bytes
        Exact original document bytes.
    .PARAMETER Name
        Display-only original name; never used as an extraction path.
    .PARAMETER Source
        Public provenance description or URL.
    #>
    param($Documents, [byte[]] $Bytes, [string] $Name, [string] $Source)
    $hash = Get-ClashSharpNoticeHash -Bytes $Bytes
    $extension = if ([IO.Path]::GetExtension($Name) -ieq '.rtf') { '.rtf' } else { '.txt' }
    $path = 'documents/' + $hash + $extension
    if (-not $Documents.ContainsKey($path)) { $Documents.Add($path, $Bytes) }
    return [ordered]@{ path = $path; length = $Bytes.Length; sha256 = $hash; originalName = $Name; source = $Source }
}

function Initialize-ClashSharpNuGetReader {
    <#
    .SYNOPSIS
        Loads the NuGet reader supplied by the repository's exact SDK.
    .DESCRIPTION
        Uses the installed SDK implementation for signed-package content hashes and integrity.
        It neither restores packages nor changes the package cache.
    .PARAMETER RepositoryRoot
        Repository containing the pinned global.json.
    #>
    param([string] $RepositoryRoot)
    $globalBytes = Read-ClashSharpNoticeFile -LiteralPath (Join-Path $RepositoryRoot 'global.json') -MaximumLength 16384
    $global = [Text.Encoding]::UTF8.GetString($globalBytes) | ConvertFrom-Json -AsHashtable
    $version = [string]$global.sdk.version
    if ($version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'The SDK pin is not an exact stable version.' }
    $lines = @(dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot locate the pinned SDK.' }
    $locations = @($lines | Where-Object { $_ -cmatch ('^' + [regex]::Escape($version) + ' \[(.+)\]$') } |
        ForEach-Object { $null = $_ -cmatch ('^' + [regex]::Escape($version) + ' \[(.+)\]$'); Join-Path $Matches[1] $version })
    if ($locations.Count -ne 1) { throw 'Expected exactly one installed copy of the pinned SDK.' }
    foreach ($name in @('NuGet.Common.dll', 'NuGet.Versioning.dll', 'NuGet.Frameworks.dll', 'NuGet.Packaging.dll')) {
        $path = Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $locations[0] $name) -RequireFile
        Add-Type -Path $path
    }
    $loaded = [NuGet.Packaging.PackageArchiveReader].Assembly.Location
    if (-not $loaded.Equals((Join-Path $locations[0] 'NuGet.Packaging.dll'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A different NuGet reader is already loaded into this process.'
    }
    return $version
}

function Add-ClashSharpNoticePackage {
    <#
    .SYNOPSIS
        Merges an exact dependency into the candidate inventory.
    .DESCRIPTION
        Rejects conflicting hashes and unsafe package identities before resolving cache paths.
    .PARAMETER Packages
        Inventory-owned package dictionary.
    .PARAMETER Id
        NuGet package identifier.
    .PARAMETER Version
        Exact restored version.
    .PARAMETER Hash
        NuGet content hash, including signed-package semantics.
    .PARAMETER Project
        Production project that references this dependency.
    .PARAMETER Folders
        Restore-declared local package cache roots.
    .PARAMETER Kind
        PackageReference or SdkDownload input kind.
    #>
    param($Packages, [string] $Id, [string] $Version, [string] $Hash, [string] $Project, $Folders, [string] $Kind)
    if ($Id -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,119}$' -or
        $Version -cnotmatch '^[0-9]+(\.[0-9]+){1,3}(-[A-Za-z0-9.-]+)?$') {
        throw 'A dependency has an unsafe or non-exact identity.'
    }
    $hashBytes = [Convert]::FromBase64String($Hash)
    if ($hashBytes.Length -ne 64 -or [Convert]::ToBase64String($hashBytes) -cne $Hash) {
        throw 'A dependency has a noncanonical SHA-512 content hash.'
    }
    $key = ($Id + '/' + $Version).ToLowerInvariant()
    if (-not $Packages.ContainsKey($key)) {
        $Packages.Add($key, [ordered]@{
            id = $Id; version = $Version; contentHash = $Hash
            projects = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            folders = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            kinds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        })
    }
    $package = $Packages[$key]
    if ($package.contentHash -cne $Hash) { throw 'Production projects disagree about a dependency content hash.' }
    $null = $package.projects.Add($Project)
    $null = $package.kinds.Add($Kind)
    foreach ($folder in $Folders) { $null = $package.folders.Add([string]$folder) }
}

function New-ClashSharpThirdPartyNotices {
    <#
    .SYNOPSIS
        Creates the offline attribution archive for all four production entry points.
    .DESCRIPTION
        Reads locked restore graphs and pinned SDK download dependencies, validates actual NuGet
        archive integrity, and preserves original package metadata, licenses, and notices. Adds
        the pinned upstream GeoData evidence and existing product/mihomo notices. No network or
        installation operation is performed. The output must be new; failures remove only the
        file created by this invocation. Build dependencies are explicitly included in scope.
    .PARAMETER RepositoryRoot
        Ordinary repository root containing the four production projects and reviewed catalog.
    .PARAMETER OutputPath
        New ZIP path beneath an existing ordinary directory.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $OutputPath
    )
    $root = Assert-ClashSharpOrdinaryPath -LiteralPath $RepositoryRoot -RequireDirectory
    $output = Assert-ClashSharpOrdinaryPath -LiteralPath $OutputPath -AllowMissing
    $null = Assert-ClashSharpOrdinaryPath -LiteralPath (Split-Path -Parent $output) -RequireDirectory
    if (Test-Path -LiteralPath $output) { throw 'The attribution output must be a new file.' }
    $sdkVersion = Initialize-ClashSharpNuGetReader -RepositoryRoot $root
    $catalogRoot = Join-Path $root 'ClashSharp/Installer/ThirdParty'
    $catalogBytes = Read-ClashSharpNoticeFile -LiteralPath (Join-Path $catalogRoot 'catalog.json')
    $catalog = [Text.Encoding]::UTF8.GetString($catalogBytes) | ConvertFrom-Json -AsHashtable -Depth 16
    if ($catalog.schemaVersion -ne 1 -or $catalog.productVersion -cne '1.0.0' -or
        $catalog.documents.Count -lt 1 -or $catalog.documents.Count -gt 256) { throw 'Unsupported attribution catalog.' }
    $documents = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::Ordinal)
    $supplements = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($document in $catalog.documents) {
        if ($document.file -cnotmatch '^[a-z0-9][a-z0-9.-]{0,119}$' -or $supplements.ContainsKey($document.file)) {
            throw 'The attribution catalog has an unsafe or duplicate document name.'
        }
        $bytes = Read-ClashSharpNoticeFile -LiteralPath (Join-Path $catalogRoot $document.file)
        if ($bytes.Length -ne $document.length -or (Get-ClashSharpNoticeHash -Bytes $bytes) -cne $document.sha256) {
            throw 'An attribution source document differs from its reviewed digest.'
        }
        $supplements.Add($document.file, (Add-ClashSharpNoticeDocument -Documents $documents -Bytes $bytes -Name $document.file -Source $document.sourceUrl))
    }
    $packages = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $projects = [Collections.Generic.List[object]]::new()
    foreach ($name in @('ClashSharp', 'ClashSharp.MihomoService', 'ClashSharp.RecoveryWatchdog', 'ClashSharp.Installer')) {
        $projectRoot = Join-Path $root ('ClashSharp/' + $name)
        $assetsBytes = Read-ClashSharpNoticeFile -LiteralPath (Join-Path $projectRoot 'obj/project.assets.json') -MaximumLength 16777216
        $assets = [Text.Encoding]::UTF8.GetString($assetsBytes) | ConvertFrom-Json -AsHashtable -Depth 64
        $lockBytes = Read-ClashSharpNoticeFile -LiteralPath (Join-Path $projectRoot 'packages.lock.json')
        $lock = [Text.Encoding]::UTF8.GetString($lockBytes) | ConvertFrom-Json -AsHashtable -Depth 32
        if ($assets.version -ne 3 -or $assets.project.restore.projectName -cne $name -or $lock.version -ne 1) {
            throw 'Unsupported or mismatched production restore graph.'
        }
        $projects.Add([ordered]@{ name = $name; lockSha256 = Get-ClashSharpNoticeHash -Bytes $lockBytes })
        foreach ($entry in $assets.libraries.GetEnumerator()) {
            $library = $entry.Value
            if ($library.type -ceq 'project') { continue }
            if ($library.type -cne 'package') { throw 'Unknown restored library kind.' }
            $parts = $entry.Key.Split('/')
            if ($parts.Count -ne 2 -or $library.path -cne $entry.Key.ToLowerInvariant()) { throw 'Noncanonical restored package path.' }
            $locked = @($lock.dependencies.Values | ForEach-Object {
                if ($_.Contains($parts[0])) { $_[$parts[0]] }
            } | Where-Object { $_.type -cne 'Project' -and $_.resolved -ceq $parts[1] -and $_.contentHash -ceq $library.sha512 })
            if ($locked.Count -eq 0) { throw 'A restored package is not bound to the checked-in lock file.' }
            Add-ClashSharpNoticePackage -Packages $packages -Id $parts[0] -Version $parts[1] -Hash $library.sha512 -Project $name -Folders $assets.packageFolders.Keys -Kind 'PackageReference'
        }
        foreach ($framework in $assets.project.frameworks.Values) {
            if (-not $framework.Contains('downloadDependencies')) { continue }
            foreach ($download in @($framework.downloadDependencies)) {
                if ($null -eq $download) { continue }
                if ($download.version -cnotmatch '^\[([^,\] ]+), \1\]$') { throw 'An SDK download dependency is not exact.' }
                $version = $Matches[1]
                $pins = @($catalog.downloadDependencies | Where-Object { $_.id -ceq $download.name -and $_.version -ceq $version })
                if ($pins.Count -ne 1) { throw 'An SDK download dependency lacks one reviewed content hash.' }
                Add-ClashSharpNoticePackage -Packages $packages -Id $download.name -Version $version -Hash $pins[0].sha512 -Project $name -Folders $assets.packageFolders.Keys -Kind 'SdkDownload'
            }
        }
    }
    if ($packages.Count -lt 1 -or $packages.Count -gt 256) { throw 'Production dependency count exceeds its contract.' }
    $inventoryPackages = [Collections.Generic.List[object]]::new()
    foreach ($key in @($packages.Keys | Sort-Object -CaseSensitive)) {
        $package = $packages[$key]
        $relative = $key + '/' + $package.id.ToLowerInvariant() + '.' + $package.version.ToLowerInvariant() + '.nupkg'
        $candidates = @($package.folders | ForEach-Object { Join-Path $_ $relative } | Where-Object { Test-Path -LiteralPath $_ })
        if ($candidates.Count -ne 1) { throw 'Expected one actual NuGet archive for a production dependency.' }
        $archivePath = Assert-ClashSharpOrdinaryPath -LiteralPath $candidates[0] -RequireFile
        $stream = [IO.File]::Open($archivePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $reader = $null
        $zip = $null
        try {
            if ($stream.Length -gt 1073741824) { throw 'NuGet archive exceeds its byte budget.' }
            $reader = [NuGet.Packaging.PackageArchiveReader]::new($stream, $true)
            $signature = $reader.GetPrimarySignatureAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
            if ($null -ne $signature) {
                $null = $reader.ValidateIntegrityAsync($signature.SignatureContent, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
            }
            if ($reader.GetContentHash([Threading.CancellationToken]::None, $null) -cne $package.contentHash) {
                throw 'The actual NuGet archive differs from the locked content hash.'
            }
            $reader.Dispose()
            $reader = $null
            $stream.Position = 0
            $archiveHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
            $stream.Position = 0
            $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($entry in $zip.Entries) {
                if (-not $names.Add($entry.FullName)) { throw 'A NuGet archive contains case-colliding or duplicate entries.' }
            }
            $nuspecs = @($zip.Entries | Where-Object { $_.FullName -notmatch '[/\\]' -and $_.Name.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
            if ($nuspecs.Count -ne 1) { throw 'Expected one root NuGet metadata document.' }
            $entryStream = $nuspecs[0].Open()
            try { $metadataBytes = Read-ClashSharpNoticeBytes -Stream $entryStream } finally { $entryStream.Dispose() }
            $settings = [Xml.XmlReaderSettings]::new()
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $memory = [IO.MemoryStream]::new($metadataBytes, $false)
            $xmlReader = [Xml.XmlReader]::Create($memory, $settings)
            try {
                $xml = [Xml.XmlDocument]::new()
                $xml.XmlResolver = $null
                $xml.Load($xmlReader)
            } finally { $xmlReader.Dispose(); $memory.Dispose() }
            $metadata = $xml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
            if ($null -eq $metadata -or $metadata.id -ine $package.id -or $metadata.version -cne $package.version) {
                throw 'NuGet metadata identity differs from the restored package.'
            }
            $license = $metadata.SelectSingleNode("*[local-name()='license']")
            $licenseUrl = $metadata.SelectSingleNode("*[local-name()='licenseUrl']")
            $licenseType = if ($null -eq $license) { 'url' } else { $license.GetAttribute('type') }
            $licenseValue = if ($null -eq $license) { if ($null -ne $licenseUrl) { $licenseUrl.InnerText } else { '' } } else { $license.InnerText }
            if ($licenseType -cnotin @('url', 'expression', 'file') -or [string]::IsNullOrWhiteSpace($licenseValue)) {
                throw 'A package has no supported license declaration.'
            }
            $packageDocuments = [Collections.Generic.List[object]]::new()
            $packageDocuments.Add((Add-ClashSharpNoticeDocument -Documents $documents -Bytes $metadataBytes -Name $nuspecs[0].Name -Source ('nuget:' + $key)))
            $licenseIncluded = $false
            foreach ($entry in $zip.Entries) {
                $isLicense = $entry.Name -imatch '^(LICENSE|COPYING)(\.[a-z0-9.-]+)?$'
                $isNotice = $entry.Name -imatch '^(NOTICE|THIRD[-_ ]?PARTY[-_ ]?NOTICES)(\.[a-z0-9.-]+)?$'
                $declaredFile = $licenseType -ceq 'file' -and $entry.FullName -ceq $licenseValue
                if (-not $isLicense -and -not $isNotice -and -not $declaredFile) { continue }
                $entryStream = $entry.Open()
                try { $bytes = Read-ClashSharpNoticeBytes -Stream $entryStream } finally { $entryStream.Dispose() }
                $packageDocuments.Add((Add-ClashSharpNoticeDocument -Documents $documents -Bytes $bytes -Name $entry.FullName -Source ('nuget:' + $key)))
                if ($declaredFile -or ($licenseType -ceq 'expression' -and $isLicense)) { $licenseIncluded = $true }
            }
            if (-not $licenseIncluded) {
                $overrides = @($catalog.packageLicenseSupplements | Where-Object {
                    $_.id -ceq $package.id -and $_.version -ceq $package.version -and
                    $_.licenseType -ceq $licenseType -and $_.licenseValue -ceq $licenseValue
                })
                if ($overrides.Count -ne 1 -or -not $supplements.ContainsKey($overrides[0].document)) {
                    throw 'A package license requires an exact reviewed source document.'
                }
                $packageDocuments.Add($supplements[$overrides[0].document])
            }
            $inventoryPackages.Add([ordered]@{
                id = $package.id; version = $package.version; nugetContentSha512 = $package.contentHash
                archiveSha256 = $archiveHash; archiveLength = $stream.Length
                inputKinds = @($package.kinds | Sort-Object); projects = @($package.projects | Sort-Object)
                license = [ordered]@{ type = $licenseType; value = $licenseValue }
                documents = @($packageDocuments)
            })
        } finally {
            if ($null -ne $zip) { $zip.Dispose() }
            if ($null -ne $reader) { $reader.Dispose() }
            $stream.Dispose()
        }
    }
    $bundled = [Collections.Generic.List[object]]::new()
    foreach ($relative in @('LICENSE', 'ClashSharp/ClashSharp/Binaries/mihomo-LICENSE.txt',
            'ClashSharp/ClashSharp/Binaries/mihomo-NOTICE.txt', 'ClashSharp/ClashSharp/Binaries/mihomo-manifest.json',
            'ClashSharp/Installer/release-inputs.json')) {
        $bytes = Read-ClashSharpNoticeFile -LiteralPath (Join-Path $root $relative)
        $bundled.Add((Add-ClashSharpNoticeDocument -Documents $documents -Bytes $bytes -Name $relative -Source 'ClashSharp repository'))
    }
    $releaseBytes = Read-ClashSharpNoticeFile -LiteralPath (Join-Path $root 'ClashSharp/Installer/release-inputs.json')
    $release = [Text.Encoding]::UTF8.GetString($releaseBytes) | ConvertFrom-Json -AsHashtable -Depth 16
    if ($release.geoData.commit -cne $catalog.geoDataEvidence.dataCommit -or
        $release.geoData.sourceCommit -cne $catalog.geoDataEvidence.sourceCommit) {
        throw 'GeoData attribution is not bound to the selected release inputs.'
    }
    $geoDocuments = @($catalog.geoDataEvidence.documents | ForEach-Object {
        if (-not $supplements.ContainsKey($_)) { throw 'GeoData evidence document is missing.' }
        $supplements[$_]
    })
    $inventory = [ordered]@{
        schemaVersion = 1; productVersion = '1.0.0'; dotnetSdkVersion = $sdkVersion
        scope = 'Production project NuGet build and runtime inputs, SDK download dependencies, and bundled component source notices.'
        projects = @($projects); packages = @($inventoryPackages); bundledDocuments = @($bundled)
        geoData = [ordered]@{
            dataCommit = $release.geoData.commit; sourceCommit = $release.geoData.sourceCommit
            files = $release.geoData.files; documents = $geoDocuments
            upstreamInputRevisionsRecorded = $catalog.geoDataEvidence.upstreamInputRevisionsRecorded
            sourceEvidenceScope = 'Pinned generator documents. Its moving upstream inputs are not reconstructed or relicensed by this archive.'
        }
    }
    $indexBytes = [Text.UTF8Encoding]::new($false).GetBytes(($inventory | ConvertTo-Json -Depth 16) + [char]10)
    $documents.Add('inventory.json', $indexBytes)
    $readme = @(
        'ClashSharp 1.0.0 third-party notices'
        ''
        'Open inventory.json for package identities, content hashes, project scope, and document paths.'
        'The documents directory preserves original license, notice, metadata, and attribution bytes.'
        'RTF documents can be opened in a compatible text editor or word processor.'
        'Build inputs are included conservatively; inclusion does not mean every package file is shipped.'
        'GeoData has multiple upstream sources. Its generator license does not replace their terms.'
        'The source snapshots do not constitute a complete corresponding-source distribution.'
        ''
    ) -join [char]10
    $documents.Add('README.txt', [Text.UTF8Encoding]::new($false).GetBytes($readme))
    $total = [long](($documents.Values | ForEach-Object { $_.Length } | Measure-Object -Sum).Sum)
    if ($total -gt 67108864 -or $documents.Count -gt 1024) { throw 'Attribution archive exceeds its aggregate budget.' }
    $outputStream = $null
    $outputZip = $null
    $created = $false
    $complete = $false
    try {
        $outputStream = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $created = $true
        $outputZip = [IO.Compression.ZipArchive]::new($outputStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        foreach ($path in @($documents.Keys | Sort-Object -CaseSensitive)) {
            $entry = $outputZip.CreateEntry($path, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $entryStream = $entry.Open()
            try { $entryStream.Write($documents[$path], 0, $documents[$path].Length) } finally { $entryStream.Dispose() }
        }
        $outputZip.Dispose()
        $outputZip = $null
        $outputStream.Flush($true)
        $complete = $true
    } finally {
        if ($null -ne $outputZip) { $outputZip.Dispose() }
        if ($null -ne $outputStream) { $outputStream.Dispose() }
        if ($created -and -not $complete) { Remove-Item -LiteralPath $output -Force }
    }
    return [pscustomobject]@{
        Path = $output; Length = (Get-Item -LiteralPath $output).Length
        Sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
        PackageCount = $packages.Count; DocumentCount = $documents.Count - 2
        GeoDataUpstreamInputRevisionsRecorded = $catalog.geoDataEvidence.upstreamInputRevisionsRecorded
    }
}

function Get-ClashSharpNoticeArchiveContent {
    <#
    .SYNOPSIS
        Validates a notices ZIP without extracting any entries.
    .DESCRIPTION
        Checks the exact entry namespace, content-addressed document hashes, and every inventory
        reference. Rejects missing, duplicate, undeclared, corrupted, or oversized documents.
    .PARAMETER Bytes
        Complete bounded ZIP bytes.
    #>
    param([byte[]] $Bytes)
    if ($Bytes.Length -lt 1 -or $Bytes.Length -gt 16777216) { throw 'Notices ZIP exceeds its byte budget.' }
    $memory = [IO.MemoryStream]::new($Bytes, $false)
    $zip = $null
    try {
        $zip = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Read, $true)
        if ($zip.Entries.Count -lt 3 -or $zip.Entries.Count -gt 1024) { throw 'Invalid notices entry count.' }
        $documents = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $total = 0L
        $inventory = $null
        foreach ($entry in $zip.Entries) {
            if (-not $seen.Add($entry.FullName) -or
                ($entry.FullName -cnotin @('README.txt', 'inventory.json') -and
                $entry.FullName -cnotmatch '^documents/[0-9a-f]{64}\.(txt|rtf)$')) {
                throw 'Unexpected or duplicate notices ZIP entry.'
            }
            $total += $entry.Length
            if ($entry.Length -lt 1 -or $entry.Length -gt 1048576 -or $total -gt 67108864) {
                throw 'Notices documents exceed their byte budget.'
            }
            $stream = $entry.Open()
            try { $content = Read-ClashSharpNoticeBytes -Stream $stream } finally { $stream.Dispose() }
            if ($entry.FullName -ceq 'inventory.json') {
                $inventory = [Text.UTF8Encoding]::new($false, $true).GetString($content) | ConvertFrom-Json -AsHashtable -Depth 32
            } elseif ($entry.FullName -cne 'README.txt') {
                $hash = Get-ClashSharpNoticeHash -Bytes $content
                if ($entry.Name.Split('.')[0] -cne $hash) { throw 'A notices document differs from its content address.' }
                $documents.Add($entry.FullName, [ordered]@{ length = $content.Length; sha256 = $hash })
            }
        }
        if ($null -eq $inventory -or -not $seen.Contains('README.txt') -or
            $inventory.schemaVersion -ne 1 -or $inventory.productVersion -cne '1.0.0' -or
            $inventory.projects.Count -ne 4 -or $inventory.packages.Count -lt 1 -or $inventory.packages.Count -gt 256) {
            throw 'Notices inventory is incomplete.'
        }
        $identities = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $references = [Collections.Generic.List[object]]::new()
        foreach ($package in $inventory.packages) {
            if (-not $identities.Add($package.id + '/' + $package.version) -or
                $package.documents.Count -lt 2 -or $package.archiveSha256 -cnotmatch '^[0-9a-f]{64}$' -or
                [Convert]::FromBase64String($package.nugetContentSha512).Length -ne 64) {
                throw 'Notices package inventory is invalid.'
            }
            foreach ($document in $package.documents) { $references.Add($document) }
        }
        foreach ($document in $inventory.bundledDocuments) { $references.Add($document) }
        foreach ($document in $inventory.geoData.documents) { $references.Add($document) }
        $referencedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($reference in $references) {
            if (-not $documents.ContainsKey($reference.path) -or
                $documents[$reference.path].length -ne $reference.length -or
                $documents[$reference.path].sha256 -cne $reference.sha256) {
                throw 'A notices inventory reference differs from its document.'
            }
            $null = $referencedPaths.Add($reference.path)
        }
        if (-not $referencedPaths.SetEquals($documents.Keys)) { throw 'Notices archive contains unreferenced documents.' }
        return [pscustomobject]@{
            Length = $Bytes.Length; Sha256 = Get-ClashSharpNoticeHash -Bytes $Bytes
            PackageCount = $inventory.packages.Count; DocumentCount = $documents.Count
            GeoDataUpstreamInputRevisionsRecorded = $inventory.geoData.upstreamInputRevisionsRecorded
        }
    } finally {
        if ($null -ne $zip) { $zip.Dispose() }
        $memory.Dispose()
    }
}

function Get-ClashSharpThirdPartyNoticesContract {
    <#
    .SYNOPSIS
        Validates one generated third-party notices archive.
    .DESCRIPTION
        Opens only an ordinary bounded ZIP and returns its verified digest and inventory counts.
    .PARAMETER LiteralPath
        Exact notices ZIP file path.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $LiteralPath)
    $bytes = Read-ClashSharpNoticeFile -LiteralPath $LiteralPath -MaximumLength 16777216
    return Get-ClashSharpNoticeArchiveContent -Bytes $bytes
}

function Assert-ClashSharpMsixThirdPartyNotices {
    <#
    .SYNOPSIS
        Verifies the notices archive carried by the actual final MSIX.
    .DESCRIPTION
        Binds the exact inner ZIP to the separately generated archive and checks its contents
        in memory. No AppX deployment, file extraction, or certificate operation is performed.
    .PARAMETER LiteralPath
        Exact final MSIX package file path.
    .PARAMETER ExpectedContract
        Verified contract of the archive supplied to the MSIX build.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $LiteralPath,
        [Parameter(Mandatory)] $ExpectedContract
    )
    $path = Assert-ClashSharpOrdinaryPath -LiteralPath $LiteralPath -RequireFile
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entries = @($zip.Entries | Where-Object { $_.FullName -ieq 'ThirdParty/THIRD-PARTY-NOTICES.zip' })
        if ($entries.Count -ne 1 -or $entries[0].FullName -cne 'ThirdParty/THIRD-PARTY-NOTICES.zip' -or
            $entries[0].Length -ne $ExpectedContract.Length) {
            throw 'The final MSIX does not contain the exact notices archive.'
        }
        $stream = $entries[0].Open()
        try { $bytes = Read-ClashSharpNoticeBytes -Stream $stream -MaximumLength 16777216 } finally { $stream.Dispose() }
        $actual = Get-ClashSharpNoticeArchiveContent -Bytes $bytes
        if ($actual.Sha256 -cne $ExpectedContract.Sha256 -or $actual.PackageCount -ne $ExpectedContract.PackageCount -or
            $actual.DocumentCount -ne $ExpectedContract.DocumentCount) {
            throw 'The final MSIX notices differ from the verified build input.'
        }
        return $actual
    } finally { $zip.Dispose() }
}

Export-ModuleMember -Function @(
    'New-ClashSharpThirdPartyNotices',
    'Get-ClashSharpThirdPartyNoticesContract',
    'Assert-ClashSharpMsixThirdPartyNotices'
)
