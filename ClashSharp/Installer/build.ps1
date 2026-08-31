#Requires -Version 7.0

[CmdletBinding()]
param(
    [switch] $Development
)

$ErrorActionPreference = "Stop"

$installerRoot = $PSScriptRoot
$repoRoot = Resolve-Path (Join-Path $installerRoot "..\..")
$appProject = Join-Path $repoRoot "ClashSharp\ClashSharp\ClashSharp.csproj"
$serviceProject = Join-Path $repoRoot "ClashSharp\ClashSharp.MihomoService\ClashSharp.MihomoService.csproj"
$watchdogProject = Join-Path $repoRoot "ClashSharp\ClashSharp.RecoveryWatchdog\ClashSharp.RecoveryWatchdog.csproj"
$installerProject = Join-Path $repoRoot "ClashSharp\ClashSharp.Installer\ClashSharp.Installer.csproj"
$appManifest = Join-Path (Split-Path -Parent $appProject) "Package.appxmanifest"
$mihomoBinary = Join-Path $repoRoot "ClashSharp\ClashSharp\Binaries\mihomo.exe"
$mihomoManifestPath = Join-Path $repoRoot "ClashSharp\ClashSharp\Binaries\mihomo-manifest.json"
$mihomoLicensePath = Join-Path $repoRoot "ClashSharp\ClashSharp\Binaries\mihomo-LICENSE.txt"
$mihomoNoticePath = Join-Path $repoRoot "ClashSharp\ClashSharp\Binaries\mihomo-NOTICE.txt"
$geoDataDirectory = Join-Path $repoRoot "ClashSharp\ClashSharp\Binaries\GeoData"
$geoDataManifest = Join-Path $geoDataDirectory "manifest.json"
$signingDir = Join-Path $installerRoot "signing"
$installerTargetRoot = Join-Path $repoRoot "artifacts\installer"
$packagingStagingRoot = Join-Path $installerTargetRoot "packaging-staging"
$releaseDir = Join-Path $installerTargetRoot "release"
$packagingContractModule = Join-Path $installerRoot "PackagingContract.psm1"

$packagingContractModuleItem = Get-Item -LiteralPath $packagingContractModule -Force
if ($packagingContractModuleItem.PSIsContainer -or
    ($packagingContractModuleItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw "PackagingContract.psm1 must be an ordinary local file."
}
Import-Module -Name $packagingContractModule -Force

$authenticodeThumbprint = if ($Development) {
    '0000000000000000000000000000000000000000'
} else {
    $configuredThumbprint = [string]$env:CLASHSHARP_AUTHENTICODE_CERTIFICATE_THUMBPRINT
    if ($configuredThumbprint -cnotmatch '^[0-9A-F]{40}$') {
        throw "Official release builds require canonical CLASHSHARP_AUTHENTICODE_CERTIFICATE_THUMBPRINT."
    }
    $configuredThumbprint
}

<#
.SYNOPSIS
Removes one generated Installer output directory after containment and reparse-point checks.
.DESCRIPTION
Accepts only an ordinary directory strictly below the fixed Installer artifact root and rejects
recursive deletion when the target, an ancestor, or any descendant is a reparse point.
.PARAMETER LiteralPath
Absolute or repository-relative path to the generated directory that may be removed.
#>
function Remove-GeneratedDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath
    )

    $targetRootFull = [IO.Path]::GetFullPath($installerTargetRoot).TrimEnd([char[]]@('\', '/'))
    $candidateFull = [IO.Path]::GetFullPath($LiteralPath).TrimEnd([char[]]@('\', '/'))
    if (-not $candidateFull.StartsWith(
            $targetRootFull + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean generated output outside the Installer target directory: $candidateFull"
    }
    $null = Assert-ClashSharpOrdinaryPath -LiteralPath $candidateFull -AllowMissing

    foreach ($ancestor in @($installerRoot, $installerTargetRoot)) {
        if (Test-Path -LiteralPath $ancestor) {
            $ancestorItem = Get-Item -LiteralPath $ancestor -Force
            if ($ancestorItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to clean generated output through a reparse ancestor: $ancestor"
            }
        }
    }

    if (-not (Test-Path -LiteralPath $candidateFull)) {
        return
    }

    $candidateItem = Get-Item -LiteralPath $candidateFull -Force
    if (-not $candidateItem.PSIsContainer -or
        ($candidateItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Generated output root must be an ordinary directory: $candidateFull"
    }
    $reparseEntry = Get-ChildItem -LiteralPath $candidateFull -Force -Recurse |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
        Select-Object -First 1
    if ($null -ne $reparseEntry) {
        throw "Refusing to recursively clean generated output containing a reparse entry: $($reparseEntry.FullName)"
    }

    Remove-Item -LiteralPath $candidateFull -Recurse -Force
}

<#
.SYNOPSIS
Returns a required, bounded ordinary file used as a trusted packaging input.
.DESCRIPTION
Rejects missing paths, directories, reparse points, empty files, and files beyond the declared
maximum before returning the FileInfo instance.
.PARAMETER LiteralPath
Literal path of the packaging input.
.PARAMETER Description
Stable human-readable name used in validation failures.
.PARAMETER MaximumLength
Maximum accepted file length in bytes.
#>
function Get-OrdinaryFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath,

        [Parameter(Mandatory = $true)]
        [string] $Description,

        [long] $MaximumLength = 1073741824
    )

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "$Description is missing: $LiteralPath"
    }

    $item = Get-Item -LiteralPath $LiteralPath -Force
    if ($item.PSIsContainer -or
        ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or
        $item.Length -lt 1 -or
        $item.Length -gt $MaximumLength) {
        throw "$Description must be an ordinary bounded file: $LiteralPath"
    }

    return $item
}

<#
.SYNOPSIS
Validates one staged Installer component against the executable payload allowlist.
.DESCRIPTION
Builds the canonical directory contract, rejects forbidden extensions and product artifacts,
requires every declared file, and optionally enforces an exact file set.
.PARAMETER LiteralPath
Root directory of the staged component.
.PARAMETER RequiredFiles
Canonical relative filenames that must be present.
.PARAMETER ExactFileSet
Requires the staged contract to contain no files beyond RequiredFiles.
#>
function Confirm-ClashSharpComponentStaging {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $LiteralPath,

        [Parameter(Mandatory)]
        [string[]] $RequiredFiles,

        [switch] $ExactFileSet
    )

    $contract = @(Get-ClashSharpDirectoryContract -LiteralPath $LiteralPath)
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $contract) {
        $relativePath = [string]$entry.RelativePath
        $null = $paths.Add($relativePath)
        if ($relativePath.Contains('/', [StringComparison]::Ordinal) -or
            [IO.Path]::GetExtension($relativePath) -cnotin @('.exe', '.dll', '.json', '.winmd') -or
            $relativePath -cmatch '(?i)(Probe|SandboxTest|Installer|Updater)' -or
            $relativePath -ceq 'packages.lock.json') {
            throw "Component staging contains a forbidden file: $relativePath"
        }
    }
    foreach ($requiredFile in $RequiredFiles) {
        if (-not $paths.Contains($requiredFile)) {
            throw "Component staging is missing required file: $requiredFile"
        }
    }
    if ($ExactFileSet -and $paths.Count -ne $RequiredFiles.Count) {
        throw "Component staging contains files outside its exact allowlist: $LiteralPath"
    }
    return $contract
}

