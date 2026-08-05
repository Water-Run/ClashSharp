#Requires -Version 7.0

[CmdletBinding()]
param(
    [switch] $Development
)

$ErrorActionPreference = "Stop"

$installerRoot = $PSScriptRoot
$repoRoot = Resolve-Path (Join-Path $installerRoot "..\..")
$appProject = Join-Path $repoRoot "ClashSharp\ClashSharp\ClashSharp.csproj"
$appManifest = Join-Path (Split-Path -Parent $appProject) "Package.appxmanifest"
$mihomoBinary = Join-Path $repoRoot "ClashSharp\ClashSharp\Binaries\mihomo.exe"
$mihomoManifestPath = Join-Path $repoRoot "ClashSharp\ClashSharp\Binaries\mihomo-manifest.json"
$mihomoLicensePath = Join-Path $repoRoot "ClashSharp\ClashSharp\Binaries\mihomo-LICENSE.txt"
$mihomoNoticePath = Join-Path $repoRoot "ClashSharp\ClashSharp\Binaries\mihomo-NOTICE.txt"
$geoDataDirectory = Join-Path $repoRoot "ClashSharp\ClashSharp\Binaries\GeoData"
$geoDataManifest = Join-Path $geoDataDirectory "manifest.json"
$payloadDir = Join-Path $installerRoot "payload"
$signingDir = Join-Path $installerRoot "signing"
$rustTarget = "x86_64-pc-windows-msvc"
$installerTargetRoot = Join-Path $installerRoot "target"
$cargoStagingRoot = Join-Path $installerTargetRoot "packaging-staging"
$releaseDir = Join-Path $installerTargetRoot "release-artifacts"
$legacyCargoReleaseDir = Join-Path $installerTargetRoot "release"

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

