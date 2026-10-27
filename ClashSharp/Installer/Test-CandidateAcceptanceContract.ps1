#Requires -Version 7.4
<#
.SYNOPSIS
    Exercises platform separation and narrowly scoped candidate payload execution receipts.
.DESCRIPTION
    Uses synthetic observations only. Never executes packages, probes or modifies host settings,
    installs a product or claims an actual Win11, recovery or publication acceptance.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'CandidateAcceptance.psm1') -Force
$script:checks=0
function Assert-Condition([bool]$Value,[string]$Message){$script:checks++;if(-not $Value){throw $Message}}
function Assert-Rejected([scriptblock]$Action,[string]$Code){
 $refused=$false;try{$null=& $Action}catch{if($Code -and -not $_.Exception.Message.Contains($Code)){throw};$refused=$true}
 Assert-Condition $refused ('Expected refusal: '+$Code)
}
function New-Fixture(){
 [pscustomobject]@{
  identity=('a'*64);commit=('b'*40);runId='12345678-1234-4567-8901-123456789abc'
  start='2026-10-24T01:00:00.0000000+00:00';finish='2026-10-24T01:00:02.0000000+00:00'
  platform=[pscustomobject]@{version='10.0.26100';build=26100;productType=3;architecture='X64'}
  audit=[pscustomobject]@{schemaVersion=1;status='passed';publishedExecutable='Setup.exe';packageVersion='1.0.0.0';payloadSha256=('c'*64);fileCount=4;totalBytes=123456L;machineFileCount=7}
  files=@(
   [ordered]@{path='payload/ClashSharp_1.0.0.0_x64.msix';length=123000L;sha256=('c'*64)},
   [ordered]@{path='payload/ClashSharp_TemporaryKey.cer';length=100L;sha256=('d'*64)},
   [ordered]@{path='payload/payload-provenance.json';length=156L;sha256=('e'*64)},
   [ordered]@{path='payload/Dependencies/x64/Microsoft.WindowsAppRuntime.1.8.msix';length=200L;sha256=('f'*64)}
  )
 }
}
function New-Document($Fixture){Get-ClashSharpPayloadAuditDocument -IdentitySha256 $Fixture.identity -SourceCommit $Fixture.commit -RunId $Fixture.runId -StartedAt $Fixture.start -FinishedAt $Fixture.finish -Platform $Fixture.platform -Audit $Fixture.audit -CandidateFiles $Fixture.files}
$f=New-Fixture;$first=New-Document $f;$second=New-Document $f
Assert-Condition ($first.canonicalJson -ceq $second.canonicalJson -and $first.receiptSha256 -ceq $second.receiptSha256) 'Equivalent observations must have stable receipt bytes.'
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($first.canonicalJson))).ToLowerInvariant()
Assert-Condition ($hash -ceq $first.receiptSha256 -and $first.canonicalJson.EndsWith("`n")) 'Receipt digest must identify exact UTF8 bytes including LF.'
Assert-Condition ($first.document.platform.family -ceq 'windows-server' -and -not $first.document.installationVerified -and -not $first.document.win11AcceptanceSatisfied -and -not $first.document.promotionAuthorized) 'Server 26100 read-only evidence cannot satisfy Win11 or installation gates.'
$f.platform.productType=2;Assert-Condition ((New-Document $f).document.platform.family -ceq 'windows-server') 'Domain controller is Server, not client.'
$f.platform.productType=1;$win11=New-Document $f
Assert-Condition ($win11.document.platform.family -ceq 'windows11' -and -not $win11.document.win11AcceptanceSatisfied) 'Even a real Win11 host needs separate installation/UI acceptance.'
$f.platform.version='10.0.19045';$f.platform.build=19045;Assert-Condition ((New-Document $f).document.platform.family -ceq 'windows10') 'Windows 10 must remain distinct.'
foreach($field in @('identity','commit','runId')){
 $f=New-Fixture;$f.$field=if($field -eq 'identity'){'d'*64}elseif($field -eq 'commit'){'d'*40}else{'22345678-1234-4567-8901-123456789abc'}
 Assert-Condition ((New-Document $f).receiptSha256 -cne $first.receiptSha256) ('Receipt must bind '+$field)
}
$f=New-Fixture;$f.identity+='x';Assert-Rejected {New-Document $f} 'acceptance.reference'
$f=New-Fixture;$f.identity='0'*64;Assert-Rejected {New-Document $f} 'acceptance.reference'
$f=New-Fixture;$f.runId=[Guid]::Empty.ToString();Assert-Rejected {New-Document $f} 'acceptance.reference'
$f=New-Fixture;$f.platform.build='26100';Assert-Rejected {New-Document $f} 'acceptance.platform-number'
$f=New-Fixture;$f.platform.build=26100.5;Assert-Rejected {New-Document $f} 'acceptance.platform-number'
$f=New-Fixture;$f.platform.version='10.0.22631';Assert-Rejected {New-Document $f} 'acceptance.platform-identity'
$f=New-Fixture;$f.platform.productType=0;Assert-Rejected {New-Document $f} 'acceptance.platform-identity'
$f=New-Fixture;$f.platform.version+="`n";Assert-Rejected {New-Document $f} 'acceptance.platform-identity'
$f=New-Fixture;$f.platform.architecture='X128';Assert-Rejected {New-Document $f} 'acceptance.platform-identity'
$f=New-Fixture;$f.finish='2026-10-24T00:59:59.0000000+00:00';Assert-Rejected {New-Document $f} 'acceptance.execution-time'
$f=New-Fixture;$f.finish='2026-10-24T01:03:00.0000000+00:00';Assert-Rejected {New-Document $f} 'acceptance.execution-time'
$f=New-Fixture;$f.start='2026-10-24T09:00:00.0000000+08:00';Assert-Rejected {New-Document $f} 'acceptance.execution-time'
$f=New-Fixture;$f.audit.status='failed';Assert-Rejected {New-Document $f} 'acceptance.payload-result'
$f=New-Fixture;$f.audit.publishedExecutable='ClashSharp-Installer-Development-Unsigned.exe';Assert-Rejected {New-Document $f} 'acceptance.payload-result'
$f=New-Fixture;$f.audit.machineFileCount=10001;Assert-Rejected {New-Document $f} 'acceptance.payload-result'
$f=New-Fixture;$f.audit.fileCount='4';Assert-Rejected {New-Document $f} 'acceptance.payload-number'
$f=New-Fixture;$f.audit.schemaVersion=$true;Assert-Rejected {New-Document $f} 'acceptance.payload-number'
$f=New-Fixture;$f.audit.totalBytes=0;Assert-Rejected {New-Document $f} 'acceptance.payload-result'
$f=New-Fixture;$f.audit.payloadSha256='0'*64;Assert-Rejected {New-Document $f} 'acceptance.payload-result'
$f=New-Fixture;$f.audit|Add-Member NoteProperty promotionAuthorized $true;Assert-Rejected {New-Document $f} 'acceptance.payload-fields'
$f=New-Fixture;$f.audit.totalBytes++;Assert-Rejected {New-Document $f} 'acceptance.payload-candidate-mismatch'
$f=New-Fixture;$f.audit.payloadSha256='d'*64;Assert-Rejected {New-Document $f} 'acceptance.payload-candidate-mismatch'
$f=New-Fixture;$f.audit.fileCount=5;Assert-Rejected {New-Document $f} 'acceptance.payload-candidate-mismatch'
$f=New-Fixture;$f.files=@($f.files|ForEach-Object{[pscustomobject]$_});Assert-Condition ((New-Document $f).receiptSha256 -ceq $first.receiptSha256) 'Canonical dictionary and JSON property observations must bind the same payload totals.'
[pscustomobject]@{success=$true;checks=$script:checks;syntheticObservations=$true;packagesExecuted=$false;hostPlatformProbed=$false;promotionAuthorized=$false}
