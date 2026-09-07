# ClashSharp SandboxTest

Offline Windows 11 package smoke tests with explicit candidate selection and
owned Sandbox sessions. The host requires PowerShell 7.4 or later and the
installed `wsb.exe` CLI. Guest scripts and contract tests support Windows
PowerShell 5.1. The runner does not enable Windows features or change host
packages, certificates, services, or proxy settings.

## Usage

Prepare a verified candidate without launching a guest:

```powershell
.\Run-SandboxTest.ps1 -PayloadPath D:\candidate\payload
```

Run the implemented offline scenarios for a development candidate:

```powershell
.\Run-SandboxTest.ps1 -PayloadPath D:\candidate\payload `
    -Scenario 'install-only,launch-no-proxy' -AllowSelfSignedCandidate -Launch
```

`-AllowSelfSignedCandidate` permits an explicitly selected self-signed
development certificate that the host does not trust. It does not add host
trust. The exact public certificate is temporarily added to **the guest's
LocalMachine/TrustedPeople** store, and AppX verifies the package during
deployment. A Windows 11 guest rejected current-user-only trust with
`0x800B0109` during actual validation.

The required source is the exact payload directory containing
`payload-provenance.json`. The runner copies only its four supported payload
files; it never searches the general artifacts tree or picks files by date.
Host checks bind lengths, SHA-256, MSIX identities, executable bytes, and signer
identity to the selected provenance. Guest checks revalidate the copied bytes.

## Executed scenarios

- `install-only` installs the selected dependency and application, verifies the
  exact package registration, then removes resources introduced by the test.
- `launch-no-proxy` additionally starts the installed executable, reads its
  actual Windows package identity, and requires a live main window throughout
  a 30-second observation interval. Cleanup terminates the process owned by
  this test; this is not evidence of graceful application exit.

Both scenarios require an initially disabled guest proxy, disabled networking,
read-only inputs, and no preexisting target package, certificate, or staging
directory. They check package and certificate absence, dependency restoration,
staging removal, process absence, unchanged ClashSharp services, and unchanged
WinINet proxy values after cleanup.

`startup-with-proxy-config`, `cleanup-uninstall`, `real-proxy-optional` and
`all` are rejected before any guest starts. They are not implemented or counted
as passing. In particular, direct MSIX removal does not validate the WPF
installer's complete uninstall, application data deletion, or recovery journal.

## Isolation and reports

- Each run gets a new run ID and explicitly reserved Sandbox ID. An exclusive
  runner mutex prevents concurrent ClashSharp runners from overlapping.
- Only the generated input directory is mapped read-only. A separate fresh
  directory accepts guest reports. Networking, clipboard, audio, video,
  printer, and virtual GPU sharing are disabled.
- The guest entry point refuses a host path or account before importing helpers.
  It then validates its Windows client build, architecture, account, profile,
  machine identity, original plan digest, and actual read-only mapping.
  Registry evidence is used because CIM queries fail in some Sandbox images.
- The host accepts only complete schema 2 reports bound to the original plan,
  candidate, run and Sandbox IDs. Required steps, their order and durations,
  typed fields, package identity, and all cleanup checks must agree.
- Guest errors include stable step/type/HRESULT codes; raw exception messages,
  proxy values, private configurations and machine environment dumps are not
  written to reports.
- The host always stops its reserved Sandbox ID, checks the ID is absent,
  compares input hashes, and verifies the host's proxy fingerprint. It does
  not terminate other Sandbox sessions or proxy processes.
- CLI calls have deadlines and drained cancellation. The desktop connection
  command does not redirect pipes that its surviving child can inherit.

Evidence remains under `.sandbox/runs/<runId>/`. `reports/result.json` is the
guest's terminal report; `host-result.json` separately records guest acceptance,
Sandbox disposal, unchanged inputs and unchanged host proxy. An unaccepted
guest report or failed host postcondition causes a failing command.

Run the host-only contract suite with either supported PowerShell edition:

```powershell
.\Test-SandboxReportContract.ps1
```

These tests exercise real fixture bytes and rejected evidence without any
package, certificate, service, or proxy mutation. They cannot establish
production installer readiness or prove an arbitrary guest is trustworthy.
