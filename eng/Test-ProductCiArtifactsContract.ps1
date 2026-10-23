#Requires -Version 7.4
<#
.SYNOPSIS
    Exercises archive digests, scope identities, ZIP boundaries and real TRX execution records.
.DESCRIPTION
    Uses tiny owned fixtures and never downloads or executes any package. Malformed contents
    receive matching fixture digests to test semantic checks independently from transport hashes.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'ProductCiArtifacts.psm1') -Force
$script:checks=0
$root=Join-Path ([IO.Path]::GetTempPath()) ('clashsharp-artifact-contract-'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($root)
$utf8=[Text.UTF8Encoding]::new($false)
function Assert-Condition([bool]$Value,[string]$Message){$script:checks++;if(-not $Value){throw $Message}}
function New-Trx(){
 $run=[Guid]::NewGuid();$test=[Guid]::NewGuid();$first=[Guid]::NewGuid();$second=[Guid]::NewGuid()
 @"
<TestRun id="$run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<Results><UnitTestResult testId="$test" executionId="$first" outcome="Passed"/><UnitTestResult testId="$test" executionId="$second" outcome="Passed"/></Results>
<TestDefinitions><UnitTest id="$test"/></TestDefinitions>
<TestEntries><TestEntry testId="$test" executionId="$first"/><TestEntry testId="$test" executionId="$second"/></TestEntries>
<ResultSummary outcome="Completed"><Counters total="2" executed="2" passed="2" failed="0" notExecuted="0"/></ResultSummary>
</TestRun>
"@
}
function New-Fixture(){
 $directory=Join-Path $root ([Guid]::NewGuid().ToString('N')+'-[inputs]');$null=[IO.Directory]::CreateDirectory($directory)
 $scope=[ordered]@{schemaVersion=1;product=$true;reason='product-or-other-path';complete=$true;changedPaths=@('ClashSharp/App.xaml');productTestsExecuted=$false;baseCommit=('a'*40);headCommit=('b'*40)}
 $scopeText=$scope|ConvertTo-Json -Depth 4
 $reports=@('tests.trx','installer-core-tests.trx','installer-presentation-tests.trx','installer-windows-tests.trx')
 $dev=@('ClashSharp-Installer-Development-Unsigned.exe','ClashSharp-Installer-Development-Unsigned.exe.sha256','DEVELOPMENT-UNSIGNED.txt','MIHOMO-SOURCE-MATERIALS.zip','THIRD-PARTY-NOTICES.zip','payload/ClashSharp_1.0.0.0_x64.msix','payload/ClashSharp_TemporaryKey.cer','payload/payload-provenance.json','payload/Dependencies/x64/Microsoft.WindowsAppRuntime.1.8.msix')
 $roles=@('ci-scope-dotnet','ci-scope-installer','dotnet-test-results','ClashSharp-1.0.0-development-package')
 $contents=@();for($role=0;$role -lt 4;$role++){$contents+=,([Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal))};$contents[0].Add('ci-scope.json',$scopeText);$contents[1].Add('ci-scope.json',$scopeText)
 foreach($name in $reports){$contents[2][$name]=New-Trx};foreach($name in $dev){$contents[3][$name]='SYNTHETIC-NOT-EXECUTABLE'}
 $metadata=[pscustomobject]@{kind='product-ci-execution-metadata';repository='Water-Run/ClashSharp';sourceCommit=('b'*40);runId=123;runAttempt=1;jobExecutionVerified=$true;artifacts=@()}
 for($index=0;$index -lt 4;$index++){$metadata.artifacts+=[pscustomobject]@{name=$roles[$index];id=($index+1);length=0;digest=''}}
 [pscustomobject]@{directory=$directory;metadata=$metadata;contents=$contents}
}
function Save-Fixture($Fixture){
 for($index=0;$index -lt 4;$index++){
  $path=Join-Path $Fixture.directory ('artifact-'+($index+1)+'.zip');if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}
  $archive=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
  try{foreach($entry in $Fixture.contents[$index].GetEnumerator()){$item=$archive.CreateEntry($entry.Key);$stream=$item.Open();try{$bytes=$utf8.GetBytes([string]$entry.Value);$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}}}finally{$archive.Dispose()}
  $Fixture.metadata.artifacts[$index].length=(Get-Item -LiteralPath $path).Length
  $Fixture.metadata.artifacts[$index].digest='sha256:'+(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
 }
}
function Assert-Rejected([scriptblock]$Mutation,[string]$Code){
 $fixture=New-Fixture;$null=& $Mutation $fixture;Save-Fixture $fixture;$refused=$false
 try{$null=Get-ClashSharpProductCiArtifactEvidence -Metadata $fixture.metadata -ArtifactDirectory $fixture.directory}catch{if($Code -and -not $_.Exception.Message.Contains($Code)){throw};if($_.Exception.Message.Contains('PRIVATE-TEST-OUTPUT')){throw 'Fixture output leaked'};$refused=$true}
 Assert-Condition $refused ('Expected refusal: '+$Code)
}
try{
 $fixture=New-Fixture;Save-Fixture $fixture
 $result=Get-ClashSharpProductCiArtifactEvidence -Metadata $fixture.metadata -ArtifactDirectory $fixture.directory
 Assert-Condition ($result.artifactBytesVerified -and $result.rawTestReportsVerified -and $result.tests.passed -eq 8 -and $result.tests.reportCount -eq 4) 'Actual fixture executions must verify.'
 Assert-Condition (@($result.tests.reports|Where-Object{$_.PSObject.Properties['path']}).Count -eq 0) 'Temporary report paths must not escape.'
 Assert-Condition ($result.developmentPackageInventoryVerified -and -not $result.developmentPackageSignaturesOrNestedPayloadsVerified -and -not $result.promotionAuthorized) 'Inventory cannot certify signatures or publication.'
 $fallback=New-Fixture
 for($index=0;$index -lt 2;$index++){$fallback.contents[$index]['ci-scope.json']=$fallback.contents[$index]['ci-scope.json'].Replace('"complete": true','"complete": false').Replace('product-or-other-path','unknown-commit')}
 Save-Fixture $fallback
 $fallbackResult=Get-ClashSharpProductCiArtifactEvidence -Metadata $fallback.metadata -ArtifactDirectory $fallback.directory
 Assert-Condition ($fallbackResult.rawTestReportsVerified -and @($fallbackResult.scopes|Where-Object complete -EQ $false).Count -eq 2) 'Conservative full-CI fallback remains valid when actual reports and source identity verify.'
 $path=Join-Path $fixture.directory 'artifact-1.zip';$held=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
 try{$refused=$false;try{$null=Get-ClashSharpProductCiArtifactEvidence -Metadata $fixture.metadata -ArtifactDirectory $fixture.directory}catch{$refused=$true};Assert-Condition $refused 'An active writer must prevent consumption.'}finally{$held.Dispose()}
 $null=Get-ClashSharpProductCiArtifactEvidence -Metadata $fixture.metadata -ArtifactDirectory $fixture.directory;Assert-Condition $true 'All read leases must release.'
 $fixture.metadata.artifacts[0].digest='sha256:'+('0'*64);$refused=$false
 try{$null=Get-ClashSharpProductCiArtifactEvidence -Metadata $fixture.metadata -ArtifactDirectory $fixture.directory}catch{if(-not $_.Exception.Message.Contains('archive-digest')){throw};$refused=$true};Assert-Condition $refused 'A digest mismatch must fail.'
 $fixture.metadata.artifacts[0].length++;$refused=$false
 try{$null=Get-ClashSharpProductCiArtifactEvidence -Metadata $fixture.metadata -ArtifactDirectory $fixture.directory}catch{if(-not $_.Exception.Message.Contains('archive-digest')){throw};$refused=$true};Assert-Condition $refused 'A length mismatch must fail before hashing.'
 Assert-Rejected {param($f)$f.contents[0]['ci-scope.json']=$f.contents[0]['ci-scope.json'].Replace('"product": true','"product": false')} 'scope-identity'
 Assert-Rejected {param($f)$f.contents[0]['ci-scope.json']=$f.contents[0]['ci-scope.json'].Replace(('b'*40),('c'*40))} 'scope-identity'
 Assert-Rejected {param($f)$f.contents[0]['ci-scope.json']=$f.contents[0]['ci-scope.json'].Replace('"product": true','"product": true, "product": true')} 'scope-fields'
 Assert-Rejected {param($f)$f.contents[0]['ci-scope.json']='PRIVATE-TEST-OUTPUT'} 'scope-json'
 Assert-Rejected {param($f)$f.contents[0]['ci-scope.json']='x'*1048577} 'entry-budget'
 Assert-Rejected {param($f)$f.contents[0]['CI-SCOPE.json']='collision'} 'zip-path'
 Assert-Rejected {param($f)$f.contents[2]['../outside.trx']='escape'} 'zip-path'
 Assert-Rejected {param($f)$f.contents[2]['C:/outside.trx']='escape'} 'zip-path'
 Assert-Rejected {param($f)$f.contents[2]['folder\outside.trx']='escape'} 'zip-path'
 Assert-Rejected {param($f)$f.contents[2]['folder./outside.trx']='escape'} 'zip-path'
 Assert-Rejected {param($f)$f.contents[2].Remove('installer-windows-tests.trx')} 'report-inventory'
 Assert-Rejected {param($f)$f.contents[2]['extra.trx']=New-Trx} 'report-inventory'
 Assert-Rejected {param($f)$f.contents[2]['tests.trx']=$f.contents[2]['tests.trx'].Replace('outcome="Passed"','outcome="Failed"')} 'test-evidence.result_not_passed'
 Assert-Rejected {param($f)$f.contents[2]['installer-core-tests.trx']=$f.contents[2]['tests.trx']} 'test-evidence.report_duplicate'
 Assert-Rejected {param($f)$f.contents[3]['secret.pfx']='private-key'} 'development-inventory'
 Assert-Rejected {param($f)$f.contents[3].Remove('DEVELOPMENT-UNSIGNED.txt')} 'development-inventory'
 Assert-Rejected {param($f)$f.contents[3]['THIRD-PARTY-NOTICES.zip']=''} 'development-inventory'
 $fixture=New-Fixture;Save-Fixture $fixture;$link=Join-Path $root 'linked-inputs';$null=New-Item -ItemType Junction -Path $link -Target $fixture.directory
 try{$refused=$false;try{$null=Get-ClashSharpProductCiArtifactEvidence -Metadata $fixture.metadata -ArtifactDirectory $link}catch{if(-not $_.Exception.Message.Contains('reparse-path')){throw};$refused=$true};Assert-Condition $refused 'Reparse input roots must fail.'}
 finally{$item=Get-Item -LiteralPath $link -Force;if(-not($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or [IO.Path]::GetFullPath($link) -cne (Join-Path $root 'linked-inputs')){throw 'Owned junction changed'};Remove-Item -LiteralPath $link -Force}
 [pscustomobject]@{success=$true;checks=$script:checks;networkUsed=$false;archiveExecuted=$false}
}finally{
 $full=[IO.Path]::GetFullPath($root);$parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar))
 if(-not $full.StartsWith($parent+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -cnotmatch '^clashsharp-artifact-contract-[0-9a-f]{32}\z'){throw 'Contract cleanup path refused'}
 $entries=@((Get-Item -LiteralPath $full -Force))+@(Get-ChildItem -LiteralPath $full -Recurse -Force);if(@($entries|Where-Object{$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Contract cleanup reparse refused'}
 Remove-Item -LiteralPath $full -Recurse -Force
}
