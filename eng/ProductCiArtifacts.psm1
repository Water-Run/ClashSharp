#Requires -Version 7.4
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'TestResultEvidence.psm1') -Force

function Assert-ClashSharpCiArtifactPath {
    <#
    .SYNOPSIS
        Rejects reparse points on an existing CI input path and its ancestors.
    .DESCRIPTION
        CI archives are ordinary files under a caller-owned directory; no links are followed.
    .PARAMETER LiteralPath
        Existing file or directory path to inspect.
    #>
    [CmdletBinding()]
    param([string] $LiteralPath)
    $path = [IO.Path]::GetFullPath($LiteralPath)
    $current = $path
    while ($current) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'ci-artifacts.reparse-path' }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $path
}

function Get-ClashSharpCiZipFiles {
    <#
    .SYNOPSIS
        Reads a bounded canonical inventory without extracting a ZIP.
    .DESCRIPTION
        Rejects traversal, rooted or nonportable paths, collisions and excessive expansion.
    .PARAMETER Archive
        ZIP opened over a retained read lease.
    #>
    [CmdletBinding()]
    param([IO.Compression.ZipArchive] $Archive)
    if ($Archive.Entries.Count -gt 4096) { throw 'ci-artifacts.zip-inventory' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $files = [Collections.Generic.List[object]]::new()
    $total = 0L
    foreach ($entry in $Archive.Entries) {
        $name = $entry.FullName
        $trimmed = $name.TrimEnd('/')
        if ($trimmed -cnotmatch '^[A-Za-z0-9_][A-Za-z0-9._/-]*\z' -or
            @($trimmed.Split('/') | Where-Object { $_ -in @('', '.', '..') -or $_.EndsWith('.') }).Count -gt 0 -or
            -not $seen.Add($trimmed)) { throw 'ci-artifacts.zip-path' }
        if ($entry.Length -lt 0 -or $entry.Length -gt 1073741824) { throw 'ci-artifacts.zip-budget' }
        $total += $entry.Length
        if ($total -gt 1073741824) { throw 'ci-artifacts.zip-budget' }
        if (-not $name.EndsWith('/')) { $files.Add($entry) }
    }
    return $files.ToArray()
}

function Copy-ClashSharpCiEntry {
    <#
    .SYNOPSIS
        Copies one bounded ZIP member to an owned stream.
    .DESCRIPTION
        Enforces the actual decompressed length, not only the untrusted ZIP header.
    .PARAMETER Entry
        Canonical inventory entry.
    .PARAMETER Destination
        Owned output stream.
    .PARAMETER MaximumBytes
        Maximum actual decompressed bytes accepted for this role.
    #>
    [CmdletBinding()]
    param([IO.Compression.ZipArchiveEntry] $Entry, [IO.Stream] $Destination, [long] $MaximumBytes)
    if ($Entry.Length -le 0 -or $Entry.Length -gt $MaximumBytes) { throw 'ci-artifacts.entry-budget' }
    $source = $Entry.Open()
    try {
        $buffer = [byte[]]::new(8192)
        $total = 0L
        while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $total += $read
            if ($total -gt $MaximumBytes -or $total -gt $Entry.Length) { throw 'ci-artifacts.entry-budget' }
            $Destination.Write($buffer, 0, $read)
        }
        if ($total -ne $Entry.Length) { throw 'ci-artifacts.entry-length' }
    } finally { $source.Dispose() }
}

