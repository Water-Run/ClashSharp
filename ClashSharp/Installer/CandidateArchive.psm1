#Requires -Version 7.4
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'CandidateIdentity.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PackagingContract.psm1') -Force

function Get-ClashSharpCandidateArchiveEntries {
    <#
    .SYNOPSIS
        Returns the fixed transport inventory in its canonical order.
    .DESCRIPTION
        Keeps Setup.exe and its sibling payload directly at the ZIP root. Only the identity
        and eight formal candidate files can cross this transport boundary.
    #>
    [CmdletBinding()]
    param()
    'candidate-identity.json'
    Get-ClashSharpCandidatePaths
}

function Get-ClashSharpNewTransportPath {
    <#
    .SYNOPSIS
        Validates a new path whose existing parent is an ordinary directory.
    .DESCRIPTION
        Does not create, overwrite or remove an existing output, including failed prior attempts.
    .PARAMETER LiteralPath
        New file or directory requested by the caller.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $LiteralPath)
    if (-not [IO.Path]::IsPathFullyQualified($LiteralPath)) { throw 'transport.absolute-output-required' }
    $leaf = [IO.Path]::GetFileName($LiteralPath.TrimEnd([char[]]@('\','/')))
    if ($leaf.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $leaf -cne $leaf.TrimEnd([char[]]@(' ','.')) -or
        $leaf -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'transport.output-name' }
    $path = [IO.Path]::GetFullPath($LiteralPath).TrimEnd([char[]]@('\','/'))
    $null = Assert-ClashSharpOrdinaryPath -LiteralPath (Split-Path -Parent $path) -RequireDirectory
    if (Test-Path -LiteralPath $path) { throw 'transport.output-exists' }
    return $path
}

function Copy-ClashSharpTransportEntry {
    <#
    .SYNOPSIS
        Copies exactly one bounded entry without trusting an archive's length claim.
    .DESCRIPTION
        Stops before writing excess decompressed bytes and rejects truncated streams.
    .PARAMETER Source
        Owned readable entry or candidate file stream.
    .PARAMETER Destination
        Owned newly created writable stream.
    .PARAMETER ExpectedLength
        Independently bounded entry length to consume exactly.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][IO.Stream] $Source,
        [Parameter(Mandatory)][IO.Stream] $Destination,
        [Parameter(Mandatory)][long] $ExpectedLength)
    if ($ExpectedLength -le 0 -or $ExpectedLength -gt 1073741824) { throw 'transport.entry-budget' }
    $buffer = [byte[]]::new(65536)
    $copied = 0L
    while (($count = $Source.Read($buffer,0,$buffer.Length)) -ne 0) {
        $copied += $count
        if ($copied -gt $ExpectedLength) { throw 'transport.entry-expanded-length' }
        $Destination.Write($buffer,0,$count)
    }
    if ($copied -ne $ExpectedLength) { throw 'transport.entry-truncated' }
}

function New-ClashSharpCandidateArchive {
    <#
    .SYNOPSIS
        Seals a pinned candidate into one transport archive without rebuilding or signing.
    .DESCRIPTION
        Holds the canonical identity and all eight candidate files through archive construction
        and final source checks. Fixed names, order, timestamp and stored bytes make transport
        repeatable. A failed operation retains its partial file; no success receipt is returned.
        The caller must trust the independent identity reference, not a self-selected manifest.
    .PARAMETER IdentityPath
        Canonical identity from the trusted signing operation.
    .PARAMETER ExpectedIdentitySha256
        Independent SHA256 returned by that operation.
    .PARAMETER ReleaseDirectory
        Exact eight-file candidate directory.
    .PARAMETER OutputPath
        New absolute ZIP path outside the candidate directory, with an existing ordinary parent.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $IdentityPath,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}\z')][string] $ExpectedIdentitySha256,
        [Parameter(Mandatory)][string] $ReleaseDirectory,
        [Parameter(Mandatory)][string] $OutputPath)
    $release = Assert-ClashSharpOrdinaryPath -LiteralPath $ReleaseDirectory -RequireDirectory
    $identityPath = Assert-ClashSharpOrdinaryPath -LiteralPath $IdentityPath -RequireFile
    $output = Get-ClashSharpNewTransportPath -LiteralPath $OutputPath
    $partial = Get-ClashSharpNewTransportPath -LiteralPath ($output + '.partial')
    if ($output.StartsWith($release.TrimEnd([char[]]@('\','/')) + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'transport.external-output-required'
    }
    $transportState = [pscustomobject]@{stream=$null}
    try {
        $source = Invoke-ClashSharpCandidateIdentity -IdentityPath $identityPath -ExpectedIdentitySha256 $ExpectedIdentitySha256 -ReleaseDirectory $release -Consume {
            param($identity)
            $total = 0L
            foreach ($file in $identity.files) { $total += [long]$file.length }
            if ($total -gt 4294901760) { throw 'transport.archive-budget' }
            $scriptResult = [ordered]@{sourceCommit=$identity.source.commit;candidateFiles=$identity.files.Count}
            # Assign the outer variable explicitly: the trusted callback executes in module scope.
            $transportState.stream = [IO.File]::Open($partial,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
            $zip = [IO.Compression.ZipArchive]::new($transportState.stream,[IO.Compression.ZipArchiveMode]::Create,$true)
            try {
                foreach ($name in Get-ClashSharpCandidateArchiveEntries) {
                    $path = if ($name -ceq 'candidate-identity.json') { $identityPath } else { Join-Path $release $name }
                    $inputLease = [IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
                    try {
                        $entry = $zip.CreateEntry($name,[IO.Compression.CompressionLevel]::NoCompression)
                        $entry.LastWriteTime = [DateTimeOffset]::new(1980,1,1,0,0,0,[TimeSpan]::Zero)
                        $entry.ExternalAttributes = 0
                        $writer = $entry.Open()
                        try { Copy-ClashSharpTransportEntry -Source $inputLease -Destination $writer -ExpectedLength $inputLease.Length }
                        finally { $writer.Dispose() }
                    } finally { $inputLease.Dispose() }
                }
            } finally { $zip.Dispose() }
            [pscustomobject]$scriptResult
        }
        $stream = $transportState.stream
        $stream.Flush($true)
        if ($stream.Length -gt 4294967296) { throw 'transport.archive-budget' }
        $length = $stream.Length
        $stream.Position = 0
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        $stream.Dispose()
        $transportState.stream = $null
        $null = Assert-ClashSharpOrdinaryPath -LiteralPath (Split-Path -Parent $output) -RequireDirectory
        [IO.File]::Move($partial,$output,$false)
        [pscustomobject][ordered]@{
            schemaVersion=1;kind='clashsharp-candidate-archive';candidateIdentitySha256=$ExpectedIdentitySha256
            sourceCommit=$source.sourceCommit;archiveSha256=$hash;archiveLength=$length;archiveEntries=9
            candidateFiles=$source.candidateFiles;candidateBytesPreserved=$true
            signatureTrustRechecked=$false;acceptanceBound=$false;promotionAuthorized=$false
        }
    } finally {
        if ($null -ne $transportState.stream) { $transportState.stream.Dispose() }
    }
}

function Expand-ClashSharpCandidateArchive {
    <#
    .SYNOPSIS
        Expands one independently pinned transport and rechecks its candidate identity.
    .DESCRIPTION
        Holds the ZIP against replacement through bounded extraction and final candidate recheck.
        Only nine exact ordered ordinary file entries are accepted. Does not execute files,
        recheck signing trust, import certificates, install, or authorize promotion. A failed
        destination is preserved and never overwritten or automatically replayed. The private
        audit workspace separates the identity from release files; this is not the ZIP layout.
    .PARAMETER ArchivePath
        Ordinary transport ZIP supplied by the trusted producer.
    .PARAMETER ExpectedArchiveSha256
        Independent archive digest, not selected from the archive or an adjacent untrusted receipt.
    .PARAMETER ExpectedIdentitySha256
        Independent original candidate digest, retained across producer and acceptance environments.
    .PARAMETER Destination
        New absolute directory under an existing ordinary parent.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $ArchivePath,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}\z')][string] $ExpectedArchiveSha256,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}\z')][string] $ExpectedIdentitySha256,
        [Parameter(Mandatory)][string] $Destination)
    $archive = Assert-ClashSharpOrdinaryPath -LiteralPath $ArchivePath -RequireFile
    $output = Get-ClashSharpNewTransportPath -LiteralPath $Destination
    $lease = [IO.File]::Open($archive,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if ($lease.Length -le 0 -or $lease.Length -gt 4294967296) { throw 'transport.archive-budget' }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($lease)).ToLowerInvariant()
        if ($hash -cne $ExpectedArchiveSha256) { throw 'transport.archive-digest' }
        $lease.Position = 0
        $zip = [IO.Compression.ZipArchive]::new($lease,[IO.Compression.ZipArchiveMode]::Read,$true)
        try {
            $expected = @(Get-ClashSharpCandidateArchiveEntries)
            if ($zip.Entries.Count -ne $expected.Count) { throw 'transport.entry-inventory' }
            $total = 0L
            for ($index=0; $index -lt $expected.Count; $index++) {
                $entry = $zip.Entries[$index]
                if ($entry.FullName -cne $expected[$index] -or $entry.ExternalAttributes -ne 0) { throw 'transport.entry-identity' }
                $limit = if ($index -eq 0) { 65536 } else { 1073741824 }
                if ($entry.Length -le 0 -or $entry.Length -gt $limit) { throw 'transport.entry-budget' }
                $total += $entry.Length
            }
            if ($total -gt 4294967296) { throw 'transport.archive-budget' }
            $null = [IO.Directory]::CreateDirectory($output)
            foreach ($entry in $zip.Entries) {
                # Entry names were matched ordinally to fixed repository literals before any write.
                $relative = if ($entry.FullName -ceq 'candidate-identity.json') { $entry.FullName } else { 'release/' + $entry.FullName }
                $path = Join-Path $output $relative
                $parent = Split-Path -Parent $path
                $null = [IO.Directory]::CreateDirectory($parent)
                $null = Assert-ClashSharpOrdinaryPath -LiteralPath $parent -RequireDirectory
                $reader = $entry.Open()
                try {
                    $writer = [IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                    try {
                        Copy-ClashSharpTransportEntry -Source $reader -Destination $writer -ExpectedLength $entry.Length
                        $writer.Flush($true)
                    } finally { $writer.Dispose() }
                } finally { $reader.Dispose() }
            }
        } finally { $zip.Dispose() }
        Invoke-ClashSharpCandidateIdentity -IdentityPath (Join-Path $output 'candidate-identity.json') -ExpectedIdentitySha256 $ExpectedIdentitySha256 -ReleaseDirectory (Join-Path $output 'release') -Consume {
            param($identity)
            $expectedFiles = @([pscustomobject]@{RelativePath='candidate-identity.json';Length=(Get-Item -LiteralPath (Join-Path $output 'candidate-identity.json') -Force).Length;Sha256=$ExpectedIdentitySha256})
            $expectedFiles += @($identity.files | ForEach-Object {
                [pscustomobject]@{RelativePath=('release/'+$_.path);Length=$_.length;Sha256=$_.sha256}
            })
            Compare-ClashSharpDirectoryContract -Expected @($expectedFiles | Sort-Object RelativePath) -Actual @(Get-ClashSharpDirectoryContract -LiteralPath $output)
            [pscustomobject][ordered]@{
                schemaVersion=1;kind='clashsharp-candidate-archive-received';candidateIdentitySha256=$ExpectedIdentitySha256
                sourceCommit=$identity.source.commit;archiveSha256=$ExpectedArchiveSha256;archiveEntries=9
                candidateFiles=$identity.files.Count;candidateBytesPreserved=$true
                signatureTrustRechecked=$false;acceptanceBound=$false;promotionAuthorized=$false
            }
        }
    } finally { $lease.Dispose() }
}

Export-ModuleMember -Function New-ClashSharpCandidateArchive, Expand-ClashSharpCandidateArchive
