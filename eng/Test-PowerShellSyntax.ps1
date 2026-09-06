#Requires -Version 5.1

<#
.SYNOPSIS
    Parses maintained PowerShell sources without executing them.
.DESCRIPTION
    Checks every tracked or non-ignored new .ps1 and .psm1 file in a Git working
    tree with the current PowerShell parser. Reports all syntax errors with file,
    line, and column positions and fails if discovery or parsing is incomplete.
    Run under Windows PowerShell 5.1 and PowerShell 7 to verify both editions.
.PARAMETER RepositoryRoot
    Directory within the Git working tree to check. Defaults to this repository.
.EXAMPLE
    ./eng/Test-PowerShellSyntax.ps1
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $PSBoundParameters.ContainsKey('RepositoryRoot')) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}

$repositoryPath = & git -C $RepositoryRoot rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryPath)) {
    throw 'Cannot resolve the Git working tree for PowerShell syntax validation.'
}

# Include new local sources while keeping build output and other ignored files
# outside the check. CI receives the same source set through its clean checkout.
$sourcePaths = @(& git -C $repositoryPath -c core.quotepath=false ls-files `
    --cached --others --exclude-standard -- '*.ps1' '*.psm1')
if ($LASTEXITCODE -ne 0 -or $sourcePaths.Count -eq 0) {
    throw 'PowerShell source discovery failed or returned no scripts.'
}

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($relativePath in $sourcePaths) {
    $sourcePath = Join-Path $repositoryPath $relativePath
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        $failures.Add("Missing PowerShell source: $relativePath")
        continue
    }

    $tokens = $null
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile(
        $sourcePath, [ref]$tokens, [ref]$parseErrors)
    foreach ($parseError in $parseErrors) {
        $failures.Add(('{0}({1},{2}): {3} [{4}]' -f
            $relativePath,
            $parseError.Extent.StartLineNumber,
            $parseError.Extent.StartColumnNumber,
            $parseError.Message,
            $parseError.ErrorId))
    }
}

if ($failures.Count -gt 0) {
    throw ("PowerShell syntax validation failed:`n" + ($failures -join "`n"))
}

Write-Output ('PowerShell {0}: parsed {1} source files; 0 syntax errors.' -f
    $PSVersionTable.PSVersion, $sourcePaths.Count)