<#
.SYNOPSIS
Converts a canonical four-part package version into a System.Version value.
.DESCRIPTION
Rejects noncanonical text and components outside the UInt16 range used by MSIX identity.
.PARAMETER Value
Four-part decimal package version to validate and convert.
#>
function ConvertTo-ClashSharpPackageVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Value
    )

    if ($Value -cnotmatch '^(0|[1-9][0-9]{0,4})(\.(0|[1-9][0-9]{0,4})){3}$') {
        throw "Package version is noncanonical: $Value"
    }
    $parts = @($Value.Split('.') | ForEach-Object { [int]$_ })
    if (@($parts | Where-Object { $_ -gt 65535 }).Count -ne 0) {
        throw "Package version component exceeds UInt16: $Value"
    }
    return [Version]::new($parts[0], $parts[1], $parts[2], $parts[3])
}

# A failed attempt must not leave an unsigned or stale file under the publishable artifact name.
Remove-GeneratedDirectory -LiteralPath $releaseDir

$null = Assert-ClashSharpOrdinaryPath -LiteralPath $packagingStagingRoot -AllowMissing
$null = [IO.Directory]::CreateDirectory($packagingStagingRoot)
$null = Assert-ClashSharpOrdinaryPath -LiteralPath $packagingStagingRoot -RequireDirectory
$packagingRunRoot = Join-Path $packagingStagingRoot ([Guid]::NewGuid().ToString('N'))
$componentStagingRoot = Join-Path $packagingRunRoot "components"
$servicePublishRoot = Join-Path $componentStagingRoot "service-publish"
$serviceStagingRoot = Join-Path $componentStagingRoot "service"
$watchdogPublishRoot = Join-Path $componentStagingRoot "watchdog-publish"
$watchdogStagingRoot = Join-Path $componentStagingRoot "watchdog"
$appPackageStagingRoot = Join-Path $packagingRunRoot "app-packages"
$payloadStagingDir = Join-Path $packagingRunRoot "payload"
$promotionStagingRoot = Join-Path $packagingRunRoot "promotion"
$installerPublishRoot = Join-Path $componentStagingRoot "installer-publish"
$installerReleaseManifestPath = Join-Path $packagingRunRoot "installer-release-manifest.json"

if (-not (Test-Path -LiteralPath $appManifest -PathType Leaf)) {
    throw "Package.appxmanifest was not found at the fixed app project path."
}

$manifestReaderSettings = [System.Xml.XmlReaderSettings]::new()
$manifestReaderSettings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
$manifestReaderSettings.XmlResolver = $null
$manifestReader = [System.Xml.XmlReader]::Create($appManifest, $manifestReaderSettings)
try {
    $manifestDocument = [System.Xml.XmlDocument]::new()
    $manifestDocument.XmlResolver = $null
    $manifestDocument.Load($manifestReader)
} finally {
    $manifestReader.Dispose()
}

