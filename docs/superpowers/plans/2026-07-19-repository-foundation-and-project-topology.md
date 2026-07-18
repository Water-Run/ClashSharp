# Repository Foundation and Project Topology Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make repository quality policy executable and prove the vertical migration path by compiling `ActiveConnection` from a production `ClashSharp.Core` assembly referenced by both the WinUI app and tests, while adding a real `ClashSharp.Infrastructure` boundary and pinned CI/toolchains.

**Architecture:** Add repository-wide build and text policy at the root. Introduce platform-neutral Core and Windows-targeted Infrastructure projects without moving interdependent runtime services yet. Move one public immutable domain type end-to-end, remove its test source link, and use architecture tests plus CI as ratchets for the new dependency direction.

**Tech Stack:** .NET SDK 10.0.201, C# 14, xUnit 2.9.3, Windows App SDK, Rust 1.95.0, cargo-audit 0.22.2, GitHub Actions on `windows-2025`.

## Global Constraints

- Work in an isolated worktree on branch `codex/architecture-stabilization-phase-01` after committing this plan.
- Use test-driven development: add the stated failing test, run it and record the expected failure, then implement only enough to pass.
- Do not move `RuntimeTrafficRateSnapshot` or a runtime service in this phase; its current internal visibility and source-linked dependency graph require a later vertical slice.
- Keep the namespace `ClashSharp.Model` for `ActiveConnection` in this phase to avoid presentation churn; physical project ownership changes now, namespace cleanup happens with its use-case migration.
- Core targets `net10.0` and has no WinUI, Windows App SDK, SQLite, process, registry, filesystem, or HTTP dependency.
- Infrastructure targets `net10.0-windows10.0.22000.0`, references Core, and is referenced by the app. It contains only a documented assembly marker until the first infrastructure adapter moves in a later phase.
- Existing source links and volatile headers are measured debt. This phase forbids new debt and removes the moved type's source link/header; Phase 12 removes the remaining inventory.
- All GitHub Actions use immutable 40-character commit SHAs and least-privilege `contents: read` permissions.

---

### Task 1: Add a failing repository topology contract

**Files:**
- Create: `ClashSharp/ClashSharp.Tests/Architecture/RepositoryTopologyTests.cs`
- Test: `ClashSharp/ClashSharp.Tests/Architecture/RepositoryTopologyTests.cs`

- [ ] **Step 1: Write the failing contract tests**

Create `RepositoryTopologyTests` with a local `FindRepositoryRoot` helper. The tests must assert:

