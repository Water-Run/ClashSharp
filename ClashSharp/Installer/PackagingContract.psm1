Set-StrictMode -Version Latest

function Assert-ClashSharpOrdinaryPath {
    <#
    .SYNOPSIS
        Rejects reparse points and non-directory ancestors for one fully qualified path.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $LiteralPath,

        [switch] $AllowMissing,

        [switch] $RequireDirectory,

        [switch] $RequireFile
    )

    if ($RequireDirectory -and $RequireFile) {
        throw 'A path cannot be required to be both a file and a directory.'
    }

    $fullPath = [IO.Path]::GetFullPath($LiteralPath)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrEmpty($pathRoot)) {
        throw "Path is not fully qualified: $LiteralPath"
    }

    $rootItem = Get-Item -LiteralPath $pathRoot -Force
    if (-not $rootItem.PSIsContainer -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Path root is not an ordinary directory: $pathRoot"
    }

    $relativePath = [IO.Path]::GetRelativePath($pathRoot, $fullPath)
    $segments = @($relativePath.Split(
            [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar),
            [StringSplitOptions]::RemoveEmptyEntries))
    $currentPath = $pathRoot
    $missing = $false
    for ($index = 0; $index -lt $segments.Count; $index++) {
        $currentPath = Join-Path $currentPath $segments[$index]
        if (-not (Test-Path -LiteralPath $currentPath)) {
            $missing = $true
            continue
        }
        if ($missing) {
            throw "Path resolves through an unexpected existing descendant: $currentPath"
        }

        $item = Get-Item -LiteralPath $currentPath -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Path traverses a reparse point: $currentPath"
        }
        $isLast = $index -eq ($segments.Count - 1)
        if (-not $isLast -and -not $item.PSIsContainer) {
            throw "Path ancestor is not a directory: $currentPath"
        }
    }

    if ($missing) {
        if (-not $AllowMissing) {
            throw "Path does not exist: $fullPath"
        }
        return $fullPath
    }

    $leaf = Get-Item -LiteralPath $fullPath -Force
    if ($RequireDirectory -and -not $leaf.PSIsContainer) {
        throw "Path is not a directory: $fullPath"
    }
    if ($RequireFile -and $leaf.PSIsContainer) {
        throw "Path is not a file: $fullPath"
    }
    return $fullPath
}

function Get-ClashSharpDirectoryContract {
    <#
    .SYNOPSIS
        Returns a sorted exact file/length/SHA-256 contract for an ordinary directory tree.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $LiteralPath
    )

    $root = Assert-ClashSharpOrdinaryPath -LiteralPath $LiteralPath -RequireDirectory
    $seenPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $contract = [Collections.Generic.List[object]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $root -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Directory contract contains a reparse point: $($item.FullName)"
        }
        if ($item.PSIsContainer) {
            continue
        }

        $relativePath = [IO.Path]::GetRelativePath($root, $item.FullName).Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            $relativePath.StartsWith('../', [StringComparison]::Ordinal) -or
            -not $seenPaths.Add($relativePath)) {
            throw "Directory contract contains an invalid or case-colliding path: $relativePath"
        }
        $sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $contract.Add([PSCustomObject]@{
                RelativePath = $relativePath
                Length       = [long]$item.Length
                Sha256       = $sha256
            })
    }
    if ($contract.Count -eq 0) {
        throw "Directory contract is empty: $root"
    }
    return @($contract | Sort-Object -Property RelativePath)
}

function Compare-ClashSharpDirectoryContract {
    <#
    .SYNOPSIS
        Throws unless two exact directory contracts have identical paths, lengths, and hashes.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object[]] $Expected,

        [Parameter(Mandatory)]
        [object[]] $Actual
    )

    if ($Expected.Count -ne $Actual.Count) {
        throw "Directory contract file count changed: expected $($Expected.Count), actual $($Actual.Count)."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        $expectedEntry = $Expected[$index]
        $actualEntry = $Actual[$index]
        if (-not ([string]$expectedEntry.RelativePath).Equals(
                [string]$actualEntry.RelativePath,
                [StringComparison]::Ordinal) -or
            [long]$expectedEntry.Length -ne [long]$actualEntry.Length -or
            -not ([string]$expectedEntry.Sha256).Equals(
                [string]$actualEntry.Sha256,
                [StringComparison]::Ordinal)) {
            throw "Directory contract changed at index $index ($($expectedEntry.RelativePath))."
        }
    }
}

