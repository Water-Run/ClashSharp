#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]] $AssetPath,

    [ValidateNotNullOrEmpty()]
    [string] $Destination = (Join-Path $PSScriptRoot "..\ClashSharp\ClashSharp\Binaries\GeoData")
)

$ErrorActionPreference = "Stop"
$allowedNames = @("Country.mmdb", "GeoIP.dat", "GeoSite.dat", "ASN.mmdb")
$resolvedDestination = [System.IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path $resolvedDestination | Out-Null
$destinationItem = Get-Item -LiteralPath $resolvedDestination
if (-not $destinationItem.PSIsContainer -or ($destinationItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
    throw "GeoData destination must be an ordinary directory: $resolvedDestination"
}
$resolvedDestination = $destinationItem.FullName

$validatedSources = @()
$seenNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($candidate in $AssetPath) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Geodata asset is not a file: $candidate"
    }

    $source = Get-Item -LiteralPath $candidate
    if ($source.PSIsContainer -or
        ($source.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or
        $source.Length -lt 1 -or
        $source.Length -gt 268435456 -or
        $allowedNames -cnotcontains $source.Name) {
        throw "Unsupported geodata asset '$candidate'. Expected one of: $($allowedNames -join ', ')."
    }

    if (-not $seenNames.Add($source.Name)) {
        throw "Duplicate geodata asset '$($source.Name)'."
    }

    $validatedSources += $source
}

if ($validatedSources.Count -eq 0) {
    throw "At least one pinned local geodata asset is required."
}

$entries = @()
foreach ($source in $validatedSources) {
    $destinationPath = [System.IO.Path]::GetFullPath((Join-Path $resolvedDestination $source.Name))
    $relativeDestination = [System.IO.Path]::GetRelativePath($resolvedDestination, $destinationPath)
    if ([System.IO.Path]::IsPathFullyQualified($relativeDestination) -or
        $relativeDestination -eq ".." -or
        $relativeDestination.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::Ordinal)) {
        throw "Refusing to stage a geodata asset outside the destination."
    }

    if (-not $source.FullName.Equals($destinationPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $source.FullName -Destination $destinationPath -Force
    }

    $copied = Get-Item -LiteralPath $destinationPath
    if ($copied.PSIsContainer -or
        ($copied.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or
        $copied.Length -ne $source.Length) {
        throw "Staged geodata asset is not an ordinary exact copy: $($source.Name)"
    }

    $entries += [ordered]@{
        name = $copied.Name
        length = $copied.Length
        sha256 = (Get-FileHash -LiteralPath $copied.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

foreach ($allowedName in $allowedNames) {
    if ($seenNames.Contains($allowedName)) {
        continue
    }

    $stalePath = [System.IO.Path]::GetFullPath((Join-Path $resolvedDestination $allowedName))
    if (Test-Path -LiteralPath $stalePath) {
        Remove-Item -LiteralPath $stalePath -Force
    }
}

$manifest = [ordered]@{
    schemaVersion = 1
    files = $entries
}
$manifestPath = Join-Path $resolvedDestination "manifest.json"
$temporaryManifestPath = Join-Path $resolvedDestination ".manifest.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporaryManifestPath -Encoding utf8NoBOM
    Move-Item -LiteralPath $temporaryManifestPath -Destination $manifestPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryManifestPath) {
        Remove-Item -LiteralPath $temporaryManifestPath -Force
    }
}
Write-Host "Prepared installer-owned geodata manifest: $manifestPath"
