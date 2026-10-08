#Requires -Version 5.1

<#
.SYNOPSIS
Exercises the .NET evidence gate with independent passing and inconsistent TRX fixtures.
.DESCRIPTION
Checks theory executions, counter and identity mismatches, missing evidence, malformed XML,
duplicate runs and summary publication. Uses only a temporary owned directory and no test runner.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module -Name (Join-Path $PSScriptRoot 'TestResultEvidence.psm1') -Force
$script:checks = 0

<#
.SYNOPSIS
Creates two distinct executions of one theory definition.
.DESCRIPTION
Returns an independent minimal VSTest report whose counters, definitions and entries agree.
#>
function New-TrxFixture {
    [CmdletBinding()]
    param()

    $run = [Guid]::NewGuid().ToString('D')
    $test = [Guid]::NewGuid().ToString('D')
    $first = [Guid]::NewGuid().ToString('D')
    $second = [Guid]::NewGuid().ToString('D')
    $document = [Xml.XmlDocument]::new()
    $document.LoadXml(@"
<TestRun id="$run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testId="$test" executionId="$first" outcome="Passed" />
    <UnitTestResult testId="$test" executionId="$second" outcome="Passed" />
  </Results>
  <TestDefinitions><UnitTest id="$test" /></TestDefinitions>
  <TestEntries>
    <TestEntry testId="$test" executionId="$first" />
    <TestEntry testId="$test" executionId="$second" />
  </TestEntries>
  <ResultSummary outcome="Completed">
    <Counters total="2" executed="2" passed="2" failed="0" notExecuted="0" />
  </ResultSummary>
</TestRun>
"@)
    return $document
}

<#
.SYNOPSIS
Asserts that an evidence operation is rejected with a stable diagnostic.
.DESCRIPTION
Fails if invalid evidence is accepted or raw fixture output leaks through the rejection message.
.PARAMETER Action
Operation to invoke without preserving its normal output.
.PARAMETER Code
Required stable diagnostic substring; an empty value accepts any rejection type.
#>
function Assert-EvidenceRejected {
    [CmdletBinding()]
    param([Parameter(Mandatory)][scriptblock] $Action, [AllowEmptyString()][string] $Code = '')

    $rejected = $false
    try { $null = & $Action } catch {
        $rejected = $true
        if ($Code.Length -gt 0 -and -not $_.Exception.Message.Contains($Code)) {
            throw "Unexpected evidence rejection; expected $Code."
        }
        if ($_.Exception.Message.Contains('PRIVATE-TEST-OUTPUT')) { throw 'Raw test output was disclosed.' }
    }
    if (-not $rejected) { throw "Invalid evidence was accepted; expected $Code." }
    $script:checks++
}

$testParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $testParent ('clashsharp-test-evidence-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
try {
    $firstPath = Join-Path $testRoot 'first.trx'
    $secondPath = Join-Path $testRoot 'second.trx'
    (New-TrxFixture).Save($firstPath)
    (New-TrxFixture).Save($secondPath)
    $summary = Get-ClashSharpTestResultSummary -LiteralPath @($firstPath, $secondPath)
    if ($summary.reportCount -ne 2 -or $summary.total -ne 4 -or $summary.passed -ne 4 -or
        $summary.failed -ne 0 -or $summary.skipped -ne 0 -or
        $summary.reports[0].sha256 -cne (Get-FileHash -LiteralPath $firstPath -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'Successful evidence did not preserve actual executions and report bytes.'
    }
    $script:checks++

    $cases = @(
        @{ Code = 'counter_mismatch'; Change = { param($d) $d.TestRun.ResultSummary.Counters.SetAttribute('total', '1') } },
        @{ Code = 'counter_mismatch'; Change = { param($d) $d.TestRun.ResultSummary.Counters.SetAttribute('executed', '1') } },
        @{ Code = 'counter_mismatch'; Change = { param($d) $d.TestRun.ResultSummary.Counters.SetAttribute('passed', '1') } },
        @{ Code = 'counter_invalid'; Change = { param($d) $d.TestRun.ResultSummary.Counters.SetAttribute('total', '-1') } },
        @{ Code = 'counter_invalid'; Change = { param($d) $d.TestRun.ResultSummary.Counters.SetAttribute('passed', 'NaN') } },
        @{ Code = 'counter_invalid'; Change = { param($d) $d.TestRun.ResultSummary.Counters.SetAttribute('passed', '9223372036854775808') } },
        @{ Code = 'nonpassing_counter'; Change = { param($d) $d.TestRun.ResultSummary.Counters.SetAttribute('failed', '1') } },
        @{ Code = 'nonpassing_counter'; Change = { param($d) $d.TestRun.ResultSummary.Counters.SetAttribute('notExecuted', '1') } },
        @{ Code = 'nonpassing_counter'; Change = { param($d) $d.TestRun.ResultSummary.Counters.SetAttribute('futureFailure', '1') } },
        @{ Code = 'counters_missing'; Change = { param($d) $d.TestRun.ResultSummary.Counters.RemoveAttribute('notExecuted') } },
        @{ Code = 'counters_missing'; Change = { param($d) $null = $d.TestRun.ResultSummary.AppendChild($d.TestRun.ResultSummary.Counters.CloneNode($true)) } },
        @{ Code = 'run_incomplete'; Change = { param($d) $d.TestRun.ResultSummary.SetAttribute('outcome', 'Aborted') } },
        @{ Code = 'run_incomplete'; Change = { param($d) $null = $d.TestRun.AppendChild($d.TestRun.ResultSummary.CloneNode($true)) } },
        @{ Code = 'run_identity'; Change = { param($d) $d.TestRun.SetAttribute('id', [Guid]::Empty.ToString('D')) } },
        @{ Code = 'results_missing'; Change = { param($d) $null = $d.TestRun.RemoveChild($d.TestRun.Results) } },
        @{ Code = 'results_invalid'; Change = { param($d) $d.TestRun.Results.RemoveAll() } },
        @{ Code = 'results_invalid'; Change = { param($d) $null = $d.TestRun.Results.AppendChild($d.CreateElement('TextResult', $d.TestRun.NamespaceURI)) } },
        @{ Code = 'result_not_passed'; Change = { param($d) $d.TestRun.Results.UnitTestResult[0].SetAttribute('outcome', 'Failed'); $d.TestRun.Results.UnitTestResult[0].InnerText = 'PRIVATE-TEST-OUTPUT' } },
        @{ Code = 'result_not_passed'; Change = { param($d) $d.TestRun.Results.UnitTestResult[0].SetAttribute('outcome', 'NotExecuted') } },
        @{ Code = 'result_identity'; Change = { param($d) $d.TestRun.Results.UnitTestResult[1].SetAttribute('executionId', $d.TestRun.Results.UnitTestResult[0].GetAttribute('executionId')) } },
        @{ Code = 'result_identity'; Change = { param($d) $d.TestRun.Results.UnitTestResult[0].SetAttribute('testId', [Guid]::NewGuid().ToString('D')) } },
        @{ Code = 'entry_mismatch'; Change = { param($d) $null = $d.TestRun.TestEntries.RemoveChild($d.TestRun.TestEntries.TestEntry[0]) } },
        @{ Code = 'entry_identity'; Change = { param($d) $d.TestRun.TestEntries.TestEntry[0].SetAttribute('testId', [Guid]::NewGuid().ToString('D')) } },
        @{ Code = 'entry_identity'; Change = { param($d) $d.TestRun.TestEntries.TestEntry[0].SetAttribute('executionId', 'invalid') } },
        @{ Code = 'entry_identity'; Change = { param($d) $null = $d.TestRun.TestEntries.AppendChild($d.TestRun.TestEntries.TestEntry[0].CloneNode($true)) } },
        @{ Code = 'definition_identity'; Change = { param($d) $d.TestRun.TestDefinitions.UnitTest.SetAttribute('id', 'invalid') } },
        @{ Code = 'definition_identity'; Change = { param($d) $null = $d.TestRun.TestDefinitions.AppendChild($d.TestRun.TestDefinitions.UnitTest.CloneNode($true)) } },
        @{ Code = 'definition_mismatch'; Change = { param($d) $extra = $d.TestRun.TestDefinitions.UnitTest.CloneNode($true); $extra.SetAttribute('id', [Guid]::NewGuid().ToString('D')); $null = $d.TestRun.TestDefinitions.AppendChild($extra) } },
        @{ Code = 'run_diagnostic'; Change = { param($d) $infos = $d.CreateElement('RunInfos', $d.TestRun.NamespaceURI); $info = $d.CreateElement('RunInfo', $d.TestRun.NamespaceURI); $info.SetAttribute('outcome', 'Error'); $null = $infos.AppendChild($info); $null = $d.TestRun.ResultSummary.AppendChild($infos) } }
    )
    $invalidPath = Join-Path $testRoot 'invalid.trx'
    foreach ($case in $cases) {
        $document = New-TrxFixture
        & $case.Change $document
        $document.Save($invalidPath)
        Assert-EvidenceRejected { Get-ClashSharpTestResultSummary -LiteralPath $invalidPath } $case.Code
    }
    foreach ($xmlCase in @(
        @{ Content = '<'; Code = 'xml_invalid' },
        @{ Content = '<!DOCTYPE TestRun [<!ENTITY external SYSTEM="file:///private">]><TestRun>&external;</TestRun>'; Code = 'xml_invalid' },
        @{ Content = (New-TrxFixture).OuterXml.Replace('http://microsoft.com/schemas/VisualStudio/TeamTest/2010', 'urn:unexpected'); Code = 'schema_invalid' }
    )) {
        [IO.File]::WriteAllText($invalidPath, $xmlCase.Content)
        Assert-EvidenceRejected { Get-ClashSharpTestResultSummary -LiteralPath $invalidPath } $xmlCase.Code
    }
    $oversizedPath = Join-Path $testRoot 'oversized.trx'
    $stream = [IO.File]::Open($oversizedPath, [IO.FileMode]::CreateNew)
    try { $stream.SetLength(67108865) } finally { $stream.Dispose() }
    Assert-EvidenceRejected { Get-ClashSharpTestResultSummary -LiteralPath $oversizedPath } 'report_size'
    Assert-EvidenceRejected { Get-ClashSharpTestResultSummary -LiteralPath @($firstPath, $firstPath) } 'report_duplicate'
    $copyPath = Join-Path $testRoot 'copied.trx'
    Copy-Item -LiteralPath $firstPath -Destination $copyPath
    Assert-EvidenceRejected { Get-ClashSharpTestResultSummary -LiteralPath @($firstPath, $copyPath) } 'report_duplicate'

    $publishedPath = Join-Path $testRoot 'published.json'
    $null = & (Join-Path $PSScriptRoot 'Test-DotNetResults.ps1') -ResultPath @($firstPath, $secondPath) -SummaryPath $publishedPath
    $saved = Get-Content -LiteralPath $publishedPath -Raw | ConvertFrom-Json
    if ($saved.total -ne 4 -or $saved.reportCount -ne 2) { throw 'Saved evidence summary is inconsistent.' }
    $script:checks++
    $savedHash = (Get-FileHash -LiteralPath $publishedPath -Algorithm SHA256).Hash
    Assert-EvidenceRejected { & (Join-Path $PSScriptRoot 'Test-DotNetResults.ps1') -ResultPath $firstPath -SummaryPath $publishedPath }
    if ((Get-FileHash -LiteralPath $publishedPath -Algorithm SHA256).Hash -cne $savedHash) { throw 'Existing summary was overwritten.' }
    $script:checks++
    $unpublishedPath = Join-Path $testRoot 'unpublished.json'
    Assert-EvidenceRejected { & (Join-Path $PSScriptRoot 'Test-DotNetResults.ps1') -ResultPath @($firstPath, $invalidPath) -SummaryPath $unpublishedPath } 'schema_invalid'
    Assert-EvidenceRejected { & (Join-Path $PSScriptRoot 'Test-DotNetResults.ps1') -ResultPath (Join-Path $testRoot 'missing.trx') -SummaryPath $unpublishedPath }
    if (Test-Path -LiteralPath $unpublishedPath) { throw 'Incomplete evidence published a success summary.' }
    $script:checks++
    Write-Output ('PowerShell {0}: .NET result evidence contract passed {1} checks.' -f $PSVersionTable.PSVersion, $script:checks)
} finally {
    # Remove only the absolute temporary directory created by this invocation.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved -ceq $testParent -or -not $resolved.StartsWith($testParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Test evidence fixture cleanup escaped its temporary parent.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