function Copy-ClashSharpVerifiedDirectory {
    <#
    .SYNOPSIS
        Copies an ordinary directory into a new destination and verifies its exact contract.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $Source,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $Destination
    )

    $sourceRoot = Assert-ClashSharpOrdinaryPath -LiteralPath $Source -RequireDirectory
    $destinationRoot = Assert-ClashSharpOrdinaryPath -LiteralPath $Destination -AllowMissing
    if (Test-Path -LiteralPath $destinationRoot) {
        throw "Verified copy destination must be new: $destinationRoot"
    }

    $expected = @(Get-ClashSharpDirectoryContract -LiteralPath $sourceRoot)
    $null = [IO.Directory]::CreateDirectory($destinationRoot)
    foreach ($entry in $expected) {
        $sourcePath = Join-Path $sourceRoot ([string]$entry.RelativePath)
        $destinationPath = Join-Path $destinationRoot ([string]$entry.RelativePath)
        $destinationParent = Split-Path -Parent $destinationPath
        if (-not (Test-Path -LiteralPath $destinationParent)) {
            $null = [IO.Directory]::CreateDirectory($destinationParent)
        }
        [IO.File]::Copy($sourcePath, $destinationPath, $false)
    }

    $actual = @(Get-ClashSharpDirectoryContract -LiteralPath $destinationRoot)
    Compare-ClashSharpDirectoryContract -Expected $expected -Actual $actual
    return $actual
}

function Copy-ClashSharpComponentPayload {
    <#
    .SYNOPSIS
        Selects only packageable runtime files from one fresh, flat dotnet publish directory.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $Source,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $Destination
    )

    $sourceRoot = Assert-ClashSharpOrdinaryPath -LiteralPath $Source -RequireDirectory
    $destinationRoot = Assert-ClashSharpOrdinaryPath -LiteralPath $Destination -AllowMissing
    if (Test-Path -LiteralPath $destinationRoot) {
        throw "Component payload destination must be new: $destinationRoot"
    }

    $sourceEntries = @(Get-ChildItem -LiteralPath $sourceRoot -Force)
    if ($sourceEntries.Count -eq 0) {
        throw "Component publish directory is empty: $sourceRoot"
    }
    $allowedExtensions = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($extension in @('.exe', '.dll', '.json', '.winmd')) {
        $null = $allowedExtensions.Add($extension)
    }
    $selectedFiles = [Collections.Generic.List[IO.FileInfo]]::new()
    foreach ($entry in $sourceEntries) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $entry.PSIsContainer) {
            throw "Component publish output must be a flat ordinary file set: $($entry.FullName)"
        }
        if ($entry.Name -cmatch '(?i)(Probe|SandboxTest|Installer|Updater)') {
            throw "Component publish output contains a forbidden product entry: $($entry.Name)"
        }

        $extension = [IO.Path]::GetExtension($entry.Name).ToLowerInvariant()
        if ($entry.Name -ceq 'packages.lock.json') {
            continue
        }
        if ($extension -ceq '.xml') {
            $assemblyPath = Join-Path $sourceRoot ([IO.Path]::GetFileNameWithoutExtension($entry.Name) + '.dll')
            if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
                throw "Unexpected component XML file has no matching assembly: $($entry.Name)"
            }
            continue
        }
        if (-not $allowedExtensions.Contains($extension)) {
            throw "Component publish output contains an unsupported file: $($entry.Name)"
        }
        $selectedFiles.Add($entry)
    }
    if ($selectedFiles.Count -eq 0) {
        throw "Component publish output has no packageable runtime files: $sourceRoot"
    }

    $null = [IO.Directory]::CreateDirectory($destinationRoot)
    foreach ($file in $selectedFiles) {
        [IO.File]::Copy($file.FullName, (Join-Path $destinationRoot $file.Name), $false)
    }
    return @(Get-ClashSharpDirectoryContract -LiteralPath $destinationRoot)
}

function Get-ClashSharpMsixManifestDocument {
    <#
    .SYNOPSIS
        Reads the one canonical, bounded AppxManifest.xml from an ordinary MSIX file.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $LiteralPath
    )

    $msixPath = Assert-ClashSharpOrdinaryPath -LiteralPath $LiteralPath -RequireFile
    $msixFile = Get-Item -LiteralPath $msixPath -Force
    if ($msixFile.Length -lt 1 -or $msixFile.Length -gt 1073741824) {
        throw "MSIX file length is outside the packaging contract: $msixPath"
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($msixPath)
    try {
        $manifestEntries = @($archive.Entries | Where-Object {
                $_.FullName.Equals('AppxManifest.xml', [StringComparison]::OrdinalIgnoreCase)
            })
        if ($manifestEntries.Count -ne 1 -or
            -not $manifestEntries[0].FullName.Equals('AppxManifest.xml', [StringComparison]::Ordinal) -or
            $manifestEntries[0].Length -lt 1 -or
            $manifestEntries[0].Length -gt 1048576) {
            throw 'MSIX must contain exactly one canonical bounded AppxManifest.xml.'
        }

        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $stream = $manifestEntries[0].Open()
        try {
            $reader = [Xml.XmlReader]::Create($stream, $settings)
            try {
                $document = [Xml.XmlDocument]::new()
                $document.XmlResolver = $null
                $document.Load($reader)
            } finally {
                $reader.Dispose()
            }
        } finally {
            $stream.Dispose()
        }
        return $document
    } finally {
        $archive.Dispose()
    }
}

