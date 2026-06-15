$ErrorActionPreference = "Stop"

$installerRoot = $PSScriptRoot
$repoRoot = Resolve-Path (Join-Path $installerRoot "..\..")
$appProject = Join-Path $repoRoot "ClashSharp\ClashSharp\ClashSharp.csproj"
$mihomoBinary = Join-Path $repoRoot "ClashSharp\ClashSharp\Binaries\mihomo.exe"
$mihomoUpdateScript = Join-Path $repoRoot "Tools\Update-Mihomo.ps1"
$payloadDir = Join-Path $installerRoot "payload"
$signingDir = Join-Path $installerRoot "signing"
$certificateSubject = "CN=linzh"
$certificatePfxPath = Join-Path $signingDir "ClashSharp_TemporaryKey.pfx"
$certificateCerPath = Join-Path $signingDir "ClashSharp_TemporaryKey.cer"
$certificatePasswordText = "ClashSharpTemporaryPassword!"

Set-Location $repoRoot

if (-not (Test-Path $mihomoBinary)) {
    & $mihomoUpdateScript
} else {
    & $mihomoBinary -v
}

New-Item -ItemType Directory -Force -Path $signingDir | Out-Null
if (-not (Test-Path $certificatePfxPath) -or -not (Test-Path $certificateCerPath)) {
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

$signingCertificate = Get-ChildItem -Path Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $certificateSubject -and ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq "1.3.6.1.5.5.7.3.3" }) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($null -eq $signingCertificate -and (Test-Path $certificatePfxPath)) {
    $certificatePassword = ConvertTo-SecureString $certificatePasswordText -AsPlainText -Force
    Import-PfxCertificate -FilePath $certificatePfxPath -CertStoreLocation Cert:\CurrentUser\My -Password $certificatePassword | Out-Null
    $signingCertificate = Get-ChildItem -Path Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $certificateSubject -and ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq "1.3.6.1.5.5.7.3.3" }) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

if ($null -eq $signingCertificate) {
    throw "No code-signing certificate was available for $certificateSubject."
}

dotnet publish $appProject `
    -c Release `
    -p:Platform=x64 `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxBundle=Never `
    -p:AppxPackageSigningEnabled=true `
    -p:PackageCertificateThumbprint=$($signingCertificate.Thumbprint)

New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
Get-ChildItem -Path $payloadDir -File -Recurse |
    Where-Object { $_.Name -ne ".gitkeep" } |
    Remove-Item -Force

$packageRoot = Join-Path (Split-Path $appProject) "AppPackages"
$latestPackageDirectory = Get-ChildItem -Path $packageRoot -Directory |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $latestPackageDirectory) {
    throw "No AppPackages output directory was produced."
}

$appPackage = Get-ChildItem -Path $latestPackageDirectory.FullName -File |
    Where-Object { $_.Extension -in ".msix", ".msixbundle" -and $_.Name -like "ClashSharp_*" } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $appPackage) {
    throw "No Clash# MSIX package was produced."
}

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
cargo build --release

$releaseDir = Join-Path $installerRoot "target\release"
$releasePayloadDir = Join-Path $releaseDir "payload"
$resolvedReleaseDir = Resolve-Path $releaseDir
if (Test-Path $releasePayloadDir) {
    $resolvedReleasePayloadDir = Resolve-Path $releasePayloadDir
    if (-not $resolvedReleasePayloadDir.Path.StartsWith($resolvedReleaseDir.Path, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean payload outside release directory."
    }
    Remove-Item -LiteralPath $releasePayloadDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $releasePayloadDir | Out-Null
Copy-Item -Path (Join-Path $payloadDir "*") -Destination $releasePayloadDir -Recurse -Force
