# AppHost and Startup Ownership Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ensure primary-instance ownership is established before AppHost construction or any shared-data, Windows, mihomo, trigger, sampling, audit, repository, or window side effect.

**Architecture:** Add a platform-neutral `ClashSharp.Application` production assembly containing the launch orchestration, side-effect-free DI host, ordered startup pipeline, and App-owned lifetime runner. WinUI supplies a narrow Windows App SDK `AppInstance` adapter and concrete startup steps. A deterministic unit suite and a real two-process probe prove that a secondary launch redirects/exits without constructing the host or writing the fake shared-state marker.

**Tech Stack:** .NET 10, C# 14, Microsoft.Extensions.DependencyInjection 10.0.0, Windows App SDK 1.8, xUnit.

---

## Normative constraints

- `App.OnLaunched` constructs only the outer lifetime runner, primary-instance adapter, and `ApplicationBootstrapper` before ownership.
- The AppHost factory is passed as a lazy delegate and is never invoked for a secondary instance.
- `AppHost.Build` registers service types/factories and builds the provider without resolving services. Constructors for startup steps and coordinators are not invoked until `StartAsync`.
- The startup coordinator executes stable ordered steps, stops on an explicit exit/fatal result, and returns typed outcomes.
- Existing proxy recovery is awaited before window construction, so it cannot race the window's configured startup-mode application.
- Trigger, sampling, audit, settings, localization, LocalData, SQLite, process, registry, and window APIs are unreachable from the secondary path.
- The `ProcessLifetimeRunner` lives outside the provider, stops then asynchronously disposes an attached host at most once, and is safe under concurrent stop requests.
- The obsolete process-enumeration/kill dialog path is removed from `MainWindow`; redirected activation brings the existing primary window forward.
- Phase 03 remains responsible for the mutation journal, full network coordinator, supervisor quiescence, and restricted-exit protocol. This phase must not pretend those later contracts are closed.

### Task 1: Add failing production startup-contract tests

**Files:**
- Create: `ClashSharp/ClashSharp.Application/ClashSharp.Application.csproj`
- Create: `ClashSharp/ClashSharp.Tests/Architecture/ApplicationStartupContractTests.cs`
- Modify: `ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj`
- Modify: `ClashSharp/ClashSharp.slnx`

- [ ] **Step 1: Create the Application project shell and references**

Create a `net10.0` project that references Core, generates XML documentation, and treats `CS1591` as an error. Add `Microsoft.Extensions.DependencyInjection` 10.0.0. Reference Application from the app and tests and add it to the solution. Do not add WinUI or Windows targeting to Application.

- [ ] **Step 2: Write RED tests against missing production contracts**

Tests must require these production types and behaviors:

- `ApplicationBootstrapper` acquires primary ownership before invoking the host factory;
- secondary ownership returns `Redirected` and leaves the host factory count at zero;
- primary ownership creates and starts exactly one host in `arbitrate, host-build, host-start` order;
- startup failure stops/disposes the just-created host and never attaches a leaked lifetime;
- `AppHost.Build` does not instantiate a registered startup coordinator or startup step;
- `AppHost.StartAsync` resolves the coordinator once and returns its typed result;
- ordered startup steps use `(Order, Name)` determinism and stop after `ExitRequested` or `Fatal`;
- `ProcessLifetimeRunner.StopAsync` calls host stop before dispose exactly once under concurrent callers.

- [ ] **Step 3: Run the focused suite and capture RED**

Run:

```powershell
$env:Platform = 'x64'
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~ApplicationStartupContractTests
Remove-Item Env:Platform
```

Expected: compilation fails because the Application startup contracts do not exist. Record the failure in Phase 02 evidence; do not commit a non-building state.

### Task 2: Implement the platform-neutral launch, host, pipeline, and lifetime contracts

**Files:**
- Create: `ClashSharp/ClashSharp.Application/Startup/AppLaunchRequest.cs`
- Create: `ClashSharp/ClashSharp.Application/Startup/PrimaryInstanceOwnership.cs`
- Create: `ClashSharp/ClashSharp.Application/Startup/IPrimaryInstanceBootstrap.cs`
- Create: `ClashSharp/ClashSharp.Application/Startup/ApplicationLaunchResult.cs`
- Create: `ClashSharp/ClashSharp.Application/Startup/ApplicationBootstrapper.cs`
- Create: `ClashSharp/ClashSharp.Application/Startup/StartupStepResult.cs`
- Create: `ClashSharp/ClashSharp.Application/Startup/IStartupStep.cs`
- Create: `ClashSharp/ClashSharp.Application/Startup/StartupCoordinator.cs`
- Create: `ClashSharp/ClashSharp.Application/Hosting/IApplicationHost.cs`
- Create: `ClashSharp/ClashSharp.Application/Hosting/IApplicationShutdownCoordinator.cs`
- Create: `ClashSharp/ClashSharp.Application/Hosting/AppHost.cs`
- Create: `ClashSharp/ClashSharp.Application/Hosting/ProcessLifetimeRunner.cs`

