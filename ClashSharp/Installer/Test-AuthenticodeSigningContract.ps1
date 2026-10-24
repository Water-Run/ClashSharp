#Requires -Version 7.4
<#
.SYNOPSIS
    Verifies bounded signing readiness and refusal to replay ambiguous signing writes.
.DESCRIPTION
    Uses ordinary temporary files and controlled observations, never a certificate or package.
    Real child probes cover bounded stream draining and held-process timeout cleanup only.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$module=Import-Module (Join-Path $PSScriptRoot 'AuthenticodeSigning.psm1') -Force -PassThru
$script:checks=0
function Assert-Condition([bool]$Value,[string]$Message){$script:checks++;if(-not $Value){throw $Message}}
function Assert-Refused([scriptblock]$Action,[string]$Code){$rejected=$false;try{$null=& $Action}catch{if(-not $_.Exception.Message.Contains($Code)){throw};$rejected=$true};Assert-Condition $rejected ('Expected refusal: '+$Code)}
$parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$root=Join-Path $parent ('clashsharp-signing-contract-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
try{
 $tool=Join-Path $root 'signtool.exe';$target=Join-Path $root 'fresh.exe'
 [IO.File]::WriteAllText($tool,'controlled tool fixture');[IO.File]::WriteAllText($target,'unsigned fixture')
 $before=(Get-FileHash $target).Hash
 $ready=Wait-ClashSharpSigningInput -LiteralPath $target -TimeoutMilliseconds 1000
 Assert-Condition ($ready.sha256 -ceq $before.ToLowerInvariant() -and $ready.length -eq (Get-Item $target).Length) 'Readiness must hash exact unmodified input.'
 Assert-Condition ((Get-FileHash $target).Hash -ceq $before) 'Readiness must never write input bytes.'
 $held=[IO.File]::Open($target,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try{$watch=[Diagnostics.Stopwatch]::StartNew();Assert-Refused {Wait-ClashSharpSigningInput -LiteralPath $target -TimeoutMilliseconds 150} 'signing.input-busy';Assert-Condition ($watch.Elapsed.TotalSeconds -lt 2) 'A sharing conflict must have a real bound.'}finally{$held.Dispose()}
 $shell=(Get-Command pwsh -CommandType Application|Select-Object -First 1).Source
 $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('[Console]::Out.Write("probe");[Console]::Error.Write("diagnostic");exit 7'))
 $native=& $module {param($path,$command) Invoke-ClashSharpSigningProcess -FilePath $path -Arguments @('-NoProfile','-NonInteractive','-EncodedCommand',$command) -TimeoutMilliseconds 10000} $shell $encoded
 Assert-Condition ($native.exitCode -eq 7 -and $native.stdout -ceq 'probe' -and $native.stderr -ceq 'diagnostic') 'Native result must preserve exit and separate streams.'
 $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('Start-Sleep -Seconds 30'))
 $watch.Restart()
 Assert-Refused {& $module {param($path,$command) Invoke-ClashSharpSigningProcess -FilePath $path -Arguments @('-NoProfile','-NonInteractive','-EncodedCommand',$command) -TimeoutMilliseconds 200} $shell $encoded} 'signing.process-timeout'
 Assert-Condition ($watch.Elapsed.TotalSeconds -lt 8) 'Timeout cleanup must terminate the owned child within grace.'
 $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('[Console]::Out.Write((''x''*100000));[Console]::Error.Write((''y''*100000))'))
 Assert-Refused {& $module {param($path,$command) Invoke-ClashSharpSigningProcess -FilePath $path -Arguments @('-NoProfile','-NonInteractive','-EncodedCommand',$command) -TimeoutMilliseconds 10000} $shell $encoded} 'signing.process-output-budget'
 $childScript=Join-Path $root 'pipe-holder.ps1';$childIdentity=Join-Path $root 'pipe-holder.identity'
 $childText='[IO.File]::WriteAllText('''+$childIdentity.Replace("'","''")+''',([string]$PID+'',''+[Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().Ticks));[Console]::Out.Write("held");Start-Sleep -Seconds 30'
 [IO.File]::WriteAllText($childScript,$childText,[Text.UTF8Encoding]::new($false))
 $parentText='$info=[Diagnostics.ProcessStartInfo]::new('''+$shell.Replace("'","''")+''');$info.UseShellExecute=$false;$info.CreateNoWindow=$true;foreach($arg in @(''-NoProfile'',''-NonInteractive'',''-File'','''+$childScript.Replace("'","''")+''')){$info.ArgumentList.Add($arg)};$p=[Diagnostics.Process]::Start($info);while(-not(Test-Path '''+$childIdentity.Replace("'","''")+''')){Start-Sleep -Milliseconds 10};exit 0'
 $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($parentText))
 try{
  $watch.Restart()
  Assert-Refused {& $module {param($path,$command) Invoke-ClashSharpSigningProcess -FilePath $path -Arguments @('-NoProfile','-NonInteractive','-EncodedCommand',$command) -TimeoutMilliseconds 2000} $shell $encoded} 'signing.process-timeout'
  Assert-Condition ($watch.Elapsed.TotalSeconds -lt 8) 'An exited root cannot make inherited output drains unbounded.'
 }finally{
  if(Test-Path $childIdentity){$parts=[IO.File]::ReadAllText($childIdentity).Split(',');$owned=Get-Process -Id ([int]$parts[0]) -ErrorAction SilentlyContinue;if($owned){$null=$owned.Handle;if($owned.StartTime.ToUniversalTime().Ticks -ne [long]$parts[1]){throw 'Fixture child identity changed'};$owned.Kill();$null=$owned.WaitForExit(5000);$owned.Dispose()}}
 }

 # Controlled policy fixtures do not grant actual Microsoft or candidate trust.
 & $module {
  function script:Get-ClashSharpSigningSignature {
   param($LiteralPath)
   if($LiteralPath -ceq $script:Fixture.tool){return [pscustomobject]@{Status=$script:Fixture.toolStatus;SignerCertificate=[pscustomobject]@{Subject='CN=fixture, O=Microsoft Corporation'};TimeStamperCertificate=[pscustomobject]@{fixture=$true}}}
   [pscustomobject]@{Status=$script:Fixture.status;SignerCertificate=[pscustomobject]@{Thumbprint=$script:Fixture.thumb};TimeStamperCertificate=if($script:Fixture.timestamp){[pscustomobject]@{fixture=$true}}else{$null}}
  }
  function script:Invoke-ClashSharpSigningProcess {
   param($FilePath,$Arguments,$TimeoutMilliseconds)
   if($Arguments[0] -ceq 'verify'){
    $script:Fixture.verifyCalls++
    try{$lease=[IO.File]::Open($script:Fixture.target,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::None);$lease.Dispose();throw 'Signed output lease missing'}catch{if($_.Exception.Message -ceq 'Signed output lease missing'){throw};$script:Fixture.signedOutputLocked=$true}
    return [pscustomobject]@{exitCode=if($script:Fixture.mode -ceq 'verifyFailure'){1}else{0};stdout='';stderr=''}
   }
   $script:Fixture.calls++
   $script:Fixture.arguments=@($Arguments)
   try{$lease=[IO.File]::Open($script:Fixture.tool,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::None);$lease.Dispose();throw 'Tool lease missing'}catch{if($_.Exception.Message -ceq 'Tool lease missing'){throw};$script:Fixture.toolLocked=$true}
   switch($script:Fixture.mode){
    timeout {throw 'signing.process-timeout'}
    exhausted {return [pscustomobject]@{exitCode=1;stdout='';stderr='known failure without a write'}}
    transient {if($script:Fixture.calls -eq 1){return [pscustomobject]@{exitCode=1;stdout='';stderr='unchanged unsigned fixture'}}}
    partial {[IO.File]::AppendAllText($script:Fixture.target,'partial');return [pscustomobject]@{exitCode=1;stdout='';stderr='partial mutation'}}
    signedFailure {$script:Fixture.status='Valid';return [pscustomobject]@{exitCode=1;stdout='';stderr='signature observed despite failed command'}}
    unchangedSuccess {$script:Fixture.status='Valid';$script:Fixture.timestamp=$true;return [pscustomobject]@{exitCode=0;stdout='';stderr=''}}
   }
   [IO.File]::AppendAllText($script:Fixture.target,'signed fixture')
   $script:Fixture.status='Valid';$script:Fixture.timestamp=$script:Fixture.mode -ne 'missingTimestamp'
   if($script:Fixture.mode -ceq 'wrongSigner'){$script:Fixture.thumb='B'*40}
   [pscustomobject]@{exitCode=0;stdout='';stderr=''}
  }
 }
 foreach($mode in @('success','transient','partial','signedFailure','timeout','exhausted','missingTimestamp','wrongSigner','verifyFailure','unchangedSuccess','alreadySigned','untrustedTool')){
  [IO.File]::WriteAllText($target,'unsigned fixture')
  $fixture=[pscustomobject]@{tool=$tool;target=$target;toolStatus=if($mode -ceq 'untrustedTool'){'NotSigned'}else{'Valid'};status=if($mode -ceq 'alreadySigned'){'Valid'}else{'NotSigned'};thumb=('A'*40);timestamp=$false;mode=$mode;calls=0;verifyCalls=0;arguments=@();toolLocked=$false;signedOutputLocked=$false}
  & $module {param($value) $script:Fixture=$value} $fixture
  $action={Invoke-ClashSharpAuthenticodeSign -LiteralPath $target -SignToolPath $tool -SignerThumbprint ('A'*40) -TimestampUri 'https://tsa.example.test/rfc3161' -InputWaitMilliseconds 1000 -TimeoutMilliseconds 1000}
  switch($mode){
   success {$result=& $action;Assert-Condition ($result.success -and $fixture.calls -eq 1 -and $fixture.toolLocked -and $fixture.signedOutputLocked -and $fixture.verifyCalls -eq 1 -and $result.nativeVerificationPassed) 'A successful command must retain tool/output ownership and verify the final signature.'}
   transient {$result=& $action;Assert-Condition ($result.success -and $fixture.calls -eq 2 -and $result.attempts.Count -eq 2) 'Only a proven unchanged unsigned failure may retry.'}
   partial {Assert-Refused $action 'signing.uncertain-output';Assert-Condition ($fixture.calls -eq 1) 'Changed bytes must never be signed a second time.'}
   signedFailure {Assert-Refused $action 'signing.uncertain-output';Assert-Condition ($fixture.calls -eq 1) 'An observed signature must prevent retries even if fixture bytes are unchanged.'}
   timeout {Assert-Refused $action 'signing.process-timeout';Assert-Condition ($fixture.calls -eq 1) 'A timeout cannot be retried as unchanged input.'}
   exhausted {Assert-Refused $action 'signing.unchanged-input-attempts-exhausted';Assert-Condition ($fixture.calls -eq 3) 'Retry count must be bounded.'}
   missingTimestamp {Assert-Refused $action 'signing.final-signature';Assert-Condition ($fixture.calls -eq 1) 'A zero exit without timestamp is not successful signing.'}
   wrongSigner {Assert-Refused $action 'signing.final-signature';Assert-Condition ($fixture.calls -eq 1) 'A different signer cannot satisfy the gate.'}
   verifyFailure {Assert-Refused $action 'signing.native-verification';Assert-Condition ($fixture.calls -eq 1 -and $fixture.verifyCalls -eq 1) 'A failed read-only verification cannot trigger another signing write.'}
   unchangedSuccess {Assert-Refused $action 'signing.unchanged-success';Assert-Condition ($fixture.calls -eq 1 -and $fixture.verifyCalls -eq 0) 'A zero exit without changed signed bytes is inconsistent evidence.'
   }
   alreadySigned {Assert-Refused $action 'signing.fresh-unsigned-input-required';Assert-Condition ($fixture.calls -eq 0) 'Existing signatures must be refused before native execution.'}
   untrustedTool {Assert-Refused $action 'signing.tool-trust';Assert-Condition ($fixture.calls -eq 0) 'Untrusted tools must not execute.'}
  }
  if($fixture.calls -gt 0){Assert-Condition ($fixture.arguments[0] -ceq 'sign' -and $fixture.arguments[-1] -ceq $target -and $fixture.arguments -contains '/tr') 'Only the fixed signing argument vector may execute.'}
 }
 [pscustomobject]@{success=$true;checks=$script:checks;realChildProbes=$true;realSigningPerformed=$false;certificatesImported=$false}
}finally{
 $resolved=& $module {param($path) Assert-ClashSharpOrdinaryPath -LiteralPath $path -RequireDirectory} $root
 if([IO.Path]::GetDirectoryName($resolved) -cne $parent -or -not [IO.Path]::GetFileName($resolved).StartsWith('clashsharp-signing-contract-',[StringComparison]::Ordinal)){throw 'Fixture cleanup escaped'}
 if(Get-ChildItem $resolved -Recurse -Force|Where-Object {$_.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)}){throw 'Fixture cleanup encountered reparse point'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
