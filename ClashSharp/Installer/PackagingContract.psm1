Set-StrictMode -Version Latest

function Assert-ClashSharpOrdinaryPath {
    <#
    .SYNOPSIS
        Rejects reparse points and non-directory ancestors for one fully qualified path.
    .DESCRIPTION
        Walks the path from its volume root, rejects reparse points and invalid ancestors, and
        optionally enforces the expected leaf kind without following an unsafe filesystem object.
    .PARAMETER LiteralPath
        Fully qualified path to validate.
    .PARAMETER AllowMissing
        Allows a canonical descendant path whose final segments do not yet exist.
    .PARAMETER RequireDirectory
        Requires an existing leaf to be an ordinary directory.
    .PARAMETER RequireFile
        Requires an existing leaf to be an ordinary file.
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
    .DESCRIPTION
        Rejects reparse entries, empty trees, traversal, and case-colliding relative paths before
        returning the canonical ordinally sorted content contract.
    .PARAMETER LiteralPath
        Ordinary directory tree to inventory.
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
    .DESCRIPTION
        Compares already canonical contracts in order and fails at the first file-count or entry
        mismatch so a copied payload cannot be accepted on partial agreement.
    .PARAMETER Expected
        Trusted source directory contract.
    .PARAMETER Actual
        Independently measured destination directory contract.
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
    .DESCRIPTION
        Inventories the source, creates a previously absent destination, copies every declared
        file without overwrite, and compares a fresh destination contract before returning it.
    .PARAMETER Source
        Ordinary source directory whose contract is authoritative for this copy.
    .PARAMETER Destination
        New destination directory to create and verify.
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
    .DESCRIPTION
        Rejects nested or reparse entries and forbidden product artifacts, filters documentation
        and lock files, copies the allowed runtime extensions, and returns the destination contract.
    .PARAMETER Source
        Fresh flat dotnet publish directory to filter.
    .PARAMETER Destination
        New component-staging directory that receives allowed runtime files.
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
    .DESCRIPTION
        Opens the package as a bounded ZIP, requires exactly one canonical manifest entry, and
        parses XML with DTD processing and external resolution disabled.
    .PARAMETER LiteralPath
        Ordinary MSIX file whose manifest is read.
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
    .DESCRIPTION
        Validates canonical identity, version, architecture, application, and framework fields and
        derives the exact full name and family name used by the Installer release contract.
    .PARAMETER LiteralPath
        Ordinary MSIX file whose identity is inspected.
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
    $publisher = [string]$identity.GetAttribute('Publisher')
    $publisherId = Get-ClashSharpPublisherId -Publisher $publisher
    $architecture = ([string]$identity.GetAttribute('ProcessorArchitecture')).ToLowerInvariant()
    $resourceId = [string]$identity.GetAttribute('ResourceId')
    $packageFullName = "{0}_{1}_{2}_{3}_{4}" -f @(
        [string]$identity.GetAttribute('Name'),
        $version,
        $architecture,
        $resourceId,
        $publisherId)
    $packageFamilyName = "{0}_{1}" -f @(
        [string]$identity.GetAttribute('Name'),
        $publisherId)

    $applicationNodes = @($document.SelectNodes(
            "/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']"))
    if ($applicationNodes.Count -gt 1) {
        throw 'MSIX manifest must not contain multiple primary Application elements.'
    }
    $applicationId = ''
    $applicationExecutable = ''
    $applicationEntryPoint = ''
    if ($applicationNodes.Count -eq 1) {
        $applicationId = [string]$applicationNodes[0].GetAttribute('Id')
        $applicationExecutable = [string]$applicationNodes[0].GetAttribute('Executable')
        $applicationEntryPoint = [string]$applicationNodes[0].GetAttribute('EntryPoint')
        if ([string]::IsNullOrWhiteSpace($applicationId) -or
            [string]::IsNullOrWhiteSpace($applicationExecutable) -or
            [string]::IsNullOrWhiteSpace($applicationEntryPoint)) {
            throw 'MSIX primary Application identity is incomplete.'
        }
    }

    $frameworkNodes = @($document.SelectNodes(
            "/*[local-name()='Package']/*[local-name()='Properties']/*[local-name()='Framework']"))
    if ($frameworkNodes.Count -gt 1 -or
        ($frameworkNodes.Count -eq 1 -and
            $frameworkNodes[0].InnerText.Trim() -cnotin @('true', 'false'))) {
        throw 'MSIX Framework property is invalid.'
    }
    $isFramework = $frameworkNodes.Count -eq 1 -and
        $frameworkNodes[0].InnerText.Trim() -ceq 'true'

    return [PSCustomObject]@{
        Name                  = [string]$identity.GetAttribute('Name')
        Publisher             = $publisher
        PublisherId           = $publisherId
        Version               = $version
        Architecture          = $architecture
        ResourceId            = $resourceId
        PackageFullName       = $packageFullName
        PackageFamilyName     = $packageFamilyName
        ApplicationId         = $applicationId
        ApplicationExecutable = $applicationExecutable
        ApplicationEntryPoint = $applicationEntryPoint
        IsFramework           = $isFramework
        Document              = $document
    }
}

