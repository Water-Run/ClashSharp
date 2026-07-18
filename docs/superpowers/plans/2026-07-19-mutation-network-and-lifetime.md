# Mutation, Network, and Process Lifetime Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. This repository session does not authorize subagent delegation, so execute locally and preserve the isolated worktree.

**Goal:** Establish one fair, non-reentrant mutation path with durable recovery state; route network transitions and shutdown through it; and replace unowned runtime/process work with awaited, supervised lifetimes.

**Architecture:** `ClashSharp.Application` owns platform-neutral mutation admission, journaling contracts, orchestration, runtime lifecycle, health, and outer-lifetime request contracts. `ClashSharp.Infrastructure` implements atomic recovery persistence and process execution. The WinUI project supplies temporary compatibility participants for the existing mihomo, proxy, trigger, audit, and sampling services and registers them only in `ClashSharpAppHostFactory`. Public UI/settings migration is incremental, but no startup, network, trigger action, or shutdown path may bypass the new coordinator after this phase.

**Tech Stack:** .NET 10 / C# 14, Microsoft.Extensions.DependencyInjection, WinUI 3, xUnit, `System.Threading.Channels`, `System.Text.Json`, Windows filesystem and process APIs.

**Normative constraints:** `docs/superpowers/specs/2026-07-18-architecture-stabilization-design.md` sections 4.3–4.6, 5.1–5.4, 10, 12.2, 15, and 17. This phase covers `P1-01`, the runtime foundation of `P1-06`/`P1-08`, `P2-RUN-01` through `P2-RUN-04`, and the mutation/lifetime portion of the 33 acceptance criteria. Settings generation/import/reset/clear-data semantics remain Phase 05 work and must plug into, not replace, these contracts.

---

## Phase invariants

- The mutation coordinator is the sole top-level owner of external settings/profile/package/network/service/lifecycle mutation.
- Admission precedes the fair mutation gate. The gate is asynchronous and deliberately non-reentrant; nested participants receive a `MutationContext` and never reacquire it.
- Ordinary admission is rejected outside `Open`. Destructive work closes admission and drains ordinary leases before taking the gate.
- A durable journal is flushed before the first external side effect and at every intent/completion boundary. A replay-capable journal forces `RecoveryOnly`.
- Caller cancellation applies while waiting and before the first side effect. Compensation/forward recovery uses an independent bounded token after that boundary.
- Network code plans, stages, applies, verifies, and compensates; it does not persist settings or publish user-visible state.
- `ShutdownAsync` never disposes its host or waits for the queue worker that invoked it. The App-owned runner stops/disposes the host only after shutdown unwinds.
- Every background loop is started, quiesced, resumed, stopped, and observed through an awaited contract. No exception may silently terminate it.
- Process execution drains stdout/stderr concurrently, has a real timeout, kills the process tree on timeout/cancellation, and returns a typed result.
- Temporary `.Instance` bridges live only under `AppHost/Compatibility` and are registered at the composition root. No new View/ViewModel service lookup is allowed.

## Checkpoint strategy

Keep every checkpoint buildable and independently reviewable:

1. mutation admission and journal contracts;
2. mutation execution/recovery engine;
3. network and startup routing;
4. supervised runtime/process primitives;
5. lifecycle handoff and complete Phase 03 evidence.

---

### Task 1: Build the fair admission barrier and non-reentrant mutation gate

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Mutations/MutationAdmissionState.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/MutationRequest.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/MutationContext.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/MutationResult.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/MutationAdmissionBarrier.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/FairAsyncMutationGate.cs`
- Create: `ClashSharp/ClashSharp.Tests/Architecture/MutationAdmissionContractTests.cs`

- [x] **Step 1: Write RED behavior tests**

Cover FIFO admission, cancellation before gate entry, closing rejection, drain-before-exclusive acquisition, terminal `ClosedForShutdown`, recovery-only rejection of ordinary work, a single recovery lease, and same-flow nested execution rejection. Use deterministic `TaskCompletionSource` barriers and unique operation IDs; do not use sleeps.

- [x] **Step 2: Capture RED**

```powershell
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~MutationAdmissionContractTests
```

Expected: compilation fails because the mutation contracts do not exist. Record the failure in Phase 03 evidence.

- [x] **Step 3: Implement the smallest correct state machines**

Use an explicit FIFO waiter queue rather than relying on undocumented `SemaphoreSlim` fairness. Lease disposal must be idempotent. State transitions and waiter signaling occur under one short critical section; continuations run asynchronously outside it. `MutationContext` carries operation ID plus an unforgeable coordinator-issued ownership token. Public callers cannot construct a valid context.

- [x] **Step 4: Prove GREEN and repeat concurrency tests**

Run the focused class ten times. Assert all waiters complete and the observed order is stable on every run.

---

### Task 2: Add versioned, hashed, atomic mutation journals

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Mutations/MutationJournal.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/MutationJournalPhase.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/MutationProbeState.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/IMutationJournalStore.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Recovery/FileMutationJournalStore.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Recovery/RecoveryRootPolicy.cs`
- Modify: `ClashSharp/ClashSharp.Infrastructure/ClashSharp.Infrastructure.csproj`
- Create: `ClashSharp/ClashSharp.Tests/Integration/FileMutationJournalStoreTests.cs`