$manifestIdentityNodes = @($manifestDocument.SelectNodes("/*[local-name()='Package']/*[local-name()='Identity']"))
if ($manifestIdentityNodes.Count -ne 1) {
    throw "Package.appxmanifest must contain exactly one Package/Identity element."
}

$manifestPublisher = [string]$manifestIdentityNodes[0].GetAttribute("Publisher")
if ([string]::IsNullOrWhiteSpace($manifestPublisher) -or $manifestPublisher -cne $manifestPublisher.Trim()) {
    throw "Package.appxmanifest Identity Publisher must be a non-empty canonical subject."
}

if (-not [string]::IsNullOrWhiteSpace($env:CLASHSHARP_CERTIFICATE_SUBJECT) -and
    -not $manifestPublisher.Equals($env:CLASHSHARP_CERTIFICATE_SUBJECT, [System.StringComparison]::Ordinal)) {
    throw "CLASHSHARP_CERTIFICATE_SUBJECT does not exactly match Package.appxmanifest Identity Publisher."
}
$certificateSubject = $manifestPublisher
$certificatePfxPath = Join-Path $signingDir "ClashSharp_TemporaryKey.pfx"
$certificateCerPath = Join-Path $signingDir "ClashSharp_TemporaryKey.cer"
$certificatePasswordText = $env:CLASHSHARP_CERTIFICATE_PASSWORD
# Keep the secret only in this script scope; no subsequently launched process may inherit it.
Remove-Item Env:\CLASHSHARP_CERTIFICATE_PASSWORD -ErrorAction SilentlyContinue

Set-Location $repoRoot

$geoDataManifestFile = Get-OrdinaryFile `
    -LiteralPath $geoDataManifest `
    -Description "Installer GeoData manifest" `
    -MaximumLength 65536
if ($null -ne $geoDataManifestFile) {
    $manifest = Get-Content -LiteralPath $geoDataManifest -Raw | ConvertFrom-Json
    $geoDataManifestProperties = @($manifest.PSObject.Properties.Name)
    if ($geoDataManifestProperties.Count -ne 2 -or
        $geoDataManifestProperties -cnotcontains "schemaVersion" -or
        $geoDataManifestProperties -cnotcontains "files" -or
        $manifest.schemaVersion -ne 1 -or
        $null -eq $manifest.files -or
        $manifest.files.Count -ne 4) {
        throw "Binaries\GeoData\manifest.json has an unsupported shape."
    }

    $allowedGeoDataNames = @("Country.mmdb", "GeoIP.dat", "GeoSite.dat", "ASN.mmdb")
    $seenGeoDataNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($asset in $manifest.files) {
        $assetProperties = @($asset.PSObject.Properties.Name)
        if ($assetProperties.Count -ne 3 -or
            $assetProperties -cnotcontains "name" -or
            $assetProperties -cnotcontains "length" -or
            $assetProperties -cnotcontains "sha256" -or
            $allowedGeoDataNames -cnotcontains $asset.name -or
            -not $seenGeoDataNames.Add([string]$asset.name)) {
            throw "GeoData manifest contains an invalid or duplicate asset name: $($asset.name)"
        }

        $assetPath = Join-Path $geoDataDirectory $asset.name
        if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
            throw "GeoData asset is missing: $($asset.name)"
        }

        $assetFile = Get-Item -LiteralPath $assetPath
        if (($assetFile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or
            $assetFile.Length -lt 1 -or
            $assetFile.Length -gt 268435456 -or
            $assetFile.Length -ne [long]$asset.length) {
            throw "GeoData asset length mismatch: $($asset.name)"
        }

        $assetHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not ([string]$asset.sha256 -cmatch '^[0-9a-f]{64}$') -or
            -not $assetHash.Equals([string]$asset.sha256, [System.StringComparison]::Ordinal)) {
            throw "GeoData asset SHA-256 mismatch: $($asset.name)"
        }
    }

    Get-ChildItem -LiteralPath $geoDataDirectory -Force |
        ForEach-Object {
            if ($_.PSIsContainer -or
                ($_.Name -ne "manifest.json" -and -not $seenGeoDataNames.Contains($_.Name))) {
                throw "GeoData payload contains an undeclared entry that would be packaged: $($_.Name)"
            }
        }
}

$mihomoManifestFile = Get-OrdinaryFile `
    -LiteralPath $mihomoManifestPath `
    -Description "Pinned mihomo manifest" `
    -MaximumLength 16384
