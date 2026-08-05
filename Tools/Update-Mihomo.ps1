#Requires -Version 7.0

[CmdletBinding()]
param(
    [switch] $Force,
    [string] $Version,
    [string] $AssetName,
    [string] $ExpectedArchiveSha256,
    [string] $ExpectedBinarySha256
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$binaryDirectory = Join-Path $repoRoot "ClashSharp\ClashSharp\Binaries"
$binaryPath = Join-Path $binaryDirectory "mihomo.exe"
$licensePath = Join-Path $binaryDirectory "mihomo-LICENSE.txt"
$noticePath = Join-Path $binaryDirectory "mihomo-NOTICE.txt"
$manifestPath = Join-Path $binaryDirectory "mihomo-manifest.json"
$workDirectory = Join-Path $repoRoot "ClashSharp\.download\mihomo"

function Get-OrdinaryFile {
    param(
        [Parameter(Mandatory = $true)] [string] $LiteralPath,
        [Parameter(Mandatory = $true)] [string] $Description,
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

function Assert-CanonicalSha256 {
    param(
        [Parameter(Mandatory = $true)] [string] $Value,
        [Parameter(Mandatory = $true)] [string] $Description
    )
    if ($Value -cnotmatch '^[0-9a-f]{64}$') {
        throw "$Description must be a canonical lowercase SHA-256 value."
    }
}

function Read-PinnedManifest {
    $manifestFile = Get-OrdinaryFile `
        -LiteralPath $manifestPath `
        -Description "Pinned mihomo manifest" `
        -MaximumLength 16384
    $manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
    $properties = @($manifest.PSObject.Properties.Name)
    if ($properties.Count -ne 5 -or
        $properties -cnotcontains "schemaVersion" -or
        $properties -cnotcontains "version" -or
        $properties -cnotcontains "sourceReleaseUrl" -or
        $properties -cnotcontains "length" -or
        $properties -cnotcontains "sha256" -or
        $manifest.schemaVersion -ne 1 -or
        [string]$manifest.version -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+$' -or
        [string]$manifest.sourceReleaseUrl -cne
            "https://github.com/MetaCubeX/mihomo/releases/tag/$($manifest.version)") {
        throw "Pinned mihomo manifest has an unsupported or noncanonical shape."
    }
    Assert-CanonicalSha256 -Value ([string]$manifest.sha256) -Description "Pinned binary hash"
    return $manifest
}

function Test-PinnedDistribution {
    $manifest = Read-PinnedManifest
    $binary = Get-OrdinaryFile -LiteralPath $binaryPath -Description "Pinned mihomo binary"
    $null = Get-OrdinaryFile -LiteralPath $licensePath -Description "Mihomo license" -MaximumLength 1048576
    $notice = Get-OrdinaryFile -LiteralPath $noticePath -Description "Mihomo notice" -MaximumLength 1048576
    $actualHash = (Get-FileHash -LiteralPath $binary.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($binary.Length -ne [long]$manifest.length -or
        -not $actualHash.Equals([string]$manifest.sha256, [System.StringComparison]::Ordinal)) {
        throw "Bundled mihomo binary does not match its pinned manifest."
    }
    $noticeText = Get-Content -LiteralPath $notice.FullName -Raw
    if (-not $noticeText.Contains(
            "Bundled binary SHA256: $($manifest.sha256)",
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $noticeText.Contains(
            "Upstream release: $($manifest.sourceReleaseUrl)",
            [System.StringComparison]::Ordinal)) {
        throw "Mihomo notice does not match its pinned manifest."
    }
    return $manifest
}

if (-not $Force) {
    $current = Test-PinnedDistribution
    Write-Output "Pinned mihomo $($current.version) verified."
    exit 0
}

if ($Version -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "-Force requires an exact Mihomo release tag; latest is not accepted."
}
if ($AssetName -cnotmatch '^mihomo-windows-amd64-[a-zA-Z0-9.-]+\.zip$' -or
    $AssetName.Contains("..", [System.StringComparison]::Ordinal)) {
    throw "-Force requires the exact safe Windows amd64 release asset name."
}
Assert-CanonicalSha256 -Value $ExpectedArchiveSha256 -Description "Expected archive hash"
Assert-CanonicalSha256 -Value $ExpectedBinarySha256 -Description "Expected binary hash"
$null = Get-OrdinaryFile -LiteralPath $licensePath -Description "Tracked Mihomo license" -MaximumLength 1048576

$releaseUri = "https://api.github.com/repos/MetaCubeX/mihomo/releases/tags/$Version"
$release = Invoke-RestMethod -Uri $releaseUri -Headers @{ "User-Agent" = "ClashSharp-Maintainer" }
if ([string]$release.tag_name -cne $Version -or
    [string]$release.html_url -cne "https://github.com/MetaCubeX/mihomo/releases/tag/$Version") {
    throw "GitHub returned a release identity that does not match the requested tag."
}
$assets = @($release.assets | Where-Object { [string]$_.name -ceq $AssetName })
if ($assets.Count -ne 1) {
    throw "The exact pinned Mihomo asset was not found uniquely in release $Version."
}
$asset = $assets[0]
if ([string]$asset.browser_download_url -cne
    "https://github.com/MetaCubeX/mihomo/releases/download/$Version/$AssetName") {
    throw "GitHub returned an unexpected Mihomo asset URL."
}

New-Item -ItemType Directory -Force -Path $workDirectory | Out-Null
$zipPath = Join-Path $workDirectory $AssetName
$extractDirectory = Join-Path $workDirectory "extract-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $extractDirectory | Out-Null
try {
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -UseBasicParsing
    $archive = Get-OrdinaryFile -LiteralPath $zipPath -Description "Downloaded Mihomo archive"
    $archiveHash = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $archiveHash.Equals($ExpectedArchiveSha256, [System.StringComparison]::Ordinal)) {
        throw "Downloaded Mihomo archive does not match the maintainer-supplied SHA-256."
    }

    Expand-Archive -LiteralPath $archive.FullName -DestinationPath $extractDirectory
    $executables = @(Get-ChildItem -LiteralPath $extractDirectory -Recurse -File |
        Where-Object { $_.Name -ceq "mihomo.exe" })
    if ($executables.Count -ne 1) {
        throw "Pinned Mihomo archive must contain exactly one mihomo.exe."
    }
    $downloadedBinary = Get-OrdinaryFile `
        -LiteralPath $executables[0].FullName `
        -Description "Downloaded Mihomo binary"
    $binaryHash = (Get-FileHash -LiteralPath $downloadedBinary.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $binaryHash.Equals($ExpectedBinarySha256, [System.StringComparison]::Ordinal)) {
        throw "Downloaded Mihomo binary does not match the maintainer-supplied SHA-256."
    }

    $versionText = (& $downloadedBinary.FullName -v | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0 -or
        [string]::IsNullOrWhiteSpace($versionText) -or
        -not $versionText.Contains($Version, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Verified Mihomo binary did not report the requested release version."
    }

    New-Item -ItemType Directory -Force -Path $binaryDirectory | Out-Null
    $binaryCandidate = Join-Path $binaryDirectory ".mihomo-$([Guid]::NewGuid().ToString('N')).exe"
    Copy-Item -LiteralPath $downloadedBinary.FullName -Destination $binaryCandidate
    $candidate = Get-OrdinaryFile -LiteralPath $binaryCandidate -Description "Staged Mihomo binary"
    $candidateHash = (Get-FileHash -LiteralPath $candidate.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($candidate.Length -ne $downloadedBinary.Length -or
        -not $candidateHash.Equals($ExpectedBinarySha256, [System.StringComparison]::Ordinal)) {
        throw "Staged Mihomo binary changed while being copied."
    }

    Move-Item -LiteralPath $candidate.FullName -Destination $binaryPath -Force
    $manifest = [ordered]@{
        schemaVersion = 1
        version = $Version
        sourceReleaseUrl = [string]$release.html_url
        length = $downloadedBinary.Length
        sha256 = $ExpectedBinarySha256
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

    $notice = @"
Bundled component: mihomo core
Bundled binary: Binaries/mihomo.exe
Bundled version: $versionText
Bundled binary SHA256: $ExpectedBinarySha256

Upstream project: MetaCubeX/mihomo
Upstream release: $($release.html_url)
Upstream asset: $AssetName
Upstream asset URL: $($asset.browser_download_url)
Upstream documentation: https://wiki.metacubex.one/

License: GPL-3.0. See mihomo-LICENSE.txt in this directory.

Source availability: the upstream release page publishes the corresponding release,
source archive links, and source-related assets. Clash# redistributes the unmodified
Windows amd64 mihomo core as a bundled runtime dependency.

Trademark/naming note: Clash# is not affiliated with MetaCubeX and does not use
"mihomo" in the application name.
"@
    Set-Content -LiteralPath $noticePath -Value $notice -Encoding utf8NoBOM
    $verified = Test-PinnedDistribution
    Write-Output "Pinned mihomo $($verified.version) prepared and verified."
} finally {
    if (Test-Path -LiteralPath $extractDirectory) {
        $resolvedExtract = Resolve-Path -LiteralPath $extractDirectory
        $resolvedWork = Resolve-Path -LiteralPath $workDirectory
        if (-not $resolvedExtract.Path.StartsWith(
                $resolvedWork.Path + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a Mihomo staging directory outside the fixed work root."
        }
        Remove-Item -LiteralPath $resolvedExtract.Path -Recurse -Force
    }
}