- [x] **Step 1: Write RED persistence and corruption tests**

Cover round-trip, schema version, SHA-256 validation, monotonically increasing generation, atomic replacement preserving the previous valid generation on injected write failure, flush-before-return, corrupt/truncated/hash-mismatched input, reparse-point rejection, same-volume validation, and cleanup only after verified completion.

- [x] **Step 2: Implement the infrastructure adapter**

Infrastructure references Application and implements the contract under a caller-provided recovery root. Production construction resolves `%LocalAppData%\ClashSharp\Recovery\v1`; tests always use unique temporary roots. Writes use a same-directory temporary file, explicit flush-to-disk, and atomic replacement. Validate path containment and reparse points before every mutation. Apply the current-user/SYSTEM/package-compatible ACL policy on Windows; expose typed policy failures rather than silently weakening protection.

- [x] **Step 3: Run failure-injection GREEN tests**

No test may mutate the real user recovery root. Prove that after every injected cut point the reader returns exactly the old valid journal or the new valid journal, never partial JSON.

---

### Task 3: Implement the top-level mutation and bounded recovery coordinator

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Mutations/IApplicationMutationCoordinator.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/IApplicationMutationParticipant.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/MutationPlan.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/ApplicationMutationCoordinator.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/RecoveryHandle.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/MutationDeadlines.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/ApplicationMutationCoordinatorTests.cs`

- [x] **Step 1: Specify RED transition matrices**

Cover ordinary success; planning failure before journaling; cancellation before the first side effect; cancellation after it; apply/verify failure with reverse compensation; compensation failure retaining `RecoveryOnly`; post-commit activation/cleanup failure returning committed recovery required; stale recovery handle/generation rejection; failed first same-process retry followed by successful second retry; unrelated operation rejection while recovery remains; and shutdown-pending winning over recovery completion.

- [x] **Step 2: Implement journal-driven execution**

Participants expose idempotent `ProbeAsync`, `StageAsync`, `ApplyAsync`, `VerifyAsync`, and `CompensateAsync`. The coordinator writes phase intent before each external call and phase completion afterward. It commits the verified durable target hash before the point-of-no-return marker. It publishes no normal completion result until locks are released. The default per-step recovery deadline is 30 seconds and total deadline is two minutes through an injectable timeout/clock policy.

- [x] **Step 3: Prove pairwise serialization and lock order**

Interleave two ordinary operations, ordinary/destructive, ordinary/recovery, destructive/shutdown, and retry/shutdown at each deterministic barrier. Assert one gate owner, FIFO ordinary order, no stale rollback overwrite, no nested gate acquisition, and no transition out of terminal shutdown.

- [x] **Step 4: Checkpoint contracts and engine**

Run Application/Infrastructure builds plus focused tests, update evidence, review the diff, and commit a buildable checkpoint.

---

### Task 4: Introduce NetworkStateCoordinator and route startup/runtime transitions

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Network/NetworkIntent.cs`
- Create: `ClashSharp/ClashSharp.Application/Network/NetworkPlan.cs`
- Create: `ClashSharp/ClashSharp.Application/Network/NetworkPhaseResult.cs`
- Create: `ClashSharp/ClashSharp.Application/Network/INetworkStateParticipant.cs`
- Create: `ClashSharp/ClashSharp.Application/Network/NetworkStateCoordinator.cs`
- Create: `ClashSharp/ClashSharp/AppHost/Compatibility/LegacyNetworkMutationParticipant.cs`
- Create: `ClashSharp/ClashSharp/AppHost/Compatibility/LegacyProxyRecoveryProbe.cs`
- Modify: `ClashSharp/ClashSharp/AppHost/Startup/ProxyRecoveryStartupStep.cs`
- Modify: `ClashSharp/ClashSharp/Service/ApplicationActionService.cs`
- Modify: `ClashSharp/ClashSharp/AppHost/ClashSharpAppHostFactory.cs`
- Modify: `ClashSharp/ClashSharp.Tests/Unit/Services/NetworkTakeoverServiceTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/NetworkMutationConcurrencyTests.cs`