$mihomoFile = Get-OrdinaryFile -LiteralPath $mihomoBinary -Description "Pinned mihomo binary"
$null = Get-OrdinaryFile -LiteralPath $mihomoLicensePath -Description "Mihomo license" -MaximumLength 1048576
$mihomoNoticeFile = Get-OrdinaryFile -LiteralPath $mihomoNoticePath -Description "Mihomo notice" -MaximumLength 1048576
$mihomoManifest = Get-Content -LiteralPath $mihomoManifestFile.FullName -Raw | ConvertFrom-Json
$mihomoManifestProperties = @($mihomoManifest.PSObject.Properties.Name)
if ($mihomoManifestProperties.Count -ne 5 -or
    $mihomoManifestProperties -cnotcontains "schemaVersion" -or
    $mihomoManifestProperties -cnotcontains "version" -or
    $mihomoManifestProperties -cnotcontains "sourceReleaseUrl" -or
    $mihomoManifestProperties -cnotcontains "length" -or
    $mihomoManifestProperties -cnotcontains "sha256" -or
    $mihomoManifest.schemaVersion -ne 1 -or
    [string]$mihomoManifest.version -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+$' -or
    [string]$mihomoManifest.sourceReleaseUrl -cne
        "https://github.com/MetaCubeX/mihomo/releases/tag/$($mihomoManifest.version)" -or
    [long]$mihomoManifest.length -ne $mihomoFile.Length -or
    [string]$mihomoManifest.sha256 -cnotmatch '^[0-9a-f]{64}$') {
    throw "Pinned mihomo manifest has an unsupported or noncanonical shape."
}

$actualMihomoSha256 = (Get-FileHash -LiteralPath $mihomoFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not $actualMihomoSha256.Equals([string]$mihomoManifest.sha256, [System.StringComparison]::Ordinal)) {
    throw "Bundled mihomo binary does not match the pinned release manifest."
}

$mihomoNotice = Get-Content -LiteralPath $mihomoNoticeFile.FullName -Raw
if (-not $mihomoNotice.Contains(
        "Bundled binary SHA256: $($mihomoManifest.sha256)",
        [System.StringComparison]::OrdinalIgnoreCase) -or
    -not $mihomoNotice.Contains(
        "Upstream release: $($mihomoManifest.sourceReleaseUrl)",
        [System.StringComparison]::Ordinal)) {
    throw "Mihomo notice does not match the pinned release manifest."
}

New-Item -ItemType Directory -Force -Path $signingDir | Out-Null
$certificatePfxExists = Test-Path -LiteralPath $certificatePfxPath -PathType Leaf
$certificateCerExists = Test-Path -LiteralPath $certificateCerPath -PathType Leaf
if ($certificatePfxExists -xor $certificateCerExists) {
    throw "MSIX signing material is incomplete; both the controlled PFX and public CER are required."
}

if (-not $certificatePfxExists) {
    if (-not $Development) {
        throw "Official release builds require controlled MSIX signing material. Use -Development only for an explicitly non-publishable local build."
    }

    if ([string]::IsNullOrWhiteSpace($certificatePasswordText)) {
        $certificatePasswordText = [Convert]::ToBase64String([Guid]::NewGuid().ToByteArray())
    }

    $certificatePassword = ConvertTo-SecureString $certificatePasswordText -AsPlainText -Force
    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $certificateSubject `
        -KeyUsage DigitalSignature `
        -FriendlyName "Clash# MSIX Development Certificate" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3") `
        -NotAfter (Get-Date).AddYears(3)
    Export-PfxCertificate -Cert $certificate -FilePath $certificatePfxPath -Password $certificatePassword | Out-Null
    Export-Certificate -Cert $certificate -FilePath $certificateCerPath | Out-Null
}

$null = Get-OrdinaryFile -LiteralPath $certificatePfxPath -Description "MSIX signing PFX" -MaximumLength 10485760
$null = Get-OrdinaryFile -LiteralPath $certificateCerPath -Description "MSIX signing CER" -MaximumLength 1048576
$payloadCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificateCerPath)
if (-not $payloadCertificate.Subject.Equals($manifestPublisher, [System.StringComparison]::Ordinal)) {
    throw "Installer signing CER subject does not exactly match Package.appxmanifest Identity Publisher. Remove Installer\signing and regenerate it."
}
if ($payloadCertificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
    throw "MSIX signing certificate has expired."
}

$expectedMsixThumbprint = [string]$env:CLASHSHARP_MSIX_CERTIFICATE_THUMBPRINT
if (-not $Development) {
    if ($expectedMsixThumbprint -cnotmatch '^[0-9A-F]{40}$' -or
        $payloadCertificate.Thumbprint -cne $expectedMsixThumbprint) {
        throw "Official release MSIX certificate does not match CLASHSHARP_MSIX_CERTIFICATE_THUMBPRINT."
    }
} elseif (-not [string]::IsNullOrWhiteSpace($expectedMsixThumbprint) -and
    $payloadCertificate.Thumbprint -cne $expectedMsixThumbprint) {
    throw "Development MSIX certificate does not match the explicitly supplied thumbprint."
}