function Read-ClashSharpCiScope {
    <#
    .SYNOPSIS
        Validates the actual scope record from an authenticated artifact archive.
    .DESCRIPTION
        Requires product scope and the expected head; incomplete discovery may legitimately have
        required all product checks. Rejects duplicate or unknown JSON fields and malformed types.
    .PARAMETER Entry
        Unique ci-scope.json member.
    .PARAMETER ExpectedCommit
        Exact source commit established by fresh CI metadata.
    #>
    [CmdletBinding()]
    param([IO.Compression.ZipArchiveEntry] $Entry, [string] $ExpectedCommit)
    $memory = [IO.MemoryStream]::new()
    try {
        Copy-ClashSharpCiEntry -Entry $Entry -Destination $memory -MaximumBytes 1048576
        $options = [Text.Json.JsonDocumentOptions]::new()
        $options.MaxDepth = 8
        try { $document = [Text.Json.JsonDocument]::Parse([Text.Encoding]::UTF8.GetString($memory.ToArray()), $options) }
        catch { throw 'ci-artifacts.scope-json' }
        try {
            $root = $document.RootElement
            if ($root.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'ci-artifacts.scope-json' }
            $allowed = @('schemaVersion','product','reason','complete','changedPaths','productTestsExecuted','baseCommit','headCommit')
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($property in $root.EnumerateObject()) {
                if ($property.Name -cnotin $allowed -or -not $seen.Add($property.Name)) { throw 'ci-artifacts.scope-fields' }
            }
            if ($seen.Count -ne $allowed.Count -or $root.GetProperty('schemaVersion').GetInt32() -ne 1 -or
                $root.GetProperty('product').ValueKind -ne [Text.Json.JsonValueKind]::True -or
                $root.GetProperty('headCommit').GetString() -cne $ExpectedCommit -or
                $root.GetProperty('complete').ValueKind -notin @([Text.Json.JsonValueKind]::True, [Text.Json.JsonValueKind]::False) -or
                $root.GetProperty('changedPaths').ValueKind -ne [Text.Json.JsonValueKind]::Array -or
                $root.GetProperty('productTestsExecuted').ValueKind -ne [Text.Json.JsonValueKind]::False) {
                throw 'ci-artifacts.scope-identity'
            }
            foreach ($path in $root.GetProperty('changedPaths').EnumerateArray()) {
                if ($path.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'ci-artifacts.scope-fields' }
            }
            if ($root.GetProperty('reason').GetString() -notin @('product-or-other-path','incomplete-diff','empty-diff',
                'unknown-commit','checkout-identity-unverified','base-unavailable','diff-unavailable')) {
                throw 'ci-artifacts.scope-identity'
            }
            [pscustomobject]@{ product = $true; headCommit = $ExpectedCommit; complete = $root.GetProperty('complete').GetBoolean() }
        } catch {
            if ($_.Exception.Message.StartsWith('ci-artifacts.', [StringComparison]::Ordinal)) { throw }
            throw 'ci-artifacts.scope-json'
        } finally { $document.Dispose() }
    } finally { $memory.Dispose() }
}

