#Requires -Version 5.1
<#
.SYNOPSIS
Generates the Windows app-list, search, tile and store PNG variants from the canonical brand drawing.
.DESCRIPTION
Uses the same WPF software rasterizer and optical small-size adjustments as the application ICO.
Requires Windows PowerShell STA, opens no window and does not launch the product.
.PARAMETER Verify
Compares generated PNG bytes with the tracked assets instead of replacing them.
#>
[CmdletBinding()]
param([switch]$Verify)
$ErrorActionPreference='Stop'
$applicationAssetsVerify=$Verify.IsPresent
$repositoryRoot=Split-Path -Parent $PSScriptRoot
$assetRoot=Join-Path $repositoryRoot 'ClashSharp\ClashSharp\Assets'
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('clashsharp-brand-png-'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($scratch)
try{
 . (Join-Path $PSScriptRoot 'Generate-InstallerIcon.ps1') -OutputPath (Join-Path $scratch 'render-reference.ico')
 $drawing=Import-InstallerDrawing -Path (Join-Path $repositoryRoot 'ClashSharp\ClashSharp.Installer\Themes\InstallerTheme.xaml')
 $specs=@()
 foreach($scale in @(100,125,150,200,400)){
  $specs+=@{name="Square44x44Logo.scale-$scale.png";size=[int][Math]::Round(44*$scale/100,[MidpointRounding]::AwayFromZero)}
  $specs+=@{name="Square150x150Logo.scale-$scale.png";size=[int][Math]::Round(150*$scale/100,[MidpointRounding]::AwayFromZero)}
  $specs+=@{name="StoreLogo.scale-$scale.png";size=[int][Math]::Round(50*$scale/100,[MidpointRounding]::AwayFromZero)}
 }
 foreach($size in @(16,20,24,32,40,48,64,96,128,256)){
  foreach($suffix in @('','_altform-unplated','_altform-lightunplated')){$specs+=@{name="Square44x44Logo.targetsize-$size$suffix.png";size=$size}}
 }
 $specs+=@{name='StoreLogo.png';size=50}
 foreach($spec in $specs){
  $bitmap=New-InstallerIconBitmap -Master $drawing -Size $spec.size
  $generated=Join-Path $scratch $spec.name
  Export-InstallerIconPreview -Bitmap $bitmap -Path $generated
  $destination=Join-Path $assetRoot $spec.name
  if($applicationAssetsVerify){
   if(-not(Test-Path -LiteralPath $destination) -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($generated)) -cne [Convert]::ToBase64String([IO.File]::ReadAllBytes($destination))){throw "Application PNG differs: $($spec.name)"}
  }else{[IO.File]::Copy($generated,$destination,$true)}
 }
 Write-Output ("Application PNGs: $($specs.Count) canonical scale/target-size variants.")
}finally{
 $absolute=[IO.Path]::GetFullPath($scratch)
 $temporaryRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
 if(-not$absolute.StartsWith($temporaryRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notlike 'clashsharp-brand-png-*'){throw 'Scratch cleanup boundary differs.'}
 if(@(Get-ChildItem -LiteralPath $absolute -Recurse -Force -Attributes ReparsePoint).Count){throw 'Scratch path redirected.'}
 Remove-Item -LiteralPath $absolute -Recurse -Force
}
