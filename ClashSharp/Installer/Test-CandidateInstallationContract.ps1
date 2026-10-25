#Requires -Version 7.4
<#
.SYNOPSIS
    Exercises installed-content mismatch, private service ownership and observation boundaries.
.DESCRIPTION
    Uses inert ZIP entries and temporary directories only. No real user data, SCM, packages,
    certificates or network settings are read or changed. No product is built or executed.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
# Execute the real collector's import order, guarding nested Force reload visibility.
$tool = Join-Path $repository 'Tools/Invoke-CandidateInstallationAudit.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($tool,[ref]$null,[ref]$null)
$installerRoot = $PSScriptRoot
foreach ($command in $ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Import-Module'},$true)) {
    $null = & ([scriptblock]::Create($command.Extent.Text))
}
$script:checks=0
function Assert-Condition([bool]$Value,[string]$Message){$script:checks++;if(-not $Value){throw $Message}}
function Assert-Rejected([scriptblock]$Action,[string]$Code){
    $refused=$false
    try{$null=& $Action}catch{if($Code -and -not $_.Exception.Message.Contains($Code)){throw};$refused=$true}
    Assert-Condition $refused ('Expected refusal: '+$Code)
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('clashsharp-installation-contract-'+[Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
try {
    foreach ($sid in @('S-1-5-21-0-456-789-1001','S-1-12-1-123-456-789-1001')) {
        Assert-ClashSharpInstallationTargetSid -TargetUserSid $sid
        Assert-Condition $true 'Canonical cloud and local identities with zero subauthorities must remain representable.'
    }
    foreach ($sid in @('S-1-5-18','S-1-5-21-01-2-3-1001','S-1-5-21-4294967296-2-3-1001',('S-1-5-21-1-2-3-1001'+[char]10))) {
        Assert-Rejected { Assert-ClashSharpInstallationTargetSid -TargetUserSid $sid } 'installation.target-sid'
    }
    $msix = Join-Path $root 'inert.zip'
    $archive = [IO.Compression.ZipFile]::Open($msix,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($path in @('ClashSharp.dll','AppxManifest.xml','Assets/Logo.svg','Binaries/Mihomo.exe',
            'Binaries/Service/ClashSharp.MihomoService.exe','Binaries/GeoData/asn.mmdb','Binaries/GeoData/country.mmdb',
            'Binaries/GeoData/geoip.dat','Binaries/GeoData/geosite.dat','Binaries/GeoData/manifest.json',
            '[Content_Types].xml','AppxBlockMap.xml','AppxSignature.p7x','AppxMetadata/CodeIntegrity.cat')) {
            $entry = $archive.CreateEntry($path)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('SYNTHETIC-NOT-EXECUTABLE:'+ $path) } finally { $writer.Dispose() }
        }
    } finally { $archive.Dispose() }
    $files = @(Get-ClashSharpInstallationContentContract -LiteralPath $msix)
    Assert-Condition ($files.Count -eq 17 -and @($files|Where-Object scope -CEQ 'machine').Count -eq 7) 'Hash all ten deployed content entries and exactly seven machine entries.'
    Assert-Condition (@($files|Where-Object path -CEQ 'Host/clashsharp.mihomoservice.exe').Count -eq 1) 'Machine mapping must match the production Host layout without doubled separators.'
    Assert-Condition (@($files|Where-Object path -Match 'AppxSignature|AppxBlockMap|Content_Types|CodeIntegrity').Count -eq 0) 'Only known packaging metadata is outside deployed content scope.'
    $package = Join-Path $root 'package'; $machine = Join-Path $root 'machine'
    foreach ($file in $files) {
        $base = if ($file.scope -eq 'package') { $package } else { $machine }
        $target = Join-Path $base $file.path
        $null = [IO.Directory]::CreateDirectory((Split-Path -Parent $target))
        $origin = if ($file.scope -eq 'package') { $file.path } elseif ($file.path -ceq 'mihomo.exe') { 'Binaries/Mihomo.exe' }
            elseif ($file.path.StartsWith('Host/')) { 'Binaries/Service/ClashSharp.MihomoService.exe' } else { 'Binaries/'+$file.path }
        [IO.File]::WriteAllText($target,'SYNTHETIC-NOT-EXECUTABLE:'+ $origin,[Text.UTF8Encoding]::new($false))
    }
    $snapshot = Invoke-ClashSharpInstalledContentSnapshot -PackageRoot $package -MachineRoot $machine -ExpectedFiles $files -Consume { 'matched' }
    Assert-Condition ($snapshot -ceq 'matched') 'Real temporary installed files must match the inert ZIP contract.'
    $dll = Join-Path $package 'ClashSharp.dll'; $original = [IO.File]::ReadAllBytes($dll)
    $changed = [byte[]]$original.Clone(); $changed[0] = $changed[0] -bxor 1; [IO.File]::WriteAllBytes($dll,$changed)
    Assert-Rejected { Invoke-ClashSharpInstalledContentSnapshot -PackageRoot $package -MachineRoot $machine -ExpectedFiles $files -Consume { throw 'callback-must-not-run' } } 'installation.content-mismatch:package/ClashSharp.dll'
    [IO.File]::WriteAllBytes($dll,$original)
    $extra = Join-Path $machine 'extra.bin'; [IO.File]::WriteAllText($extra,'extra')
    Assert-Rejected { Invoke-ClashSharpInstalledContentSnapshot -PackageRoot $package -MachineRoot $machine -ExpectedFiles $files -Consume { throw 'callback-must-not-run' } } ''
    [IO.File]::Delete($extra)
    foreach ($unsafePath in @('Assets/bad:stream','Assets/bad?.png','Assets/CON.png','/outside.dll')) {
        $unsafeZip = Join-Path $root ([Guid]::NewGuid().ToString('N')+'.zip')
        [IO.File]::Copy($msix,$unsafeZip)
        $unsafeArchive = [IO.Compression.ZipFile]::Open($unsafeZip,[IO.Compression.ZipArchiveMode]::Update)
        try { $writer = [IO.StreamWriter]::new($unsafeArchive.CreateEntry($unsafePath).Open()); try { $writer.Write('inert') } finally { $writer.Dispose() } }
        finally { $unsafeArchive.Dispose() }
        Assert-Rejected { Get-ClashSharpInstallationContentContract -LiteralPath $unsafeZip } ''
    }
    $null = Invoke-ClashSharpInstalledContentSnapshot -PackageRoot $package -MachineRoot $machine -ExpectedFiles $files -Consume {
        Assert-Rejected { [IO.File]::WriteAllText($dll,'altered') } ''
        Assert-Rejected { [IO.File]::Delete((Join-Path $machine 'mihomo.exe')) } ''
    }
    Assert-Rejected { Invoke-ClashSharpInstalledContentSnapshot -PackageRoot $package -MachineRoot $machine -ExpectedFiles $files -Consume { [IO.File]::WriteAllText($extra,'extra'); 'must-not-escape' } } ''
    [IO.File]::Delete($extra)
    $null = [IO.File]::WriteAllText((Join-Path $package 'Windows-generated.txt'),'outside-content-scope')
    Assert-Condition ((Invoke-ClashSharpInstalledContentSnapshot -PackageRoot $package -MachineRoot $machine -ExpectedFiles $files -Consume { 'matched' }) -ceq 'matched') 'Windows generated package additions do not make this an exact package inventory claim.'
    $expected = [pscustomobject]@{Name='Synthetic';Publisher='CN=Synthetic';PublisherId='synthetic1234';Version='1.0.0.0';Architecture='x64';ResourceId='';PackageFullName='Synthetic_1.0.0.0_x64__synthetic1234';PackageFamilyName='Synthetic_synthetic1234'}
    $actual = $expected | ConvertTo-Json | ConvertFrom-Json
    foreach ($name in @('IsBundle','IsDevelopmentMode','IsFramework','IsResourcePackage','IsPartiallyStaged')) { $actual | Add-Member NoteProperty $name $false }
    $actual | Add-Member NoteProperty Status 0
    Assert-ClashSharpInstallationRegistration -Expected $expected -Actual $actual
    Assert-Condition $true 'Matching complete registration must pass before content comparison.'
    foreach ($name in @('Name','Publisher','PublisherId','Version','Architecture','ResourceId','PackageFullName','PackageFamilyName')) {
        $saved = $actual.$name; $actual.$name = 'other'
        Assert-Rejected { Assert-ClashSharpInstallationRegistration -Expected $expected -Actual $actual } 'installation.package-identity'
        $actual.$name = $saved
    }
    foreach ($name in @('IsBundle','IsDevelopmentMode','IsFramework','IsResourcePackage','IsPartiallyStaged')) {
        $actual.$name = $true; Assert-Rejected { Assert-ClashSharpInstallationRegistration -Expected $expected -Actual $actual } 'installation.package-kind'; $actual.$name = $false
    }
    $actual.Status=1; Assert-Rejected { Assert-ClashSharpInstallationRegistration -Expected $expected -Actual $actual } 'installation.package-health'
    $owner = 'S-1-5-21-123-456-789-1001'; $token = 'a'*64
    $association = [ordered]@{schemaVersion=1;ownerSid=$owner;authenticationToken=$token} | ConvertTo-Json -Compress
    $pipe = 'ClashSharp.Mihomo.'+[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('ClashSharp.Mihomo.IPC'+[char]0+$owner+[char]0+$token))).Substring(0,32).ToLowerInvariant()
    $profile = Join-Path $root 'profile'; $family = $expected.PackageFamilyName
    $commandLine = '"'+(Join-Path $machine 'Host/ClashSharp.MihomoService.exe')+'" --mihomo "'+(Join-Path $machine 'mihomo.exe')+'" --config "'+(Join-Path $profile ('AppData/Local/Packages/'+$family+'/LocalState/mihomo/config.yaml'))+'" --pipe-name "'+$pipe+'" --ipc-token "'+$token+'" --allowed-sid "'+$owner+'"'
    $parameters = @{CommandLine=$commandLine;AssociationJson=$association;TargetUserSid=$owner;ProfileRoot=$profile;MachineRoot=$machine;PackageFamilyName=$family}
    $result = @(Assert-ClashSharpInstallationServiceCommand @parameters)
    Assert-Condition ($result.Count -eq 0) 'Private service verification must not return commands, tokens or association content.'
    $parameters.TargetUserSid='S-1-5-21-123-456-789-1002'; Assert-Rejected { Assert-ClashSharpInstallationServiceCommand @parameters } 'installation.association-owner'; $parameters.TargetUserSid=$owner
    foreach ($bad in @(($commandLine+' --extra'), $commandLine.Replace($token,('b'*64)), $commandLine.Replace($pipe,('ClashSharp.Mihomo.'+('b'*32))), $commandLine.Replace('mihomo.exe','other.exe'), $commandLine.Replace($family,'Other_family'))) {
        $parameters.CommandLine=$bad; Assert-Rejected { Assert-ClashSharpInstallationServiceCommand @parameters } 'installation.service-'
    }
    $parameters.CommandLine=$commandLine
    foreach ($bad in @(($association+[char]10), $association.Replace('"schemaVersion":1','"schemaVersion":1,"schemaVersion":1'), $association.Replace('"schemaVersion":1','"schemaVersion":"1"'))) {
        $parameters.AssociationJson=$bad; Assert-Rejected { Assert-ClashSharpInstallationServiceCommand @parameters } 'installation.association-'
    }
    $parameters.AssociationJson=$association
    $parameters.CommandLine=$commandLine.Replace($token,('b'*64))
    try { Assert-ClashSharpInstallationServiceCommand @parameters; throw 'expected-refusal' }
    catch { Assert-Condition (-not $_.Exception.Message.Contains($token) -and -not $_.Exception.Message.Contains($commandLine)) 'Private values must not appear in rejection messages.' }
    $source = Get-Content -LiteralPath $tool -Raw
    Assert-Condition ($source.Contains('Get-AppxPackage -User $TargetUserSid') -and $source.Contains('GetOwnerSid') -and $source.Contains('CreationDate -ne')) 'Actual collector must query the explicit user and check service process lifetime and identity.'
    Assert-Condition ($source -notmatch 'Start-Process|Add-AppxPackage|Remove-AppxPackage|Start-Service|Stop-Service|Import-Certificate|Restart-Computer') 'Collector must have no install, product execution, trust or network mutation path.'
    Assert-Condition ($source.Contains('installationActionVerified=$false') -and $source.Contains('win11AcceptanceSatisfied=$false') -and $source.Contains('promotionAuthorized=$false')) 'Content observation must preserve unproved gates.'
    $existing = Join-Path $root 'preserved-output'; $null = [IO.Directory]::CreateDirectory($existing)
    $sentinel = Join-Path $existing 'previous.json'; [IO.File]::WriteAllText($sentinel,'retained')
    Assert-Rejected { & $tool -IdentityPath (Join-Path $root 'absent.json') -ExpectedIdentitySha256 ('a'*64) -ReleaseDirectory $package -TargetUserSid $owner -OutputDirectory $existing } 'installation.new-external-output-required'
    Assert-Condition ([IO.File]::ReadAllText($sentinel) -ceq 'retained') 'The actual collector must refuse existing output before probing a host installation.'
    [pscustomobject]@{success=$true;checks=$script:checks;inertFileIo=$true;hostInstallationProbed=$false;productExecuted=$false;promotionAuthorized=$false}
} finally {
    $resolved = (Get-Item -LiteralPath $root -Force).FullName
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@('\','/'))
    $items = @((Get-Item -LiteralPath $root -Force))+@(Get-ChildItem -LiteralPath $root -Force -Recurse)
    if (-not $resolved.StartsWith($temp+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -cnotmatch '^clashsharp-installation-contract-[0-9a-f]{32}\z' -or
        @($items|Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'installation.fixture-cleanup-refused' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