- [ ] **Step 1: Implement typed ownership and launch results**

Use explicit `Primary` and `Redirected` results. `IPrimaryInstanceBootstrap.AcquireAsync` owns redirection; `ApplicationBootstrapper` only decides whether the lazy host factory may run. Validate all injected arguments and propagate cancellation before host startup.

- [ ] **Step 2: Implement side-effect-free AppHost construction**

`AppHost.Build(Action<IServiceCollection>)` may allocate the collection/provider but must not call `GetService`, enumerate startup steps, or instantiate registered implementations. `StartAsync` resolves `StartupCoordinator` lazily. `StopAsync` delegates to an optional/no-op shutdown coordinator and is idempotent. Async disposal follows stop and is idempotent.

- [ ] **Step 3: Implement ordered typed startup steps**

Each step has stable `Name` and integer `Order`. Reject duplicate `(Order, Name)` registrations, order by `Order` then ordinal `Name`, and execute sequentially. Continue on `Succeeded`/`Warning`, return immediately for `ExitRequested`/`Fatal`, and convert no unexpected exception to apparent success.

- [ ] **Step 4: Implement the App-owned lifetime runner**

Allow exactly one host attachment. Concurrent `StopAsync` callers await the same task. The runner calls host `StopAsync` and then `DisposeAsync`; attach-after-stop and second-host attachment fail deterministically. A host startup exception is disposed directly by `ApplicationBootstrapper` before it can be attached.

- [ ] **Step 5: Run GREEN tests and the Application build**

```powershell
$env:Platform = 'x64'
dotnet format ClashSharp/ClashSharp.slnx --no-restore
dotnet build ClashSharp/ClashSharp.Application/ClashSharp.Application.csproj -c Debug
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~ApplicationStartupContractTests
Remove-Item Env:Platform
```

Expected: all focused tests pass and Application remains platform-neutral.

### Task 3: Add the Windows primary-instance adapter and move launch ownership into App

**Files:**
- Create: `ClashSharp/ClashSharp/AppHost/WindowsPrimaryInstanceBootstrap.cs`
- Create: `ClashSharp/ClashSharp/AppHost/ClashSharpAppHostFactory.cs`
- Modify: `ClashSharp/ClashSharp/App.xaml.cs`

- [ ] **Step 1: Wrap Windows App SDK AppInstance**

Use `AppInstance.FindOrRegisterForKey` with one stable application key. For a secondary instance, obtain the current activation arguments, await `RedirectActivationToAsync`, and return `Redirected`. For the primary, subscribe to `AppInstance.Activated` and marshal the bring-to-front callback onto the WinUI dispatcher. The adapter constructor must not register, read LocalData, or resolve services.

- [ ] **Step 2: Make App.OnLaunched ownership-first**

`OnLaunched` may be `async void` only as the framework event override. It creates the outer runner and adapter, awaits `ApplicationBootstrapper.LaunchAsync`, exits immediately for `Redirected` or helper-completed outcomes, and never calls AppHost construction directly before ownership. Catch startup failures at this boundary, stop/dispose any attached host, report through one safe diagnostic path, and exit without an unobserved task.

- [ ] **Step 3: Handle redirected activation without a second window**

Store the primary `Window` only after the window startup step creates it. A redirected activation activates that window on its dispatcher; if activation arrives before window creation, retain one pending bring-to-front request and consume it after attachment.

### Task 4: Replace direct App startup side effects with registered ordered steps

**Files:**
- Create: `ClashSharp/ClashSharp/AppHost/Startup/ConfigureLocalizationStartupStep.cs`
- Create: `ClashSharp/ClashSharp/AppHost/Startup/StartupRestoreFallbackStep.cs`
- Create: `ClashSharp/ClashSharp/AppHost/Startup/ProxyRecoveryStartupStep.cs`
- Create: `ClashSharp/ClashSharp/AppHost/Startup/AppSettingsAuditStartupStep.cs`
- Create: `ClashSharp/ClashSharp/AppHost/Startup/TriggerSupervisorStartupStep.cs`
- Create: `ClashSharp/ClashSharp/AppHost/Startup/WindowShellStartupStep.cs`
- Create: `ClashSharp/ClashSharp/AppHost/Startup/ConnectionSamplingStartupStep.cs`
- Modify: `ClashSharp/ClashSharp/App.xaml.cs`
- Modify: `ClashSharp/ClashSharp/MainWindow.xaml.cs`

- [ ] **Step 1: Register startup steps by implementation type**

The factory registers types/factories only; it does not dereference any legacy `.Instance` property while building the provider. Compatibility steps may resolve a legacy singleton only inside `ExecuteAsync`, after primary ownership.