- [x] **Step 1: Write RED ownership and rollback tests**

Prove network participants reject a missing/foreign/stale `MutationContext`; planning captures baseline and desired mode/TUN/port; apply failure compensates and verifies baseline; success reports observed runtime/proxy state; two mode/port/TUN requests serialize; and no participant persists settings or publishes UI events.

- [x] **Step 2: Route startup recovery through the mutation coordinator**

The startup probe only decides whether recovery is required. Recovery itself is one top-level mutation after durable-journal recovery and before window construction. Remove `Task.Run` and direct `ProxyRecoveryService.Instance` access from the startup step. Keep the compatibility bridge internal and constructor-injected.

- [x] **Step 3: Route shared application actions through the coordinator**

`SwitchProxyMode` submits one intent and persists/publishes only the verified result after the coordinator returns and locks are released. Failed/cancelled operations preserve the displayed/applied baseline. TUN and port UI setters remain explicitly pending until Phase 05 wires their settings transaction; they may not claim runtime application.

- [x] **Step 4: Strengthen the real two-process test**

Extend the process-independent trace to record fake network/core mutations and prove secondary launch changes none while the primary owns RuleTakeover. A packaged real-app smoke remains required before closing `P1-01`.

---

### Task 5: Add awaited runtime lifecycle and outer handoff

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Lifecycle/IRuntimeParticipant.cs`
- Create: `ClashSharp/ClashSharp.Application/Lifecycle/QuiescedState.cs`
- Create: `ClashSharp/ClashSharp.Application/Lifecycle/QuiescenceSession.cs`
- Create: `ClashSharp/ClashSharp.Application/Lifecycle/RuntimeLifecycleCoordinator.cs`
- Create: `ClashSharp/ClashSharp.Application/Lifecycle/IApplicationLifetimeRequestSink.cs`
- Create: `ClashSharp/ClashSharp.Application/Lifecycle/ApplicationLifetimeRequestChannel.cs`
- Create: `ClashSharp/ClashSharp/AppHost/Compatibility/LegacyRuntimeParticipants.cs`
- Modify: `ClashSharp/ClashSharp/Service/ApplicationLifecycleService.cs`
- Modify: `ClashSharp/ClashSharp/Service/ApplicationActionService.cs`
- Modify: `ClashSharp/ClashSharp/AppHost/ClashSharpAppHostFactory.cs`
- Modify: `ClashSharp/ClashSharp/App.xaml.cs`
- Delete after replacement: `ClashSharp/ClashSharp/Service/RuntimeShutdownService.cs`
- Delete after replacement: `ClashSharp/ClashSharp/Service/RuntimeShutdownServiceFactory.cs`
- Replace: `ClashSharp/ClashSharp.Tests/Unit/Services/RuntimeShutdownServiceTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/RuntimeLifecycleCoordinatorTests.cs`

- [x] **Step 1: Write RED quiescence and self-join tests**

Cover ordered quiescence, blocked in-flight work, a trigger action waiting before mutation admission, 30-second timeout, partial pause with reverse resume, resume failure typed as degraded, shutdown from a participant queue worker, and disposal blocked until the shutdown call stack unwinds.

- [x] **Step 2: Implement awaited lifecycle contracts**

Normal shutdown closes admission, drains leases, quiesces producers, executes the configured network exit policy as one mutation, stops runtime participants, releases mutation resources, and returns `PreparedForHostDisposal`. It never deletes/reset/truncates user data. Recovery-only shutdown freezes the existing journal and skips the exit-policy mutation.

- [x] **Step 3: Hand exit/restart to the App-owned channel**

Trigger/UI services enqueue an exit or restart request and return; they do not call `Environment.Exit`, close a WinUI window from a worker, or dispose AppHost. `App` consumes the request on its dispatcher, awaits host shutdown through `ProcessLifetimeRunner`, launches restart only after preparation succeeds, and then exits. Concurrent close/exit requests collapse into one stop/dispose task.

- [x] **Step 4: Replace the no-op host shutdown coordinator**

Register the real runtime lifecycle coordinator as `IApplicationShutdownCoordinator`. Prove `AppHost.Build` remains side-effect free and host disposal occurs exactly once.

---

### Task 6: Replace connection sampling with a supervised awaited participant

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Supervision/ISupervisorClock.cs`
- Create: `ClashSharp/ClashSharp.Application/Supervision/SupervisorHealth.cs`
- Create: `ClashSharp/ClashSharp.Application/Supervision/SupervisorBackoffPolicy.cs`
- Create: `ClashSharp/ClashSharp.Application/Supervision/SupervisedLoop.cs`
- Modify: `ClashSharp/ClashSharp/Service/ConnectionSamplingService.cs`
- Modify: `ClashSharp/ClashSharp/Service/ConnectionSamplingServiceFactory.cs`
- Modify: `ClashSharp/ClashSharp/AppHost/Startup/ConnectionSamplingStartupStep.cs`
- Modify: `ClashSharp/ClashSharp.Tests/Unit/Services/ConnectionSamplingServiceTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Unit/Supervision/SupervisedLoopTests.cs`