$signingCertificate = Get-ChildItem -Path Cert:\CurrentUser\My |
    Where-Object {
        $_.Thumbprint -ceq $payloadCertificate.Thumbprint -and
        $_.Subject -ceq $manifestPublisher -and
        $_.HasPrivateKey -and
        ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq "1.3.6.1.5.5.7.3.3" })
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($null -eq $signingCertificate -and (Test-Path $certificatePfxPath)) {
    if ([string]::IsNullOrWhiteSpace($certificatePasswordText)) {
        throw "Set CLASHSHARP_CERTIFICATE_PASSWORD to import the existing signing PFX, or remove Installer\signing to generate a new development certificate."
    }

    $certificatePassword = ConvertTo-SecureString $certificatePasswordText -AsPlainText -Force
    Import-PfxCertificate -FilePath $certificatePfxPath -CertStoreLocation Cert:\CurrentUser\My -Password $certificatePassword | Out-Null
    $signingCertificate = Get-ChildItem -Path Cert:\CurrentUser\My |
        Where-Object {
            $_.Thumbprint -ceq $payloadCertificate.Thumbprint -and
            $_.Subject -ceq $manifestPublisher -and
            $_.HasPrivateKey -and
            ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq "1.3.6.1.5.5.7.3.3" })
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

if ($null -eq $signingCertificate) {
    throw "No private code-signing certificate matching the manifest Publisher and packaged CER was available for $certificateSubject."
}

if (-not $Development -and $signingCertificate.Thumbprint -cne $expectedMsixThumbprint) {
    throw "The private MSIX signing key does not match the controlled release certificate."
}

# Do not expose the PFX password to MSBuild or dependency build scripts.
$certificatePasswordText = $null
Remove-Variable -Name certificatePassword -Scope Script -ErrorAction SilentlyContinue

$packagingSucceeded = $false
try {
$null = Assert-ClashSharpOrdinaryPath -LiteralPath $packagingRunRoot -AllowMissing
$null = New-Item -ItemType Directory -Path $packagingRunRoot
$null = Assert-ClashSharpOrdinaryPath -LiteralPath $packagingRunRoot -RequireDirectory
Set-Location $installerRoot
$null = New-Item -ItemType Directory -Path $componentStagingRoot
dotnet publish $serviceProject `
    -c Release `
    --no-restore `
    -p:Platform=x64 `
    -p:ClashSharpFormalInstallerComponent=true `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -p:PublishDocumentationFiles=false `
    -o $servicePublishRoot
if ($LASTEXITCODE -ne 0) {
    throw "MihomoService publish failed with exit code $LASTEXITCODE."
}
$null = Copy-ClashSharpComponentPayload `
    -Source $servicePublishRoot `
    -Destination $serviceStagingRoot
$null = Confirm-ClashSharpComponentStaging `
    -LiteralPath $serviceStagingRoot `
    -RequiredFiles @('ClashSharp.MihomoService.exe') `
    -ExactFileSet

dotnet publish $watchdogProject `
    -c Release `
    --no-restore `
    -p:Platform=x64 `
    -p:ClashSharpFormalInstallerComponent=true `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -p:PublishDocumentationFiles=false `
    -o $watchdogPublishRoot
if ($LASTEXITCODE -ne 0) {
    throw "RecoveryWatchdog publish failed with exit code $LASTEXITCODE."
}
$null = Copy-ClashSharpComponentPayload `
    -Source $watchdogPublishRoot `
    -Destination $watchdogStagingRoot
$null = Confirm-ClashSharpComponentStaging `
    -LiteralPath $watchdogStagingRoot `
    -RequiredFiles @('ClashSharp.RecoveryWatchdog.exe') `
    -ExactFileSet

$null = New-Item -ItemType Directory -Path $appPackageStagingRoot
dotnet publish $appProject `
    -c Release `
    --no-restore `
    -p:Platform=x64 `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxBundle=Never `
    -p:AppxPackageSigningEnabled=true `
    -p:AppxPackageDir=$appPackageStagingRoot `
    -p:ClashSharpInstallerServiceRoot=$serviceStagingRoot `
    -p:ClashSharpInstallerWatchdogRoot=$watchdogStagingRoot `
    -p:PackageCertificateThumbprint=$($signingCertificate.Thumbprint)
if ($LASTEXITCODE -ne 0) {
    throw "MSIX publish failed with exit code $LASTEXITCODE."
}

$null = Assert-ClashSharpOrdinaryPath -LiteralPath $appPackageStagingRoot -RequireDirectory
$null = Get-ClashSharpDirectoryContract -LiteralPath $appPackageStagingRoot
$allPackageFiles = @(Get-ChildItem -LiteralPath $appPackageStagingRoot -File -Recurse |
    Where-Object { $_.Extension.Equals('.msix', [StringComparison]::OrdinalIgnoreCase) })
$dependencyPackages = @($allPackageFiles | Where-Object {
        ([IO.Path]::GetRelativePath(
                $appPackageStagingRoot,
                $_.FullName).Replace('\', '/')) -match '(?i)(^|/)Dependencies/[^/]+/[^/]+\.msix$'
    })
$appPackages = @($allPackageFiles | Where-Object {
        $dependencyPackages.FullName -cnotcontains $_.FullName
    })
if ($appPackages.Count -ne 1) {
    throw "The isolated build did not produce exactly one primary Clash# MSIX package."
}
$appPackage = $appPackages[0]
$appIdentity = Get-ClashSharpMsixIdentity -LiteralPath $appPackage.FullName
if (-not $appIdentity.Publisher.Equals($manifestPublisher, [StringComparison]::Ordinal) -or
    $appIdentity.Architecture -cne 'x64') {
    throw "The final main MSIX Publisher or architecture does not match the packaging contract."
}
$declaredDependencies = @(Get-ClashSharpMainPackageDependency `
        -ManifestDocument $appIdentity.Document)
