# Phase 01 Repository Foundation Evidence

**Recorded:** 2026-07-19

**Branch:** `codex/architecture-stabilization-phase-01`

**Plan:** `docs/superpowers/plans/2026-07-19-repository-foundation-and-project-topology.md`

## Toolchain and repository policy

- `dotnet --version`: `10.0.201`
- `rustc --version`: `rustc 1.95.0 (59807616e 2026-04-14)`
- `cargo-audit --version`: `cargo-audit 0.22.2`
- LF policy: `.gitattributes` and `.editorconfig`
- Build policy: `Directory.Build.props`
- Immutable CI action revisions: `.github/workflows/ci.yml`

## TDD evidence

`RepositoryTopologyTests` was first run before the production projects existed and failed with `CS0234` for the missing `ClashSharp.Infrastructure` namespace. After the Core/Infrastructure projects, references, moved model, CI policy, and ledger were added, all five repository topology tests passed.

The dependency-audit exception contract was first run without `eng/dependency-audit-exceptions.json` and failed with `FileNotFoundException`. After the scoped, owned, expiring exception record was added, the contract passed.

## .NET verification

The following CI-equivalent sequence passed with `CI=true` and `Platform=x64`:

```powershell
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode
dotnet format ClashSharp/ClashSharp.slnx --verify-no-changes --no-restore
dotnet build ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build --logger "trx;LogFileName=tests.trx"
```

Result: Release build completed with 0 warnings and 0 errors; 683 tests passed, 0 failed, 0 skipped. `ActiveConnection` is emitted by `ClashSharp.Core.dll`, and `InfrastructureAssemblyMarker` is emitted by `ClashSharp.Infrastructure.dll`.

## Rust verification

Both `ClashSharp/Installer` and `ClashSharp/SandboxTest` passed `cargo fmt --check` plus locked `cargo clippy --all-targets -- -D warnings` and `cargo test --all-targets`. Installer passed 12 tests; SandboxTest passed 11 tests.

Updating the Installer lock graph from Slint 1.16.1 to 1.17.1 removed `RUSTSEC-2026-0204` by upgrading `crossbeam-epoch` to 0.9.20. `RUSTSEC-2026-0194` and `RUSTSEC-2026-0195` remain only in the Cargo.lock branch `winit -> wayland-scanner -> quick-xml`, which is not compiled into the `x86_64-pc-windows-msvc` release. The two exceptions expire on 2026-10-19 and are recorded in `eng/dependency-audit-exceptions.json`; four upstream maintenance warnings are assigned to Phase 11 there. With those scoped exceptions, Installer and SandboxTest audits completed with zero applicable vulnerabilities.

Target filtering was checked with `cargo tree --locked --target x86_64-pc-windows-msvc -i quick-xml`, which reported no matching package. `cargo tree --locked --target all -i quick-xml` exposes the non-Windows chain through `wayland-scanner 0.31.10`, providing the release-target evidence for the temporary exceptions.

## Pending external evidence

The GitHub workflow has not run on the remote service from this local branch. `P2-QA-01` therefore remains `Evidence Pending` rather than `Closed`. Most legacy test source links, `UNIT_TESTS` forks, and volatile headers also remain open for their scheduled migration phases.
