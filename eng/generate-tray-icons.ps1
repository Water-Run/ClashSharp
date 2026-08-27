<#
.SYNOPSIS
Regenerates the checked-in Windows tray ICO derivatives from their SVG sources.

.DESCRIPTION
Uses ImageMagick to create one ICO with 256, 128, 64, 48, 32, 24, 20, and
16 pixel frames for each tray visual state. Run this script after editing a
source under ClashSharp/ClashSharp/Assets/Tray, then review the binary diff.

.PARAMETER ImageMagickCommand
ImageMagick executable name or absolute path. Defaults to "magick".
#>
[CmdletBinding()]
param(
    [string]$ImageMagickCommand = "magick"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$trayAssetRoot = Join-Path $repositoryRoot "ClashSharp/ClashSharp/Assets/Tray"
$sourceNames = @("Logo.Inactive", "Logo.SystemProxy", "Logo.Tun")

foreach ($sourceName in $sourceNames) {
    $sourcePath = Join-Path $trayAssetRoot "$sourceName.svg"
    $targetPath = Join-Path $trayAssetRoot "$sourceName.ico"
    & $ImageMagickCommand `
        -background none `
        -density 384 `
        $sourcePath `
        -define "icon:auto-resize=256,128,64,48,32,24,20,16" `
        $targetPath
    if ($LASTEXITCODE -ne 0) {
        throw "ImageMagick failed to generate $targetPath."
    }
}
