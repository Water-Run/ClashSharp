[CmdletBinding()]
param(
    [switch]$Launch,
    [string]$Configuration = "Debug"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $PSCommandPath
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..\..")
$manifestPath = Join-Path $scriptRoot "Cargo.toml"
$sharedDir = Join-Path $scriptRoot ".sandbox\shared"
$sharedScriptsDir = Join-Path $sharedDir "scripts"
$sandboxScript = Join-Path $scriptRoot "scripts\Run-InSandbox.ps1"
$sandboxScriptTarget = Join-Path $sharedScriptsDir "Run-InSandbox.ps1"
$hostPlanPath = Join-Path $sharedDir "host-plan.json"

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw "Rust cargo was not found on PATH. Install Rust before running SandboxTest."
}

New-Item -ItemType Directory -Force -Path $sharedScriptsDir | Out-Null
Copy-Item -Force -Path $sandboxScript -Destination $sandboxScriptTarget

$hostPlan = [ordered]@{
    generatedAt = (Get-Date).ToUniversalTime().ToString("o")
    configuration = $Configuration
    repoRoot = $repoRoot.Path
    artifactsDir = (Join-Path $repoRoot.Path "artifacts")
    sharedDir = $sharedDir
    purpose = "Framework placeholder for future ClashSharp Windows Sandbox end-to-end tests."
}

$hostPlan | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 -Path $hostPlanPath

$emitArgs = @(
    "run",
    "--quiet",
    "--manifest-path",
    $manifestPath,
    "--",
    "emit-wsb",
    "--repo-root",
    $repoRoot.Path
)
$wsbPath = (& cargo @emitArgs | Select-Object -Last 1)

$planArgs = @(
    "run",
    "--quiet",
    "--manifest-path",
    $manifestPath,
    "--",
    "plan",
    "--repo-root",
    $repoRoot.Path
)
& cargo @planArgs

Write-Host ""
Write-Host "Prepared Sandbox files:"
Write-Host "  WSB: $wsbPath"
Write-Host "  Shared: $sharedDir"
Write-Host "  Host plan: $hostPlanPath"

if ($Launch) {
    if (-not (Test-Path $wsbPath)) {
        throw "Expected WSB file was not created: $wsbPath"
    }

    Start-Process -FilePath $wsbPath
    Write-Host "Windows Sandbox launch requested."
} else {
    Write-Host "Dry run complete. Re-run with -Launch to open Windows Sandbox."
}
