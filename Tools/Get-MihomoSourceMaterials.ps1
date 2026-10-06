#Requires -Version 7.0
<#
.SYNOPSIS
    Acquires the exact source, vendor, toolchain and public CA inputs for the bundled mihomo.
.DESCRIPTION
    Reuses only matching cache files, bounds every transfer, verifies immutable input hashes
    before promotion, and derives the public CA bytes from the verified bundled executable.
    Does not execute downloads, install software, sign, or alter certificate stores.
.PARAMETER Destination
    Ordinary cache directory; defaults to ignored release inputs for version 1.0.0.
#>
[CmdletBinding()]
param([ValidateNotNullOrEmpty()][string] $Destination = (Join-Path $PSScriptRoot '../artifacts/release-inputs/1.0.0/source-materials'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module -Name (Join-Path $repositoryRoot 'ClashSharp/Installer/PackagingContract.psm1') -Force
Import-Module -Name (Join-Path $repositoryRoot 'ClashSharp/Installer/SourceMaterials.psm1') -Force
$pins = Get-ClashSharpMihomoSourceManifest -RepositoryRoot $repositoryRoot
$cache = Assert-ClashSharpOrdinaryPath -LiteralPath $Destination -AllowMissing
$null = New-Item -ItemType Directory -Path $cache -Force
$null = Assert-ClashSharpOrdinaryPath -LiteralPath $cache -RequireDirectory
$binaryPath = Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $repositoryRoot 'ClashSharp/ClashSharp/Binaries/mihomo.exe') -RequireFile
$binary = [IO.File]::Open($binaryPath, 'Open', 'Read', 'Read')
$client = $null
try {
    if ($binary.Length -ne $pins.Manifest.bundledBinary.length -or
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($binary)).ToLowerInvariant() -cne $pins.Manifest.bundledBinary.sha256) {
        throw 'The bundled executable does not match the source-materials binary pin.'
    }
    foreach ($file in $pins.Manifest.files) {
        $path = Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $cache $file.file) -AllowMissing
        if (-not (Test-Path -LiteralPath $path)) {
            $temporary = Join-Path $cache ('.source-input-' + [Guid]::NewGuid().ToString('N'))
            try {
                if ($file.role -ceq 'public-ca') {
                    $binary.Position = [long]$file.binaryOffset
                    $bytes = [byte[]]::new([int]$file.length)
                    $binary.ReadExactly($bytes)
                    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $file.sha256) {
                        throw 'The observed public CA block does not match its fixed digest.'
                    }
                    $output = [IO.File]::Open($temporary, 'CreateNew', 'Write', 'None')
                    try { $output.Write($bytes, 0, $bytes.Length) } finally { $output.Dispose() }
                } else {
                    if ($null -eq $client) { $client = [Net.Http.HttpClient]::new() }
                    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(3))
                    $response = $null
                    $source = $null
                    $output = $null
                    try {
                        $response = $client.GetAsync($file.sourceUrl, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $timeout.Token).GetAwaiter().GetResult()
                        $null = $response.EnsureSuccessStatusCode()
                        if ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -ne $file.length) {
                            throw 'Source input transfer length does not match its pin.'
                        }
                        $source = $response.Content.ReadAsStreamAsync($timeout.Token).GetAwaiter().GetResult()
                        $output = [IO.File]::Open($temporary, 'CreateNew', 'Write', 'None')
                        $buffer = [byte[]]::new(65536)
                        $received = 0L
                        while (($count = $source.ReadAsync($buffer, 0, $buffer.Length, $timeout.Token).GetAwaiter().GetResult()) -gt 0) {
                            $received += $count
                            if ($received -gt $file.length) { throw 'Source input transfer exceeded its byte budget.' }
                            $output.Write($buffer, 0, $count)
                        }
                        if ($received -ne $file.length) { throw 'Source input transfer ended before its pinned length.' }
                    } finally {
                        if ($null -ne $output) { $output.Dispose() }
                        if ($null -ne $source) { $source.Dispose() }
                        if ($null -ne $response) { $response.Dispose() }
                        $timeout.Dispose()
                    }
                }
                if ((Get-FileHash -LiteralPath $temporary).Hash.ToLowerInvariant() -cne $file.sha256) { throw 'Downloaded source input digest mismatch.' }
                $null = Assert-ClashSharpOrdinaryPath -LiteralPath $path -AllowMissing
                [IO.File]::Move($temporary, $path)
            } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
        }
        if ((Get-Item -LiteralPath $path).Length -ne $file.length -or
            (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $file.sha256) {
            throw ('Cached source input does not match its pin: ' + $file.file)
        }
        Write-Output ('Verified source input: ' + $file.file)
    }
} finally {
    $binary.Dispose()
    if ($null -ne $client) { $client.Dispose() }
}
Write-Output ('All fixed mihomo source materials are ready in ' + $cache)
