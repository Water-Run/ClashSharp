Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-SandboxStartupEvidence {
    <#
    .SYNOPSIS
        Requires completed credential, interactive shell, and final startup steps without failures.
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
        credential values or returns log text. Native resources are closed on every result.
        See https://learn.microsoft.com/dotnet/standard/data/sqlite/custom-versions and
        https://sqlite.org/c3ref/open.html for the system provider and read-only open contract.
    .PARAMETER LiteralPath
        Exact existing log database in the owned guest's candidate LocalState directory.
    .PARAMETER StartedAtUnixTime
        UTC seconds captured immediately before starting this candidate process.
    #>
    param([Parameter(Mandatory)][string]$LiteralPath,
        [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$StartedAtUnixTime)
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
    $evidence = [pscustomobject]@{ credentialCompletions = $counts[0]; windowCompletions = $counts[1]
        pipelineCompletions = $counts[2]; failures = $counts[3]; startedAtUnixTime = $StartedAtUnixTime }
    Assert-SandboxStartupEvidence $evidence
    return $evidence
}

Export-ModuleMember -Function Assert-SandboxStartupEvidence, Get-SandboxStartupEvidence