```csharp
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClashSharp.Infrastructure;
using ClashSharp.Model;

namespace ClashSharp.Tests.Architecture;

/// <summary>Guards repository policy and production assembly boundaries.</summary>
public sealed class RepositoryTopologyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    /// <summary>Verifies required repository policy artifacts are version controlled.</summary>
    [Fact]
    public void RepositoryPolicyArtifacts_ArePresent()
    {
        string[] paths =
        [
            ".gitattributes",
            "global.json",
            "Directory.Build.props",
            "rust-toolchain.toml",
            ".github/workflows/ci.yml",
            "docs/architecture/stabilization-ledger.md",
        ];

        Assert.All(paths, path => Assert.True(File.Exists(Path.Combine(RepositoryRoot, path)), path));
    }

    /// <summary>Verifies tests and the app reference the production Core and Infrastructure projects.</summary>
    [Fact]
    public void ProductionProjects_AreReferencedWithoutActiveConnectionSourceLink()
    {
        string testProjectPath = Path.Combine(RepositoryRoot, "ClashSharp", "ClashSharp.Tests", "ClashSharp.Tests.csproj");
        string appProjectPath = Path.Combine(RepositoryRoot, "ClashSharp", "ClashSharp", "ClashSharp.csproj");
        XDocument testProject = XDocument.Load(testProjectPath);
        XDocument appProject = XDocument.Load(appProjectPath);

        AssertProjectReference(testProject, "ClashSharp.Core");
        AssertProjectReference(testProject, "ClashSharp.Infrastructure");
        AssertProjectReference(appProject, "ClashSharp.Core");
        AssertProjectReference(appProject, "ClashSharp.Infrastructure");

        IEnumerable<string> compileIncludes = testProject.Descendants("Compile")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>();
        Assert.DoesNotContain(compileIncludes, include => include.EndsWith("Model\\ActiveConnection.cs", StringComparison.Ordinal));
    }

    /// <summary>Verifies migrated types are loaded from production assemblies.</summary>
    [Fact]
    public void MigratedTypes_AreLoadedFromProductionAssemblies()
    {
        Assert.Equal("ClashSharp.Core", typeof(ActiveConnection).Assembly.GetName().Name);
        Assert.Equal("ClashSharp.Infrastructure", typeof(InfrastructureAssemblyMarker).Assembly.GetName().Name);
    }

    /// <summary>Verifies workflow actions are immutable and workflow permissions are read-only.</summary>
    [Fact]
    public void ContinuousIntegration_UsesImmutableActionsAndReadOnlyPermissions()
    {
        string workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "ci.yml"));
        MatchCollection uses = Regex.Matches(workflow, @"uses:\s*[^@\s]+@(?<revision>[^\s#]+)");

        Assert.NotEmpty(uses);
        Assert.All(uses.Cast<Match>(), match => Assert.Matches("^[0-9a-f]{40}$", match.Groups["revision"].Value));
        Assert.Contains("permissions:\n  contents: read", workflow.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target", workflow, StringComparison.Ordinal);
    }

    private static void AssertProjectReference(XDocument project, string projectName)
    {
        IEnumerable<string> includes = project.Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>();
        Assert.Contains(includes, include => include.Contains(projectName, StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ClashSharp", "ClashSharp.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ClashSharp repository root.");
    }
}
```

This test intentionally will not compile yet because the production projects and `ClashSharp.Infrastructure` namespace do not exist.

- [ ] **Step 2: Run the focused test and verify the RED state**

Run:

```powershell
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Debug --filter FullyQualifiedName~RepositoryTopologyTests
```

Expected: compilation fails with missing `ClashSharp.Infrastructure`/`InfrastructureAssemblyMarker` and missing project artifacts. Do not weaken or conditionally skip the assertions.

- [ ] **Step 3: Commit the test-only RED state only if the repository permits non-building commits**

Do not commit this deliberately non-building state in this repository. Continue to Task 2 and include the test with the first passing implementation commit.

### Task 2: Establish deterministic repository policy

**Files:**
- Create: `.gitattributes`
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `rust-toolchain.toml`
- Create: `eng/tool-versions.json`
- Modify: `.editorconfig`
- Replace: `CodingStyle.md`

- [ ] **Step 1: Pin the .NET and Rust toolchains**

Create `global.json`:

```json
{
  "sdk": {
    "version": "10.0.201",
    "rollForward": "disable",
    "allowPrerelease": false
  }
}
```

Create `rust-toolchain.toml`:

```toml
[toolchain]
channel = "1.95.0"
profile = "minimal"
components = ["clippy", "rustfmt"]
targets = ["x86_64-pc-windows-msvc"]
```

Create `eng/tool-versions.json`:

```json
{
  "cargoAudit": "0.22.2"
}
```

- [ ] **Step 2: Make LF and binary classification authoritative**

Create `.gitattributes`:

```gitattributes
* text=auto eol=lf
*.bmp binary
*.db binary
*.dll binary
*.exe binary
*.gif binary
*.ico binary
*.jpeg binary
*.jpg binary
*.msix binary
*.msixbundle binary
*.p12 binary
*.pfx binary
*.png binary
*.snk binary
*.sqlite binary
*.webp binary
*.zip binary
```

Change `.editorconfig` from `end_of_line = crlf` to `end_of_line = lf`. Preserve all other existing rules.

- [ ] **Step 3: Add build-enforced shared policy**

