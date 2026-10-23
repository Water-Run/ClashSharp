#Requires -Version 7.4
<#
.SYNOPSIS
    Exercises canonical candidate references, file ownership and independent digest consumption.
.DESCRIPTION
    Uses small synthetic bytes and explicitly synthetic signature observations for the pure policy.
    Does not execute or sign inputs, import trust, install packages or claim actual signatures valid.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'CandidateIdentity.psm1') -Force
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$consumer=Join-Path $repository 'Tools/Test-SignedCandidateIdentity.ps1'
$root=Join-Path ([IO.Path]::GetTempPath()) ('clashsharp-candidate-contract-'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($root)
$script:checks=0
function Assert-Condition([bool]$Value,[string]$Message){$script:checks++;if(-not $Value){throw $Message}}
function New-Fixture(){
 $directory=Join-Path $root ([Guid]::NewGuid().ToString('N')+'-release');$null=[IO.Directory]::CreateDirectory($directory)
 foreach($path in Get-ClashSharpCandidatePaths){$file=Join-Path $directory $path;$null=[IO.Directory]::CreateDirectory((Split-Path -Parent $file));[IO.File]::WriteAllText($file,'SYNTHETIC-NOT-EXECUTABLE:'+$path)}
 $signatures=[pscustomobject]@{
  installer=[pscustomobject]@{subject='CN=Synthetic';thumbprint=('A'*40);status='Valid';timestamp=$true}
  primary=[pscustomobject]@{subject='CN=Synthetic';thumbprint=('B'*40);status='Valid';timestamp=$false}
  dependency=[pscustomobject]@{subject='CN=Synthetic';thumbprint=('C'*40);status='Valid';timestamp=$true}
 }
 $files=Get-ClashSharpCandidateFileSnapshot -ReleaseDirectory $directory -Consume {param($f)$f}
 [pscustomobject]@{directory=$directory;files=$files;signatures=$signatures;commit=('a'*40);tree=('b'*40);ci=123L;ciHash=('c'*64)}
}
function New-Document($Fixture){Get-ClashSharpCandidateDocument -SourceCommit $Fixture.commit -SourceTree $Fixture.tree -CiRunId $Fixture.ci -CiEvidenceSha256 $Fixture.ciHash -Files $Fixture.files -Signatures $Fixture.signatures}
function Assert-Rejected([scriptblock]$Action,[string]$Code){
 $refused=$false;try{$null=& $Action}catch{if($Code -and -not $_.Exception.Message.Contains($Code)){throw};$refused=$true}
 Assert-Condition $refused ('Expected refusal: '+$Code)
}
try{
 $fixture=New-Fixture;$first=New-Document $fixture;$second=New-Document $fixture
 Assert-Condition ($first.identitySha256 -ceq $second.identitySha256 -and $first.canonicalJson -ceq $second.canonicalJson) 'Equivalent input must yield one stable reference.'
 $manifest=Join-Path $root 'candidate.json';[IO.File]::WriteAllText($manifest,$first.canonicalJson,[Text.UTF8Encoding]::new($false))
 Assert-Condition ((Get-FileHash $manifest).Hash.ToLowerInvariant() -ceq $first.identitySha256) 'Identity must hash the exact manifest bytes, including final LF.'
 $result=& $consumer -IdentityPath $manifest -ExpectedIdentitySha256 $first.identitySha256 -ReleaseDirectory $fixture.directory
 Assert-Condition ($result.fileBytesMatchedIdentity -and -not $result.signatureTrustRechecked -and -not $result.acceptanceBound -and -not $result.promotionAuthorized) 'Rechecking synthetic identity must not certify signatures or acceptance.'
 $original=$fixture.commit;$fixture.commit='d'*40;Assert-Condition ((New-Document $fixture).identitySha256 -cne $first.identitySha256) 'Source commit must bind the identity.';$fixture.commit=$original
 $original=$fixture.tree;$fixture.tree='d'*40;Assert-Condition ((New-Document $fixture).identitySha256 -cne $first.identitySha256) 'Source tree must bind the identity.';$fixture.tree=$original
 $fixture.ci++;Assert-Condition ((New-Document $fixture).identitySha256 -cne $first.identitySha256) 'CI run must bind the identity.';$fixture.ci--
 $original=$fixture.ciHash;$fixture.ciHash='d'*64;Assert-Condition ((New-Document $fixture).identitySha256 -cne $first.identitySha256) 'Consumed CI evidence digest must bind the identity.';$fixture.ciHash=$original
 $original=$fixture.signatures.installer.thumbprint;$fixture.signatures.installer.thumbprint='D'*40;Assert-Condition ((New-Document $fixture).identitySha256 -cne $first.identitySha256) 'Signing identity must bind the reference.';$fixture.signatures.installer.thumbprint=$original
 $fixture.files=@($fixture.files|Sort-Object path -Descending);Assert-Condition ((New-Document $fixture).identitySha256 -ceq $first.identitySha256) 'Enumeration order must not change identity.'
 $fixture.signatures.installer.status='NotTrusted';Assert-Rejected {New-Document $fixture} 'signature-identity';$fixture.signatures.installer.status='Valid'
 $fixture.signatures.installer.timestamp=$false;Assert-Rejected {New-Document $fixture} 'signature-identity';$fixture.signatures.installer.timestamp=$true
 $fixture.signatures.primary.timestamp='true';Assert-Rejected {New-Document $fixture} 'signature-identity';$fixture.signatures.primary.timestamp=$false
 $fixture.signatures.primary.thumbprint='0'*40;Assert-Rejected {New-Document $fixture} 'signature-identity';$fixture.signatures.primary.thumbprint='B'*40
 $fixture.commit='a'*40+"`n";Assert-Rejected {New-Document $fixture} 'identity-input';$fixture.commit='a'*40
 $bad=New-Fixture;$bad.files[0].path='../outside';Assert-Rejected {New-Document $bad} 'file-identity'
 $bad=New-Fixture;$bad.files[0].sha256='invalid';Assert-Rejected {New-Document $bad} 'file-identity'
 $bad=New-Fixture;$bad.files[0].length=0;Assert-Rejected {New-Document $bad} 'file-identity'
 $bad=New-Fixture;$bad.files+=$bad.files[0];Assert-Rejected {New-Document $bad} 'file-inventory'
 Assert-Rejected {& $consumer -IdentityPath $manifest -ExpectedIdentitySha256 ('0'*64) -ReleaseDirectory $fixture.directory} 'manifest-digest'
 [IO.File]::AppendAllText($manifest,' ');$newHash=(Get-FileHash $manifest).Hash.ToLowerInvariant()
 Assert-Rejected {& $consumer -IdentityPath $manifest -ExpectedIdentitySha256 $newHash -ReleaseDirectory $fixture.directory} 'manifest-noncanonical'
 $mutated=$first.canonicalJson.Replace('"promotionAuthorized":false','"promotionAuthorized":true');[IO.File]::WriteAllText($manifest,$mutated,[Text.UTF8Encoding]::new($false))
 Assert-Rejected {& $consumer -IdentityPath $manifest -ExpectedIdentitySha256 (Get-FileHash $manifest).Hash.ToLowerInvariant() -ReleaseDirectory $fixture.directory} 'manifest-state'
 [IO.File]::WriteAllText($manifest,$first.canonicalJson,[Text.UTF8Encoding]::new($false))
 $file=Join-Path $fixture.directory 'ClashSharp-Installer.exe';[IO.File]::AppendAllText($file,'tampered')
 Assert-Rejected {& $consumer -IdentityPath $manifest -ExpectedIdentitySha256 $first.identitySha256 -ReleaseDirectory $fixture.directory} 'file-changed'
 $script:callbackReached=$false
 Assert-Rejected {Invoke-ClashSharpCandidateIdentity -IdentityPath $manifest -ExpectedIdentitySha256 $first.identitySha256 -ReleaseDirectory $fixture.directory -Consume {$script:callbackReached=$true}} 'file-changed'
 Assert-Condition (-not $script:callbackReached) 'Changed candidate must be refused before a collector starts.'
 $held=New-Fixture;$heldDocument=New-Document $held;$heldManifest=Join-Path $root 'held.json'
 [IO.File]::WriteAllText($heldManifest,$heldDocument.canonicalJson,[Text.UTF8Encoding]::new($false))
 Invoke-ClashSharpCandidateIdentity -IdentityPath $heldManifest -ExpectedIdentitySha256 $heldDocument.identitySha256 -ReleaseDirectory $held.directory -Consume {
  param($candidate)
  Assert-Condition ($candidate.source.commit -ceq $held.commit) 'Collector must receive the verified source identity.'
  Assert-Rejected {[IO.File]::WriteAllText($heldManifest,'changed')} ''
  Assert-Rejected {[IO.File]::AppendAllText((Join-Path $held.directory 'payload/ClashSharp_1.0.0.0_x64.msix'),'changed')} ''
 }|Out-Null
 Assert-Rejected {Invoke-ClashSharpCandidateIdentity -IdentityPath $heldManifest -ExpectedIdentitySha256 $heldDocument.identitySha256 -ReleaseDirectory $held.directory -Consume {throw 'fixture.collector-failure'}} 'fixture.collector-failure'
 $writeLease=[IO.File]::Open($heldManifest,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::Read);$writeLease.Dispose()
 Assert-Condition $true 'Collector exceptions must release the manifest lease.'
 $fresh=New-Fixture
 Get-ClashSharpCandidateFileSnapshot -ReleaseDirectory $fresh.directory -Consume {
  param($files)
  Assert-Rejected {$s=[IO.File]::Open((Join-Path $fresh.directory 'ClashSharp-Installer.exe'),[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite);$s.Dispose()} ''
 }|Out-Null
 Assert-Condition $true 'All candidate files must stay read-locked for trusted consumption.'
 Assert-Rejected {Get-ClashSharpCandidateFileSnapshot -ReleaseDirectory $fresh.directory -Consume {param($files)[IO.File]::WriteAllText((Join-Path $fresh.directory 'extra.txt'),'unexpected')}} 'file count changed'
 $extra=Join-Path $fresh.directory 'extra.txt';Remove-Item -LiteralPath $extra
 [IO.File]::WriteAllText((Join-Path $fresh.directory 'private.pfx'),'not-a-key');Assert-Rejected {Get-ClashSharpCandidateFileSnapshot -ReleaseDirectory $fresh.directory -Consume {param($files)$files}} 'file-inventory'
 [pscustomobject]@{success=$true;checks=$script:checks;packagesExecuted=$false;signaturesActuallyCreatedOrVerified=$false}
}finally{
 $full=[IO.Path]::GetFullPath($root);$parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar))
 if(-not $full.StartsWith($parent+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -cnotmatch '^clashsharp-candidate-contract-[0-9a-f]{32}\z'){throw 'Candidate contract cleanup path refused'}
 $nodes=@((Get-Item -LiteralPath $full -Force))+@(Get-ChildItem -LiteralPath $full -Recurse -Force);if(@($nodes|Where-Object{$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Candidate contract cleanup reparse refused'}
 Remove-Item -LiteralPath $full -Recurse -Force
}
