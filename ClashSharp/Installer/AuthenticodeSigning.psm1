#Requires -Version 7.4
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'PackagingContract.psm1')

if (-not ('ClashSharp.Release.SigningOutput' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace ClashSharp.Release {
    public sealed class SigningCapture {
        public string Text { get; }
        public bool Truncated { get; }
        public SigningCapture(string text, bool truncated) { Text = text; Truncated = truncated; }
    }
    public static class SigningOutput {
        public static async Task<SigningCapture> DrainAsync(TextReader reader, int limit, CancellationToken token) {
            var kept = new StringBuilder();
            var buffer = new char[4096];
            bool truncated = false;
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) != 0) {
                int retain = System.Math.Min(count, limit - kept.Length);
                if (retain > 0) { kept.Append(buffer, 0, retain); }
                truncated |= retain != count;
            }
            return new SigningCapture(kept.ToString(), truncated);
        }
    }
}
'@
}

function Get-ClashSharpSigningSignature {
    <#
    .SYNOPSIS
        Reads Windows Authenticode state without executing the selected file.
    .DESCRIPTION
        Kept separate from process execution so the contract can exercise ambiguous signing outcomes.
    .PARAMETER LiteralPath
        Ordinary file whose signature is being inspected.
    #>
    param([Parameter(Mandatory)][string] $LiteralPath)
    Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $LiteralPath
}

function Wait-ClashSharpSigningInput {
    <#
    .SYNOPSIS
        Waits for one bounded signing input to become exclusively readable and writable.
    .DESCRIPTION
        Only sharing and lock violations are retried. No bytes are written. The exclusive lease
        is released before SignTool opens the file; final signing and verification remain mandatory.
    .PARAMETER LiteralPath
        Newly published ordinary unsigned executable.
    .PARAMETER TimeoutMilliseconds
        Maximum total wait, including hashing, before the caller refuses signing.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $LiteralPath,
        [ValidateRange(100,30000)][int] $TimeoutMilliseconds = 10000)
    $path = Assert-ClashSharpOrdinaryPath -LiteralPath $LiteralPath -RequireFile
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $attempts = 0
    while ($watch.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        $attempts++
        $lease = $null
        try {
            $lease = [IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
            if ($lease.Length -le 0 -or $lease.Length -gt 104857600) { throw 'signing.input-budget' }
            $length = $lease.Length
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($lease)).ToLowerInvariant()
            if ($watch.ElapsedMilliseconds -gt $TimeoutMilliseconds) { throw 'signing.input-busy' }
            return [pscustomobject]@{length=$length;sha256=$hash;attempts=$attempts;waitMilliseconds=$watch.ElapsedMilliseconds}
        } catch {
            $cause = $_.Exception.GetBaseException()
            $native = $cause.HResult -band 65535
            if ($cause -isnot [IO.IOException] -or $native -notin @(32,33)) { throw }
        } finally { if ($null -ne $lease) { $lease.Dispose() } }
        $remaining = $TimeoutMilliseconds - [int]$watch.ElapsedMilliseconds
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([Math]::Min(100,$remaining)) }
    }
    throw 'signing.input-busy'
}