function Get-ClashSharpPublisherId {
    <#
    .SYNOPSIS
        Derives the canonical 13-character Windows package PublisherId.
    .DESCRIPTION
        Hashes the exact canonical publisher subject as UTF-16LE and encodes the Windows-defined
        leading digest bits with the package identity alphabet.
    .PARAMETER Publisher
        Canonical certificate publisher subject from the MSIX identity.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $Publisher
    )

    if ($Publisher -cne $Publisher.Trim() -or
        @($Publisher.ToCharArray() | Where-Object { [char]::IsControl($_) }).Count -ne 0) {
        throw 'Package Publisher is not canonical.'
    }

    $publisherBytes = [Text.Encoding]::Unicode.GetBytes($Publisher)
    try {
        $digest = [Security.Cryptography.SHA256]::HashData($publisherBytes)
        $alphabet = '0123456789abcdefghjkmnpqrstvwxyz'
        $result = [Text.StringBuilder]::new(13)
        for ($chunk = 0; $chunk -lt 13; $chunk++) {
            $value = 0
            for ($offset = 0; $offset -lt 5; $offset++) {
                $bitIndex = ($chunk * 5) + $offset
                $bit = if ($bitIndex -lt 64) {
                    ($digest[($bitIndex -shr 3)] -shr (7 - ($bitIndex % 8))) -band 1
                } else {
                    0
                }
                $value = ($value -shl 1) -bor $bit
            }
            $null = $result.Append($alphabet[$value])
        }
        return $result.ToString()
    } finally {
        [Array]::Clear($publisherBytes, 0, $publisherBytes.Length)
    }
}