function Get-ClashSharpMsixIdentity {
    <#
    .SYNOPSIS
        Returns the strict package identity from an MSIX manifest.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $LiteralPath
    )

    $document = Get-ClashSharpMsixManifestDocument -LiteralPath $LiteralPath
    $nodes = @($document.SelectNodes("/*[local-name()='Package']/*[local-name()='Identity']"))
    if ($nodes.Count -ne 1) {
        throw 'MSIX manifest must contain exactly one Package/Identity element.'
    }
    $identity = $nodes[0]
    foreach ($attribute in @('Name', 'Publisher', 'Version', 'ProcessorArchitecture')) {
        if ([string]::IsNullOrWhiteSpace([string]$identity.GetAttribute($attribute))) {
            throw "MSIX Identity $attribute is missing."
        }
    }
    $version = [string]$identity.GetAttribute('Version')
    if ($version -cnotmatch '^(0|[1-9][0-9]{0,4})(\.(0|[1-9][0-9]{0,4})){3}$' -or
        @($version.Split('.') | Where-Object { [uint32]$_ -gt [uint16]::MaxValue }).Count -ne 0) {
        throw 'MSIX Identity Version is noncanonical.'
    }
    return [PSCustomObject]@{
        Name         = [string]$identity.GetAttribute('Name')
        Publisher    = [string]$identity.GetAttribute('Publisher')
        Version      = [string]$identity.GetAttribute('Version')
        Architecture = ([string]$identity.GetAttribute('ProcessorArchitecture')).ToLowerInvariant()
        Document     = $document
    }
}

function Get-ClashSharpMainPackageDependency {
    <#
    .SYNOPSIS
        Returns the exact PackageDependency declarations from a main MSIX identity document.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [Xml.XmlDocument] $ManifestDocument
    )

    $dependencies = [Collections.Generic.List[object]]::new()
    $nodes = @($ManifestDocument.SelectNodes(
            "/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']"))
    foreach ($node in $nodes) {
        foreach ($attribute in @('Name', 'Publisher', 'MinVersion')) {
            if ([string]::IsNullOrWhiteSpace([string]$node.GetAttribute($attribute))) {
                throw "PackageDependency $attribute is missing."
            }
        }
        $minVersion = [string]$node.GetAttribute('MinVersion')
        if ($minVersion -cnotmatch '^(0|[1-9][0-9]{0,4})(\.(0|[1-9][0-9]{0,4})){3}$' -or
            @($minVersion.Split('.') | Where-Object { [uint32]$_ -gt [uint16]::MaxValue }).Count -ne 0) {
            throw 'PackageDependency MinVersion is noncanonical.'
        }
        $dependencies.Add([PSCustomObject]@{
                Name       = [string]$node.GetAttribute('Name')
                Publisher  = [string]$node.GetAttribute('Publisher')
                MinVersion = [string]$node.GetAttribute('MinVersion')
            })
    }
    return @($dependencies)
}

function Get-ClashSharpPackageSignature {
    <#
    .SYNOPSIS
        Verifies a package signer and returns its canonical subject and thumbprint.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $LiteralPath,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $ExpectedSubject,

        [string] $ExpectedThumbprint,

        [switch] $RequireTrusted,

        [switch] $RequireTimestamp
    )

    $packagePath = Assert-ClashSharpOrdinaryPath -LiteralPath $LiteralPath -RequireFile
    if (-not [string]::IsNullOrWhiteSpace($ExpectedThumbprint) -and
        $ExpectedThumbprint -cnotmatch '^[0-9A-F]{40}$') {
        throw 'Expected package signer thumbprint must be canonical uppercase SHA-1.'
    }
    $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $packagePath
    if ($null -eq $signature.SignerCertificate -or
        -not $signature.SignerCertificate.Subject.Equals($ExpectedSubject, [StringComparison]::Ordinal) -or
        $signature.SignerCertificate.Thumbprint -cnotmatch '^[0-9A-F]{40}$' -or
        (-not [string]::IsNullOrWhiteSpace($ExpectedThumbprint) -and
            $signature.SignerCertificate.Thumbprint -cne $ExpectedThumbprint) -or
        ($RequireTrusted -and
            $signature.Status -ne [Management.Automation.SignatureStatus]::Valid) -or
        ($RequireTimestamp -and $null -eq $signature.TimeStamperCertificate)) {
        throw "Package signer, thumbprint, trust, or timestamp is invalid: $packagePath"
    }
    return [PSCustomObject]@{
        Subject    = $signature.SignerCertificate.Subject
        Thumbprint = $signature.SignerCertificate.Thumbprint
        Status     = [string]$signature.Status
        Timestamp  = $null -ne $signature.TimeStamperCertificate
    }
}

Export-ModuleMember -Function @(
    'Assert-ClashSharpOrdinaryPath',
    'Get-ClashSharpDirectoryContract',
    'Compare-ClashSharpDirectoryContract',
    'Copy-ClashSharpVerifiedDirectory',
    'Copy-ClashSharpComponentPayload',
    'Get-ClashSharpMsixManifestDocument',
    'Get-ClashSharpMsixIdentity',
    'Get-ClashSharpMainPackageDependency',
    'Get-ClashSharpPackageSignature'
)