function Get-ClashSharpProductCiArtifactEvidence {
    <#
    .SYNOPSIS
        Verifies four downloaded artifact snapshots and their actual scope and TRX contents.
    .DESCRIPTION
        Callers must obtain metadata through the fresh authenticated CI gate. Hashes and parsing
        use the same read-locked archive bytes. ZIP contents are never executed. The development
        package gets an inventory check, not signature or nested payload certification. Returned
        evidence is a snapshot, not an enduring lease or publication authority.
    .PARAMETER Metadata
        Fresh product-ci-execution-metadata descriptor, not an arbitrary stored approval.
    .PARAMETER ArtifactDirectory
        Ordinary directory containing artifact-ID.zip for each recorded artifact.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Metadata, [Parameter(Mandatory)][string] $ArtifactDirectory)
    if ($Metadata.kind -cne 'product-ci-execution-metadata' -or -not $Metadata.jobExecutionVerified -or
        $Metadata.repository -cne 'Water-Run/ClashSharp' -or $Metadata.sourceCommit -cnotmatch '^[0-9a-f]{40}\z' -or
        $Metadata.runId -le 0 -or $Metadata.artifacts.Count -ne 4) { throw 'ci-artifacts.metadata-identity' }
    $directory = Assert-ClashSharpCiArtifactPath $ArtifactDirectory
    if (-not (Get-Item -LiteralPath $directory).PSIsContainer) { throw 'ci-artifacts.directory-required' }
    $roles = @('ci-scope-dotnet','ci-scope-installer','dotnet-test-results','ClashSharp-1.0.0-development-package')
    if (@($Metadata.artifacts.name | Sort-Object -Unique).Count -ne 4 -or
        @($Metadata.artifacts.id | Sort-Object -Unique).Count -ne 4 -or
        @($Metadata.artifacts | Where-Object { $_.name -cnotin $roles }).Count) { throw 'ci-artifacts.role-inventory' }
    $temporaryParent = Assert-ClashSharpCiArtifactPath ([IO.Path]::GetTempPath())
    $temporary = Join-Path $temporaryParent ('clashsharp-ci-artifacts-' + [Guid]::NewGuid().ToString('N'))
    $null = [IO.Directory]::CreateDirectory($temporary)
    $verified = [Collections.Generic.List[object]]::new()
    $scopes = [Collections.Generic.List[object]]::new()
    $summary = $null
    try {
        foreach ($artifact in $Metadata.artifacts) {
            if ([string]$artifact.id -cnotmatch '^[1-9][0-9]{0,18}\z' -or $artifact.length -le 0 -or
                $artifact.length -gt 1073741824 -or $artifact.digest -cnotmatch '^sha256:[0-9a-f]{64}\z') {
                throw 'ci-artifacts.metadata-identity'
            }
            $path = Assert-ClashSharpCiArtifactPath (Join-Path $directory ('artifact-' + $artifact.id + '.zip'))
            $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                if ($stream.Length -ne $artifact.length) { throw 'ci-artifacts.archive-digest' }
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $digest = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() } finally { $sha.Dispose() }
                if ($stream.Length -ne $artifact.length -or ('sha256:' + $digest) -cne $artifact.digest) {
                    throw 'ci-artifacts.archive-digest'
                }
                $stream.Position = 0
                try { $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true) }
                catch { throw 'ci-artifacts.zip-invalid' }
                try {
                    $entries = @(Get-ClashSharpCiZipFiles $archive)
                    if ($artifact.name.StartsWith('ci-scope-', [StringComparison]::Ordinal)) {
                        if ($entries.Count -ne 1 -or $entries[0].FullName -cne 'ci-scope.json') { throw 'ci-artifacts.scope-inventory' }
                        $scope = Read-ClashSharpCiScope -Entry $entries[0] -ExpectedCommit $Metadata.sourceCommit
                        $scopes.Add([pscustomobject]@{ artifact = $artifact.name; headCommit = $scope.headCommit; complete = $scope.complete })
                    } elseif ($artifact.name -ceq 'dotnet-test-results') {
                        $names = @('tests.trx','installer-core-tests.trx','installer-presentation-tests.trx','installer-windows-tests.trx')
                        $reports = @($entries | Where-Object { $_.FullName.EndsWith('.trx', [StringComparison]::OrdinalIgnoreCase) })
                        if ($reports.Count -ne 4) { throw 'ci-artifacts.report-inventory' }
                        $paths = @(foreach ($name in $names) {
                            $matching = @($reports | Where-Object { ($_.FullName.Split('/')[-1]) -ceq $name })
                            if ($matching.Count -ne 1) { throw 'ci-artifacts.report-inventory' }
                            $target = Join-Path $temporary $name
                            $output = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                            try { Copy-ClashSharpCiEntry -Entry $matching[0] -Destination $output -MaximumBytes 67108864 }
                            finally { $output.Dispose() }
                            $target
                        })
                        $summary = Get-ClashSharpTestResultSummary -LiteralPath $paths
                    } else {
                        $names = @('ClashSharp-Installer-Development-Unsigned.exe','LICENSE','README.md','metadata/ClashSharp-Installer-Development-Unsigned.exe.sha256',
                            'metadata/DEVELOPMENT-UNSIGNED.txt','licenses/MIHOMO-SOURCE-MATERIALS.zip','licenses/THIRD-PARTY-NOTICES.zip',
                            'payload/ClashSharp_1.0.0.0_x64.msix','payload/ClashSharp_TemporaryKey.cer','payload/payload-provenance.json',
                            'payload/Dependencies/x64/Microsoft.WindowsAppRuntime.1.8.msix')
                        if ($entries.Count -ne $names.Count -or @($entries | Where-Object { $_.FullName -cnotin $names -or $_.Length -le 0 }).Count) {
                            throw 'ci-artifacts.development-inventory'
                        }
                    }
                    $verified.Add([pscustomobject]@{ name = $artifact.name; id = $artifact.id; length = $stream.Length; digest = 'sha256:' + $digest })
                } finally { $archive.Dispose() }
            } finally { $stream.Dispose() }
        }
        if ($null -eq $summary -or $scopes.Count -ne 2) { throw 'ci-artifacts.evidence-incomplete' }
        [pscustomobject][ordered]@{
            schemaVersion = 1
            kind = 'verified-product-ci-artifact-snapshots'
            repository = $Metadata.repository
            sourceCommit = $Metadata.sourceCommit
            runId = $Metadata.runId
            runAttempt = $Metadata.runAttempt
            artifactBytesVerified = $true
            rawTestReportsVerified = $true
            artifacts = $verified.ToArray()
            scopes = $scopes.ToArray()
            tests = [pscustomobject]@{ passed = $summary.passed; failed = 0; skipped = 0; reportCount = 4;
                reports = @($summary.reports | ForEach-Object { [pscustomobject]@{ name = [IO.Path]::GetFileName($_.path); sha256 = $_.sha256; runId = $_.runId; passed = $_.passed } }) }
            developmentPackageInventoryVerified = $true
            developmentPackageSignaturesOrNestedPayloadsVerified = $false
            promotionAuthorized = $false
        }
    } finally {
        $full = [IO.Path]::GetFullPath($temporary)
        $parentPrefix = $temporaryParent.TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
        if (-not $full.StartsWith($parentPrefix + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($full) -cnotmatch '^clashsharp-ci-artifacts-[0-9a-f]{32}\z') {
            throw 'ci-artifacts.cleanup-path'
        }
        $nodes = @((Get-Item -LiteralPath $full -Force)) + @(Get-ChildItem -LiteralPath $full -Recurse -Force)
        if (@($nodes | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'ci-artifacts.cleanup-reparse' }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

Export-ModuleMember -Function Get-ClashSharpProductCiArtifactEvidence