- [x] **Step 1: Write fake-clock RED tests**

Assert exact retry delays `1/2/5/10/30/30`, fifth-failure and 60-second degradation, two-success recovery, relapse, production jitter bounds, all health fields, intentional quiesce as `Stopped`, and zero work after awaited stop. Include SQLite, IO, HTTP, JSON, and unexpected exceptions.

- [x] **Step 2: Implement the reusable supervisor**

The loop owns one tracked task and cancellation source. `StartAsync`, `QuiesceAsync`, `ResumeAsync`, and `StopAsync` are idempotent and await in-flight iteration completion. No fire-and-forget restart continuation remains. Unexpected iteration exceptions update health and cannot fault the supervisor task.

- [x] **Step 3: Migrate sampling and startup**

Sampling becomes an injected runtime participant. The startup step awaits start; lifecycle quiescence/stop awaits it; storage errors participate in backoff rather than silently killing the loop. Remove volatile author/file/date banners while editing these files.

---

### Task 7: Build one safe process runner and migrate `sc.exe`

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Processes/ProcessRequest.cs`
- Create: `ClashSharp/ClashSharp.Application/Processes/ProcessRunResult.cs`
- Create: `ClashSharp/ClashSharp.Application/Processes/IProcessRunner.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Processes/WindowsProcessRunner.cs`
- Modify: `ClashSharp/ClashSharp/Service/MihomoServiceManager.cs`
- Modify: `ClashSharp/ClashSharp/Service/MihomoServiceManagerFactory.cs`
- Modify: `ClashSharp/ClashSharp.Tests/Unit/Services/MihomoServiceManagerTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/WindowsProcessRunnerTests.cs`
- Create helper project if needed: `ClashSharp/ClashSharp.ProcessProbe/`

- [ ] **Step 1: Write RED real-process tests**

Use a controlled helper to emit stdout/stderr concurrently, hang, spawn a child, and exit with a selected code. Prove complete output, typed timeout/cancellation, process-tree termination, bounded completion, and no helper leak.

- [ ] **Step 2: Implement and migrate**

Drain both redirected streams concurrently. Race process completion against the injected timeout and caller token. On timeout/cancellation kill the entire process tree and await final exit/drain. `sc.exe` callers always re-query SCM state after elevated operations; cancellation is not represented as an unexplained exit code.

---

### Task 8: Replace concurrent diagnostic `StringBuilder`

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Diagnostics/ConcurrentBoundedTextBuffer.cs`
- Modify: `ClashSharp/ClashSharp/Service/MihomoCoreService.cs`
- Create: `ClashSharp/ClashSharp.Tests/Unit/Diagnostics/ConcurrentBoundedTextBufferTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Unit/Services/MihomoCoreServiceTests.cs`

- [ ] **Step 1: Write RED concurrency stress tests**

