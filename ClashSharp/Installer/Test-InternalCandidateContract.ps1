#Requires -Version 7.4
<#
.SYNOPSIS
    Guards internal-candidate workflow authority, transport references and private-key boundaries.
.DESCRIPTION
    Uses source contracts and rejected preflight inputs only. Does not create certificates,
    change trust, compile, sign, install or execute a package. Real key cleanup and the complete
    independent jobs are verified separately.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $PSScriptRoot 'InternalSigning.psm1') -Force
$script:checks=0
function Assert-Condition([bool]$Value,[string]$Message){$script:checks++;if(-not $Value){throw $Message}}
function Assert-Rejected([scriptblock]$Action,[string]$Code){
 $rejected=$false;try{$null=& $Action}catch{if(-not $_.Exception.Message.Contains($Code)){throw};$rejected=$true}
 Assert-Condition $rejected ('Expected refusal: '+$Code)
}
$yaml=Get-Content "$repository/.github/workflows/internal-candidate.yml" -Raw
Assert-Condition ($yaml.Contains('workflow_dispatch:') -and $yaml.Contains('$env:GITHUB_REF -cne') -and $yaml.Contains('$env:GITHUB_EVENT_NAME -cne') -and $yaml -notmatch '(?m)^  (push|pull_request|workflow_run):') 'Only explicit mainline dispatch may create an internal signed candidate.'
Assert-Condition ($yaml.Contains('contents: read') -and $yaml.Contains('actions: read') -and $yaml -notmatch '(?m)^\s+(contents|actions|id-token): write') 'Candidate and collector require read-only repository authority.'
Assert-Condition ($yaml -notmatch 'secrets\.|self-hosted|(?m)^\s+environment:|gh\s+release') 'Internal validation must not acquire release credentials, persistent runners or publication.'
$actions=[regex]::Matches($yaml,'(?m)^\s+uses: ([^\r\n]+)')
foreach($action in $actions){Assert-Condition ($action.Groups[1].Value -cmatch '^actions/[a-z-]+@[0-9a-f]{40}\z') 'Every action must retain a complete pinned commit.'}
Assert-Condition ([regex]::Matches($yaml,'persist-credentials: false').Count -eq 3) 'Both controllers and the exact source checkout must discard checkout credentials.'
Assert-Condition ($yaml.Contains('ref: ${{ inputs.source_commit }}') -and $yaml.Contains('global-json-file: candidate-source/global.json')) 'Compilation and SDK must come from the exact admitted source.'
Assert-Condition ($yaml.Contains('./Tools/Get-ProductCiArtifacts.ps1') -and $yaml.Contains('-SourceCommit $env:PRODUCT_SOURCE_COMMIT')) 'A full byte and original-report gate must precede signing.'
Assert-Condition ($yaml.IndexOf('Verify exact product CI bytes') -lt $yaml.IndexOf('Sign and seal internal candidate')) 'Signing must follow actual input verification.'
Assert-Condition ($yaml.Contains('needs: candidate') -and $yaml.Contains('artifact-ids: ${{ needs.candidate.outputs.artifact_id }}')) 'Collector must consume the exact successful producer artifact ID.'
foreach($reference in @('archive_sha256','identity_sha256','authenticode_thumbprint','msix_thumbprint')){
 Assert-Condition ($yaml.Contains('${{ needs.candidate.outputs.'+$reference+' }}')) ('Collector needs an independent '+$reference+' reference.')
}
Assert-Condition ($yaml.Contains('platform: windows-server') -and $yaml.Contains('EXPECTED_PLATFORM: ${{ matrix.platform }}')) 'Hosted Server must remain a Server observation.'
Assert-Condition ($yaml.Contains('github.run_attempt') -and $yaml -notmatch '(?m)^\s+overwrite:\s*true') 'New attempts must retain prior artifacts without replacing them.'
$publicPaths=@([regex]::Matches($yaml,'(?m)^            artifacts/internal-operation/public/([^\r\n]+)')|ForEach-Object {$_.Groups[1].Value})
Assert-Condition (($publicPaths -join ',') -ceq 'internal-candidate.zip,candidate-identity.json,ci-evidence.json,internal-candidate-result.json') 'Only four exact public output files may be uploaded.'
Assert-Condition ($yaml.Contains('artifacts/internal-operation/build.log') -and $yaml.Contains('artifacts/internal-operation/operation-result.json') -and $yaml.Contains('if: failure()')) 'Failed builds must retain logs and actual cleanup observations.'
Assert-Condition ($yaml.Contains('Installation, Win11 UI and public promotion remain separate gates.')) 'Workflow summary must preserve its actual read-only scope.'