$windowsAppRuntimePublisher = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
if ($declaredDependencies.Count -ne 1 -or
    $declaredDependencies[0].Name -cne 'Microsoft.WindowsAppRuntime.1.8' -or
    -not $declaredDependencies[0].Publisher.Equals(
        $windowsAppRuntimePublisher,
        [StringComparison]::Ordinal)) {
    throw "The final main MSIX dependency declaration is outside the exact product contract."
}

if ($dependencyPackages.Count -ne $declaredDependencies.Count) {
    throw "The staged dependency package count does not match the final AppxManifest."
}

$null = New-Item -ItemType Directory -Path $payloadStagingDir
$primaryPayloadPath = Join-Path $payloadStagingDir $appPackage.Name
Copy-Item -LiteralPath $appPackage.FullName -Destination $primaryPayloadPath
$certificatePayloadPath = Join-Path $payloadStagingDir (Split-Path $certificateCerPath -Leaf)
Copy-Item -LiteralPath $certificateCerPath -Destination $certificatePayloadPath
$null = Get-OrdinaryFile `
    -LiteralPath $certificatePayloadPath `
    -Description "staged payload signing CER" `
    -MaximumLength 1048576
$stagedPayloadCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certificatePayloadPath)
if (-not $stagedPayloadCertificate.Subject.Equals($manifestPublisher, [StringComparison]::Ordinal) -or
    $stagedPayloadCertificate.Thumbprint -cne $payloadCertificate.Thumbprint) {
    throw "The staged payload CER does not match the verified MSIX signing identity."
}
$payloadDependencyDir = Join-Path $payloadStagingDir "Dependencies\x64"
$null = New-Item -ItemType Directory -Path $payloadDependencyDir

$dependencyProvenance = [Collections.Generic.List[object]]::new()
$csharpDependencyContracts = [Collections.Generic.List[object]]::new()
$expectedDependencyThumbprint = [string]$env:CLASHSHARP_WINDOWS_APP_RUNTIME_SIGNER_THUMBPRINT
if ($expectedDependencyThumbprint -cnotmatch '^[0-9A-F]{40}$') {
    throw "Packaging requires canonical CLASHSHARP_WINDOWS_APP_RUNTIME_SIGNER_THUMBPRINT."
}
foreach ($declaration in $declaredDependencies) {
    $matchingPackages = @($dependencyPackages | Where-Object {
            $_.Name -ceq "$($declaration.Name).msix"
        })
    if ($matchingPackages.Count -ne 1) {
        throw "Dependency payload is missing the exact package for $($declaration.Name)."
    }
    $dependencySource = $matchingPackages[0]
    $dependencyRelativeSource = [IO.Path]::GetRelativePath(
        $appPackageStagingRoot,
        $dependencySource.FullName).Replace('\', '/')
    if ($dependencyRelativeSource -cnotmatch '(^|/)Dependencies/x64/[^/]+\.msix$') {
        throw "Dependency package is outside the exact x64 dependency directory."
    }
    $dependencyIdentity = Get-ClashSharpMsixIdentity -LiteralPath $dependencySource.FullName
    if ($dependencyIdentity.Name -cne $declaration.Name -or
        -not $dependencyIdentity.Publisher.Equals($declaration.Publisher, [StringComparison]::Ordinal) -or
        $dependencyIdentity.Architecture -cne 'x64' -or
        (ConvertTo-ClashSharpPackageVersion -Value $dependencyIdentity.Version) -lt
            (ConvertTo-ClashSharpPackageVersion -Value $declaration.MinVersion)) {
        throw "Dependency identity, publisher, version, or architecture is invalid: $($dependencySource.Name)"
    }

    $dependencyPayloadPath = Join-Path $payloadDependencyDir $dependencySource.Name
    Copy-Item -LiteralPath $dependencySource.FullName -Destination $dependencyPayloadPath
    $dependencySignature = Get-ClashSharpPackageSignature `
        -LiteralPath $dependencyPayloadPath `
        -ExpectedSubject $declaration.Publisher `
        -ExpectedThumbprint $expectedDependencyThumbprint `
        -RequireTrusted `
        -RequireTimestamp
    $dependencyPayloadFile = Get-Item -LiteralPath $dependencyPayloadPath -Force
    $dependencyProvenance.Add([PSCustomObject]@{
            path               = "Dependencies/x64/$($dependencySource.Name)"
            length             = [long]$dependencyPayloadFile.Length
            sha256             = (Get-FileHash -LiteralPath $dependencyPayloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
            name               = $dependencyIdentity.Name
            publisher          = $dependencyIdentity.Publisher
            version            = $dependencyIdentity.Version
            architecture       = $dependencyIdentity.Architecture
            signerSubject      = $dependencySignature.Subject
            signerThumbprint   = $dependencySignature.Thumbprint
            signatureTimestamp = [bool]$dependencySignature.Timestamp
        })
    $csharpDependencyContracts.Add([PSCustomObject]@{
            Path           = "dependencies/x64/$($dependencySource.Name.ToLowerInvariant())"
            MinimumVersion = [string]$declaration.MinVersion
            Identity       = $dependencyIdentity
        })
}

