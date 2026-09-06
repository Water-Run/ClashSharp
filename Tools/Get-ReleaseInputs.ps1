#Requires -Version 7.0

<#
.SYNOPSIS
Acquires the exact offline GeoData inputs pinned for ClashSharp 1.0.0.
.DESCRIPTION
Downloads only from the checked-in upstream commit, bounds transfer size and duration, and
verifies every SHA-256 before promoting a file into the cache. Existing cache bytes must match.
Does not install anything or modify system networking. Run Prepare-GeoData.ps1 to stage the cache.
.PARAMETER Destination
Ordinary local cache directory. Defaults to the ignored release-inputs artifact directory.
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $Destination = (Join-Path $PSScriptRoot '..\artifacts\release-inputs\1.0.0')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$installerRoot = Join-Path $PSScriptRoot '..\ClashSharp\Installer'
Import-Module -Name (Join-Path $installerRoot 'PackagingContract.psm1') -Force
$pinPath = Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $installerRoot 'release-inputs.json') -RequireFile
if ((Get-Item -LiteralPath $pinPath).Length -gt 65536) {
    throw 'Release input manifest exceeds its byte budget.'
}
$pins = Get-Content -LiteralPath $pinPath -Raw | ConvertFrom-Json -AsHashtable -Depth 8
$expectedNames = @{
    'Country.mmdb' = 'country.mmdb'
    'GeoIP.dat' = 'geoip.dat'
    'GeoSite.dat' = 'geosite.dat'
    'ASN.mmdb' = 'GeoLite2-ASN.mmdb'
}
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
if ($pins.schemaVersion -ne 1 -or $pins.productVersion -cne '1.0.0' -or
    $pins.geoData.repository -cne 'MetaCubeX/meta-rules-dat' -or
    $pins.geoData.commit -cnotmatch '^[0-9a-f]{40}$' -or $pins.geoData.files.Count -ne 4) {
    throw 'Release input manifest is outside the 1.0.0 acquisition contract.'
}
foreach ($asset in $pins.geoData.files) {
    if (-not $expectedNames.ContainsKey($asset.name) -or -not $seen.Add($asset.name) -or
        $asset.sourceName -cne $expectedNames[$asset.name] -or
        $asset.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        ($asset.length -isnot [long] -and $asset.length -isnot [int]) -or
        $asset.length -lt 1 -or $asset.length -gt 268435456) {
        throw 'Release input manifest contains an invalid asset.'
    }
}
if (-not $seen.SetEquals([string[]]@($expectedNames.Keys))) {
    throw 'Release input manifest must contain all four canonical assets.'
}
$root = Assert-ClashSharpOrdinaryPath -LiteralPath $Destination -AllowMissing
$null = New-Item -ItemType Directory -Path $root -Force
$root = Assert-ClashSharpOrdinaryPath -LiteralPath $root -RequireDirectory
$client = [Net.Http.HttpClient]::new()
try {
    foreach ($asset in $pins.geoData.files) {
        $path = Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $root $asset.name) -AllowMissing
        if (-not (Test-Path -LiteralPath $path)) {
            $temporaryPath = Join-Path $root ('.download-' + [Guid]::NewGuid().ToString('N'))
            $sourceUri = "https://raw.githubusercontent.com/$($pins.geoData.repository)/$($pins.geoData.commit)/$($asset.sourceName)"
            $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(3))
            $response = $null
            $inputStream = $null
            $outputStream = $null
            try {
                $response = $client.GetAsync($sourceUri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $timeout.Token).GetAwaiter().GetResult()
                $null = $response.EnsureSuccessStatusCode()
                if ($null -ne $response.Content.Headers.ContentLength -and
                    $response.Content.Headers.ContentLength -ne [long]$asset.length) {
                    throw "Pinned input content length mismatch: $($asset.name)"
                }
                $inputStream = $response.Content.ReadAsStreamAsync($timeout.Token).GetAwaiter().GetResult()
                $outputStream = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                $buffer = [byte[]]::new(65536)
                $received = 0L
                while (($count = $inputStream.ReadAsync($buffer, 0, $buffer.Length, $timeout.Token).GetAwaiter().GetResult()) -gt 0) {
                    $received += $count
                    if ($received -gt [long]$asset.length) {
                        throw "Pinned input exceeded its byte budget: $($asset.name)"
                    }
                    $outputStream.Write($buffer, 0, $count)
                }
                $outputStream.Dispose()
                $outputStream = $null
                if ($received -ne [long]$asset.length -or
                    (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset.sha256) {
                    throw "Pinned input digest or length mismatch: $($asset.name)"
                }
                Move-Item -LiteralPath $temporaryPath -Destination $path
            } finally {
                if ($null -ne $outputStream) { $outputStream.Dispose() }
                if ($null -ne $inputStream) { $inputStream.Dispose() }
                if ($null -ne $response) { $response.Dispose() }
                $timeout.Dispose()
                if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
            }
        }
        $file = Get-Item -LiteralPath $path
        if ($file.Length -ne [long]$asset.length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset.sha256) {
            throw "Cached input does not match the release pin: $($asset.name)"
        }
        Write-Output "Verified $($asset.name): $($asset.length) bytes, SHA-256 $($asset.sha256)"
    }
} finally {
    $client.Dispose()
}
Write-Output "All four pinned GeoData inputs are ready in $root"
