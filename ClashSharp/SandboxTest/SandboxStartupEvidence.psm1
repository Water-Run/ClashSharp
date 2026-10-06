Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SandboxInputContract.psm1') -Scope Local

function Read-SandboxStartupMetadata {
    <#
    .SYNOPSIS
        Reads bounded generation metadata after rejecting observed reparse points.
    .DESCRIPTION
        Checks every existing ancestor, bounds strict UTF-8 bytes, and returns the text and digest.
        The shared read handle permits the application's atomic manifest replacement.
    .PARAMETER LiteralPath
        Exact generation manifest or immutable identity marker path.
    #>
    param([Parameter(Mandatory)][string]$LiteralPath)
    $ancestor = [IO.Path]::GetFullPath($LiteralPath)
    while ($ancestor) {
        $item = Get-Item -LiteralPath $ancestor -Force -ErrorAction Stop
        if ($item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'sandbox.startup.reparse_path' }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    $stream = [IO.File]::Open($LiteralPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
    try {
        if ($stream.Length -lt 2 -or $stream.Length -gt 4096) { throw 'sandbox.startup.metadata_length' }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $count = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($count -eq 0) { throw 'sandbox.startup.metadata_truncated' }
            $offset += $count
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
        return [pscustomobject]@{ Text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes); Hash = $digest }
    } finally { $stream.Dispose() }
}

function Resolve-SandboxStartupGeneration {
    <#
    .SYNOPSIS
        Resolves the startup log only through the application's verified current generation.
    .DESCRIPTION
        Checks the manifest shape, digest, canonical descriptor and matching immutable marker.
        Never scans directories or falls back to a legacy log that could contain stale evidence.
    .PARAMETER LocalStatePath
        Existing LocalState directory of the exact candidate in the owned guest.
    #>
    param([Parameter(Mandatory)][string]$LocalStatePath)
    if (-not [IO.Path]::IsPathRooted($LocalStatePath)) { throw 'sandbox.startup.local_state_path' }
    $dataRoot = Join-Path ([IO.Path]::GetFullPath($LocalStatePath)) 'Data\v1'
    $manifestPath = Join-Path $dataRoot 'current-generation.json'
    $metadata = Read-SandboxStartupMetadata $manifestPath
    $envelope = $metadata.Text | ConvertFrom-Json
    Assert-SandboxObject $envelope @('schemaVersion', 'payload', 'contentHash')
    if ($envelope.schemaVersion -isnot [int] -and $envelope.schemaVersion -isnot [long]) { throw 'sandbox.startup.manifest_schema' }
    if ($envelope.schemaVersion -ne 1 -or $envelope.payload -isnot [string] -or
        $envelope.contentHash -isnot [string] -or $envelope.contentHash -cnotmatch '^[0-9a-f]{64}$') {
        throw 'sandbox.startup.manifest_shape'
    }
    $payloadBytes = [Convert]::FromBase64String($envelope.payload)
    if ([Convert]::ToBase64String($payloadBytes) -cne $envelope.payload) { throw 'sandbox.startup.manifest_base64' }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $payloadHash = ([BitConverter]::ToString($sha.ComputeHash($payloadBytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    if ($payloadHash -cne $envelope.contentHash) { throw 'sandbox.startup.manifest_hash' }
    $canonicalEnvelope = '{"schemaVersion":1,"payload":"' + $envelope.payload + '","contentHash":"' + $payloadHash + '"}'
    # System.Text.Json escapes '+' in strings; PowerShell's fixture serializer does not.
    if ($metadata.Text -cne $canonicalEnvelope -and $metadata.Text -cne $canonicalEnvelope.Replace('+', '\u002B')) {
        throw 'sandbox.startup.manifest_encoding'
    }
    $payloadText = [Text.UTF8Encoding]::new($false, $true).GetString($payloadBytes)
    $descriptor = $payloadText | ConvertFrom-Json
    Assert-SandboxObject $descriptor @('schemaVersion', 'manifestRevision', 'generationId',
        'generationNumber', 'highestGenerationNumber', 'rootRelativePath')
    foreach ($name in @('schemaVersion', 'manifestRevision', 'generationNumber', 'highestGenerationNumber')) {
        if (($descriptor.$name -isnot [int] -and $descriptor.$name -isnot [long]) -or $descriptor.$name -lt 1) {
            throw 'sandbox.startup.generation_number'
        }
    }
    $generationId = [Guid]::Empty
    if ($descriptor.schemaVersion -ne 1 -or $descriptor.generationId -isnot [string] -or
        -not [Guid]::TryParseExact($descriptor.generationId, 'D', [ref]$generationId) -or
        $generationId -eq [Guid]::Empty -or $generationId.ToString('D') -cne $descriptor.generationId -or
        $descriptor.highestGenerationNumber -lt $descriptor.generationNumber -or $descriptor.rootRelativePath -isnot [string] -or
        $descriptor.rootRelativePath -cne ('generations/' + $generationId.ToString('N'))) {
        throw 'sandbox.startup.generation_descriptor'
    }
    $canonicalDescriptor = [ordered]@{ schemaVersion = 1; manifestRevision = $descriptor.manifestRevision
        generationId = $generationId.ToString('D'); generationNumber = $descriptor.generationNumber
        highestGenerationNumber = $descriptor.highestGenerationNumber; rootRelativePath = $descriptor.rootRelativePath }
    if (($canonicalDescriptor | ConvertTo-Json -Compress) -cne $payloadText) { throw 'sandbox.startup.generation_encoding' }
    $generationRoot = Join-Path $dataRoot $descriptor.rootRelativePath
    $marker = Read-SandboxStartupMetadata (Join-Path $generationRoot '.generation-identity.json')
    $canonicalMarker = [ordered]@{ schemaVersion = 1; generationId = $generationId.ToString('D'); generationNumber = $descriptor.generationNumber }
    if (($canonicalMarker | ConvertTo-Json -Compress) -cne $marker.Text) { throw 'sandbox.startup.generation_identity' }
    $logPath = Join-Path $generationRoot 'ClashSharpLogs.sqlite3'
    $log = Get-Item -LiteralPath $logPath -Force -ErrorAction Stop
    if ($log.PSIsContainer -or $log.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'sandbox.startup.log_path' }
    return [pscustomobject]@{ LogPath = $logPath; ManifestHash = $metadata.Hash; MarkerHash = $marker.Hash }
}

function Assert-SandboxStartupEvidence {
    <#
    .SYNOPSIS
        Requires completed credential, interactive shell, and final startup steps without failures.
    .DESCRIPTION
        Rejects partial, duplicated, failed, or incorrectly typed aggregate observations.
    .PARAMETER Evidence
        Aggregate observations from the candidate's startup log after this launch began.
    #>
    param([object]$Evidence)
    $names = @('credentialCompletions', 'windowCompletions', 'pipelineCompletions', 'failures', 'startedAtUnixTime')
    if ($Evidence -isnot [pscustomobject] -or @($Evidence.PSObject.Properties).Count -ne $names.Count) {
        throw 'sandbox.startup.evidence_shape'
    }
    foreach ($name in $names) {
        if (@($Evidence.PSObject.Properties.Name) -cnotcontains $name -or
            ($Evidence.$name -isnot [int] -and $Evidence.$name -isnot [long])) {
            throw 'sandbox.startup.evidence_type'
        }
    }
    if ($Evidence.credentialCompletions -ne 1 -or $Evidence.windowCompletions -ne 1 -or
        $Evidence.pipelineCompletions -ne 1 -or $Evidence.failures -ne 0 -or $Evidence.startedAtUnixTime -le 0) {
        throw 'sandbox.startup.not_ready'
    }
}

function Get-SandboxStartupEvidence {
    <#
    .SYNOPSIS
        Reads only aggregate startup evidence from the newly launched candidate's SQLite log.
    .DESCRIPTION
        Opens the existing database read-only with the Windows system SQLite library. Never reads
        credential values or returns log text. With LocalStatePath, validates the active generation
        before and after querying it. Native resources are closed on every result.
        See https://learn.microsoft.com/dotnet/standard/data/sqlite/custom-versions and
        https://sqlite.org/c3ref/open.html for the system provider and read-only open contract.
    .PARAMETER LiteralPath
        Exact existing database for isolated reader fixtures.
    .PARAMETER LocalStatePath
        Exact candidate LocalState directory whose current generation owns the startup log.
    .PARAMETER StartedAtUnixTime
        UTC seconds captured immediately before starting this candidate process.
    #>
    [CmdletBinding(DefaultParameterSetName = 'Database')]
    param([Parameter(Mandatory, ParameterSetName = 'Database')][string]$LiteralPath,
        [Parameter(Mandatory, ParameterSetName = 'Generation')][string]$LocalStatePath,
        [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$StartedAtUnixTime)
    $generation = $null
    if ($PSCmdlet.ParameterSetName -ceq 'Generation') {
        $generation = Resolve-SandboxStartupGeneration $LocalStatePath
        $LiteralPath = $generation.LogPath
    }
    if (-not [IO.Path]::IsPathRooted($LiteralPath) -or -not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw 'sandbox.startup.database_missing'
    }
    if (-not ('ClashSharpSandboxStartupReader' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ClashSharpSandboxStartupReader {
    private const string Library = @"C:\Windows\System32\winsqlite3.dll";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_db_readonly(IntPtr db, byte[] name);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(IntPtr statement, int column);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);
    private static byte[] Utf8(string value) { return Encoding.UTF8.GetBytes(value + "\0"); }
    public static long[] Read(string path, long startedAt) {
        IntPtr db = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        try {
            // SQLITE_OPEN_READONLY; no CREATE, URI, extension loading, or write fallback.
            if (sqlite3_open_v2(Utf8(path), out db, 1, IntPtr.Zero) != 0 ||
                sqlite3_db_readonly(db, Utf8("main")) != 1 || sqlite3_busy_timeout(db, 1000) != 0) {
                throw new InvalidOperationException("sandbox.startup.database_open");
            }
            const string sql = @"SELECT
                COUNT(CASE WHEN Message = 'Startup step ''controller-credential'' completed.' AND
                    Detail GLOB 'order=140; stage=Completed; outcome=Succeeded; code=; elapsedMs=*' THEN 1 END),
                COUNT(CASE WHEN Message = 'Startup step ''window-shell'' completed.' AND
                    Detail GLOB 'order=600; stage=Completed; outcome=Succeeded; code=; elapsedMs=*' THEN 1 END),
                COUNT(CASE WHEN Message = 'Startup step ''profile-subscription-updates'' completed.' AND
                    Detail GLOB 'order=710; stage=Completed; outcome=Succeeded; code=; elapsedMs=*' THEN 1 END),
                COUNT(CASE WHEN Level = 'Error' THEN 1 END)
                FROM Logs WHERE Source = 'StartupPipeline' AND CreatedAtUnixTime >= ?1";
            if (sqlite3_prepare_v2(db, Utf8(sql), -1, out statement, IntPtr.Zero) != 0 ||
                sqlite3_bind_int64(statement, 1, startedAt) != 0 || sqlite3_step(statement) != 100) {
                throw new InvalidOperationException("sandbox.startup.database_query");
            }
            long[] counts = new long[4];
            for (int index = 0; index < counts.Length; index++) { counts[index] = sqlite3_column_int64(statement, index); }
            if (sqlite3_step(statement) != 101) { throw new InvalidOperationException("sandbox.startup.database_result"); }
            return counts;
        } finally {
            if (statement != IntPtr.Zero) { sqlite3_finalize(statement); }
            if (db != IntPtr.Zero) { sqlite3_close(db); }
        }
    }
}
'@
    }
    $counts = [ClashSharpSandboxStartupReader]::Read($LiteralPath, $StartedAtUnixTime)
    if ($null -ne $generation) {
        $current = Resolve-SandboxStartupGeneration $LocalStatePath
        if ($current.ManifestHash -cne $generation.ManifestHash -or $current.MarkerHash -cne $generation.MarkerHash -or
            $current.LogPath -cne $generation.LogPath) { throw 'sandbox.startup.generation_changed' }
    }
    $evidence = [pscustomobject]@{ credentialCompletions = $counts[0]; windowCompletions = $counts[1]
        pipelineCompletions = $counts[2]; failures = $counts[3]; startedAtUnixTime = $StartedAtUnixTime }
    Assert-SandboxStartupEvidence $evidence
    return $evidence
}

Export-ModuleMember -Function Assert-SandboxStartupEvidence, Get-SandboxStartupEvidence