Create `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <LangVersion>14</LangVersion>
    <Nullable>enable</Nullable>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
    <TreatWarningsAsErrors Condition="'$(Configuration)' == 'Release' or '$(CI)' == 'true'">true</TreatWarningsAsErrors>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    <RestoreLockedMode Condition="'$(CI)' == 'true'">true</RestoreLockedMode>
    <NuGetAudit>true</NuGetAudit>
    <NuGetAuditMode>all</NuGetAuditMode>
    <NuGetAuditLevel>low</NuGetAuditLevel>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Replace prose-only coding rules with repository-aligned rules**

Replace `CodingStyle.md` with a concise policy that states:

```markdown
# ClashSharp Coding Style

ClashSharp targets .NET 10 and C# 14. Repository build configuration is authoritative when this document and executable policy disagree.

## Language and nullability

Nullable analysis is enabled at project level. Do not add redundant per-file `#nullable enable` directives. Public signatures express nullability accurately, validate inputs at trust boundaries, and use explicit result types for expected operational failures.

## Documentation

Document public contracts and non-obvious application interfaces with concise English XML documentation. Private members need documentation only when their invariant, ownership, cancellation, thread-safety, or side-effect behavior is not clear from structure and naming. Do not add volatile author, file-path, or last-modified-date banners; source control is authoritative.

## Structure and naming

Use file-scoped namespaces, braces for every control-flow body, PascalCase public symbols, camelCase locals and parameters, `_camelCase` private fields, `I`-prefixed interfaces, and `Async` suffixes for awaitable methods. A file normally contains one primary type whose name matches the file.

## Dependencies and side effects

Use constructor injection. Presentation code must not resolve application services through static `.Instance` access. Constructors validate immutable arguments only; they do not start tasks, open user data, mutate Windows or mihomo state, or register long-lived handlers. Every background operation has explicit ownership, cancellation, observation, and awaited shutdown.

## Async and errors

Avoid `async void` except platform event handlers. Cancellable I/O accepts a final `CancellationToken`. Never swallow exceptions or expose raw exception text to the UI. Route unexpected asynchronous failures to the application error sink and log only non-sensitive diagnostic data.

## Formatting

Repository text uses UTF-8, LF, final newlines, and trimmed trailing whitespace. `dotnet format --verify-no-changes` is the formatting contract. Prefer readable modern C# syntax and extract named steps when a LINQ or fluent chain becomes difficult to audit.
```

- [ ] **Step 5: Normalize tracked text and verify tool selection**

Run:

```powershell
git add --renormalize .
dotnet --version
rustc --version
cargo fmt --manifest-path ClashSharp/Installer/Cargo.toml -- --check
cargo fmt --manifest-path ClashSharp/SandboxTest/Cargo.toml -- --check
```

Expected: `dotnet --version` is exactly `10.0.201`; `rustc --version` begins `rustc 1.95.0`; both formatting checks exit zero. Review `git diff --stat` and confirm non-policy bulk changes are line-ending-only.

### Task 3: Create production project boundaries and move one domain type

**Files:**
- Create: `ClashSharp/ClashSharp.Core/ClashSharp.Core.csproj`
- Create: `ClashSharp/ClashSharp.Core/Domain/Connections/ActiveConnection.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/ClashSharp.Infrastructure.csproj`
- Create: `ClashSharp/ClashSharp.Infrastructure/InfrastructureAssemblyMarker.cs`
- Delete: `ClashSharp/ClashSharp/Model/ActiveConnection.cs`
- Modify: `ClashSharp/ClashSharp/ClashSharp.csproj`
- Modify: `ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj`
- Modify: `ClashSharp/ClashSharp.slnx`
- Test: `ClashSharp/ClashSharp.Tests/Architecture/RepositoryTopologyTests.cs`

- [ ] **Step 1: Create platform-neutral Core**

Create `ClashSharp.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>ClashSharp</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <WarningsAsErrors>$(WarningsAsErrors);CS1591</WarningsAsErrors>
  </PropertyGroup>
