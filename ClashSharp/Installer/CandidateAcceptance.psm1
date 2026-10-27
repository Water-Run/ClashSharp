#Requires -Version 7.4
Set-StrictMode -Version Latest

function Get-ClashSharpWindowsAcceptancePlatform {
    <#
    .SYNOPSIS
        Classifies an independently observed Windows product type and kernel build.
    .DESCRIPTION
        Server and domain-controller product types remain Server even when their kernel build
        overlaps Windows 11. This pure policy does not probe a machine or prove an installation.
    .PARAMETER Observation
        Actual CIM version, numeric build/product type and runtime architecture from the collector.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Observation)
    foreach ($name in @('build','productType')) {
        if ($Observation.$name -isnot [int] -and $Observation.$name -isnot [long]) { throw 'acceptance.platform-number' }
    }
    $versionMatch = [regex]::Match([string]$Observation.version,'^10\.0\.([1-9][0-9]{3,5})\z')
    if ($Observation.version -isnot [string] -or -not $versionMatch.Success -or
        $Observation.build -ne [int]$versionMatch.Groups[1].Value -or $Observation.productType -notin @(1,2,3) -or
        $Observation.architecture -cnotin @('X64','Arm64','X86')) { throw 'acceptance.platform-identity' }
    $family = if ($Observation.productType -ne 1) { 'windows-server' }
        elseif ($Observation.build -ge 22000) { 'windows11' } else { 'windows10' }
    [ordered]@{ family=$family; version=$Observation.version; build=[int]$Observation.build;
        productType=[int]$Observation.productType; architecture=[string]$Observation.architecture }
}

function Get-ClashSharpPayloadAuditDocument {
    <#
    .SYNOPSIS
        Binds one actual read-only payload execution to a pinned candidate and observed platform.
    .DESCRIPTION
        Canonicalizes trusted collector observations. Synthetic callers do not acquire execution
        authority by calling this pure function. Its narrow scope never satisfies installation,
        Win11 UI, restoration or publication gates, including when the platform itself is Win11.
    .PARAMETER IdentitySha256
        Independently pinned candidate identity held by the collector throughout execution.
    .PARAMETER SourceCommit
        Source from the canonical candidate, not a separately chosen report field.
    .PARAMETER RunId
        Fresh canonical GUID identifying this one invocation.
    .PARAMETER StartedAt
        UTC round-trip timestamp immediately before the actual process started.
    .PARAMETER FinishedAt
        UTC round-trip timestamp after that process terminated.
    .PARAMETER Platform
        Raw local OS observations to classify with the shared platform policy.
    .PARAMETER Audit
        Exact successful eight-field JSON returned by the fixed --verify-payload route.
    .PARAMETER CandidateFiles
        File observations from the verified candidate, used to bind the payload digest and totals.
    #>
    [CmdletBinding()]
    param([string] $IdentitySha256,[string] $SourceCommit,[string] $RunId,
        [string] $StartedAt,[string] $FinishedAt,$Platform,$Audit,[object[]] $CandidateFiles)
    if ($IdentitySha256 -cnotmatch '^[0-9a-f]{64}\z' -or $IdentitySha256 -ceq ('0'*64) -or
        $SourceCommit -cnotmatch '^[0-9a-f]{40}\z' -or $SourceCommit -ceq ('0'*40) -or
        $RunId -cnotmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z' -or
        $RunId -ceq [Guid]::Empty.ToString()) { throw 'acceptance.reference' }
    $start = [DateTimeOffset]::ParseExact($StartedAt,'o',[Globalization.CultureInfo]::InvariantCulture)
    $finish = [DateTimeOffset]::ParseExact($FinishedAt,'o',[Globalization.CultureInfo]::InvariantCulture)
    if ($start.Offset -ne [TimeSpan]::Zero -or $finish.Offset -ne [TimeSpan]::Zero -or
        $finish -lt $start -or ($finish-$start).TotalSeconds -gt 120) { throw 'acceptance.execution-time' }
    $expected = @('schemaVersion','status','publishedExecutable','packageVersion','payloadSha256','fileCount','totalBytes','machineFileCount')
    $names = @($Audit.PSObject.Properties.Name)
    if ($names.Count -ne $expected.Count -or @($names | Where-Object { $_ -cnotin $expected }).Count) {
        throw 'acceptance.payload-fields'
    }
    foreach ($name in @('schemaVersion','fileCount','totalBytes','machineFileCount')) {
        if ($Audit.$name -isnot [int] -and $Audit.$name -isnot [long]) { throw 'acceptance.payload-number' }
    }
    if ($Audit.schemaVersion -ne 1 -or $Audit.status -cne 'passed' -or
        $Audit.publishedExecutable -cne 'Setup.exe' -or $Audit.packageVersion -cne '1.0.0.0' -or
        $Audit.payloadSha256 -cnotmatch '^[0-9a-f]{64}\z' -or $Audit.payloadSha256 -ceq ('0'*64) -or
        $Audit.fileCount -le 0 -or $Audit.fileCount -gt 10000 -or $Audit.totalBytes -le 0 -or $Audit.totalBytes -gt 4294967296 -or
        $Audit.machineFileCount -le 0 -or $Audit.machineFileCount -gt 10000) { throw 'acceptance.payload-result' }
    $payloadFiles = @($CandidateFiles | Where-Object { $_.path.StartsWith('payload/',[StringComparison]::Ordinal) })
    $primary = @($CandidateFiles | Where-Object path -CEQ 'payload/ClashSharp_1.0.0.0_x64.msix')
    $total = 0L
    # Canonical candidate files are ordered dictionaries; use their keys rather than PSObject
    # property enumeration, which differs from deserialized JSON in Measure-Object.
    foreach ($file in $payloadFiles) { $total += [long]$file.length }
    if ($primary.Count -ne 1 -or $Audit.payloadSha256 -cne $primary[0].sha256 -or
        $Audit.fileCount -ne $payloadFiles.Count -or $Audit.totalBytes -ne $total) {
        throw 'acceptance.payload-candidate-mismatch'
    }
    $document = [ordered]@{
        schemaVersion=1; kind='clashsharp-candidate-payload-execution'; repository='Water-Run/ClashSharp'
        candidateIdentitySha256=$IdentitySha256; sourceCommit=$SourceCommit; runId=$RunId
        scope='installer-read-only-payload'; status='passed'; startedAt=$StartedAt; finishedAt=$FinishedAt
        platform=(Get-ClashSharpWindowsAcceptancePlatform -Observation $Platform)
        payload=[ordered]@{ packageVersion=$Audit.packageVersion; sha256=$Audit.payloadSha256;
            fileCount=[int]$Audit.fileCount; totalBytes=[long]$Audit.totalBytes; machineFileCount=[int]$Audit.machineFileCount }
        installationVerified=$false; win11AcceptanceSatisfied=$false; promotionAuthorized=$false
    }
    $json = ($document | ConvertTo-Json -Depth 10 -Compress) + "`n"
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    [pscustomobject]@{document=$document; canonicalJson=$json;
        receiptSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()}
}

Export-ModuleMember -Function Get-ClashSharpWindowsAcceptancePlatform, Get-ClashSharpPayloadAuditDocument