function Get-ClashSharpMsixMachineFileContract {
    <#
    .SYNOPSIS
        Hashes the exact machine-scope payload inside a bounded ordinary primary MSIX.
    .DESCRIPTION
        Requires the fixed service, Mihomo, and GeoData entry allowlist with canonical paths and
        byte budgets, then returns each entry's expanded length and SHA-256 digest.
    .PARAMETER LiteralPath
        Ordinary primary MSIX file containing the machine-scope payload.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $LiteralPath
    )

    $msixPath = Assert-ClashSharpOrdinaryPath -LiteralPath $LiteralPath -RequireFile
    $requiredPaths = [string[]]@(
        'binaries/geodata/asn.mmdb',
        'binaries/geodata/country.mmdb',
        'binaries/geodata/geoip.dat',
        'binaries/geodata/geosite.dat',
        'binaries/geodata/manifest.json',
        'binaries/mihomo.exe',
        'binaries/service/clashsharp.mihomoservice.exe'
    )
    $requiredSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($requiredPath in $requiredPaths) {
        $null = $requiredSet.Add($requiredPath)
    }
    $observed = [Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::Ordinal)
    $allPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)

    $archive = [IO.Compression.ZipFile]::OpenRead($msixPath)
    try {
        if ($archive.Entries.Count -lt 3 -or $archive.Entries.Count -gt 4096) {
            throw 'Primary MSIX central-directory entry count is outside its budget.'
        }

        $expandedLength = 0L
        foreach ($entry in $archive.Entries) {
            $path = [string]$entry.FullName
            $segments = @($path.Split('/'))
            if ($path.Length -lt 1 -or $path.Length -gt 512 -or
                $path.StartsWith('/', [StringComparison]::Ordinal) -or
                $path.EndsWith('/', [StringComparison]::Ordinal) -or
                $path.Contains([char]92) -or
                $path.Contains('//', [StringComparison]::Ordinal) -or
                @($segments | Where-Object {
                        $_ -ceq '.' -or $_ -ceq '..' -or
                        $_.EndsWith('.', [StringComparison]::Ordinal) -or
                        $_.EndsWith(' ', [StringComparison]::Ordinal)
                    }).Count -ne 0 -or
                [long]$entry.Length -lt 1 -or
                -not $allPaths.Add($path)) {
                throw "Primary MSIX contains an unsafe or case-colliding entry: $path"
            }
            if ([long]$entry.Length -gt (2147483648L - $expandedLength)) {
                throw 'Primary MSIX expanded length exceeds its budget.'
            }
            $expandedLength += [long]$entry.Length

            $normalizedPath = $path.ToLowerInvariant()
            $isMachineScope = $normalizedPath -ceq 'binaries/mihomo.exe' -or
                $normalizedPath.StartsWith('binaries/service/', [StringComparison]::Ordinal) -or
                $normalizedPath.StartsWith('binaries/geodata/', [StringComparison]::Ordinal)
            if (-not $isMachineScope) {
                continue
            }
            if (-not $requiredSet.Contains($normalizedPath) -or
                -not $observed.TryAdd($normalizedPath, $entry)) {
                throw "Primary MSIX machine payload is outside its exact allowlist: $path"
            }
        }

        if ($observed.Count -ne $requiredPaths.Length) {
            throw 'Primary MSIX machine payload is incomplete.'
        }

        $contract = [Collections.Generic.List[object]]::new()
        $machineLength = 0L
        foreach ($path in $requiredPaths) {
            $entry = $observed[$path]
            $maximumLength = if ($path -ceq 'binaries/geodata/manifest.json') {
                65536L
            } elseif ($path.StartsWith('binaries/geodata/', [StringComparison]::Ordinal)) {
                268435456L
            } else {
                536870912L
            }
            if ([long]$entry.Length -lt 1 -or [long]$entry.Length -gt $maximumLength -or
                [long]$entry.Length -gt (1073741824L - $machineLength)) {
                throw "Primary MSIX machine file exceeds its budget: $path"
            }
            $machineLength += [long]$entry.Length

            $stream = $entry.Open()
            $hasher = [Security.Cryptography.IncrementalHash]::CreateHash(
                [Security.Cryptography.HashAlgorithmName]::SHA256)
            $buffer = [byte[]]::new(65536)
            $digest = $null
            try {
                $actualLength = 0L
                while (($count = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $actualLength += [long]$count
                    if ($actualLength -gt [long]$entry.Length) {
                        throw "Primary MSIX machine file length changed while hashing: $path"
                    }
                    $hasher.AppendData($buffer, 0, $count)
                }
                if ($actualLength -ne [long]$entry.Length) {
                    throw "Primary MSIX machine file length changed while hashing: $path"
                }
                $digest = $hasher.GetHashAndReset()
                $hash = [Convert]::ToHexString($digest).ToLowerInvariant()
            } finally {
                if ($null -ne $digest) {
                    [Array]::Clear($digest, 0, $digest.Length)
                }
                [Array]::Clear($buffer, 0, $buffer.Length)
                $hasher.Dispose()
                $stream.Dispose()
            }

            $contract.Add([PSCustomObject]@{
                    Path   = $path
                    Length = [long]$entry.Length
                    Sha256 = $hash
                })
        }
        return @($contract)
    } finally {
        $archive.Dispose()
    }
}