$mainSignatureParameters = @{
    LiteralPath        = $primaryPayloadPath
    ExpectedSubject    = $manifestPublisher
    ExpectedThumbprint = $payloadCertificate.Thumbprint
}
if (-not $Development) {
    $mainSignatureParameters.RequireTrusted = $true
}
$mainSignature = Get-ClashSharpPackageSignature @mainSignatureParameters
$primaryPayloadFile = Get-Item -LiteralPath $primaryPayloadPath -Force
$certificatePayloadFile = Get-Item -LiteralPath $certificatePayloadPath -Force
$provenance = [ordered]@{
    schemaVersion = 1
    primary       = [ordered]@{
        path             = $appPackage.Name
        length           = [long]$primaryPayloadFile.Length
        sha256           = (Get-FileHash -LiteralPath $primaryPayloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
        name             = $appIdentity.Name
        publisher        = $appIdentity.Publisher
        version          = $appIdentity.Version
        architecture     = $appIdentity.Architecture
        signerSubject    = $mainSignature.Subject
        signerThumbprint = $mainSignature.Thumbprint
    }
    certificate   = [ordered]@{
        path       = $certificatePayloadFile.Name
        length     = [long]$certificatePayloadFile.Length
        sha256     = (Get-FileHash -LiteralPath $certificatePayloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
        subject    = $stagedPayloadCertificate.Subject
        thumbprint = $stagedPayloadCertificate.Thumbprint
    }
    dependencies  = @($dependencyProvenance)
}
$provenancePath = Join-Path $payloadStagingDir 'payload-provenance.json'
$provenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $provenancePath -Encoding utf8NoBOM
$stagedPayloadCertificate.Dispose()
$null = Get-ClashSharpDirectoryContract -LiteralPath $payloadStagingDir

$installerReleaseManifest = New-ClashSharpInstallerReleaseManifest `
    -PayloadRoot $payloadStagingDir `
    -PrimaryIdentity $appIdentity `
    -PrimaryRelativePath ($appPackage.Name.ToLowerInvariant()) `
    -DependencyContracts @($csharpDependencyContracts) `
    -CertificateRelativePath ($certificatePayloadFile.Name.ToLowerInvariant()) `
    -CertificateThumbprint $payloadCertificate.Thumbprint `
    -AuthenticodeCertificateThumbprint $authenticodeThumbprint `
    -OutputPath $installerReleaseManifestPath

dotnet publish $installerProject `
    -c Release `
    --no-restore `
    -p:Platform=x64 `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -p:PublishDocumentationFiles=false `
    -p:ClashSharpFormalInstallerBuild=true `
    "-p:ClashSharpInstallerReleaseManifestPath=$($installerReleaseManifest.FullName)" `
    -o $installerPublishRoot
if ($LASTEXITCODE -ne 0) {
    throw "WPF Installer publish failed with exit code $LASTEXITCODE."
}

$installerEntries = @(Get-ChildItem -LiteralPath $installerPublishRoot -Force)
if ($installerEntries.Count -ne 1 -or
    $installerEntries[0].PSIsContainer -or
    ($installerEntries[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -or
    $installerEntries[0].Name -cne 'ClashSharp.Installer.exe') {
    throw 'The WPF Installer must publish as one self-contained executable.'
}
Write-Host 'WPF Installer passed its isolated single-file build contract.'

    $stagedInstallerExecutable = $installerEntries[0].FullName
    $null = Get-OrdinaryFile `
        -LiteralPath $stagedInstallerExecutable `
        -Description "staged WPF Installer executable"
    $null = New-Item -ItemType Directory -Path $promotionStagingRoot
    $developmentExecutable = Join-Path $promotionStagingRoot "ClashSharp-Installer-Development-Unsigned.exe"
    $developmentMarker = Join-Path $promotionStagingRoot "DEVELOPMENT-UNSIGNED.txt"

    if ($Development) {
        Move-Item `
            -LiteralPath $stagedInstallerExecutable `
            -Destination $developmentExecutable
        $installerExecutable = $developmentExecutable
        Set-Content `
            -LiteralPath $developmentMarker `
            -Encoding utf8NoBOM `
            -Value "Development-only unsigned Installer. Do not publish or distribute this artifact."
        Write-Warning "Built an explicitly unsigned development Installer. It is not a release artifact."
    } else {
        $timestampUrlText = [string]$env:CLASHSHARP_AUTHENTICODE_TIMESTAMP_URL
        try {
            $timestampUri = [Uri]$timestampUrlText
        } catch {
            throw "CLASHSHARP_AUTHENTICODE_TIMESTAMP_URL is not a valid absolute URI."
        }
        if (-not $timestampUri.IsAbsoluteUri -or
            $timestampUri.Scheme -cne "https" -or
            -not [string]::IsNullOrEmpty($timestampUri.UserInfo)) {
            throw "CLASHSHARP_AUTHENTICODE_TIMESTAMP_URL must be an HTTPS URI without user information."
        }

        $authenticodeCertificate = Get-ChildItem -Path Cert:\CurrentUser\My |
            Where-Object {
                $_.Thumbprint -ceq $authenticodeThumbprint -and
                $_.HasPrivateKey -and
                $_.NotAfter.ToUniversalTime() -gt [DateTime]::UtcNow -and
                ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq "1.3.6.1.5.5.7.3.3" })
            } |
            Select-Object -First 1
        if ($null -eq $authenticodeCertificate) {
            throw "The controlled Authenticode certificate/private key is unavailable or invalid."
        }

        $windowsSdkVersion = [string]$env:CLASHSHARP_WINDOWS_SDK_VERSION
        if ($windowsSdkVersion -cnotmatch '^10\.0\.[0-9]+\.[0-9]+$') {
            throw "Official release builds require a pinned CLASHSHARP_WINDOWS_SDK_VERSION."
        }

        $windowsKitsBin = Join-Path `
            ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)) `
            "Windows Kits\10\bin"
        $windowsKitsBinItem = Get-Item -LiteralPath $windowsKitsBin -Force
        $windowsSdkBin = Join-Path $windowsKitsBin $windowsSdkVersion
        $windowsSdkBinItem = Get-Item -LiteralPath $windowsSdkBin -Force
        $windowsSdkX64Bin = Join-Path $windowsSdkBin "x64"
        $windowsSdkX64BinItem = Get-Item -LiteralPath $windowsSdkX64Bin -Force
        if (($windowsKitsBinItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or
            ($windowsSdkBinItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or
            ($windowsSdkX64BinItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "The pinned Windows SDK SignTool path must not traverse a reparse point."
        }

        $signToolPath = Join-Path $windowsSdkX64Bin "signtool.exe"
        $signToolFile = Get-OrdinaryFile `
            -LiteralPath $signToolPath `
            -Description "Pinned Windows SDK SignTool" `
            -MaximumLength 104857600
        $signToolPath = $signToolFile.FullName
        $signToolSignature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature `
            -LiteralPath $signToolPath
        if ($signToolSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
            $null -eq $signToolSignature.SignerCertificate -or
            $null -eq $signToolSignature.TimeStamperCertificate -or
            $signToolSignature.SignerCertificate.Subject -cnotmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
            throw "The pinned Windows SDK SignTool is not a trusted, timestamped Microsoft executable."
        }

        & $signToolPath sign `
            /fd SHA256 `
            /sha1 $authenticodeThumbprint `
            /tr $timestampUri.AbsoluteUri `
            /td SHA256 `
            $stagedInstallerExecutable
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode signing failed with exit code $LASTEXITCODE."
        }

        & $signToolPath verify /pa /all /tw /v $stagedInstallerExecutable
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode verification failed with exit code $LASTEXITCODE."
        }

        $authenticodeSignature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature `
            -LiteralPath $stagedInstallerExecutable
        if ($authenticodeSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
            $null -eq $authenticodeSignature.SignerCertificate -or
            $authenticodeSignature.SignerCertificate.Thumbprint -cne $authenticodeThumbprint -or
            $null -eq $authenticodeSignature.TimeStamperCertificate) {
            throw "Final Installer Authenticode signer, trust chain, or RFC3161 timestamp is invalid."
        }

        # The official filename appears only after the staged executable is signed and verified.
        $installerExecutable = Join-Path $promotionStagingRoot "ClashSharp-Installer.exe"
        Move-Item -LiteralPath $stagedInstallerExecutable -Destination $installerExecutable
    }

    $installerSha256 = (Get-FileHash -LiteralPath $installerExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
    $installerHashPath = "$installerExecutable.sha256"
    Set-Content `
        -LiteralPath $installerHashPath `
        -Encoding ascii `
        -Value "$installerSha256 *$(Split-Path -Leaf $installerExecutable)"
    Write-Host "Installer artifact SHA-256: $installerSha256"

    $promotionPayloadDir = Join-Path $promotionStagingRoot "payload"
    $null = Copy-ClashSharpVerifiedDirectory `
        -Source $payloadStagingDir `
        -Destination $promotionPayloadDir
    $promotionContract = @(Get-ClashSharpDirectoryContract -LiteralPath $promotionStagingRoot)
    $releaseContract = @(Copy-ClashSharpVerifiedDirectory `
            -Source $promotionStagingRoot `
            -Destination $releaseDir)
    Compare-ClashSharpDirectoryContract -Expected $promotionContract -Actual $releaseContract
    $packagingSucceeded = $true
} finally {
    try {
        if (-not $packagingSucceeded) {
            Remove-GeneratedDirectory -LiteralPath $releaseDir
        }
    } finally {
        Remove-GeneratedDirectory -LiteralPath $packagingRunRoot
    }
}