</Project>
```

Move `ActiveConnection` to `Domain/Connections/ActiveConnection.cs`, preserve its namespace and API, remove the volatile banner and redundant `using System;`, and keep its existing public XML documentation.

- [ ] **Step 2: Create Windows Infrastructure boundary**

Create `ClashSharp.Infrastructure.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.22000.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.22000.0</TargetPlatformMinVersion>
    <RootNamespace>ClashSharp</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <WarningsAsErrors>$(WarningsAsErrors);CS1591</WarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\ClashSharp.Core\ClashSharp.Core.csproj" />
  </ItemGroup>
</Project>
```

Create the marker:

```csharp
namespace ClashSharp.Infrastructure;

/// <summary>Identifies the assembly that owns operating-system and persistence adapters.</summary>
public static class InfrastructureAssemblyMarker
{
}
```

- [ ] **Step 3: Wire the solution and production references**

Add Core and Infrastructure project entries to `ClashSharp.slnx`. Add ordinary `ProjectReference` entries from both `ClashSharp.csproj` and `ClashSharp.Tests.csproj` to both new projects. Remove only this source-link entry from the test project:

```xml
<Compile Include="..\ClashSharp\Model\ActiveConnection.cs" Link="Model\ActiveConnection.cs" />
```

Do not alter other compatibility source links in this task.

- [ ] **Step 4: Run the topology contract and relevant behavior tests**

Run:

```powershell
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Debug --filter "FullyQualifiedName~RepositoryTopologyTests|FullyQualifiedName~ConnectionSamplingServiceTests|FullyQualifiedName~RuntimeTrafficRateServiceTests"
dotnet build ClashSharp/ClashSharp.slnx -c Debug -p:Platform=x64
```

Expected: the topology contract passes; connection behavior is unchanged; the complete solution builds with `ActiveConnection` emitted only by `ClashSharp.Core.dll`.

- [ ] **Step 5: Commit the first passing architecture slice**

```powershell
git add .gitattributes .editorconfig global.json Directory.Build.props CodingStyle.md rust-toolchain.toml eng/tool-versions.json ClashSharp/ClashSharp.slnx ClashSharp/ClashSharp.Core ClashSharp/ClashSharp.Infrastructure ClashSharp/ClashSharp/ClashSharp.csproj ClashSharp/ClashSharp/Model/ActiveConnection.cs ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj ClashSharp/ClashSharp.Tests/Architecture/RepositoryTopologyTests.cs
git commit -m "build: establish production project boundaries"
```

### Task 4: Lock dependencies and add the stabilization ledger

**Files:**
- Create: `ClashSharp/ClashSharp/packages.lock.json`
- Create: `ClashSharp/ClashSharp.Tests/packages.lock.json`
- Create: `ClashSharp/ClashSharp.MihomoService/packages.lock.json`
- Create if generated: `ClashSharp/ClashSharp.Core/packages.lock.json`
- Create if generated: `ClashSharp/ClashSharp.Infrastructure/packages.lock.json`
- Create: `docs/architecture/stabilization-ledger.md`
- Modify: `ClashSharp/Installer/Cargo.lock` only if the pinned toolchain makes a deterministic lockfile update
- Modify: `ClashSharp/SandboxTest/Cargo.lock` only if the pinned toolchain makes a deterministic lockfile update

- [ ] **Step 1: Generate and validate .NET lock files**

Run:

```powershell
dotnet restore ClashSharp/ClashSharp.slnx -p:RestorePackagesWithLockFile=true
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode
```

Expected: lock files are created for projects with resolved packages; the second restore exits zero without changing them. Do not hand-edit generated lock files.

- [ ] **Step 2: Create the stabilization ledger**

Create `docs/architecture/stabilization-ledger.md` with columns `ID`, `Severity`, `Owner`, `Status`, `Plan task`, `Evidence`, `Closure commit`, `Reviewer`, and `Closure date`. Add one row for every ID in the normative traceability table. Mark `P1-11`, `P2-QA-01`, and `P3 nullable/docs/analyzer/header drift` as `In Progress` with this plan path; all other rows start `Open`. Use `—` for evidence/closure fields until a real artifact exists; never mark a row `Closed` without evidence and a commit.

- [ ] **Step 3: Verify Rust locks and audit the resolved dependency graphs**

Run:

```powershell
cargo test --manifest-path ClashSharp/Installer/Cargo.toml --locked --all-targets
cargo test --manifest-path ClashSharp/SandboxTest/Cargo.toml --locked --all-targets
cargo install cargo-audit --version 0.22.2 --locked
cargo audit --file ClashSharp/Installer/Cargo.lock
cargo audit --file ClashSharp/SandboxTest/Cargo.lock
```

Expected: tests and both audits exit zero. If an advisory is present, stop this task and fix or explicitly document the affected dependency before creating CI; do not add an ignore merely to obtain green output.

- [ ] **Step 4: Commit locks and the ledger**

```powershell
git add ClashSharp/*/packages.lock.json ClashSharp/Installer/Cargo.lock ClashSharp/SandboxTest/Cargo.lock docs/architecture/stabilization-ledger.md
git commit -m "build: lock dependency resolution and track stabilization"
```

### Task 5: Add pinned Windows CI and close first-phase evidence

**Files:**
- Create: `.github/workflows/ci.yml`
- Modify: `docs/architecture/stabilization-ledger.md`
- Test: `ClashSharp/ClashSharp.Tests/Architecture/RepositoryTopologyTests.cs`

- [ ] **Step 1: Create least-privilege pinned CI**

Create `.github/workflows/ci.yml` exactly as follows. The pinned revisions correspond to official `actions/checkout` v7.0.0, `actions/setup-dotnet` v6.0.0, and `actions/upload-artifact` v7.0.1 releases verified on 2026-07-19.

```yaml
name: CI

on:
  pull_request:
  push:
    branches:
      - main

permissions:
  contents: read

concurrency:
  group: ci-${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true

jobs:
  dotnet:
    name: .NET build, format, and test
    runs-on: windows-2025
    steps:
      - name: Check out repository
        uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0

      - name: Set up pinned .NET SDK
        uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68
        with:
          global-json-file: global.json

      - name: Restore locked dependencies
        run: dotnet restore ClashSharp/ClashSharp.slnx --locked-mode

      - name: Verify formatting
        run: dotnet format ClashSharp/ClashSharp.slnx --verify-no-changes --no-restore

      - name: Build Release
        run: dotnet build ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore

      - name: Run tests
        run: dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build --logger "trx;LogFileName=tests.trx"

      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a
        with:
          name: dotnet-test-results
          path: ClashSharp/ClashSharp.Tests/TestResults/**/*.trx
          if-no-files-found: error

  rust:
    name: Rust ${{ matrix.name }}
    runs-on: windows-2025
    strategy:
      fail-fast: false
      matrix:
        include:
          - name: Installer
            manifest: ClashSharp/Installer/Cargo.toml
            lockfile: ClashSharp/Installer/Cargo.lock
          - name: SandboxTest
            manifest: ClashSharp/SandboxTest/Cargo.toml
            lockfile: ClashSharp/SandboxTest/Cargo.lock
    steps:
      - name: Check out repository
        uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0

      - name: Activate pinned Rust toolchain
        run: rustup show

      - name: Verify formatting
        run: cargo fmt --manifest-path ${{ matrix.manifest }} -- --check

      - name: Run Clippy
        run: cargo clippy --manifest-path ${{ matrix.manifest }} --locked --all-targets -- -D warnings

      - name: Run tests
        run: cargo test --manifest-path ${{ matrix.manifest }} --locked --all-targets

      - name: Install pinned cargo-audit
        run: cargo install cargo-audit --version 0.22.2 --locked

      - name: Audit dependencies
        run: cargo audit --file ${{ matrix.lockfile }}
