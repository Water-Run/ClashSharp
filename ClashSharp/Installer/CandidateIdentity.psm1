#Requires -Version 7.4
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'PackagingContract.psm1') -Force

function Get-ClashSharpCandidatePaths {
    <#
    .SYNOPSIS
        Returns the fixed formal-shaped candidate inventory.
    .DESCRIPTION
        Development markers, unsigned entry points, private keys and extra files are not candidate inputs.
    #>
    [CmdletBinding()]
    param()
    @('ClashSharp-Installer.exe','ClashSharp-Installer.exe.sha256','MIHOMO-SOURCE-MATERIALS.zip',
        'THIRD-PARTY-NOTICES.zip','payload/ClashSharp_1.0.0.0_x64.msix',
        'payload/ClashSharp_TemporaryKey.cer','payload/payload-provenance.json',
        'payload/Dependencies/x64/Microsoft.WindowsAppRuntime.1.8.msix')
}

function Get-ClashSharpCandidateDocument {
    <#
    .SYNOPSIS
        Canonicalizes independently observed candidate identities and computes their reference digest.
    .DESCRIPTION
        This pure policy function does not authenticate supplied observations. The producer must
        obtain source, CI, file and signature observations through their actual authorities. Its
        digest identifies this precise set, not a signing approval, VM result or release decision.
    .PARAMETER SourceCommit
        Canonical source commit checked out for the candidate build.
    .PARAMETER SourceTree
        Canonical Git tree identity of that clean checkout.
    .PARAMETER CiRunId
        Exact full product run for the same source.
    .PARAMETER CiEvidenceSha256
        SHA256 of the consumed artifact-and-TRX evidence snapshot.
    .PARAMETER Files
        Eight ordinary candidate file observations with path, length and SHA256.
    .PARAMETER Signatures
        Fresh trusted signature observations keyed installer, primary and dependency.
    #>
    [CmdletBinding()]
    param(
        [string] $SourceCommit, [string] $SourceTree, [long] $CiRunId,
        [string] $CiEvidenceSha256, [object[]] $Files, $Signatures
    )
    if ($SourceCommit -cnotmatch '^[0-9a-f]{40}\z' -or $SourceCommit -ceq ('0'*40) -or
        $SourceTree -cnotmatch '^[0-9a-f]{40}\z' -or $SourceTree -ceq ('0'*40) -or
        $CiRunId -le 0 -or $CiEvidenceSha256 -cnotmatch '^[0-9a-f]{64}\z') {
        throw 'candidate.identity-input'
    }
    $expected = @(Get-ClashSharpCandidatePaths)
    if ($Files.Count -ne $expected.Count) { throw 'candidate.file-inventory' }
    $ordered = @(foreach ($path in $expected) {
        $matching = @($Files | Where-Object { $_.path -ceq $path })
        if ($matching.Count -ne 1 -or $matching[0].length -le 0 -or
            $matching[0].length -gt 1073741824 -or $matching[0].sha256 -cnotmatch '^[0-9a-f]{64}\z') {
            throw 'candidate.file-identity'
        }
        [ordered]@{ path=$path; length=[long]$matching[0].length; sha256=[string]$matching[0].sha256 }
    })
    $signers = [ordered]@{}
    foreach ($role in @('installer','primary','dependency')) {
        $signature = $Signatures.$role
        if ($signature.status -cne 'Valid' -or $signature.thumbprint -cnotmatch '^[0-9A-F]{40}\z' -or
            $signature.thumbprint -ceq ('0'*40) -or [string]::IsNullOrWhiteSpace($signature.subject) -or
            $signature.subject.Length -gt 4096 -or $signature.timestamp -isnot [bool] -or
            ($role -in @('installer','dependency') -and $signature.timestamp -ne $true)) {
            throw 'candidate.signature-identity'
        }
        $signers[$role] = [ordered]@{ subject=[string]$signature.subject; thumbprint=[string]$signature.thumbprint;
            status='Valid'; timestamp=[bool]$signature.timestamp }
    }
    $document = [ordered]@{
        schemaVersion=1
        kind='clashsharp-signed-candidate-identity'
        repository='Water-Run/ClashSharp'
        source=[ordered]@{ commit=$SourceCommit; tree=$SourceTree }
        ci=[ordered]@{ runId=$CiRunId; artifactEvidenceSha256=$CiEvidenceSha256 }
        files=$ordered
        signatures=$signers
        acceptanceBound=$false
        promotionAuthorized=$false
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($document | ConvertTo-Json -Depth 10 -Compress) + "`n")
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    [pscustomobject]@{ document=$document; identitySha256=$hash; canonicalJson=[Text.Encoding]::UTF8.GetString($bytes) }
}

function Get-ClashSharpCandidateFileSnapshot {
    <#
    .SYNOPSIS
        Hashes the exact candidate set and executes its verifier while read leases remain held.
    .DESCRIPTION
        Holds all eight files against writes and deletion for the full callback. This is a scoped
        consumption primitive, not a durable lock on paths after return. The callback is trusted
        producer code, never loaded from the candidate manifest or directory.
    .PARAMETER ReleaseDirectory
        Ordinary candidate output directory.
    .PARAMETER Consume
        Trusted producer operation accepting the file snapshot while leases remain open.
    #>
    [CmdletBinding()]
    param([string] $ReleaseDirectory, [Parameter(Mandatory)][scriptblock] $Consume)
    $root = Assert-ClashSharpOrdinaryPath -LiteralPath $ReleaseDirectory -RequireDirectory
    $inventory = @(Get-ClashSharpDirectoryContract -LiteralPath $root)
    $expected = @(Get-ClashSharpCandidatePaths)
    if ($inventory.Count -ne 8 -or @($inventory | Where-Object { $_.relativePath -cnotin $expected }).Count) {
        throw 'candidate.file-inventory'
    }
    $leases = [Collections.Generic.List[IO.FileStream]]::new()
    try {
        $files = @(foreach ($path in $expected) {
            $file = Assert-ClashSharpOrdinaryPath -LiteralPath (Join-Path $root $path) -RequireFile
            $stream = [IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            $leases.Add($stream)
            if ($stream.Length -le 0 -or $stream.Length -gt 1073741824) { throw 'candidate.file-budget' }
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
            [pscustomobject]@{ path=$path; length=$stream.Length; sha256=$hash }
        })
        $result = & $Consume $files
        $after = @(Get-ClashSharpDirectoryContract -LiteralPath $root)
        $expectedContract = @($files | ForEach-Object {
            [pscustomobject]@{ RelativePath=$_.path; Length=$_.length; Sha256=$_.sha256 }
        } | Sort-Object -Property RelativePath)
        Compare-ClashSharpDirectoryContract -Expected $expectedContract -Actual $after
        return $result
    } finally { foreach ($lease in $leases) { $lease.Dispose() } }
}

Export-ModuleMember -Function Get-ClashSharpCandidatePaths, Get-ClashSharpCandidateDocument, Get-ClashSharpCandidateFileSnapshot
