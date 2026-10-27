#Requires -Version 7.4
<#
.SYNOPSIS
    Runs the pinned Installer's read-only payload audit and records its actual Windows platform.
.DESCRIPTION
    Only executes the fixed --verify-payload route of an independently trusted candidate. Holds
    its canonical manifest and all ten output files against changes through process termination.
    No trust import, elevation, installation, service change or UI launch is performed. Retains
    the owned process journal and diagnostics on failure; an existing output is never replayed.
.PARAMETER IdentityPath
    Canonical identity document returned by the controlled signing operation.
.PARAMETER ExpectedIdentitySha256
    Independent candidate reference from that operation, never selected from an untrusted document.
.PARAMETER ReleaseDirectory
    Exact ten-file signed output belonging to that identity.
.PARAMETER OutputDirectory
    New evidence directory outside ReleaseDirectory; failures remain for observation.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $IdentityPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}\z')][string] $ExpectedIdentitySha256,
    [Parameter(Mandatory)][string] $ReleaseDirectory,
    [Parameter(Mandatory)][string] $OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if (-not $IsWindows) { throw 'acceptance.windows-required' }
$installerRoot=Join-Path $PSScriptRoot '../ClashSharp/Installer'
Import-Module (Join-Path $installerRoot 'CandidateIdentity.psm1') -Force
Import-Module (Join-Path $installerRoot 'CandidateAcceptance.psm1') -Force
Import-Module (Join-Path $installerRoot 'PackagingContract.psm1') -Force
$release=Assert-ClashSharpOrdinaryPath -LiteralPath $ReleaseDirectory -RequireDirectory
$output=[IO.Path]::GetFullPath($OutputDirectory)
$parent=Assert-ClashSharpOrdinaryPath -LiteralPath (Split-Path -Parent $output) -RequireDirectory
if ((Test-Path -LiteralPath $output) -or $output.Equals($release,[StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($release.TrimEnd([char[]]@('\','/'))+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'acceptance.new-external-output-required'
}
$null=[IO.Directory]::CreateDirectory($output)
$runId=[Guid]::NewGuid().ToString('D')
$os=@(Get-CimInstance -ClassName Win32_OperatingSystem -Property Version,BuildNumber,ProductType)
if ($os.Count -ne 1) { throw 'acceptance.local-platform-unavailable' }
$platform=[pscustomobject]@{version=[string]$os[0].Version; build=[int]$os[0].BuildNumber;
    productType=[int]$os[0].ProductType; architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()}
$null=Get-ClashSharpWindowsAcceptancePlatform -Observation $platform
$receipt=Invoke-ClashSharpCandidateIdentity -IdentityPath $IdentityPath -ExpectedIdentitySha256 $ExpectedIdentitySha256 -ReleaseDirectory $release -Consume {
    param($identity)
    $info=[Diagnostics.ProcessStartInfo]::new()
    $info.FileName=Join-Path $release 'Setup.exe'
    $info.WorkingDirectory=$release
    $info.ArgumentList.Add('--verify-payload')
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$info
    $started=$false; $outTask=$null; $errTask=$null
    $start=[DateTimeOffset]::UtcNow.ToString('o')
    $journal=[ordered]@{runId=$runId;candidateIdentitySha256=$ExpectedIdentitySha256;startedAt=$start;
        platform=$platform;argument='--verify-payload';processId=$null;terminated=$false}
    try {
        if (-not $process.Start()) { throw 'acceptance.payload-start' }
        $started=$true; $journal.processId=$process.Id
        $journal|ConvertTo-Json -Compress|Set-Content -LiteralPath (Join-Path $output 'process.json') -Encoding utf8NoBOM
        $outTask=$process.StandardOutput.ReadToEndAsync(); $errTask=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(90000)) { throw 'acceptance.payload-timeout' }
        $finish=[DateTimeOffset]::UtcNow.ToString('o')
        $stdout=$outTask.GetAwaiter().GetResult(); $stderr=$errTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or $stdout.Length -gt 4096 -or -not [string]::IsNullOrWhiteSpace($stderr)) {
            throw 'acceptance.payload-process-result'
        }
        # Inspect property occurrences before ConvertFrom-Json can collapse duplicate names.
        $json=[Text.Json.JsonDocument]::Parse($stdout)
        try {
            if ($json.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'acceptance.payload-json' }
            $names=@($json.RootElement.EnumerateObject()|ForEach-Object Name)
            if ($names.Count -ne 8 -or @($names|Sort-Object -Unique).Count -ne 8) { throw 'acceptance.payload-fields' }
        } finally { $json.Dispose() }
        $audit=$stdout|ConvertFrom-Json -Depth 5
        Get-ClashSharpPayloadAuditDocument -IdentitySha256 $ExpectedIdentitySha256 -SourceCommit $identity.source.commit `
            -RunId $runId -StartedAt $start -FinishedAt $finish -Platform $platform -Audit $audit -CandidateFiles $identity.files
    } finally {
        try {
            if ($started) {
                if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                $journal.terminated=$true; $journal.exitCode=$process.ExitCode
                $journal.finishedAt=[DateTimeOffset]::UtcNow.ToString('o')
                if ($null -ne $outTask) {
                    $text=$outTask.GetAwaiter().GetResult()
                    [IO.File]::WriteAllText((Join-Path $output 'stdout.json'),$text.Substring(0,[Math]::Min(4096,$text.Length)),[Text.UTF8Encoding]::new($false))
                }
                if ($null -ne $errTask) {
                    $text=$errTask.GetAwaiter().GetResult()
                    [IO.File]::WriteAllText((Join-Path $output 'stderr.txt'),$text.Substring(0,[Math]::Min(4096,$text.Length)),[Text.UTF8Encoding]::new($false))
                }
            }
            $journal|ConvertTo-Json -Compress|Set-Content -LiteralPath (Join-Path $output 'process.json') -Encoding utf8NoBOM
        } finally { $process.Dispose() }
    }
}
# The shared verifier has completed its final inventory check before any passed receipt is saved.
$path=Join-Path $output 'payload-execution.json'
$bytes=[Text.UTF8Encoding]::new($false).GetBytes($receipt.canonicalJson)
$stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try { $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
[pscustomobject]@{kind='clashsharp-candidate-payload-execution'; candidateIdentitySha256=$ExpectedIdentitySha256;
    receiptSha256=$receipt.receiptSha256; platform=$receipt.document.platform.family;
    status='passed';installationVerified=$false;win11AcceptanceSatisfied=$false;promotionAuthorized=$false}
