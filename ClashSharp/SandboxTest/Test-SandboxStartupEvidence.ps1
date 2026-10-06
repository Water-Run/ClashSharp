#Requires -Version 5.1
<#
.SYNOPSIS
    Verifies startup evidence using isolated SQLite fixtures without opening application data.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SandboxStartupEvidence.psm1') -Force
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ClashSharpSandboxStartupFixture {
    private const string Library = @"C:\Windows\System32\winsqlite3.dll";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr context, IntPtr error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);
    public static void Create(string path, string sql) {
        IntPtr db = IntPtr.Zero;
        try {
            if (sqlite3_open_v2(Encoding.UTF8.GetBytes(path + "\0"), out db, 6, IntPtr.Zero) != 0 ||
                sqlite3_exec(db, Encoding.UTF8.GetBytes(sql + "\0"), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0) {
                throw new InvalidOperationException("sandbox.fixture.database_failed");
            }
        } finally { if (db != IntPtr.Zero) { sqlite3_close(db); } }
    }
}
'@
$testParent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.sandbox\startup-evidence-tests'))
$testRoot = Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($testRoot)
$startedAt = 1788860000L
$assertions = 0
$fixtureSql = @'
CREATE TABLE Logs (CreatedAtUnixTime INTEGER, Level TEXT, Source TEXT, Message TEXT, Detail TEXT);
INSERT INTO Logs VALUES (1788860000, 'Info', 'StartupPipeline', 'Startup step ''controller-credential'' completed.', 'order=140; stage=Completed; outcome=Succeeded; code=; elapsedMs=1.000; exceptionType=; exceptionMessage=');
INSERT INTO Logs VALUES (1788860000, 'Info', 'StartupPipeline', 'Startup step ''window-shell'' completed.', 'order=600; stage=Completed; outcome=Succeeded; code=; elapsedMs=1.000; exceptionType=; exceptionMessage=');
INSERT INTO Logs VALUES (1788860000, 'Info', 'StartupPipeline', 'Startup step ''profile-subscription-updates'' completed.', 'order=710; stage=Completed; outcome=Succeeded; code=; elapsedMs=1.000; exceptionType=; exceptionMessage=');
'@
function Assert-StartupReadRejected {
    <#
    .SYNOPSIS
        Requires a real SQLite read to reject incomplete or unavailable startup evidence.
    .DESCRIPTION
        Counts a negative assertion only when the production reader rejects the owned fixture.
    .PARAMETER Path
        Owned fixture database path.
    #>
    param([string]$Path)
    $rejected = $false
    try { $null = Get-SandboxStartupEvidence -LiteralPath $Path -StartedAtUnixTime $startedAt }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Accepted incomplete startup fixture.' }
    $script:assertions++
}
function Assert-StartupGenerationRejected {
    <#
    .SYNOPSIS
        Rejects invalid current-generation metadata even when a legacy log could pass.
    .DESCRIPTION
        Invokes the real generation resolver and read-only SQLite evidence reader.
    .PARAMETER LocalStatePath
        Owned fixture LocalState directory.
    #>
    param([string]$LocalStatePath)
    $rejected = $false
    try { $null = Get-SandboxStartupEvidence -LocalStatePath $LocalStatePath -StartedAtUnixTime $startedAt }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Accepted invalid current-generation startup evidence.' }
    $script:assertions++
}
try {
    $validPath = Join-Path $testRoot "candidate ' unicode-$([char]0x4E2D).sqlite3"
    [ClashSharpSandboxStartupFixture]::Create($validPath, $fixtureSql)
    $beforeHash = (Get-FileHash -LiteralPath $validPath).Hash
    $result = Get-SandboxStartupEvidence -LiteralPath $validPath -StartedAtUnixTime $startedAt
    if ($result.credentialCompletions -ne 1 -or $result.windowCompletions -ne 1 -or
        $result.pipelineCompletions -ne 1 -or $result.failures -ne 0 -or
        (Get-FileHash -LiteralPath $validPath).Hash -cne $beforeHash) { throw 'Read-only startup query mismatch.' }
    $assertions++
    [IO.File]::SetAttributes($validPath, [IO.FileAttributes]::ReadOnly)
    try { $null = Get-SandboxStartupEvidence -LiteralPath $validPath -StartedAtUnixTime $startedAt; $assertions++ }
    finally { [IO.File]::SetAttributes($validPath, [IO.FileAttributes]::Normal) }
    $locked = [IO.File]::Open($validPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try { Assert-StartupReadRejected $validPath } finally { $locked.Dispose() }
    $null = Get-SandboxStartupEvidence -LiteralPath $validPath -StartedAtUnixTime $startedAt
    $assertions++
    $invalidCases = @(
        'UPDATE Logs SET CreatedAtUnixTime = 1788859999;',
        "DELETE FROM Logs WHERE Detail LIKE 'order=140;%';",
        "DELETE FROM Logs WHERE Detail LIKE 'order=600;%';",
        "DELETE FROM Logs WHERE Detail LIKE 'order=710;%';",
        "UPDATE Logs SET Detail = REPLACE(Detail, 'outcome=Succeeded', 'outcome=Fatal');",
        "UPDATE Logs SET Detail = REPLACE(Detail, 'stage=Completed', 'stage=Started');",
        "UPDATE Logs SET Source = 'Unrelated';",
        "INSERT INTO Logs SELECT * FROM Logs WHERE Detail LIKE 'order=140;%';",
        "INSERT INTO Logs VALUES (1788860000, 'Error', 'StartupPipeline', 'failed', 'private-fixture-detail');",
        'DROP TABLE Logs; CREATE TABLE Logs (wrong TEXT);'
    )
    for ($index = 0; $index -lt $invalidCases.Count; $index++) {
        $invalidPath = Join-Path $testRoot ('invalid-' + $index + '.sqlite3')
        [ClashSharpSandboxStartupFixture]::Create($invalidPath, ($fixtureSql + [Environment]::NewLine + $invalidCases[$index]))
        $invalidHash = (Get-FileHash -LiteralPath $invalidPath).Hash
        Assert-StartupReadRejected $invalidPath
        if ((Get-FileHash -LiteralPath $invalidPath).Hash -cne $invalidHash) { throw 'Rejected evidence was modified.' }
    }
    $missingPath = Join-Path $testRoot 'missing.sqlite3'
    Assert-StartupReadRejected $missingPath
    if (Test-Path -LiteralPath $missingPath) { throw 'Read created a missing database.' }
    $corruptPath = Join-Path $testRoot 'corrupt.sqlite3'
    [IO.File]::WriteAllText($corruptPath, 'private-invalid-database-fixture')
    Assert-StartupReadRejected $corruptPath
    if ([IO.File]::ReadAllText($corruptPath) -cne 'private-invalid-database-fixture') { throw 'Corrupt evidence was overwritten.' }
    $localState = Join-Path $testRoot 'LocalState'
    $dataRoot = Join-Path $localState 'Data\v1'
    $generationId = '57f1b952-23c3-4f6a-977b-46b435aed08b'
    $relativeRoot = 'generations/' + $generationId.Replace('-', '')
    $generationRoot = Join-Path $dataRoot $relativeRoot
    $null = [IO.Directory]::CreateDirectory($generationRoot)
    $generationLog = Join-Path $generationRoot 'ClashSharpLogs.sqlite3'
    [ClashSharpSandboxStartupFixture]::Create($generationLog, $fixtureSql)
    $marker = [ordered]@{ schemaVersion = 1; generationId = $generationId; generationNumber = 2 }
    [IO.File]::WriteAllText((Join-Path $generationRoot '.generation-identity.json'),
        ($marker | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    $manifest = [ordered]@{ schemaVersion = 1; manifestRevision = 2; generationId = $generationId
        generationNumber = 2; highestGenerationNumber = 2; rootRelativePath = $relativeRoot }
    $payloadBytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $payloadHash = ([BitConverter]::ToString($sha.ComputeHash($payloadBytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $envelope = [ordered]@{ schemaVersion = 1; payload = [Convert]::ToBase64String($payloadBytes); contentHash = $payloadHash }
    $manifestPath = Join-Path $dataRoot 'current-generation.json'
    $manifestText = $envelope | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($manifestPath, $manifestText, [Text.UTF8Encoding]::new($false))
    # This is the current production layout: the old root-level log is absent.
    Assert-StartupReadRejected (Join-Path $localState 'ClashSharpLogs.sqlite3')
    $generationHash = (Get-FileHash -LiteralPath $generationLog).Hash
    $result = Get-SandboxStartupEvidence -LocalStatePath $localState -StartedAtUnixTime $startedAt
    if ($result.pipelineCompletions -ne 1 -or (Get-FileHash -LiteralPath $generationLog).Hash -cne $generationHash) {
        throw 'Current generation startup evidence was not read without mutation.'
    }
    $assertions++
    # A healthy legacy database must not hide failed startup in the active generation.
    Copy-Item -LiteralPath $validPath -Destination (Join-Path $localState 'ClashSharpLogs.sqlite3')
    [ClashSharpSandboxStartupFixture]::Create($generationLog, "UPDATE Logs SET Level = 'Error';")
    Assert-StartupGenerationRejected $localState
    [ClashSharpSandboxStartupFixture]::Create($generationLog, "UPDATE Logs SET Level = 'Info';")
    $badDescriptors = @(
        @{ Name = 'rootRelativePath'; Value = '../outside' },
        @{ Name = 'rootRelativePath'; Value = @($relativeRoot) },
        @{ Name = 'generationId'; Value = $generationId.ToUpperInvariant() },
        @{ Name = 'generationId'; Value = [Guid]::Empty.ToString('D') },
        @{ Name = 'generationNumber'; Value = 0 },
        @{ Name = 'generationNumber'; Value = '2' },
        @{ Name = 'manifestRevision'; Value = $true },
        @{ Name = 'highestGenerationNumber'; Value = 1 },
        @{ Name = 'schemaVersion'; Value = 2 }
    )
    foreach ($case in $badDescriptors) {
        $badDescriptor = ($manifest | ConvertTo-Json -Compress) | ConvertFrom-Json
        $badDescriptor.($case.Name) = $case.Value
        $badBytes = [Text.Encoding]::UTF8.GetBytes(($badDescriptor | ConvertTo-Json -Compress))
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $badHash = ([BitConverter]::ToString($sha.ComputeHash($badBytes))).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
        $badEnvelope = [ordered]@{ schemaVersion = 1; payload = [Convert]::ToBase64String($badBytes); contentHash = $badHash }
        [IO.File]::WriteAllText($manifestPath, ($badEnvelope | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
        Assert-StartupGenerationRejected $localState
    }
    [IO.File]::WriteAllText($manifestPath, $manifestText.Replace($payloadHash, ('0' * 64)), [Text.UTF8Encoding]::new($false))
    Assert-StartupGenerationRejected $localState
    [IO.File]::WriteAllText($manifestPath, (' ' * 4097), [Text.UTF8Encoding]::new($false))
    Assert-StartupGenerationRejected $localState
    [IO.File]::WriteAllText($manifestPath, $manifestText, [Text.UTF8Encoding]::new($false))
    $markerPath = Join-Path $generationRoot '.generation-identity.json'
    $markerText = [IO.File]::ReadAllText($markerPath)
    [IO.File]::WriteAllText($markerPath, $markerText.Replace('"generationNumber":2', '"generationNumber":1'), [Text.UTF8Encoding]::new($false))
    Assert-StartupGenerationRejected $localState
    [IO.File]::Delete($markerPath)
    Assert-StartupGenerationRejected $localState
    [IO.File]::WriteAllText($markerPath, $markerText, [Text.UTF8Encoding]::new($false))
    $linkedState = Join-Path $testRoot 'linked-state'
    $null = [IO.Directory]::CreateDirectory((Join-Path $linkedState 'Data'))
    $junction = Join-Path $linkedState 'Data\v1'
    $null = New-Item -ItemType Junction -Path $junction -Target $dataRoot
    try { Assert-StartupGenerationRejected $linkedState }
    finally { [IO.Directory]::Delete($junction) }
    if (-not (Test-Path -LiteralPath $generationLog)) { throw 'Junction cleanup changed its target.' }
    $null = Get-SandboxStartupEvidence -LocalStatePath $localState -StartedAtUnixTime $startedAt
    $assertions++
    Write-Output "Sandbox startup evidence: $assertions assertions passed."
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith($testParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Get-Item -LiteralPath $resolved).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop
}
