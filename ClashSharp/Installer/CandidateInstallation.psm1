#Requires -Version 7.4
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'PackagingContract.psm1') -Force

function Assert-ClashSharpInstallationTargetSid {
    <#
    .SYNOPSIS
        Validates a canonical target SID without limiting users to local SAM accounts.
    .DESCRIPTION
        Uses Windows SID parsing and canonical round-trip identity, with the same explicit
        privileged or noninteractive exclusions as the installer protocol. Account existence
        and package ownership still require independent local observations.
    .PARAMETER TargetUserSid
        Independent target user SID expected by the operator.
    #>
    [CmdletBinding()]
    param([string] $TargetUserSid)
    if ($TargetUserSid.Length -gt 184 -or $TargetUserSid -cnotmatch '^S-1-[0-9-]{3,178}\z' -or
        $TargetUserSid -cin @('S-1-1-0','S-1-5-2','S-1-5-7','S-1-5-18','S-1-5-32-544')) {
        throw 'installation.target-sid'
    }
    try { $sid = [Security.Principal.SecurityIdentifier]::new($TargetUserSid) }
    catch { throw 'installation.target-sid' }
    if ($sid.Value -cne $TargetUserSid) { throw 'installation.target-sid' }
}

function Get-ClashSharpInstallationContentContract {
    <#
    .SYNOPSIS
        Hashes candidate MSIX content and derives its fixed machine deployment paths.
    .DESCRIPTION
        Reads bounded ZIP entries without extraction. Excludes only four packaging metadata
        files from the package content comparison; machine files use the production allowlist.
        These observations do not authenticate a self-selected MSIX or certify its signature.
    .PARAMETER LiteralPath
        Primary MSIX already held by the independently pinned candidate lease.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $LiteralPath)
    $machine = @(Get-ClashSharpMsixMachineFileContract -LiteralPath $LiteralPath)
    $archive = [IO.Compression.ZipFile]::OpenRead($LiteralPath)
    try {
        $metadata = @('[Content_Types].xml','AppxBlockMap.xml','AppxSignature.p7x','AppxMetadata/CodeIntegrity.cat')
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $files = [Collections.Generic.List[object]]::new()
        $total = 0L
        foreach ($entry in $archive.Entries) {
            $path = [string]$entry.FullName
            $segments = @($path.Split('/'))
            if ($path.Length -gt 512 -or $path -cmatch '[\x00-\x1f\x7f\\:*?"<>|]' -or
                @($segments | Where-Object { $_ -in @('','.','..') -or $_ -match '[. ]\z' -or
                    $_ -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|\z)' }).Count -or
                -not $seen.Add($path) -or $entry.Length -le 0 -or $entry.Length -gt 536870912 -or
                $entry.Length -gt (2147483648L - $total)) { throw 'installation.content-path-or-budget' }
            $total += $entry.Length
            if ($path -cin $metadata) { continue }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            $files.Add([pscustomobject]@{scope='package';path=$path;length=[long]$entry.Length;sha256=$hash})
        }
        if ($files.Count -lt 1 -or $files.Count -gt 4096) { throw 'installation.content-inventory' }
        foreach ($file in $machine) {
            $target = if ($file.Path -ceq 'binaries/mihomo.exe') { 'mihomo.exe' }
                elseif ($file.Path.StartsWith('binaries/service/',[StringComparison]::Ordinal)) { 'Host/'+$file.Path.Substring(17) }
                else { 'GeoData/'+$file.Path.Substring(17) }
            $files.Add([pscustomobject]@{scope='machine';path=$target;length=[long]$file.Length;sha256=$file.Sha256})
        }
        return @($files)
    } finally { $archive.Dispose() }
}