Run concurrent stdout/stderr writers while another task snapshots the buffer. Assert no exception/corruption, bounded memory, complete-line snapshots, deterministic truncation marker, and no writes accepted after completion.

- [ ] **Step 2: Implement and integrate**

Use a small lock or channel-owned buffer; never expose the mutable builder. Mihomo startup waits for stream-drain completion before producing a failure diagnostic and renders only the bounded snapshot.

---

### Task 9: Establish tracked async command error handling without claiming the full MVVM migration

**Files:**

- Modify: `ClashSharp/ClashSharp/ViewModel/AsyncRelayCommand.cs`
- Modify: `ClashSharp/ClashSharp.Tests/Unit/ViewModel/AsyncRelayCommandTests.cs`
- Create: `ClashSharp/ClashSharp/ApplicationErrorSink.cs` or equivalent injected presentation adapter
- Modify only the Phase 03 action paths in `MasterControlViewModel` and `SettingsViewModel`

- [ ] **Step 1: Write RED command/error-state tests**

Prove `Execute` observes completion through one error sink, `ExecuteAsync` preserves cancellation, busy state is reset in `finally`, reentrancy remains blocked, and a failed network transition restores applied/displayed state rather than showing optimistic success.

- [ ] **Step 2: Implement the bounded migration**

Do not globally rewrite presentation in this phase. Fix command infrastructure and the mode/port/TUN/startup-task paths touched by mutation/lifecycle work. Record remaining View/ViewModel `.Instance` and optimistic-command debt against Phases 05 and 07; `P2-RUN-03` remains `In Progress` until those phases finish.

---

### Task 10: Architecture gates, evidence, and Phase 03 closure

**Files:**

- Create: `ClashSharp/ClashSharp.Tests/Architecture/MutationLifetimeArchitectureTests.cs`
- Modify: `ClashSharp/ClashSharp.Tests/Architecture/RepositoryTopologyTests.cs`
- Create: `docs/architecture/evidence/phase-03-mutation-network-lifetime.md`
- Modify: `docs/architecture/stabilization-ledger.md`
- Modify: `docs/superpowers/plans/2026-07-19-architecture-stabilization-roadmap.md`

- [ ] **Step 1: Add executable architecture gates**

Reject direct network mutation outside registered compatibility/infrastructure participants, unowned `Task.Run`, sync-over-async, old synchronous `Stop`/`RestartFromSettings` sampling calls, `Environment.Exit` below App, and new service `.Instance` access in View/ViewModel. Prefer behavior tests; source contracts are allowed only for dependency/layer artifacts with a documented reason.

- [ ] **Step 2: Run clean final verification**

```powershell
$env:CI = 'true'
$env:Platform = 'x64'
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode --force
dotnet format ClashSharp/ClashSharp.slnx --verify-no-changes --no-restore
dotnet build ClashSharp/ClashSharp.slnx -c Debug --no-restore
dotnet build ClashSharp/ClashSharp.slnx -c Release --no-restore
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release --no-build --no-restore
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Mutation|FullyQualifiedName~Lifecycle|FullyQualifiedName~Supervised|FullyQualifiedName~ProcessRunner" --repeat 10
git diff --check
```

If the installed test runner does not support `--repeat`, invoke the filtered command ten times from PowerShell and fail on the first non-zero exit.

- [ ] **Step 3: Run platform and process smoke**

Run the helper two-process zero-side-effect test repeatedly, the process-tree timeout/cancellation suite, and a packaged primary/secondary RuleTakeover smoke when the local package harness is available. Missing packaged evidence keeps `P1-01` `In Progress`; it does not block closing the implemented Phase 03 contracts.

- [ ] **Step 4: Update ledger precisely**

Close only rows whose required evidence is complete. Expected Phase 03 outcome: `P2-RUN-01`, `P2-RUN-02`, and `P2-RUN-04` can close; `P2-RUN-03`, `P1-06`, and `P1-08` remain `In Progress` for presentation/settings phases; `P1-01` closes only with the packaged real-app proof. Record checkpoint commit, reviewer method, date, test counts, repetitions, and any retained compatibility bridges.

- [ ] **Step 5: Review and checkpoint**

Use `superpowers:requesting-code-review`, address Critical/Important findings with `superpowers:receiving-code-review`, rerun the complete verification, mark Phase 03 complete in the roadmap, and commit implementation plus evidence. Preserve the worktree for Phase 04.