function Invoke-ClashSharpSigningProcess {
    <#
    .SYNOPSIS
        Runs one owned trusted signing process with a deadline and drained output.
    .DESCRIPTION
        A timeout terminates the running held process tree, cancels owned reads and is never retryable.
        Output is diagnostic from a prevalidated Microsoft tool; it cannot select another command.
    .PARAMETER FilePath
        Exact pinned executable selected by the caller.
    .PARAMETER Arguments
        Native argument vector assembled by the trusted signing policy.
    .PARAMETER TimeoutMilliseconds
        Bound for this one native command.
    #>
    param([Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][ValidateRange(100,120000)][int] $TimeoutMilliseconds)
    $start = [Diagnostics.ProcessStartInfo]::new($FilePath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    $stdout = $null
    $stderr = $null
    $deadline = [Threading.CancellationTokenSource]::new($TimeoutMilliseconds)
    $operationFailed = $false
    try {
        $started = $process.Start()
        if (-not $started) { throw 'signing.process-start' }
        $stdout = [ClashSharp.Release.SigningOutput]::DrainAsync($process.StandardOutput,65536,$deadline.Token)
        $stderr = [ClashSharp.Release.SigningOutput]::DrainAsync($process.StandardError,65536,$deadline.Token)
        if (-not $process.WaitForExit($TimeoutMilliseconds)) { throw 'signing.process-timeout' }
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        if ($out.Truncated -or $err.Truncated) { throw 'signing.process-output-budget' }
        [pscustomobject]@{exitCode=$process.ExitCode;stdout=$out.Text;stderr=$err.Text}
    } catch {
        $operationFailed = $true
        if ($_.Exception.GetBaseException() -is [OperationCanceledException]) { throw 'signing.process-timeout' }
        throw
    } finally {
        try {
            $deadline.Cancel()
            if ($started -and -not $process.HasExited) {
                $process.Kill($true)
                if (-not $process.WaitForExit(5000)) { throw 'signing.process-termination-unconfirmed' }
            }
            foreach ($read in @($stdout,$stderr)) {
                if ($null -ne $read) {
                    try {
                        if (-not $read.IsCompleted -and -not $read.Wait(1000)) { throw 'signing.output-termination-unconfirmed' }
                        $null = $read.GetAwaiter().GetResult()
                    } catch {
                        $cause = $_.Exception.GetBaseException()
                        if (-not $operationFailed -or $cause -isnot [OperationCanceledException]) { throw }
                    }
                }
            }
        } finally { $process.Dispose(); $deadline.Dispose() }
    }
}

function Invoke-ClashSharpAuthenticodeSign {
    <#
    .SYNOPSIS
        Signs one fresh output and refuses uncertain or incomplete native outcomes.
    .DESCRIPTION
        The caller selects its pinned Windows SDK SignTool. Its trusted Microsoft identity is
        checked again and its read lease survives every child. A failed command is retryable only
        when the exact input bytes remain unchanged and Windows still reports NotSigned. Changed,
        signed, timed-out or unverifiable results are never automatically signed again.
    .PARAMETER LiteralPath
        Fresh unsigned Installer output, never an existing release artifact.
    .PARAMETER SignToolPath
        SDK-validated signtool.exe chosen by the controlled build.
    .PARAMETER SignerThumbprint
        Exact authorized code-signing certificate in the current owner's store.
    .PARAMETER TimestampUri
        RFC3161 endpoint parsed by the release endpoint policy.
    .PARAMETER TimeoutMilliseconds
        Per-command deadline; failure aborts rather than starting another uncertain command.
    .PARAMETER InputWaitMilliseconds
        Maximum exclusive-open readiness wait for each input observation.
    .PARAMETER MaximumAttempts
        Bounded number of signing attempts for proven unchanged unsigned input only.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $LiteralPath,
        [Parameter(Mandatory)][string] $SignToolPath,
        [Parameter(Mandatory)][ValidatePattern('^[0-9A-F]{40}\z')][string] $SignerThumbprint,
        [Parameter(Mandatory)][Uri] $TimestampUri,
        [ValidateRange(100,120000)][int] $TimeoutMilliseconds = 120000,
        [ValidateRange(100,30000)][int] $InputWaitMilliseconds = 10000,
        [ValidateRange(1,3)][int] $MaximumAttempts = 3)
    $path = Assert-ClashSharpOrdinaryPath -LiteralPath $LiteralPath -RequireFile
    $tool = Assert-ClashSharpOrdinaryPath -LiteralPath $SignToolPath -RequireFile
    if ([IO.Path]::GetFileName($tool) -cne 'signtool.exe' -or $path.Equals($tool,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'signing.tool-path'
    }
    $endpoint = Get-ClashSharpAuthenticodeTimestampUri -Value $TimestampUri.AbsoluteUri
    $toolLease = [IO.File]::Open($tool,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $toolSignature = Get-ClashSharpSigningSignature -LiteralPath $tool
        if ([string]$toolSignature.Status -cne 'Valid' -or $null -eq $toolSignature.SignerCertificate -or
            $null -eq $toolSignature.TimeStamperCertificate -or
            $toolSignature.SignerCertificate.Subject -cnotmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
            throw 'signing.tool-trust'
        }
        $baseline = Wait-ClashSharpSigningInput -LiteralPath $path -TimeoutMilliseconds $InputWaitMilliseconds
        $initial = Get-ClashSharpSigningSignature -LiteralPath $path
        if ([string]$initial.Status -cne 'NotSigned') { throw 'signing.fresh-unsigned-input-required' }
        $attempts = [Collections.Generic.List[object]]::new()
        for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
            $observedInput = Wait-ClashSharpSigningInput -LiteralPath $path -TimeoutMilliseconds $InputWaitMilliseconds
            if ($observedInput.sha256 -cne $baseline.sha256 -or $observedInput.length -ne $baseline.length) { throw 'signing.input-changed' }
            $result = Invoke-ClashSharpSigningProcess -FilePath $tool -TimeoutMilliseconds $TimeoutMilliseconds -Arguments @(
                'sign','/fd','SHA256','/sha1',$SignerThumbprint,'/tr',$endpoint.AbsoluteUri,'/td','SHA256',$path)
            if ($result.stdout) { Write-Host $result.stdout }
            if ($result.stderr) { Write-Host $result.stderr }
            $observed = Wait-ClashSharpSigningInput -LiteralPath $path -TimeoutMilliseconds $InputWaitMilliseconds
            $signature = Get-ClashSharpSigningSignature -LiteralPath $path
            $attempts.Add([ordered]@{attempt=$attempt;exitCode=$result.exitCode;sha256=$observed.sha256;signatureStatus=[string]$signature.Status})
            if ($result.exitCode -eq 0) {
                if ($observed.sha256 -ceq $baseline.sha256) { throw 'signing.unchanged-success' }
                if ([string]$signature.Status -cne 'Valid' -or $null -eq $signature.SignerCertificate -or
                    $signature.SignerCertificate.Thumbprint -cne $SignerThumbprint -or $null -eq $signature.TimeStamperCertificate) {
                    throw 'signing.final-signature'
                }
                $signedLease = [IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
                try {
                    $sealedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($signedLease)).ToLowerInvariant()
                    if ($sealedHash -cne $observed.sha256 -or $signedLease.Length -ne $observed.length) { throw 'signing.signed-output-changed' }
                    $verification = Invoke-ClashSharpSigningProcess -FilePath $tool -TimeoutMilliseconds $TimeoutMilliseconds -Arguments @('verify','/pa','/all','/tw','/v',$path)
                    if ($verification.stdout) { Write-Host $verification.stdout }
                    if ($verification.stderr) { Write-Host $verification.stderr }
                    if ($verification.exitCode -ne 0) { throw 'signing.native-verification' }
                    $finalSignature = Get-ClashSharpSigningSignature -LiteralPath $path
                    if ([string]$finalSignature.Status -cne 'Valid' -or $null -eq $finalSignature.SignerCertificate -or
                        $finalSignature.SignerCertificate.Thumbprint -cne $SignerThumbprint -or $null -eq $finalSignature.TimeStamperCertificate) {
                        throw 'signing.final-signature'
                    }
                    return [pscustomobject]@{success=$true;attempts=$attempts.ToArray();inputSha256=$baseline.sha256;signedSha256=$sealedHash;nativeVerificationPassed=$true}
                } finally { $signedLease.Dispose() }
            }
            if ($observed.sha256 -cne $baseline.sha256 -or $observed.length -ne $baseline.length -or [string]$signature.Status -cne 'NotSigned') {
                throw 'signing.uncertain-output'
            }
            if ($attempt -eq $MaximumAttempts) { throw 'signing.unchanged-input-attempts-exhausted' }
            Start-Sleep -Milliseconds 250
        }
    } finally { $toolLease.Dispose() }
}

Export-ModuleMember -Function Wait-ClashSharpSigningInput, Invoke-ClashSharpAuthenticodeSign