function New-ClashSharpInstallerReleaseManifest {
    <#
    .SYNOPSIS
        Generates the compact strict C# Installer release manifest from final staged payload bytes.
    .DESCRIPTION
        Verifies the exact payload role set, primary and dependency identities, certificate and
        Authenticode anchors, and embedded machine files before atomically emitting bounded UTF-8
        JSON and checking its read-back hash.
    .PARAMETER PayloadRoot
        Ordinary final payload directory whose bytes are bound into the manifest.
    .PARAMETER PrimaryIdentity
        Validated primary MSIX identity object.
    .PARAMETER PrimaryRelativePath
        Canonical payload-relative path of the primary MSIX.
    .PARAMETER DependencyContracts
        Validated dependency identity and minimum-version contracts.
    .PARAMETER CertificateRelativePath
        Canonical payload-relative path of the package signing certificate.
    .PARAMETER CertificateThumbprint
        Uppercase SHA-1 thumbprint expected for the MSIX signer.
    .PARAMETER AuthenticodeCertificateThumbprint
        Uppercase SHA-1 thumbprint expected for the final Installer executable signer.
    .PARAMETER OutputPath
        Previously absent path at which to write the embedded release manifest.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $PayloadRoot,

        [Parameter(Mandatory)]
        [ValidateNotNull()]
        [object] $PrimaryIdentity,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $PrimaryRelativePath,

        [Parameter(Mandatory)]
        [ValidateNotNull()]
        [object[]] $DependencyContracts,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $CertificateRelativePath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{40}$')]
        [string] $CertificateThumbprint,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{40}$')]
        [string] $AuthenticodeCertificateThumbprint,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $OutputPath
    )

    $root = Assert-ClashSharpOrdinaryPath -LiteralPath $PayloadRoot -RequireDirectory
    $output = Assert-ClashSharpOrdinaryPath -LiteralPath $OutputPath -AllowMissing
    if (Test-Path -LiteralPath $output) {
        throw "Installer release manifest output must be new: $output"
    }

    $canonicalPrimaryPath = $PrimaryRelativePath.Replace('\', '/').ToLowerInvariant()
    $canonicalCertificatePath = $CertificateRelativePath.Replace('\', '/').ToLowerInvariant()
    if ($canonicalPrimaryPath.Contains('/') -or
        -not $canonicalPrimaryPath.EndsWith('.msix', [StringComparison]::Ordinal) -or
        $canonicalCertificatePath -cne 'clashsharp_temporarykey.cer' -or
        [string]$PrimaryIdentity.Architecture -cne 'x64' -or
        -not [string]::IsNullOrEmpty([string]$PrimaryIdentity.ResourceId) -or
        [string]::IsNullOrWhiteSpace([string]$PrimaryIdentity.ApplicationId) -or
        [string]::IsNullOrWhiteSpace([string]$PrimaryIdentity.ApplicationExecutable) -or
        [string]::IsNullOrWhiteSpace([string]$PrimaryIdentity.ApplicationEntryPoint) -or
        [bool]$PrimaryIdentity.IsFramework) {
        throw 'Primary package identity is outside the C# Installer release contract.'
    }

    $dependencyByPath = [Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::Ordinal)
    foreach ($dependencyContract in $DependencyContracts) {
        $dependencyPath = ([string]$dependencyContract.Path).Replace('\', '/').ToLowerInvariant()
        $dependencyIdentity = $dependencyContract.Identity
        if (-not $dependencyPath.StartsWith('dependencies/x64/', [StringComparison]::Ordinal) -or
            -not $dependencyPath.EndsWith('.msix', [StringComparison]::Ordinal) -or
            -not $dependencyByPath.TryAdd($dependencyPath, $dependencyContract) -or
            [string]$dependencyIdentity.Architecture -cne 'x64' -or
            -not [string]::IsNullOrEmpty([string]$dependencyIdentity.ResourceId) -or
            -not [bool]$dependencyIdentity.IsFramework -or
            -not [string]::IsNullOrEmpty([string]$dependencyIdentity.ApplicationId) -or
            ([Version][string]$dependencyContract.MinimumVersion) -gt
                ([Version][string]$dependencyIdentity.Version)) {
            throw "Dependency identity is outside the C# Installer release contract: $dependencyPath"
        }
    }
    if ($dependencyByPath.Count -lt 1) {
        throw 'The C# Installer release manifest requires at least one dependency package.'
    }

    $payloadContract = @(Get-ClashSharpDirectoryContract -LiteralPath $root)
    if ($payloadContract.Count -lt 4 -or $payloadContract.Count -gt 64) {
        throw 'The C# Installer payload file count is outside its budget.'
    }
    $payloadByPath = [Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::Ordinal)
    $totalPayloadLength = 0L
    foreach ($entry in $payloadContract) {
        $path = ([string]$entry.RelativePath).Replace('\', '/').ToLowerInvariant()
        if ($path -cnotmatch '^[a-z0-9._/-]{1,240}$' -or
            -not $payloadByPath.TryAdd($path, $entry) -or
            [long]$entry.Length -lt 1 -or
            [long]$entry.Length -gt 536870912) {
            throw "The C# Installer payload contains a noncanonical path: $path"
        }
        $totalPayloadLength += [long]$entry.Length
        if ($totalPayloadLength -gt 1073741824) {
            throw 'The C# Installer payload exceeds its combined byte budget.'
        }
    }

    $expectedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $null = $expectedPaths.Add($canonicalPrimaryPath)
    $null = $expectedPaths.Add($canonicalCertificatePath)
    $null = $expectedPaths.Add('payload-provenance.json')
    foreach ($dependencyPath in $dependencyByPath.Keys) {
        $null = $expectedPaths.Add($dependencyPath)
    }
    if (-not $expectedPaths.SetEquals($payloadByPath.Keys)) {
        throw 'The C# Installer payload does not match its exact role allowlist.'
    }
    if ([long]$payloadByPath[$canonicalCertificatePath].Length -gt 1048576 -or
        [long]$payloadByPath['payload-provenance.json'].Length -gt 65536) {
        throw 'The C# Installer certificate or provenance document exceeds its role budget.'
    }

    $primaryMsixPath = Join-Path $root (
        [string]$payloadByPath[$canonicalPrimaryPath].RelativePath)
    $machineContract = @(
        Get-ClashSharpMsixMachineFileContract -LiteralPath $primaryMsixPath)

    $sortedPaths = [string[]]@($payloadByPath.Keys)
    [Array]::Sort($sortedPaths, [StringComparer]::Ordinal)
    $files = [Collections.Generic.List[object]]::new()
    foreach ($path in $sortedPaths) {
        $entry = $payloadByPath[$path]
        $role = if ($path -ceq $canonicalPrimaryPath) {
            'primaryPackage'
        } elseif ($path -ceq $canonicalCertificatePath) {
            'certificate'
        } elseif ($path -ceq 'payload-provenance.json') {
            'provenance'
        } elseif ($dependencyByPath.ContainsKey($path)) {
            'dependencyPackage'
        } else {
            throw "The C# Installer payload role is unknown: $path"
        }
        $files.Add([ordered]@{
                path   = $path
                role   = $role
                length = [long]$entry.Length
                sha256 = [string]$entry.Sha256
            })
    }

    $sortedDependencyPaths = [string[]]@($dependencyByPath.Keys)
    [Array]::Sort($sortedDependencyPaths, [StringComparer]::Ordinal)
    $dependencies = [Collections.Generic.List[object]]::new()
    foreach ($path in $sortedDependencyPaths) {
        $contract = $dependencyByPath[$path]
        $identity = $contract.Identity
        $dependencies.Add([ordered]@{
                path              = $path
                name              = [string]$identity.Name
                publisher         = [string]$identity.Publisher
                publisherId       = [string]$identity.PublisherId
                version           = [string]$identity.Version
                minimumVersion    = [string]$contract.MinimumVersion
                architecture      = [string]$identity.Architecture
                resourceId        = [string]$identity.ResourceId
                packageFullName   = [string]$identity.PackageFullName
                packageFamilyName = [string]$identity.PackageFamilyName
            })
    }

    $machineFiles = [Collections.Generic.List[object]]::new()
    foreach ($entry in $machineContract) {
        $machineFiles.Add([ordered]@{
                path   = [string]$entry.Path
                length = [long]$entry.Length
                sha256 = [string]$entry.Sha256
            })
    }

    $manifest = [ordered]@{
        schema                       = 2
        expectedPackageVersion       = [string]$PrimaryIdentity.Version
        installerPayloadSha256       = [string]$payloadByPath[$canonicalPrimaryPath].Sha256
        authenticodeCertificateThumbprint = $AuthenticodeCertificateThumbprint
        packageCertificateThumbprint = $CertificateThumbprint
        certificateSha256            = [string]$payloadByPath[$canonicalCertificatePath].Sha256
        packageIdentity               = [ordered]@{
            name                  = [string]$PrimaryIdentity.Name
            publisher             = [string]$PrimaryIdentity.Publisher
            publisherId           = [string]$PrimaryIdentity.PublisherId
            architecture          = [string]$PrimaryIdentity.Architecture
            resourceId            = [string]$PrimaryIdentity.ResourceId
            packageFullName       = [string]$PrimaryIdentity.PackageFullName
            packageFamilyName     = [string]$PrimaryIdentity.PackageFamilyName
            applicationId         = [string]$PrimaryIdentity.ApplicationId
            applicationExecutable = [string]$PrimaryIdentity.ApplicationExecutable
            applicationEntryPoint = [string]$PrimaryIdentity.ApplicationEntryPoint
        }
        dependencies                  = @($dependencies)
        machineFiles                  = @($machineFiles)
        files                         = @($files)
    }
    $json = $manifest | ConvertTo-Json -Depth 8 -Compress
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)
    if ($bytes.Length -lt 1 -or $bytes.Length -gt 65536) {
        throw 'The generated C# Installer release manifest exceeds its byte budget.'
    }
    [IO.File]::WriteAllBytes($output, $bytes)
    $written = Get-Item -LiteralPath $output -Force
    $expectedManifestHash = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes))
    $actualManifestHash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
    if ($written.PSIsContainer -or
        ($written.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $written.Length -ne $bytes.Length -or
        $actualManifestHash -cne $expectedManifestHash) {
        throw 'The generated C# Installer release manifest failed read-back validation.'
    }
    return $written
}

function Get-ClashSharpMainPackageDependency {
    <#
    .SYNOPSIS
        Returns the exact PackageDependency declarations from a main MSIX identity document.
    .DESCRIPTION
        Reads every direct PackageDependency and rejects missing identity fields or noncanonical
        minimum versions before returning the ordered dependency contracts.
    .PARAMETER ManifestDocument
        Securely parsed primary AppxManifest XML document.
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
    .DESCRIPTION
        Requires the expected subject, optional exact thumbprint, and optional Windows trust and
        timestamp evidence before returning a bounded signer summary.
    .PARAMETER LiteralPath
        Ordinary signed package file to inspect.
    .PARAMETER ExpectedSubject
        Exact certificate subject required for the signer.
    .PARAMETER ExpectedThumbprint
        Optional canonical uppercase SHA-1 thumbprint required for the signer.
    .PARAMETER RequireTrusted
        Requires Windows Authenticode status to be Valid.
    .PARAMETER RequireTimestamp
        Requires a timestamp signer certificate to be present.
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
    'Get-ClashSharpPublisherId',
    'Get-ClashSharpMsixMachineFileContract',
    'Get-ClashSharpMainPackageDependency',
    'Get-ClashSharpPackageSignature',
    'New-ClashSharpInstallerReleaseManifest'
)