- [ ] **Step 2: Await proxy recovery before constructing the window**

Move fallback and ordinary recovery logic out of `App`. Recovery remains best-effort and logged, but its task is tracked and awaited. Complete recovery before `WindowShellStartupStep`; this removes the existing `Task.Run(ApplyStartupProxyRecovery)` versus `MainWindow.ApplyMode` race without prematurely implementing Phase 03's network transaction model.

- [ ] **Step 3: Start owned services only in the primary pipeline**

Start audit, trigger runtime, window shell, and sampling through explicit ordered steps. Helper launch returns `ExitRequested` before audit/trigger/window/sampling. Normal launch cannot reach any of these steps from the secondary path.

- [ ] **Step 4: Remove MainWindow single-instance UI and process killing**

Delete `ResolveSingleInstanceConflictAsync`, the process-enumeration service and tests, and the obsolete localization dialog keys. Keep startup prompts/conflict diagnostics after the shell becomes available. Update source-contract tests so they no longer require direct service starts in `App.xaml.cs`.

### Task 5: Add the real two-process zero-side-effect regression

**Files:**
- Create: `ClashSharp/ClashSharp.StartupProbe/ClashSharp.StartupProbe.csproj`
- Create: `ClashSharp/ClashSharp.StartupProbe/Program.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/SecondaryInstanceIsolationTests.cs`
- Modify: `ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj`
- Modify: `ClashSharp/ClashSharp.slnx`

- [ ] **Step 1: Build a process-independent arbitration probe**

The helper uses a unique named mutex as its injected primary-instance boundary and `ApplicationBootstrapper` from the production Application assembly. Its host factory appends to a unique shared marker only when invoked. The primary holds until a release file appears; the secondary redirects logically and exits.

- [ ] **Step 2: Write and run the two-process test**

Start the primary, wait for its readiness marker, start and await the secondary, release the primary, and assert exactly one host-build/start marker and zero secondary shared-state markers. Bound every wait and kill both helpers in test cleanup.

```powershell
$env:Platform = 'x64'
dotnet build ClashSharp/ClashSharp.slnx -c Debug -p:Platform=x64
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Debug -p:Platform=x64 --no-build --filter FullyQualifiedName~SecondaryInstanceIsolationTests
Remove-Item Env:Platform
```

Expected: the two-process test passes repeatedly and never leaves a helper process behind.

### Task 6: Lock dependencies, update evidence, and verify the phase

**Files:**
- Create: `ClashSharp/ClashSharp.Application/packages.lock.json`
- Create: `ClashSharp/ClashSharp.StartupProbe/packages.lock.json`
- Create: `docs/architecture/evidence/phase-02-apphost-startup.md`
- Modify: `docs/architecture/stabilization-ledger.md`
- Modify: `docs/superpowers/plans/2026-07-19-architecture-stabilization-roadmap.md`
- Modify: this plan

- [ ] **Step 1: Generate and validate locked restores**

```powershell
dotnet restore ClashSharp/ClashSharp.slnx --force-evaluate
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode
```

Commit every generated project lock file and ensure the CI solution restore covers the two new projects.

- [ ] **Step 2: Run clean final verification**

```powershell
$env:CI = 'true'
$env:Platform = 'x64'
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode --force
dotnet format ClashSharp/ClashSharp.slnx --verify-no-changes --no-restore
dotnet build ClashSharp/ClashSharp.slnx -c Debug -p:Platform=x64 --no-restore -t:Rebuild
dotnet build ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore -t:Rebuild
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build
Remove-Item Env:CI
Remove-Item Env:Platform
git diff --check
```

Expected: zero warnings/errors, all existing and new tests pass, and formatting/locked restore remain clean.

- [ ] **Step 3: Prove ordering and forbidden old paths**

Confirm through behavior tests plus targeted inspection that:

- secondary outcome has no host or marker;
- AppHost build has no service instantiation;
- App uses the lazy host factory only through `ApplicationBootstrapper`;
- `App.xaml.cs` contains no direct trigger/audit/sampling/recovery call;
- `MainWindow.xaml.cs` contains no single-instance check/process close path;
- no untracked `Task.Run(ApplyStartupProxyRecovery)` remains.

- [ ] **Step 4: Update evidence without overstating closure**

Record RED/GREEN evidence, full test count, two-process trace, and checkpoint commit. Mark `P1-01` `In Progress` until Phase 03 serializes all network mutations and a packaged real-app two-instance smoke runs; record the Application composition portion of `P3 code size/static singleton debt` as `In Progress`. Do not mark either row `Closed` yet.

- [ ] **Step 5: Review and checkpoint**

Use `superpowers:requesting-code-review`, address Critical/Important findings with `superpowers:receiving-code-review`, rerun the complete verification, mark Phase 02 complete in the roadmap, and commit the implementation and evidence. Preserve the worktree for Phase 03.
