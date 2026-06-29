# ClashSharp SandboxTest

This directory is the framework for full Windows Sandbox based smoke tests.
The current shape follows approach 3: PowerShell owns host orchestration and
Rust owns test-harness helper logic that is easy to unit test.

## Current scope

- `Run-SandboxTest.ps1` prepares the shared Sandbox directory.
- `scripts/Run-InSandbox.ps1` is copied into the shared directory and runs
  inside Windows Sandbox.
- `clashsharp-sandbox-test` generates the `.wsb` file and prints the dry-run
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

Run the Rust helper tests:

```powershell
cargo test
```

## Next implementation steps

1. Select or build the installer artifact from `artifacts`.
2. Copy the artifact into `.sandbox/shared`.
3. Expand `scripts/Run-InSandbox.ps1` to install ClashSharp.
4. Add structured smoke checks and write reports under `.sandbox/shared/reports`.
5. Teach the host runner to collect and validate those reports.
