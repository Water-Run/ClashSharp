# ClashSharp SandboxTest

This directory contains the PowerShell driver for full Windows Sandbox based
smoke tests. The driver owns host orchestration and emits each isolated `.wsb`
configuration without an additional helper runtime.

## Current scope

- `Run-SandboxTest.ps1` prepares one isolated shared directory per scenario.
- `scripts/Run-InSandbox.ps1` is copied into the shared directory and runs
  inside Windows Sandbox.
- `install-only` imports the selected certificate, installs the MSIX and its
  dependencies, and verifies the exact package registration.
- The host accepts only an exact `passed` report bound to the known schema,
  scenario, run ID, valid timestamps/environment, passed steps, and
  scenario-specific checks. `install-only` additionally requires the fixed
  package identity and complete package evidence. Unknown fields, `skipped`,
  failed, timed-out, empty, mismatched, and partially written reports fail.
- `launch-no-proxy`, `startup-with-proxy-config`, `cleanup-uninstall`, and
  `real-proxy-optional` currently publish explicit failed/not-executed evidence.
  They have no passing host contract and cannot be mistaken for passing.

## Usage

Run a dry preparation pass:

```powershell
.\Run-SandboxTest.ps1
```

Generate files and open Windows Sandbox:

```powershell
.\Run-SandboxTest.ps1 -Launch
```

Run the host-side report contract without launching Sandbox:

```powershell
.\Test-SandboxReportContract.ps1
```

`-Scenario all -Launch` intentionally fails until all three required smoke
scenarios have executable evidence. The optional real-proxy scenario is never
included by `all` and also fails when selected without explicit inputs.

## Next implementation steps

1. Implement `launch-no-proxy` with packaged-process identity and clean WinINet
   postconditions.
2. Implement `startup-with-proxy-config` with explicit seeded proxy ownership
   and restoration evidence.
3. Implement `cleanup-uninstall` with package, process, certificate, service,
   payload, and proxy absence postconditions.
4. Add the optional real-proxy inputs and bounded connectivity assertions.