```

Do not add caches, write permissions, secrets, `pull_request_target`, mutable action tags, or network downloads beyond package/tool restore in this baseline workflow.

- [ ] **Step 2: Run every CI command locally in the same order**

Run:

```powershell
$env:CI = 'true'
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode
dotnet format ClashSharp/ClashSharp.slnx --verify-no-changes --no-restore
dotnet build ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build --logger "trx;LogFileName=tests.trx"
cargo fmt --manifest-path ClashSharp/Installer/Cargo.toml -- --check
cargo clippy --manifest-path ClashSharp/Installer/Cargo.toml --locked --all-targets -- -D warnings
cargo test --manifest-path ClashSharp/Installer/Cargo.toml --locked --all-targets
cargo audit --file ClashSharp/Installer/Cargo.lock
cargo fmt --manifest-path ClashSharp/SandboxTest/Cargo.toml -- --check
cargo clippy --manifest-path ClashSharp/SandboxTest/Cargo.toml --locked --all-targets -- -D warnings
cargo test --manifest-path ClashSharp/SandboxTest/Cargo.toml --locked --all-targets
cargo audit --file ClashSharp/SandboxTest/Cargo.lock
Remove-Item Env:CI
```

Expected: every command exits zero, `dotnet format` reports no changes, all current .NET tests pass, both Rust projects pass fmt/clippy/test/audit, and a TRX file exists.

- [ ] **Step 3: Update ledger evidence without overstating closure**

Record the topology test, workflow path, local TRX, format output, locked restore, and Rust outputs as evidence. Set `P2-QA-01` to `Evidence Pending` until the workflow has run on GitHub. Keep `P1-11` `In Progress` because most test source links and `UNIT_TESTS` still exist. Keep the nullable/header row `In Progress` because legacy volatile headers remain inventoried debt.

- [ ] **Step 4: Review the complete phase diff and commit**

Run:

```powershell
git diff --check
git status --short
git diff --stat
git diff -- .github/workflows/ci.yml docs/architecture/stabilization-ledger.md
git add .github/workflows/ci.yml docs/architecture/stabilization-ledger.md
git commit -m "ci: enforce locked cross-language quality gates"
```

Expected: no whitespace errors, only intentional phase files/normalization are present, and the commit succeeds.

### Task 6: Phase completion review

**Files:**
- Modify: `docs/superpowers/plans/2026-07-19-repository-foundation-and-project-topology.md`
- Modify: `docs/superpowers/plans/2026-07-19-architecture-stabilization-roadmap.md`

- [ ] **Step 1: Run final clean-tree verification from scratch**

Run:

```powershell
git clean -ndx
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode --force
dotnet format ClashSharp/ClashSharp.slnx --verify-no-changes --no-restore
dotnet build ClashSharp/ClashSharp.slnx -c Debug -p:Platform=x64 --no-restore
dotnet build ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build
git diff --check
```

Expected: the clean preview lists only ignored build/tool artifacts, both configurations build, all .NET tests pass, locked restore and formatting pass, and no diff error is reported.

- [ ] **Step 2: Inspect dependency direction and migration proof**

Confirm:

```powershell
dotnet msbuild ClashSharp/ClashSharp.Core/ClashSharp.Core.csproj -getProperty:TargetFramework
rg -n "ActiveConnection.cs" ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj ClashSharp/ClashSharp.Core ClashSharp/ClashSharp
rg -n "ProjectReference" ClashSharp/ClashSharp.Core ClashSharp/ClashSharp.Infrastructure ClashSharp/ClashSharp/ClashSharp.csproj ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj
```

Expected: Core reports `net10.0`; the only production definition is under Core; tests have no ActiveConnection source link; dependency direction is Infrastructure → Core and App/Tests → Core + Infrastructure.

- [ ] **Step 3: Mark plan state and obtain task-scoped code review**

Check off completed items in this plan and Phase 01 in the roadmap. Use `superpowers:requesting-code-review`, address findings with `superpowers:receiving-code-review`, rerun final verification, then commit plan-state/evidence updates with:

```powershell
git add -f docs/superpowers/plans/2026-07-19-repository-foundation-and-project-topology.md docs/superpowers/plans/2026-07-19-architecture-stabilization-roadmap.md
git commit -m "docs: record repository foundation evidence"
```

- [ ] **Step 4: Begin Phase 02 only after this checkpoint is green**

Create the Phase 02 detailed plan against the actual post-Phase-01 topology. Do not create empty future projects or move runtime services speculatively in this phase.