function Remove-GeneratedFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath
    )

    $targetRootFull = [IO.Path]::GetFullPath($installerTargetRoot).TrimEnd([char[]]@('\', '/'))
    $candidateFull = [IO.Path]::GetFullPath($LiteralPath)
    if (-not $candidateFull.StartsWith(
            $targetRootFull + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean generated file outside the Installer target directory: $candidateFull"
    }
    foreach ($ancestor in @($installerRoot, $installerTargetRoot)) {
        if (Test-Path -LiteralPath $ancestor) {
            $ancestorItem = Get-Item -LiteralPath $ancestor -Force
            if ($ancestorItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to clean generated file through a reparse ancestor: $ancestor"
            }
        }
    }
    if (-not (Test-Path -LiteralPath $candidateFull)) {
        return
    }

    $item = Get-Item -LiteralPath $candidateFull -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Generated output must be an ordinary file: $candidateFull"
    }
    Remove-Item -LiteralPath $candidateFull -Force
}

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

# A failed attempt must not leave an unsigned or stale file under the publishable artifact name.
Remove-GeneratedDirectory -LiteralPath $cargoStagingRoot
Remove-GeneratedDirectory -LiteralPath $releaseDir
if (Test-Path -LiteralPath $legacyCargoReleaseDir) {
    $legacyCargoReleaseItem = Get-Item -LiteralPath $legacyCargoReleaseDir -Force
    if (-not $legacyCargoReleaseItem.PSIsContainer -or
        ($legacyCargoReleaseItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Legacy Cargo release output must be an ordinary directory."
    }
}
foreach ($legacyArtifactName in @(
        "ClashSharp-Installer.exe",
        "ClashSharp-Installer.exe.sha256",
        "ClashSharp-Installer-Development-Unsigned.exe",
        "ClashSharp-Installer-Development-Unsigned.exe.sha256",
        "ClashSharp-Installer-Development-Unsigned.stale.exe",
        "DEVELOPMENT-UNSIGNED.txt")) {
    Remove-GeneratedFile -LiteralPath (Join-Path $legacyCargoReleaseDir $legacyArtifactName)
}
Remove-GeneratedDirectory -LiteralPath (Join-Path $legacyCargoReleaseDir "payload")

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

# Do not expose the PFX password to MSBuild, Cargo, or dependency build scripts.
$certificatePasswordText = $null
Remove-Variable -Name certificatePassword -Scope Script -ErrorAction SilentlyContinue

$packageBuildStartedUtc = [DateTime]::UtcNow
dotnet publish $appProject `
    -c Release `
    --no-restore `
    -p:Platform=x64 `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxBundle=Never `
    -p:AppxPackageSigningEnabled=true `
    -p:PackageCertificateThumbprint=$($signingCertificate.Thumbprint)
if ($LASTEXITCODE -ne 0) {
    throw "MSIX publish failed with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
Get-ChildItem -Path $payloadDir -File -Recurse |
    Where-Object { $_.Name -ne ".gitkeep" } |
    Remove-Item -Force

$packageRoot = Join-Path (Split-Path $appProject) "AppPackages"
$latestPackageDirectory = Get-ChildItem -LiteralPath $packageRoot -Directory |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if ($null -eq $latestPackageDirectory) {
    throw "No AppPackages output directory was produced."
}

$appPackages = @(Get-ChildItem -LiteralPath $latestPackageDirectory.FullName -File |
    Where-Object {
        $_.Extension -eq ".msix" -and
        $_.Name -like "ClashSharp_*" -and
        $_.LastWriteTimeUtc -ge $packageBuildStartedUtc.AddSeconds(-2)
    })

if ($appPackages.Count -ne 1) {
    throw "The current build did not produce exactly one fresh Clash# MSIX package."
}
$appPackage = $appPackages[0]

Copy-Item -LiteralPath $appPackage.FullName -Destination (Join-Path $payloadDir $appPackage.Name) -Force
Copy-Item -LiteralPath $certificateCerPath -Destination (Join-Path $payloadDir (Split-Path $certificateCerPath -Leaf)) -Force

$x64DependencyDir = Join-Path $latestPackageDirectory.FullName "Dependencies\x64"
if (Test-Path $x64DependencyDir) {
    $payloadDependencyDir = Join-Path $payloadDir "Dependencies\x64"
    New-Item -ItemType Directory -Force -Path $payloadDependencyDir | Out-Null
    Get-ChildItem -Path $x64DependencyDir -File -Filter "*.msix" |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $payloadDependencyDir $_.Name) -Force
        }
}

Set-Location $installerRoot
$packagingSucceeded = $false
try {
    New-Item -ItemType Directory -Force -Path $cargoStagingRoot | Out-Null
    $previousPackagingMode = $env:CLASHSHARP_INSTALLER_PACKAGING_MODE
    try {
        $env:CLASHSHARP_INSTALLER_PACKAGING_MODE = if ($Development) { "development" } else { "official" }
        cargo build --release --frozen --target $rustTarget --target-dir $cargoStagingRoot
        if ($LASTEXITCODE -ne 0) {
            throw "Rust Installer release build failed with exit code $LASTEXITCODE."
        }
    } finally {
        if ($null -eq $previousPackagingMode) {
            Remove-Item Env:\CLASHSHARP_INSTALLER_PACKAGING_MODE -ErrorAction SilentlyContinue
        } else {
            $env:CLASHSHARP_INSTALLER_PACKAGING_MODE = $previousPackagingMode
        }
    }

    $stagedInstallerExecutable = Join-Path `
        $cargoStagingRoot `
        "$rustTarget\release\ClashSharp-Installer.exe"
    $null = Get-OrdinaryFile `
        -LiteralPath $stagedInstallerExecutable `
        -Description "staged Rust Installer executable"
    New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
    $developmentExecutable = Join-Path $releaseDir "ClashSharp-Installer-Development-Unsigned.exe"
    $developmentMarker = Join-Path $releaseDir "DEVELOPMENT-UNSIGNED.txt"

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
        $authenticodeThumbprint = [string]$env:CLASHSHARP_AUTHENTICODE_CERTIFICATE_THUMBPRINT
        if ($authenticodeThumbprint -cnotmatch '^[0-9A-F]{40}$') {
            throw "Official release builds require canonical CLASHSHARP_AUTHENTICODE_CERTIFICATE_THUMBPRINT."
        }

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
        $installerExecutable = Join-Path $releaseDir "ClashSharp-Installer.exe"
        Move-Item -LiteralPath $stagedInstallerExecutable -Destination $installerExecutable
    }

    $installerSha256 = (Get-FileHash -LiteralPath $installerExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
    $installerHashPath = "$installerExecutable.sha256"
    Set-Content `
        -LiteralPath $installerHashPath `
        -Encoding ascii `
        -Value "$installerSha256 *$(Split-Path -Leaf $installerExecutable)"
    Write-Host "Installer artifact SHA-256: $installerSha256"

    $releasePayloadDir = Join-Path $releaseDir "payload"
    New-Item -ItemType Directory -Path $releasePayloadDir | Out-Null
    Copy-Item -Path (Join-Path $payloadDir "*") -Destination $releasePayloadDir -Recurse
    $packagingSucceeded = $true
} finally {
    try {
        if (-not $packagingSucceeded) {
            Remove-GeneratedDirectory -LiteralPath $releaseDir
        }
    } finally {
        Remove-GeneratedDirectory -LiteralPath $cargoStagingRoot
    }
}
