param(
    [switch]$Force,
    [string]$Version = "latest",
    [string]$ExpectedSha256 = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$binaryDirectory = Join-Path $repoRoot "ClashSharp\ClashSharp\Binaries"
$binaryPath = Join-Path $binaryDirectory "mihomo.exe"
$workDirectory = Join-Path $repoRoot "ClashSharp\.download\mihomo"

if ((Test-Path $binaryPath) -and -not $Force) {
    & $binaryPath -v
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
& $binaryPath -v
