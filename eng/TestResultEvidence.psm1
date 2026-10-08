#Requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS
Reads one successful VSTest TRX report and verifies its actual execution records.
.DESCRIPTION
Checks counters against results, matches execution entries and test definitions, and hashes the
same read-locked bytes. Theory cases may share a test definition but must have distinct executions.
Rejects malformed or incomplete evidence without including test output or exception text.
.PARAMETER LiteralPath
Existing TRX file, bounded to 64 MiB. XML entities and external resources are prohibited.
#>
function Get-ClashSharpTrxEvidence {
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $LiteralPath)

    $path = (Get-Item -LiteralPath $LiteralPath -ErrorAction Stop).FullName
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -le 0 -or $stream.Length -gt 67108864) {
            throw 'test-evidence.report_size'
        }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $settings.MaxCharactersInDocument = 67108864
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        try {
            try { $document.Load($reader) } catch { throw 'test-evidence.xml_invalid' }
        } finally {
            $reader.Dispose()
        }
        $namespace = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
        $root = $document.DocumentElement
        if ($root.LocalName -cne 'TestRun' -or $root.NamespaceURI -cne $namespace) {
            throw 'test-evidence.schema_invalid'
        }
        $runId = [Guid]::Empty
        if (-not [Guid]::TryParse($root.GetAttribute('id'), [ref]$runId) -or $runId -eq [Guid]::Empty) {
            throw 'test-evidence.run_identity'
        }
        $manager = [Xml.XmlNamespaceManager]::new($document.NameTable)
        $manager.AddNamespace('t', $namespace)
        $summaries = $root.SelectNodes('t:ResultSummary', $manager)
        if ($summaries.Count -ne 1 -or $summaries[0].GetAttribute('outcome') -cne 'Completed') {
            throw 'test-evidence.run_incomplete'
        }
        $summary = $summaries[0]
        $counterNodes = $summary.SelectNodes('t:Counters', $manager)
        if ($counterNodes.Count -ne 1) { throw 'test-evidence.counters_missing' }
        $counters = @{}
        foreach ($attribute in $counterNodes[0].Attributes) {
            if ($attribute.NamespaceURI -eq 'http://www.w3.org/2000/xmlns/') { continue }
            $value = 0L
            if ($attribute.NamespaceURI.Length -ne 0 -or -not [long]::TryParse(
                $attribute.Value, [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$value)) {
                throw 'test-evidence.counter_invalid'
            }
            $counters[$attribute.Name] = $value
        }
        foreach ($required in @('total', 'executed', 'passed', 'failed', 'notExecuted')) {
            if (-not $counters.ContainsKey($required)) { throw 'test-evidence.counters_missing' }
        }
        foreach ($name in $counters.Keys) {
            if ($name -cnotin @('total', 'executed', 'passed') -and $counters[$name] -ne 0) {
                throw 'test-evidence.nonpassing_counter'
            }
        }
        foreach ($info in $summary.SelectNodes('t:RunInfos/t:RunInfo', $manager)) {
            if ($info.GetAttribute('outcome') -cnotin @('Passed', 'Completed')) {
                throw 'test-evidence.run_diagnostic'
            }
        }
        $resultContainers = $root.SelectNodes('t:Results', $manager)
        if ($resultContainers.Count -ne 1) { throw 'test-evidence.results_missing' }
        $results = $resultContainers[0].SelectNodes('t:UnitTestResult', $manager)
        if ($results.Count -eq 0 -or $resultContainers[0].SelectNodes('*').Count -ne $results.Count) {
            throw 'test-evidence.results_invalid'
        }
        foreach ($name in @('total', 'executed', 'passed')) {
            if ($counters[$name] -ne $results.Count) { throw 'test-evidence.counter_mismatch' }
        }

        $definitions = [Collections.Generic.HashSet[Guid]]::new()
        foreach ($definition in $root.SelectNodes('t:TestDefinitions/t:UnitTest', $manager)) {
            $testId = [Guid]::Empty
            if (-not [Guid]::TryParse($definition.GetAttribute('id'), [ref]$testId) -or
                $testId -eq [Guid]::Empty -or -not $definitions.Add($testId)) {
                throw 'test-evidence.definition_identity'
            }
        }
        $entries = [Collections.Generic.Dictionary[Guid, Guid]]::new()
        foreach ($entry in $root.SelectNodes('t:TestEntries/t:TestEntry', $manager)) {
            $executionId = [Guid]::Empty
            $testId = [Guid]::Empty
            if (-not [Guid]::TryParse($entry.GetAttribute('executionId'), [ref]$executionId) -or
                -not [Guid]::TryParse($entry.GetAttribute('testId'), [ref]$testId) -or
                $executionId -eq [Guid]::Empty -or -not $definitions.Contains($testId) -or
                $entries.ContainsKey($executionId)) {
                throw 'test-evidence.entry_identity'
            }
            $entries.Add($executionId, $testId)
        }
        if ($entries.Count -ne $results.Count) { throw 'test-evidence.entry_mismatch' }
        $executions = [Collections.Generic.HashSet[Guid]]::new()
        $executedTests = [Collections.Generic.HashSet[Guid]]::new()
        foreach ($result in $results) {
            if ($result.GetAttribute('outcome') -cne 'Passed') { throw 'test-evidence.result_not_passed' }
            $executionId = [Guid]::Empty
            $testId = [Guid]::Empty
            if (-not [Guid]::TryParse($result.GetAttribute('executionId'), [ref]$executionId) -or
                -not [Guid]::TryParse($result.GetAttribute('testId'), [ref]$testId) -or
                -not $executions.Add($executionId) -or -not $entries.ContainsKey($executionId) -or
                $entries[$executionId] -ne $testId) {
                throw 'test-evidence.result_identity'
            }
            $null = $executedTests.Add($testId)
        }
        if (-not $definitions.SetEquals($executedTests)) { throw 'test-evidence.definition_mismatch' }

        # Read locking prevents a writer or rename from changing evidence between parsing and hashing.
        $stream.Position = 0
        $hash = [Security.Cryptography.SHA256]::Create()
        try { $digest = [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $hash.Dispose() }
        return [pscustomobject][ordered]@{
            path = $path
            runId = $runId.ToString('D')
            sha256 = $digest
            total = $results.Count
            passed = $results.Count
            failed = 0
            skipped = 0
        }
    } finally {
        $stream.Dispose()
    }
}

<#
.SYNOPSIS
Verifies distinct successful TRX reports and returns a compact evidence summary.
.DESCRIPTION
Rejects duplicate paths or copied run identities before reporting a combined total. This proves
the integrity of executed results, not that a caller's filters cover an entire suite or product.
No tests, package mutations, network operations or system settings changes are performed.
.PARAMETER LiteralPath
One to sixteen explicit TRX reports to verify. Wildcard expansion and directory scans are not used.
#>
function Get-ClashSharpTestResultSummary {
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateCount(1, 16)][string[]] $LiteralPath)

    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $runs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $reports = [Collections.Generic.List[object]]::new()
    $total = 0L
    foreach ($path in $LiteralPath) {
        $report = Get-ClashSharpTrxEvidence -LiteralPath $path
        if (-not $paths.Add($report.path) -or -not $runs.Add($report.runId)) {
            throw 'test-evidence.report_duplicate'
        }
        $reports.Add($report)
        $total += $report.total
    }
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        verifiedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        reportCount = $reports.Count
        total = $total
        passed = $total
        failed = 0
        skipped = 0
        reports = $reports.ToArray()
    }
}

Export-ModuleMember -Function Get-ClashSharpTestResultSummary