$creator=Get-Content "$repository/Tools/New-InternalCandidate.ps1" -Raw
$auditor=Get-Content "$repository/Tools/Invoke-InternalCandidateAudit.ps1" -Raw
$signing=Get-Content (Join-Path $PSScriptRoot 'InternalSigning.psm1') -Raw
foreach($path in @("$repository/Tools/New-InternalCandidate.ps1","$repository/Tools/Invoke-InternalCandidateAudit.ps1")){
 $ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$null,[ref]$null)
 $imports=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Import-Module'},$true))
 Assert-Condition ($imports.Count -ge 2) 'Each controller must declare its actual module composition.'
 $resolved=& {
  param($commands,$root)
  $controllerRoot=$root;$repository=$root
  foreach($command in $commands){$null=& ([scriptblock]::Create($command.Extent.Text))}
  $fixture=Join-Path ([IO.Path]::GetTempPath()) ('clashsharp-composition-'+[Guid]::NewGuid().ToString('N'))
  $null=[IO.Directory]::CreateDirectory($fixture)
  $sentinel=Join-Path $fixture 'existing-material.txt';[IO.File]::WriteAllText($sentinel,'non-executable fixture')
  $owned=[pscustomobject]@{lease=$null}
  try{
   if(@($commands|Where-Object {$_.Extent.Text.Contains('InternalSigning.psm1')}).Count){
    $sdk=Get-ClashSharpInternalSigningSdk
    Assert-Condition ($sdk.version -ceq '10.0.26100.0' -and $sdk.trustedMicrosoftTool) 'Actual controller SDK preflight must resolve the fixed trusted Microsoft tool.'
    $admin=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    $expected=if($admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){'internal-signing.existing-material'}else{'internal-signing.elevated-disposable-context-required'}
    Assert-Rejected {$owned.lease=New-ClashSharpInternalSigningLease -Publisher 'CN=Composition fixture' -SigningDirectory $fixture} $expected
   }else{
    Assert-Rejected {Get-ClashSharpCandidateFileSnapshot -ReleaseDirectory $fixture -Consume {param($files)$files}} 'candidate.file-inventory'
   }
  }finally{
   if($null -ne $owned.lease){$null=Close-ClashSharpInternalSigningLease -Lease $owned.lease}
   Remove-Item -LiteralPath $sentinel
   [IO.Directory]::Delete($fixture)
  }
  Assert-ClashSharpOrdinaryPath -LiteralPath $root -RequireDirectory
 } $imports $repository
 Assert-Condition ($resolved -ceq $repository) 'Actual production import order must keep path validation visible in the controller scope.'
}
Assert-Condition ($creator.Contains('Close-ClashSharpInternalSigningLease') -and $creator.IndexOf('Close-ClashSharpInternalSigningLease') -lt $creator.IndexOf('$result.temporarySigningMaterialRemoved=$true')) 'Successful references require observed key and trust cleanup.'
Assert-Condition ($creator.Contains('New-SignedCandidateIdentity.ps1') -and $creator.Contains('New-CandidateArchive.ps1') -and -not $creator.Contains('build.ps1 -Development')) 'Formal-shaped internal output must retain existing signature, identity and archive gates.'
Assert-Condition ($creator.Contains('publicReleaseAuthorized=$false;promotionAuthorized=$false')) 'Self-signed producer cannot authorize a public release.'
Assert-Condition ($creator.Contains("'CLASHSHARP_WINDOWS_SDK_VERSION'") -and $creator.Contains('$sdk.version') -and $creator.IndexOf('Get-ClashSharpInternalSigningSdk') -lt $creator.IndexOf('$signingLease=New-ClashSharpInternalSigningLease')) 'The fixed SDK must be preflighted before keys and passed to the unmodified build.'
Assert-Condition ($creator.Contains('internal-signing-cleanup-observation') -and $creator.Contains('cleanupConfirmed=')) 'Failures must retain observed cleanup rather than infer it from a thrown build error.'
Assert-Condition ($creator.Contains('internal-candidate.disposable-mainline-ci-required') -and $yaml.Contains('INTERNAL_DISPOSABLE_RUNNER: ${{ runner.environment }}')) 'Fresh key creation must stay in the explicitly disposable hosted CI context.'
Assert-Condition ($signing.Contains('Container=$keyName') -and $signing.IndexOf('$state.keys.Add($keyName)') -lt $signing.IndexOf('New-SelfSignedCertificate @parameters')) 'A known-absent owned key slot must be recorded before enrollment.'
Assert-Condition ($signing.Contains('[object]::ReferenceEquals($Lease,$state.token)') -and $signing.Contains('rawSha256') -and $signing.Contains('CngKeyOpenOptions]::Silent')) 'Cleanup must retain live ownership, exact identity and silent key handling.'
Assert-Condition ($signing.Contains('originalMachineTrustPreserved=$true') -and $signing.Contains('temporaryMachineRootsRemoved=$true')) 'Temporary roots cannot become persistent machine trust.'
Assert-Condition ($signing.Contains("'NonExportable'") -and $signing.Contains('[Security.Cryptography.RandomNumberGenerator]::GetBytes(48)')) 'Installer key must remain non-exportable and the MSIX PFX must use a fresh random password.'
Assert-Condition ($auditor.IndexOf('internal-audit.clean-controller-required') -lt $auditor.IndexOf('Invoke-CandidatePayloadAudit.ps1')) 'A clean trusted collector is required before candidate execution.'
Assert-Condition ($auditor.Contains('Get-ClashSharpWindowsAcceptancePlatform') -and $auditor.Contains('internal-audit.platform-mismatch')) 'Collector must enforce the actual platform instead of the runner label.'
Assert-Condition ($auditor.Contains('installationVerified=$false;win11AcceptanceSatisfied=$false;publicReleaseAuthorized=$false;promotionAuthorized=$false')) 'Read-only success cannot complete installation, UI or promotion.'
Assert-Condition (-not $auditor.Contains('Import-Certificate') -and -not $auditor.Contains('New-SelfSignedCertificate')) 'Independent payload collector cannot import the producer trust.'
$previousDisposable=[Environment]::GetEnvironmentVariable('INTERNAL_DISPOSABLE_RUNNER','Process')
try{
 [Environment]::SetEnvironmentVariable('INTERNAL_DISPOSABLE_RUNNER','contract-no-signing','Process')
 Assert-Rejected {& "$repository/Tools/New-InternalCandidate.ps1" -RepositoryRoot $repository -SourceCommit ('a'*40) -CiEvidencePath "$repository/absent-contract-input.json" -ExpectedCiEvidenceSha256 ('b'*64) -OutputDirectory (Join-Path ([IO.Path]::GetTempPath()) 'never-created-internal-contract')} 'internal-candidate.disposable-mainline-ci-required'
}finally{[Environment]::SetEnvironmentVariable('INTERNAL_DISPOSABLE_RUNNER',$previousDisposable,'Process')}
Assert-Rejected {Close-ClashSharpInternalSigningLease -Lease ([pscustomobject]@{operationId=[Guid]::NewGuid().ToString('D')})} 'internal-signing.unknown-lease'
Assert-Rejected {Get-ClashSharpInternalSigningLeaseInfo -Lease ([pscustomobject]@{})} 'internal-signing.unknown-lease'
[pscustomobject]@{success=$true;checks=$script:checks;certificatesCreated=$false;trustModified=$false;packagesExecuted=$false;promotionAuthorized=$false}