function Assert-ClashSharpInstallationRegistration {
    <#
    .SYNOPSIS
        Requires a healthy ordinary package registration matching the candidate identity.
    .DESCRIPTION
        Compares every package identity field and rejects development, bundle, framework,
        resource or partially staged registrations, including another build of the same version.
        File bytes are checked separately; identity alone never proves the installed build.
    .PARAMETER Expected
        Identity parsed from the pinned candidate primary MSIX.
    .PARAMETER Actual
        Fresh target-user registration projected by the trusted local collector.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Expected,[Parameter(Mandatory)] $Actual)
    foreach ($name in @('Name','Publisher','PublisherId','Version','Architecture','ResourceId','PackageFullName','PackageFamilyName')) {
        if ($Actual.$name -isnot [string] -or $Actual.$name -cne $Expected.$name) { throw 'installation.package-identity' }
    }
    foreach ($name in @('IsBundle','IsDevelopmentMode','IsFramework','IsResourcePackage','IsPartiallyStaged')) {
        if ($Actual.$name -isnot [bool] -or $Actual.$name) { throw 'installation.package-kind' }
    }
    if (($Actual.Status -isnot [int] -and $Actual.Status -isnot [long]) -or $Actual.Status -ne 0) {
        throw 'installation.package-health'
    }
}

function Assert-ClashSharpInstallationServiceCommand {
    <#
    .SYNOPSIS
        Checks the complete private SCM command against the exact association and owner paths.
    .DESCRIPTION
        Requires the fixed argument order, token, derived pipe, owner and candidate family.
        Keeps credentials in memory only; returns no command, token, association or token digest.
        Diagnostics use fixed codes so rejected private input cannot escape through an error.
    .PARAMETER CommandLine
        Fresh local SCM PathName, never written to public evidence.
    .PARAMETER AssociationJson
        Bounded machine association text read under a lease by the local collector.
    .PARAMETER TargetUserSid
        Independent target SID supplied by the operator, not selected by the association.
    .PARAMETER ProfileRoot
        Target profile root observed from Win32_UserProfile for that exact SID.
    .PARAMETER MachineRoot
        Fixed service current directory derived from the Windows ProgramFiles known folder.
    .PARAMETER PackageFamilyName
        Exact family derived from the pinned primary MSIX.
    #>
    [CmdletBinding()]
    param([string] $CommandLine,[string] $AssociationJson,[string] $TargetUserSid,
        [string] $ProfileRoot,[string] $MachineRoot,[string] $PackageFamilyName)
    Assert-ClashSharpInstallationTargetSid -TargetUserSid $TargetUserSid
    if ([string]::IsNullOrWhiteSpace($AssociationJson) -or [Text.Encoding]::UTF8.GetByteCount($AssociationJson) -gt 4096 -or
        $CommandLine.Length -gt 32767) { throw 'installation.service-input' }
    try {
        $json = [Text.Json.JsonDocument]::Parse($AssociationJson)
        try {
            $names = @($json.RootElement.EnumerateObject() | ForEach-Object Name)
            if (($names -join ',') -cne 'schemaVersion,ownerSid,authenticationToken') { throw 'shape' }
            $schema = $json.RootElement.GetProperty('schemaVersion').GetInt32()
            $owner = $json.RootElement.GetProperty('ownerSid').GetString()
            $token = $json.RootElement.GetProperty('authenticationToken').GetString()
        } finally { $json.Dispose() }
    } catch { throw 'installation.association-json' }
    if ($schema -ne 1 -or $owner -cne $TargetUserSid -or $token -cnotmatch '^[0-9a-f]{64}\z') {
        throw 'installation.association-owner'
    }
    $canonical = [ordered]@{schemaVersion=1;ownerSid=$owner;authenticationToken=$token} | ConvertTo-Json -Compress
    if ($canonical -cne $AssociationJson) { throw 'installation.association-canonical' }
    $pattern = '^"(?<host>[^"\r\n]+)" --mihomo "(?<mihomo>[^"\r\n]+)" --config "(?<config>[^"\r\n]+)" --pipe-name "(?<pipe>ClashSharp\.Mihomo\.[0-9a-f]{32})" --ipc-token "(?<token>[0-9a-f]{64})" --allowed-sid "(?<sid>S-[0-9-]+)"\z'
    $match = [regex]::Match($CommandLine,$pattern)
    if (-not $match.Success -or $match.Groups['sid'].Value -cne $owner -or $match.Groups['token'].Value -cne $token) {
        throw 'installation.service-command'
    }
    $pipeInput = [Text.Encoding]::UTF8.GetBytes('ClashSharp.Mihomo.IPC'+[char]0+$owner+[char]0+$token)
    try { $pipe = 'ClashSharp.Mihomo.'+[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pipeInput)).Substring(0,32).ToLowerInvariant() }
    finally { [Array]::Clear($pipeInput) }
    if ($match.Groups['pipe'].Value -cne $pipe) { throw 'installation.service-pipe' }
    $paths = [ordered]@{
        host=(Join-Path $MachineRoot 'Host/ClashSharp.MihomoService.exe')
        mihomo=(Join-Path $MachineRoot 'mihomo.exe')
        config=(Join-Path $ProfileRoot ('AppData/Local/Packages/'+$PackageFamilyName+'/LocalState/mihomo/config.yaml'))
    }
    foreach ($name in $paths.Keys) {
        $path = $match.Groups[$name].Value
        if (-not [IO.Path]::IsPathFullyQualified($path) -or $path.StartsWith('\\',[StringComparison]::Ordinal) -or
            -not [IO.Path]::GetFullPath($path).Equals([IO.Path]::GetFullPath($paths[$name]),[StringComparison]::OrdinalIgnoreCase)) {
            throw 'installation.service-path'
        }
    }
}

