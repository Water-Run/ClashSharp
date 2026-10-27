#Requires -Version 7.4
<#
.SYNOPSIS
    Exercises exact candidate transport with real ZIP streams and synthetic candidate bytes.
.DESCRIPTION
    Covers deterministic transfer, independent references, hostile inventory and entry lengths,
    preserved failed output, and bounded copying. Never signs, executes or installs a package.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'CandidateArchive.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'CandidateIdentity.psm1') -Force
$module=Get-Module CandidateArchive
$root=Join-Path ([IO.Path]::GetTempPath()) ('clashsharp-transport-contract-'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($root)
$script:checks=0
function Assert-Condition([bool]$Value,[string]$Message){$script:checks++;if(-not $Value){throw $Message}}
function Assert-Rejected([scriptblock]$Action,[string]$Code){
 $rejected=$false;try{$null=& $Action}catch{if($Code -and -not $_.Exception.Message.Contains($Code)){throw};$rejected=$true}
 Assert-Condition $rejected ('Expected refusal: '+$Code)
}
function Get-Digest([string]$Path){(Get-FileHash -LiteralPath $Path).Hash.ToLowerInvariant()}
function New-Path([string]$Suffix){Join-Path $root ([Guid]::NewGuid().ToString('N')+$Suffix)}
function New-Fixture(){
 $release=New-Path '-release';$null=[IO.Directory]::CreateDirectory($release)
 foreach($name in Get-ClashSharpCandidatePaths){
  $path=Join-Path $release $name;$null=[IO.Directory]::CreateDirectory((Split-Path -Parent $path))
  [IO.File]::WriteAllText($path,'SYNTHETIC-NONEXECUTABLE:'+ $name,[Text.UTF8Encoding]::new($false))
 }
 $signatures=[pscustomobject]@{
  installer=[pscustomobject]@{subject='CN=Synthetic';thumbprint=('A'*40);status='Valid';timestamp=$true}
  primary=[pscustomobject]@{subject='CN=Synthetic';thumbprint=('B'*40);status='Valid';timestamp=$false}
  dependency=[pscustomobject]@{subject='CN=Synthetic';thumbprint=('C'*40);status='Valid';timestamp=$true}
 }
 $files=Get-ClashSharpCandidateFileSnapshot -ReleaseDirectory $release -Consume {param($values)$values}
 $doc=Get-ClashSharpCandidateDocument -SourceCommit ('a'*40) -SourceTree ('b'*40) -CiRunId 123 -CiEvidenceSha256 ('c'*64) -Files $files -Signatures $signatures
 $identity=New-Path '.json';[IO.File]::WriteAllText($identity,$doc.canonicalJson,[Text.UTF8Encoding]::new($false))
 [pscustomobject]@{release=$release;identity=$identity;hash=$doc.identitySha256;files=$files}
}
function New-Archive([object]$Fixture,[string]$Path){
 New-ClashSharpCandidateArchive -IdentityPath $Fixture.identity -ExpectedIdentitySha256 $Fixture.hash -ReleaseDirectory $Fixture.release -OutputPath $Path
}
function Receive-Archive([string]$Path,[string]$Digest,[string]$Identity,[string]$Destination){
 Expand-ClashSharpCandidateArchive -ArchivePath $Path -ExpectedArchiveSha256 $Digest -ExpectedIdentitySha256 $Identity -Destination $Destination
}
function New-ChangedArchive([string]$Original,[string[]]$Names,[int]$ChangedEntry=-1,[bool]$Reparse=$false){
 $path=New-Path '.zip'
 $inputZip=[IO.Compression.ZipFile]::OpenRead($Original)
 $outputStream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
 $outputZip=[IO.Compression.ZipArchive]::new($outputStream,[IO.Compression.ZipArchiveMode]::Create)
 try{
  for($i=0;$i -lt $Names.Count;$i++){
   $entry=$outputZip.CreateEntry($Names[$i],[IO.Compression.CompressionLevel]::NoCompression)
   $entry.LastWriteTime=[DateTimeOffset]::new(1980,1,1,0,0,0,[TimeSpan]::Zero)
   $entry.ExternalAttributes=if($Reparse -and $i -eq 1){[int][IO.FileAttributes]::ReparsePoint}else{0}
   $writer=$entry.Open()
   try{
    if($i -eq $ChangedEntry){$bytes=[Text.Encoding]::UTF8.GetBytes('changed non-executable bytes');$writer.Write($bytes)}
    else{$reader=$inputZip.Entries[[Math]::Min($i,$inputZip.Entries.Count-1)].Open();try{$reader.CopyTo($writer)}finally{$reader.Dispose()}}
   }finally{$writer.Dispose()}
  }
 }finally{$outputZip.Dispose();$outputStream.Dispose();$inputZip.Dispose()}
 return $path
}
function Set-FirstDeclaredLength([string]$Original,[uint32]$Length){
 $path=New-Path '.zip';$bytes=[IO.File]::ReadAllBytes($Original)
 # This controlled fixture has no EOCD comment and no ZIP64 records.
 $central=[BitConverter]::ToInt32($bytes,$bytes.Length-6)
 if([BitConverter]::ToUInt32($bytes,$central) -ne 0x02014b50){throw 'Fixture central directory unavailable'}
 [BitConverter]::GetBytes($Length).CopyTo($bytes,$central+24)
 [IO.File]::WriteAllBytes($path,$bytes);return $path
}
try{
 $fixture=New-Fixture;$archive=New-Path '.zip';$first=New-Archive $fixture $archive
 Assert-Condition ($first.archiveSha256 -ceq (Get-Digest $archive) -and $first.candidateIdentitySha256 -ceq $fixture.hash -and $first.archiveEntries -eq 9) 'Producer must return the exact archive and candidate digests.'
 Assert-Condition ($first.candidateBytesPreserved -and -not $first.signatureTrustRechecked -and -not $first.acceptanceBound -and -not $first.promotionAuthorized) 'Transport is not signing trust or acceptance.'
 $other=New-Path '.zip';$second=New-Archive $fixture $other
 Assert-Condition ($first.archiveSha256 -ceq $second.archiveSha256) 'The same source bytes must produce the same stored archive.'
 $destination=New-Path '-received';$received=Receive-Archive $archive $first.archiveSha256 $fixture.hash $destination
 Assert-Condition ($received.archiveSha256 -ceq $first.archiveSha256 -and $received.candidateIdentitySha256 -ceq $fixture.hash -and $received.candidateFiles -eq 8) 'Receiver must retain both producer references.'
 Assert-Condition (-not $received.signatureTrustRechecked -and -not $received.acceptanceBound -and -not $received.promotionAuthorized) 'Receiving cannot authorize installation or promotion.'
 Assert-Condition ((Get-Digest (Join-Path $destination 'candidate-identity.json')) -ceq $fixture.hash) 'Identity bytes must survive transport.'
 foreach($file in $fixture.files){Assert-Condition ((Get-Digest (Join-Path "$destination/release" $file.path)) -ceq $file.sha256) ('Original file bytes must survive: '+$file.path)}
 Assert-Rejected {New-Archive $fixture $archive} 'transport.output-exists'
 Assert-Condition ((Get-Digest $archive) -ceq $first.archiveSha256) 'Existing archive must remain unchanged.'
 Assert-Rejected {Receive-Archive $archive $first.archiveSha256 $fixture.hash $destination} 'transport.output-exists'
 $wrong=New-Path '-wrong';Assert-Rejected {Receive-Archive $archive ('d'*64) $fixture.hash $wrong} 'transport.archive-digest'
 Assert-Condition (-not (Test-Path $wrong)) 'Digest rejection must happen before destination creation.'
 $wrongIdentity=New-Path '-wrong-id';Assert-Rejected {Receive-Archive $archive $first.archiveSha256 ('d'*64) $wrongIdentity} 'candidate.manifest-digest'
 Assert-Condition (Test-Path "$wrongIdentity/candidate-identity.json") 'Rejected extraction must remain observable.'
 Assert-Rejected {Receive-Archive $archive $first.archiveSha256 $fixture.hash $wrongIdentity} 'transport.output-exists'
 $output=New-Path '.zip';[IO.File]::WriteAllText(($output+'.partial'),'retained failed attempt')
 Assert-Rejected {New-Archive $fixture $output} 'transport.output-exists'
 Assert-Condition ((Get-Content ($output+'.partial') -Raw) -ceq 'retained failed attempt' -and -not (Test-Path $output)) 'Existing partial attempt must be preserved.'
 Assert-Rejected {New-Archive $fixture (Join-Path $fixture.release 'nested.zip')} 'transport.external-output-required'
 Assert-Rejected {New-Archive $fixture 'relative.zip'} 'transport.absolute-output-required'
 $untrusted=New-Path '.zip';Assert-Rejected {New-ClashSharpCandidateArchive -IdentityPath $fixture.identity -ExpectedIdentitySha256 ('d'*64) -ReleaseDirectory $fixture.release -OutputPath $untrusted} 'candidate.manifest-digest'
 Assert-Condition (-not (Test-Path $untrusted) -and -not (Test-Path ($untrusted+'.partial'))) 'Wrong producer identity must not create an archive attempt.'
 foreach($leaf in @('bad.zip:stream','bad.zip.','bad.zip ','NUL.zip')){Assert-Rejected {New-Archive $fixture (Join-Path $root $leaf)} 'transport.output-name'}
 $zip=[IO.Compression.ZipFile]::OpenRead($archive);try{$names=@($zip.Entries.FullName);$identityLength=$zip.Entries[0].Length}finally{$zip.Dispose()}
 Assert-Condition ($names -ccontains 'Setup.exe' -and $names -ccontains 'Setup.exe.sha256') 'The install entry and its checksum must be directly at the ZIP root.'
 Assert-Condition (@($names|Where-Object{$_ -like 'release/*'}).Count -eq 0) 'The downloaded ZIP must not introduce a release directory.'
 $ordinary=New-Path '-ordinary-unzip';[IO.Compression.ZipFile]::ExtractToDirectory($archive,$ordinary)
 Assert-Condition (Test-Path -LiteralPath (Join-Path $ordinary 'Setup.exe') -PathType Leaf) 'A standard ZIP extraction must expose Setup.exe immediately.'
 Assert-Condition ((Get-Digest (Join-Path $ordinary 'Setup.exe')) -ceq (Get-Digest (Join-Path $fixture.release 'Setup.exe'))) 'Root placement must preserve the exact signed executable bytes.'
 Assert-Condition (Test-Path -LiteralPath (Join-Path $ordinary 'payload/ClashSharp_1.0.0.0_x64.msix') -PathType Leaf) 'The payload must remain next to Setup.exe after standard extraction.'
 foreach($name in @('../escape.txt','payload/../escape.txt','/escape.txt','C:/escape.txt','release/Setup.exe','payload\Setup.exe','SETUP.exe','ClashSharp-Installer.exe',$names[0])){
  $changed=[string[]]$names.Clone();$changed[1]=$name;$bad=New-ChangedArchive $archive $changed
  $out=New-Path '-refused';Assert-Rejected {Receive-Archive $bad (Get-Digest $bad) $fixture.hash $out} 'transport.entry-identity'
  Assert-Condition (-not (Test-Path $out)) 'Invalid names must be rejected before extraction.'
 }
 $bad=New-ChangedArchive $archive @($names+'extra.pfx');Assert-Rejected {Receive-Archive $bad (Get-Digest $bad) $fixture.hash (New-Path '-extra')} 'transport.entry-inventory'
 $bad=New-ChangedArchive $archive @($names|Select-Object -SkipLast 1);Assert-Rejected {Receive-Archive $bad (Get-Digest $bad) $fixture.hash (New-Path '-missing')} 'transport.entry-inventory'
 $bad=New-ChangedArchive $archive $names -Reparse $true;Assert-Rejected {Receive-Archive $bad (Get-Digest $bad) $fixture.hash (New-Path '-reparse')} 'transport.entry-identity'
 $bad=New-ChangedArchive $archive $names -ChangedEntry 1;Assert-Rejected {Receive-Archive $bad (Get-Digest $bad) $fixture.hash (New-Path '-changed')} 'candidate.file-changed'
 $bad=Set-FirstDeclaredLength $archive 65537;Assert-Rejected {Receive-Archive $bad (Get-Digest $bad) $fixture.hash (New-Path '-budget')} 'transport.entry-budget'
 $bad=Set-FirstDeclaredLength $archive ([uint32]($identityLength-1));Assert-Rejected {Receive-Archive $bad (Get-Digest $bad) $fixture.hash (New-Path '-expanded')} 'transport.entry-expanded-length'
 $bad=New-Path '.zip';$bytes=[IO.File]::ReadAllBytes($archive);[IO.File]::WriteAllBytes($bad,$bytes[0..($bytes.Length-24)])
 Assert-Rejected {Receive-Archive $bad (Get-Digest $bad) $fixture.hash (New-Path '-truncated')} ''
 $source=[IO.MemoryStream]::new([byte[]](1,2,3,4));$target=[IO.MemoryStream]::new()
 try{Assert-Rejected {& $module {param($s,$d)Copy-ClashSharpTransportEntry -Source $s -Destination $d -ExpectedLength 3} $source $target} 'transport.entry-expanded-length';Assert-Condition ($target.Length -eq 0) 'Excess bytes may not be written.'}finally{$source.Dispose();$target.Dispose()}
 $source=[IO.MemoryStream]::new([byte[]](1,2));$target=[IO.MemoryStream]::new()
 try{Assert-Rejected {& $module {param($s,$d)Copy-ClashSharpTransportEntry -Source $s -Destination $d -ExpectedLength 3} $source $target} 'transport.entry-truncated'}finally{$source.Dispose();$target.Dispose()}
 $linked=New-Path '-junction';$outside=New-Path '-target';$null=[IO.Directory]::CreateDirectory($outside)
 try{
  $null=New-Item -ItemType Junction -Path $linked -Target $outside
  Assert-Rejected {New-Archive $fixture (Join-Path $linked 'no.zip')} 'reparse'
  Assert-Condition (@(Get-ChildItem -LiteralPath $outside -Force).Count -eq 0) 'Rejected reparse parent must remain empty.'
 }finally{if(Test-Path -LiteralPath $linked){[IO.Directory]::Delete($linked)}}
 $tools=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../Tools'))
 $cliArchive=New-Path '.zip'
 $cliProduced=& "$tools/New-CandidateArchive.ps1" -IdentityPath $fixture.identity -ExpectedIdentitySha256 $fixture.hash -ReleaseDirectory $fixture.release -OutputPath $cliArchive
 $cliReceived=& "$tools/Expand-CandidateArchive.ps1" -ArchivePath $cliArchive -ExpectedArchiveSha256 $cliProduced.archiveSha256 -ExpectedIdentitySha256 $fixture.hash -Destination (New-Path '-cli')
 Assert-Condition ($cliProduced.archiveSha256 -ceq $first.archiveSha256 -and $cliReceived.candidateIdentitySha256 -ceq $fixture.hash) 'Both maintained CLI wrappers must preserve exact references.'
 [pscustomobject]@{success=$true;checks=$script:checks;syntheticCandidateBytes=$true;packagesExecuted=$false;signaturesCreatedOrVerified=$false;promotionAuthorized=$false}
}finally{
 $full=[IO.Path]::GetFullPath($root);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@('\','/'))
 if(-not $full.StartsWith($temp+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -cnotmatch '^clashsharp-transport-contract-[0-9a-f]{32}\z'){throw 'Transport fixture cleanup refused'}
 $nodes=@((Get-Item -LiteralPath $full -Force))+@(Get-ChildItem -LiteralPath $full -Recurse -Force)
 if(@($nodes|Where-Object{$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Transport fixture cleanup reparse refused'}
 Remove-Item -LiteralPath $full -Recurse -Force
}
