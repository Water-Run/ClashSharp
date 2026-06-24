param(
    [switch]$Force,
    [string]$Version = "latest",
    [string]$ExpectedSha256 = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$binaryDirectory = Join-Path $repoRoot "ClashSharp\ClashSharp\Binaries"
$binaryPath = Join-Path $binaryDirectory "mihomo.exe"
$licensePath = Join-Path $binaryDirectory "mihomo-LICENSE.txt"
$noticePath = Join-Path $binaryDirectory "mihomo-NOTICE.txt"
$workDirectory = Join-Path $repoRoot "ClashSharp\.download\mihomo"

function Ensure-MihomoDistributionFiles {
    param(
        [string]$VersionText,
        [string]$AssetName = "existing bundled binary",
        [string]$AssetUrl = "",
        [string]$ReleaseUrl = "https://github.com/MetaCubeX/mihomo/releases"
    )

    if (-not (Test-Path $licensePath)) {
        Invoke-WebRequest -Uri "https://www.gnu.org/licenses/gpl-3.0.txt" -OutFile $licensePath -UseBasicParsing
    }

    $binarySha256 = if (Test-Path $binaryPath) {
        (Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash
    } else {
        "unknown"
    }

    $notice = @"
Bundled component: mihomo core
Bundled binary: Binaries/mihomo.exe
Bundled version: $VersionText
Bundled binary SHA256: $binarySha256

Upstream project: MetaCubeX/mihomo
Upstream release: $ReleaseUrl
Upstream asset: $AssetName
Upstream asset URL: $AssetUrl
Upstream documentation: https://wiki.metacubex.one/

License: GPL-3.0. See mihomo-LICENSE.txt in this directory.

Source availability: the upstream release page publishes the corresponding release,
source archive links, and source-related assets such as vendor.tar.gz and
toolchain.tar.gz. Clash# redistributes the unmodified Windows amd64 mihomo core
as a bundled runtime dependency.

Trademark/naming note: Clash# is not affiliated with MetaCubeX and does not use
"mihomo" in the application name.
"@

    Set-Content -LiteralPath $noticePath -Value $notice -Encoding UTF8
}

if ((Test-Path $binaryPath) -and -not $Force) {
    $versionText = (& $binaryPath -v | Select-Object -First 1)
    Ensure-MihomoDistributionFiles -VersionText $versionText
    Write-Output $versionText
    exit 0
}

New-Item -ItemType Directory -Force -Path $binaryDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $workDirectory | Out-Null

$releaseUri = if ($Version -eq "latest") {
    "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest"
} else {
    "https://api.github.com/repos/MetaCubeX/mihomo/releases/tags/$Version"
}

$release = Invoke-RestMethod -Uri $releaseUri -Headers @{ "User-Agent" = "ClashSharp-Build" }
$asset = $release.assets |
    Where-Object { $_.name -match "^mihomo-windows-amd64-compatible-.*\.zip$" } |
    Select-Object -First 1

if ($null -eq $asset) {
    $asset = $release.assets |
        Where-Object { $_.name -match "^mihomo-windows-amd64-v1-v.*\.zip$" } |
        Select-Object -First 1
}

if ($null -eq $asset) {
    $asset = $release.assets |
        Where-Object { $_.name -match "^mihomo-windows-amd64-v[0-9.]+\.zip$" } |
        Select-Object -First 1
}

if ($null -eq $asset) {
    throw "No Windows amd64 mihomo ZIP asset was found for release $($release.tag_name)."
}

$zipPath = Join-Path $workDirectory $asset.name
$extractDirectory = Join-Path $workDirectory "extract"

Remove-Item -LiteralPath $extractDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $extractDirectory | Out-Null

Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -UseBasicParsing
if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
    $actualSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    if (-not $actualSha256.Equals($ExpectedSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Downloaded mihomo archive hash mismatch. Expected $ExpectedSha256 but got $actualSha256."
    }
}

Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDirectory -Force

$downloadedBinary = Get-ChildItem -Path $extractDirectory -Recurse -File |
    Where-Object { $_.Extension -eq ".exe" } |
    Select-Object -First 1

if ($null -eq $downloadedBinary) {
    throw "Downloaded mihomo archive did not contain an executable."
}

Copy-Item -LiteralPath $downloadedBinary.FullName -Destination $binaryPath -Force
$versionText = (& $binaryPath -v | Select-Object -First 1)
Ensure-MihomoDistributionFiles `
    -VersionText $versionText `
    -AssetName $asset.name `
    -AssetUrl $asset.browser_download_url `
    -ReleaseUrl $release.html_url
Write-Output $versionText