function Invoke-ClashSharpInstalledContentSnapshot {
    <#
    .SYNOPSIS
        Matches installed content while all expected files remain leased against changes.
    .DESCRIPTION
        Rejects missing, changed or redirected files and extra machine files before and after
        the trusted callback. Package metadata or Windows generated additions are outside this
        content scope. This is an observation, not an install operation or a durable host lock.
    .PARAMETER PackageRoot
        Ordinary installed package location obtained from the exact target registration.
    .PARAMETER MachineRoot
        Ordinary fixed machine current directory derived from the Windows known folder.
    .PARAMETER ExpectedFiles
        Content entries derived from the pinned candidate, never supplied by the installation.
    .PARAMETER Consume
        Trusted local observation callback executed while installed content leases remain held.
    #>
    [CmdletBinding()]
    param([string] $PackageRoot,[string] $MachineRoot,[object[]] $ExpectedFiles,
        [Parameter(Mandatory)][scriptblock] $Consume)
    $roots = @{package=(Assert-ClashSharpOrdinaryPath -LiteralPath $PackageRoot -RequireDirectory);
        machine=(Assert-ClashSharpOrdinaryPath -LiteralPath $MachineRoot -RequireDirectory)}
    $leases = [Collections.Generic.List[IO.FileStream]]::new()
    $expectedMachine = @($ExpectedFiles | Where-Object scope -CEQ 'machine' | ForEach-Object {
        [pscustomobject]@{RelativePath=$_.path;Length=$_.length;Sha256=$_.sha256}
    } | Sort-Object RelativePath)
    try {
        if ($ExpectedFiles.Count -lt 8 -or $ExpectedFiles.Count -gt 4103 -or $expectedMachine.Count -ne 7) {
            throw 'installation.expected-inventory'
        }
        foreach ($file in $ExpectedFiles) {
            if ($file.scope -cnotin @('package','machine')) { throw 'installation.expected-scope' }
            $root = $roots[$file.scope]
            $path = [IO.Path]::GetFullPath((Join-Path $root $file.path))
            if (-not $path.StartsWith($root.TrimEnd([char[]]@('\','/'))+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
                throw 'installation.content-containment'
            }
            $null = Assert-ClashSharpOrdinaryPath -LiteralPath $path -RequireFile
            $stream = [IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            $leases.Add($stream)
            if ($stream.Length -ne $file.length -or
                [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() -cne $file.sha256) {
                throw ('installation.content-mismatch:'+ $file.scope + '/' + $file.path)
            }
        }
        Compare-ClashSharpDirectoryContract -Expected $expectedMachine -Actual @(Get-ClashSharpDirectoryContract -LiteralPath $roots.machine)
        $result = & $Consume
        # Recheck path identities/inventories before allowing any successful callback result out.
        foreach ($file in $ExpectedFiles) {
            $null = Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $roots[$file.scope] $file.path) -RequireFile
        }
        Compare-ClashSharpDirectoryContract -Expected $expectedMachine -Actual @(Get-ClashSharpDirectoryContract -LiteralPath $roots.machine)
        return $result
    } finally { foreach ($lease in $leases) { $lease.Dispose() } }
}

Export-ModuleMember -Function Assert-ClashSharpInstallationTargetSid, Get-ClashSharpInstallationContentContract, Assert-ClashSharpInstallationRegistration, Assert-ClashSharpInstallationServiceCommand, Invoke-ClashSharpInstalledContentSnapshot
