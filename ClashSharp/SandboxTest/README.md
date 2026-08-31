# ClashSharp SandboxTest

This directory contains the PowerShell driver for full Windows Sandbox based
smoke tests. The driver owns host orchestration and emits each isolated `.wsb`
configuration without an additional helper runtime.

## Current scope

- `Run-SandboxTest.ps1` prepares the shared Sandbox directory.
- `scripts/Run-InSandbox.ps1` is copied into the shared directory and runs
  inside Windows Sandbox.
- `Run-SandboxTest.ps1` generates the `.wsb` file and prints the dry-run
  execution plan.
- No real install, launch, proxy, or service checks run yet.

## Usage

Run a dry preparation pass:

```powershell
.\Run-SandboxTest.ps1
```

Generate files and open Windows Sandbox:

```powershell
.\Run-SandboxTest.ps1 -Launch
```

## Next implementation steps

1. Select or build the installer artifact from `artifacts`.
2. Copy the artifact into `.sandbox/shared`.
3. Expand `scripts/Run-InSandbox.ps1` to install ClashSharp.
4. Add structured smoke checks and write reports under `.sandbox/shared/reports`.
5. Teach the host runner to collect and validate those reports.
