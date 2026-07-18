# ClashSharp Architecture Stabilization Design

**Date:** 2026-07-18
**Status:** Ready for user review
**Baseline:** `main` at `0fae2d8`
**Source audit:** `docs/reviews/2026-07-18-clashsharp-detailed-audit.md`

## 1. Objective

Repair every confirmed P1 issue, every testable P2 issue, and every actionable P3 item from the detailed audit, while replacing the architectural causes that allowed UI state, persisted settings, Windows state, mihomo state, and background services to diverge. Security candidates from the audit must be dynamically verified and then either hardened or closed with recorded evidence.

The result must be suitable for long-term maintenance rather than a collection of local patches. Large changes are allowed. The application must remain buildable and testable at every migration checkpoint.

## 2. Scope

### 2.1 Required outcomes

1. Single-instance arbitration completes before any shared data, Windows proxy, trigger, sampling, or mihomo side effect.
2. Network recovery, mode application, port/TUN changes, shutdown, and rollback are serialized through one coordinator.
3. Trigger storage is atomic and recovers from corrupt files without blocking application launch.
4. Trigger evaluation uses typed parameters, deterministic edge/once semantics, task-level serialization, and durable idempotency state.
5. Editing a multi-condition trigger preserves and edits every condition with AND semantics.
6. Settings changes, import, reset, and clear-data use one transactional application path with validation, rollback, cache invalidation, and explicit restart state.
7. All background producers support awaited shutdown/quiescence before data deletion or application exit.
8. The Connections page is reachable through the same navigation registry used by shell and tray navigation.
9. Presentation code no longer resolves application services through static `.Instance` access.
10. View code-behind contains only platform/visual concerns; domain editing, validation, asynchronous state, and errors live in ViewModels/use cases.
11. Explicit translations are complete for every released language before fallback is applied; culture-aware formatting follows the selected language.
12. Accessibility, adaptive layout, selection rollback, dialog reentrancy, and navigation-selection issues identified by the audit are fixed and regression-tested where automatable.
13. Tests reference production assemblies. Source-linked production copies and `UNIT_TESTS` behavior forks are removed.
14. CI, formatting, dependency audit, Rust gates, package signing checks, and executable Windows Sandbox scenarios are restored.
15. MSIX signing subject derives from the package manifest and is validated before signing. Installer trust anchors and downloaded binaries have a complete cleanup/integrity story.
16. The mihomo Windows service has an explicit least-privilege identity, accepts only per-call-authorized local RPC from the packaged AppContainer broker, and consumes executable/configuration inputs only from ACL-protected locations.
17. Log storage maintenance cannot monopolize foreground sampling/trigger reads; lock wait and maintenance behavior are observable and tested.
18. Additional quality findings discovered during design review—disabled Profiles/Links commands, work-area-aware window sizing, and overlay resize behavior—are corrected.
19. Disabled triggers short-circuit before context creation; enabled trigger contexts are fully asynchronous and convert controller timeout, malformed JSON, SQLite, and IO failures into typed degraded data or typed operation failures without blocking the UI thread.
20. Sampling and scheduling loops are supervised: transient failures use bounded backoff and continue, persistent failures publish degraded health, and no loop can fault silently.
21. Live language changes, including package import, update every active shell/page ViewModel in one notification cycle and never show raw exception text.
22. Port/TUN/Profile/StartupTask operations expose pending/applied/rollback state and have explicit success, denial, mismatch, cancellation, and rollback verification.
23. Release artifacts include an SBOM plus standard provenance and custom signed release metadata tying the pinned mihomo binary, dependency locks, toolchain/action pins, signing identity, app version, package, installer, and source revision together.

### 2.2 Necessary quality work

- Normalize line endings through `.gitattributes` and make `dotnet format --verify-no-changes` deterministic.
- Turn the chosen analyzer/documentation policy into build configuration rather than prose-only guidance.
- Establish a single version source for app, manifest, installer, and displayed version.
- Remove or explicitly deprecate stale installer entry points.
- Track unexpected asynchronous failures through a central error sink and structured logs.
- Update patch-level dependencies, establish an explicit xUnit v2-to-v3 decision, and record approved major-version deferrals.

### 2.3 Non-goals

- No visual brand redesign.
- No change of the mihomo external API unless needed for correctness or cancellation.
- No new user-facing feature unrelated to an audited issue.
- No big-bang rewrite that leaves the application unbuildable between milestones.
- No silent backward incompatibility for existing settings, trigger files, profiles, or data packages.
- No permanent test-only branch inside production behavior; safe UI verification uses injected fake infrastructure at the composition root.

## 3. Target Solution Structure

The current WinUI project remains the executable, but domain and application logic move into assemblies that tests reference directly.

```text
ClashSharp.slnx
├─ ClashSharp.Core
│  ├─ Domain
│  ├─ Application
│  ├─ Settings
│  ├─ Triggers
│  └─ Abstractions
├─ ClashSharp.Infrastructure
│  ├─ Persistence
│  ├─ Mihomo
│  ├─ Windows
│  ├─ Packaging
│  └─ Observability
├─ ClashSharp                  # WinUI executable/presentation
│  ├─ AppHost
│  ├─ ProcessLifetime
│  ├─ Navigation
│  ├─ ViewModel
│  ├─ View
│  └─ Components
├─ ClashSharp.PrivilegedBroker # packaged AppContainer app-service/RPC broker
├─ ClashSharp.MihomoService
├─ ClashSharp.Tests            # ProjectReference to production libraries
├─ Installer
└─ SandboxTest
```

Migration is vertical: a use case moves with its models, interfaces, implementation, and tests. Existing types may temporarily remain in the app project, but no new source-link test entries or static service lookups may be added.

## 4. Dependency and Lifetime Model

### 4.1 AppHost composition root

`AppHost` is the only dependency-injection/application-service composition root. It registers:

- immutable configuration and paths;
- repositories and OS/mihomo adapters;
- application coordinators;
- background services;
- ViewModels and navigation factories;
- an application-wide error sink.

Microsoft dependency injection is used for constructor injection and explicit lifetime management. Building the service provider is side-effect free: constructors may validate immutable arguments but may not open user data, register events, start timers/tasks/processes, access Windows proxy/registry/services, or mutate external state. Hosted services are started only by `StartupCoordinator` after primary-instance ownership. The WinUI `App` owns a `ProcessLifetimeRunner` outside AppHost; after primary-instance ownership it builds and attaches the host, and only that outer runner may stop and dispose AppHost after an awaited shutdown call has fully unwound.

### 4.2 Static singleton rule

- View, component, and ViewModel code may not call service `.Instance` members.
- Domain and application assemblies may not expose service locators.
- Framework-owned process singletons may be wrapped by injected adapters at the composition root.
- Temporary compatibility adapters are internal, isolated under `AppHost/Compatibility`, and must have a removal task in the implementation plan.

### 4.3 Lifetime categories

| Lifetime | Examples | Rule |
|---|---|---|
| App outer lifetime | `ProcessLifetimeRunner`, lifecycle request channel | Constructed and disposed by WinUI `App`, registered into AppHost only as a non-owned request sink, and guaranteed to outlive AppHost |
| Host singleton | repository facades, navigation registry, mutation coordinator, lifecycle coordinator | Side-effect-free construction; one instance owned and disposed by AppHost |
| Data generation scope | concrete settings/profile/log repositories | Atomically replaceable child scope owned by `DataGenerationManager` |
| Background supervisor | sampling, trigger scheduler, audit writer | Long-lived controller that can quiesce, detach an old generation, and attach/start a new generation |
| Window scope | MainWindow VM, dialog/navigation services | Created on activation and released after awaited window shutdown |
| Page scope | page VM/editor VM | Default non-cached; supports activate/deactivate, cancellation, event disposal, and async disposal |
| Operation scope | import/reset/network transition | Own cancellation, snapshot, rollback journal, result |

### 4.4 Executable quality policy

- Repository text uses LF, enforced by `.gitattributes` and matching `.editorconfig` settings on every checkout.
- Project-level nullable analysis is authoritative; `CodingStyle.md` no longer requires redundant per-file `#nullable enable` directives.
- `AnalysisLevel=latest-recommended`, code-style enforcement, and warnings-as-errors run in CI and Release builds.
- XML documentation is required for public contracts and non-obvious application interfaces, not every private implementation member. The policy and compiler settings must agree.
- Formatting/import ordering is normalized once, then enforced by CI.
- Volatile author/file/date banner comments are removed; source control is the authority for authorship and change dates. Only stable license/copyright headers required by policy remain, and CI rejects newly added volatile date headers.

### 4.5 Global mutation ownership and lock order

`ApplicationMutationCoordinator` is the only top-level owner of operations that mutate settings, profiles, package files, data generations, mihomo/TUN/Windows proxy state, StartupTask/service state, or lifecycle state. Public Settings and Network use cases delegate to:

```csharp
Task<MutationResult<T>> ExecuteAsync<T>(
    MutationRequest request,
    Func<MutationContext, Task<T>> operation,
    CancellationToken cancellationToken);
```

The mutation gate is process-wide, fair, asynchronous, and deliberately non-reentrant. `MutationContext` proves ownership and is required by all internal stage/apply/verify/rollback methods. Nested coordinators never reacquire the gate, persist settings, or publish events. Standalone mode/port/TUN actions are also top-level mutation requests; they do not bypass the coordinator.

Every ordinary top-level mutation first obtains a lease from `MutationAdmissionBarrier`, then waits for the mutation gate. Shutdown, clear-data, import, and any generated plan marked `RequiresQuiescence` use a reserved exclusive lane and a two-phase admission/drain protocol:

1. atomically change admission from `Open` to `Closing`, reject new UI/trigger/background mutation leases, and signal cancellation to admitted requests that have not entered the mutation gate;
2. without holding the mutation gate, quiesce producer scheduling and wait for queued leases to leave and for any current gate owner to commit or compensate;
3. only after the lease count reaches zero, acquire the exclusive destructive lease and then the mutation gate;
4. on pre-commit cancellation, timeout, or failure, resume every participant that was paused, dispose the destructive lease, and reopen admission; after a clear-data commit, attach the new generation before reopening; shutdown deliberately leaves admission closed.

`MutationAdmissionBarrier` has explicit `Open`, `Closing`, `RecoveryOnly`, `RecoveryClosing`, and `ClosedForShutdown` states. Ordinary UI, trigger, and supervisor requests are admitted only in `Open`. Any journal with a remaining compensation, target-replay, activation, or cleanup obligation moves the barrier to `RecoveryOnly`; only `ApplicationMutationCoordinator.RetryRecoveryAsync(operationId)` can obtain its recovery-exclusive lease and then the ordinary mutation gate, and the barrier records that attempt as active. The public caller supplies only the operation ID. After gate entry, the coordinator reloads the latest flushed journal, verifies its operation ID and hash, and creates an internal, attempt-scoped `RecoveryHandle` containing the journal generation/current hash and allowed direction. Before the commit marker the handle permits only the recorded idempotent compensation to the baseline; after the marker it permits only the recorded idempotent forward activation, verification, and cleanup. It cannot change `Desired`, start an unrelated participant, roll back a committed target, or publish normal mutation events before target health is verified.

Every recovery phase-intent/phase-complete flush increments the journal generation, re-hashes it, and replaces the in-memory handle; participant calls reject a stale generation. Caller cancellation is honored while waiting for the recovery lease/gate, but once the first recovery side effect starts, every step uses the independent bounded recovery token. On timeout or failure, the coordinator flushes the latest recoverable phase; if that flush itself fails, the last durable generation remains authoritative and safe mode records a separate diagnostic without rewriting it. A later call can reload that generation and continue without restarting the process. Successful recovery verifies every participant plus the durable target hash and removes the journal/recovery material. In every outcome the coordinator then disposes the handle and releases the mutation gate, but retains the recovery-exclusive lease until it calls `MutationAdmissionBarrier.CompleteRecoveryAttempt(finalGeneration, journalPresent, verifiedSuccess)`.

`CompleteRecoveryAttempt` is the single linearization point. Under one barrier critical section it atomically chooses `ClosedForShutdown` when a shutdown is pending, `Open` only for verified success with no journal, or `RecoveryOnly` for a retained obligation; it releases the recovery lease and signals waiters as part of the same transition. `ClosedForShutdown` is terminal and no recovery completion can reopen it. A shutdown request calls `RequestRecoveryShutdownAsync`: the barrier records shutdown-pending, changes `RecoveryOnly` to `RecoveryClosing`, and rejects new recovery attempts. With no active lease it freezes the current journal generation and closes immediately. With an active attempt it waits outside the barrier lock; the attempt exits without changing the journal if it has not crossed its first recovery side effect, otherwise it uses the independent token to reach a flushed success/failure boundary and hands that final generation to `CompleteRecoveryAttempt`. The restricted exit path in 5.4 starts only after this handoff returns the `ClosedForShutdown` snapshot. Thus retry and exit cannot concurrently mutate/dispose participants, bytes are frozen from one explicit boundary, and there is never more than one replay-capable journal.

A trigger execution waiting to submit a settings/network action is cancellable before gate entry and leaves its durable outbox action pending for later reconciliation. An action that already crossed its first side effect completes or compensates with the independent recovery token. `QuiesceAsync` is forbidden from waiting for work that can only finish by acquiring a gate already held by its caller.

The lock order is fixed and enforced by debug assertions/tests:

1. application mutation gate;
2. data-generation/repository transaction in the order settings → profile catalog → trigger state → logs;
3. staged network/process adapter operations;
4. UI notification dispatch after all locks are released.

Routine append-only logging/sampling does not take the global mutation gate. Destructive operations use the admission/drain protocol before quiescing those producers. No code may acquire the mutation gate while holding a repository, process, network, UI-dispatcher, or quiescence-participant lock.

### 4.6 Durable mutation journal and crash recovery

Before the first external side effect, the top-level owner atomically writes and flushes a versioned `MutationJournal` containing operation ID/type, baseline and desired snapshot hashes, backup/staging paths, ordered steps, current phase, compensation data, and no commit marker. Each participant supplies idempotent `ProbeAsync`, `ApplyAsync`, `VerifyAsync`, and `CompensateAsync` operations. A probe must classify observed state as `Baseline`, `Desired`, `Partial`, or `Unknown`; a plan is rejected before mutation if any side effect lacks enough identity, backup, query, or compensation information to make that classification and restore a verified state.

The owner flushes a phase-intent record before each side effect, runs the idempotent apply plus verification, and then flushes phase-complete. Intent without phase-complete is explicitly in doubt, not assumed unapplied. After all external state verifies, the owner atomically promotes and flushes the durable target settings/files/generation manifest, re-reads their hashes, and only then flushes the commit marker. The flushed commit marker is the point of no return and contains the operation ID plus verified target hash. Backups and staging are cleaned only afterward.

The journal, control manifests, and operation backup/staging directories live under a dedicated `%LocalAppData%\ClashSharp\Recovery\v1` recovery root, never inside a replaceable data-generation directory and never included in clear-data enumeration. Its protected DACL grants Full Control only to the current user and SYSTEM (plus the package SID where packaged access requires it), removes inherited broad write access, and is validated against reparse points before use. The canonical data root is on the same volume so manifest/file promotion uses same-volume atomic replace. A participant targeting another volume must keep rollback material on that target volume and record both locations in the control journal; otherwise planning fails before mutation. Journal and backup cleanup occurs only after committed-state verification or an explicit, separately journaled diagnostic export/repair decision.

Startup recovery runs after primary-instance arbitration and before repositories, windows, or hosted services start:

- a committed journal probes every participant, idempotently completes the desired state when it is safely repairable, verifies the target hash, and finishes cleanup;
- an uncommitted journal walks both completed and intent-only phases in reverse order; `Baseline` needs no compensation, `Desired` or `Partial` is compensated idempotently and verified against the baseline, and `Unknown` enters safe mode rather than guessing;
- a corrupt journal or failed compensation enters localized safe mode, disables ClashSharp-owned Windows proxy state when ownership can be proven, preserves all backups, and blocks normal mutations until repair/retry/export-diagnostics;
- recovery is idempotent across repeated crashes.

Caller cancellation applies directly only before the first side effect. From the first side effect until the commit marker, it requests compensation; compensation uses an independent recovery token with a 30-second per-step and two-minute total deadline. After the commit marker, caller cancellation cannot roll back the committed target: forward activation/cleanup continues with an independent token and returns a typed committed result. If any bounded post-commit step fails and leaves a replay-capable journal, the coordinator transitions admission to `RecoveryOnly` before releasing mutation resources and returns `CommittedRecoveryRequired` (specialized as `CommittedDegraded` when target health is not established). No later mutation is admitted until that journal is recovered, verified, and removed, so an older committed target can never replay over a newer change. Cancellation, timeout, and rollback outcomes include final-state verification.

Quiescence has a 30-second deadline and is represented by a `QuiescenceSession` that records each participant's prior state and each successful pause. If admission/drain or quiescence cannot complete, destructive work never begins; already paused participants resume in reverse order with an independent recovery token, admission reopens, and the result reports both timeout and restored health. Failure to restore a participant is a typed degraded/safe-mode outcome, never an apparently clean abort.

Crash tests launch a helper process, terminate it after every phase-intent, side-effect, phase-complete, durable-target-promotion, and commit-marker cut point, then restart and prove the result is exactly the verified baseline or the verified committed target—never an undocumented mixture. The suite includes `Partial` and `Unknown` probe results, repeated recovery crashes, cleanup failure after the point of no return, a first same-process retry that advances the journal and fails followed by a successful second retry, and operation A's post-commit cleanup failure rejecting operation B until A's journal is removed.

## 5. Application Coordinators

### 5.1 StartupCoordinator

`App.OnLaunched` first executes a minimal `PrimaryInstanceBootstrap` that depends only on the OS app-instance/activation channel. It does not build AppHost, touch LocalData, construct repositories, or perform network/registry/service work. A secondary instance redirects activation to the primary instance and exits without creating the main window or any application service.

After ownership, the side-effect-free AppHost is built and the ordered startup pipeline runs:

1. recover any durable mutation journal under the global mutation gate;
2. create the first data-generation scope, validate/migrate persisted documents, and expose only verified `AppliedState` values or explicitly diagnosed safe fallbacks;
3. reconcile auto-eligible pending-application batches in fixed `LiveReconcile` then `Restart` order, one top-level mutation per batch, advancing `AppliedState` only after verification or stopping on a typed failure with the working baseline/safe fallback retained;
4. evaluate stale proxy state as a top-level mutation;
5. start the window shell;
6. apply configured startup behavior as a top-level mutation;
7. attach/start sampling, triggers, and audit supervisors;
8. run non-blocking startup prompts and diagnostics.

Every step returns a typed result. Fatal startup failures are shown through one localized startup error surface; optional diagnostics become warnings and do not crash an `async void` handler.

### 5.2 NetworkStateCoordinator

`NetworkStateCoordinator` is a mutation participant, not a transaction owner. Operations that affect mihomo, TUN, mixed port, Windows proxy, service state, or current mode run only with a valid `MutationContext` supplied by `ApplicationMutationCoordinator`.

Internal staged contract:

```csharp
Task<NetworkPlan> PlanAsync(MutationContext context, NetworkIntent intent, CancellationToken cancellationToken);
Task<NetworkPhaseResult> StageAsync(MutationContext context, NetworkPlan plan, CancellationToken cancellationToken);
Task<NetworkPhaseResult> ApplyAsync(MutationContext context, NetworkPlan plan, CancellationToken cancellationToken);
Task<NetworkPhaseResult> VerifyAsync(MutationContext context, NetworkPlan plan, CancellationToken cancellationToken);
Task<RollbackOutcome> CompensateAsync(MutationContext context, NetworkPlan plan, CancellationToken recoveryToken);
```

A plan captures baseline runtime/Windows state and desired profile/port/TUN/mode, validates conflicts, and produces journal-ready compensation data without mutation. Stage writes only temporary configuration. Apply performs mihomo/service/TUN/Windows-proxy changes. Verify checks controller health and effective Windows state. The top-level owner then commits all files/settings and, after releasing locks, publishes one aggregate state event.

Network code never commits settings/current mode and never publishes user-visible state independently. Compensation is idempotent, uses the independent bounded recovery token, returns `RollbackOutcome`, and verifies the final state. This eliminates duplicate import transitions and nested gate acquisition.

### 5.3 SettingsCoordinator

Every setting is described by one metadata registry entry containing:

- stable key and schema version;
- type, default value, parser, and validator;
- import/export inclusion;
- application handler;
- cache invalidation handler;
- live-apply versus restart requirement;
- rollback behavior;
- localization category and sensitive-data flag.

The registry generates package descriptors and completeness tests. `KnownKeys`, package descriptors, reset lists, and UI option mappings may not be maintained as unrelated manual lists.

Public operations:

```csharp
Task<SettingsApplyResult> ApplyAsync(SettingsChangeSet changes, CancellationToken cancellationToken);
Task<SettingsApplyResult> ImportAsync(DataPackage package, CancellationToken cancellationToken);
Task<SettingsApplyResult> ResetAsync(SettingsResetScope scope, CancellationToken cancellationToken);
Task<SettingsApplyResult> ClearDataAsync(CancellationToken cancellationToken);
```

All four operations build one `SettingsMutationPlan` and submit it once to `ApplicationMutationCoordinator`. The plan contains settings/file changes, repository-generation actions, a single optional `NetworkPlan`, hosted-service changes, cache invalidations, aggregate notifications, and a computed `RequiresQuiescence` flag. A plan requiring quiescence enters the exclusive admission/drain protocol before it can acquire the mutation gate. Handlers contribute plan/stage/verify/compensation steps; they may not call another public coordinator, commit persistence, or publish events.

Import therefore does not call ordinary settings apply and then perform a second network transition. It creates one combined plan, executes each participant once, writes one journal, commits once, and publishes once.

### 5.4 RuntimeLifecycleCoordinator

Background components implement an awaited contract:

```csharp
Task StartAsync(CancellationToken cancellationToken);
Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken);
Task ResumeAsync(QuiescedState priorState, CancellationToken cancellationToken);
Task StopAsync(CancellationToken cancellationToken);
```

Generation-aware participants additionally prepare an attachment to a staged generation in a paused state and expose a readiness probe; preparation may open/read the staged repository and validate dependencies but may not schedule work or publish events. `QuiesceAsync` prevents new work and awaits in-flight writes/actions without acquiring the mutation gate. The lifecycle coordinator aggregates returned states in a `QuiescenceSession`; cancellation, timeout, or pre-commit failure resumes successfully paused participants in reverse order. Normal shutdown and clear-data are separate state machines.

The numbered shutdown path below applies only when admission starts in `Open`. A shutdown request received in `RecoveryOnly` calls `RequestRecoveryShutdownAsync` and waits for the barrier handoff described in 4.5; `ShutdownRecoveryStateAsync` begins only from the returned `ClosedForShutdown` snapshot. From that linearization point it preserves the reported journal generation/recovery files byte-for-byte. It does not create a second mutation journal, run the configured network exit policy, compensate or advance the existing recovery operation, or invoke any external-state participant covered by that journal. It only stops new in-process scheduling, awaits and releases host-owned leases, emits an append-only diagnostic explicitly excluded from mutation snapshots, and returns `PreparedForHostDisposal` to the App-owned runner. The next primary launch resumes the preserved committed or uncommitted recovery before opening repositories or consumers.

`ShutdownAsync`:

1. close mutation admission, cancel/drain gate waiters, and stop new producer scheduling without holding the mutation gate;
2. quiesce trigger, sampling, audit, and other supervisors within 30 seconds; on failure, resume the recorded prior states and reopen admission;
3. acquire the exclusive destructive lease and mutation gate, then write one top-level journal;
4. apply the configured network exit policy, stop mihomo/supervisors, and verify the durable target lifecycle/network state;
5. flush target persistence and the commit marker, release mutation resources, close the window scope, and return `PreparedForHostDisposal`; admission remains `ClosedForShutdown` and `ShutdownAsync` never disposes AppHost or its own caller;
6. after the coordinator call stack has unwound, the App-owned `ProcessLifetimeRunner` invokes AppHost stop/disposal, which releases the current data-generation scope and all remaining host singletons; normal shutdown never deletes or resets user data, a crash before the marker restores the baseline, and a crash after it finishes committed shutdown cleanup on the next primary-instance launch.

`ClearDataAsync` is a settings mutation:

1. close mutation admission, cancel/drain gate waiters, and quiesce producers without holding the mutation gate; timeout/cancellation resumes every paused producer and reopens admission before returning;
2. acquire the exclusive destructive lease and mutation gate, then journal the old generation/manifest and stage a fully validated default generation under a new identity without disposing or modifying the old generation;
3. apply and verify default settings/network state while the old facade remains authoritative; prepare every new-generation supervisor attachment paused and pass its readiness probe;
4. atomically promote and flush the durable current-generation manifest/settings, then swap the in-memory facade to the staged generation while admission is still closed;
5. verify the promoted generation plus every paused attachment and flush the commit marker, establishing the rollback cut point;
6. after the marker, dispose the old generation/delete its files and idempotently resume the already prepared supervisors; a crash here recovers forward and completes cleanup/activation;
7. reopen admission only after post-commit supervisor health succeeds. Resume/cleanup has a 30-second forward-recovery deadline; failure returns `CommittedDegraded`, retains the committed journal, transitions admission to `RecoveryOnly`, and exposes the operation-ID-bound retry/export-diagnostics surface. The retry acquires the recovery-exclusive lease and mutation gate without reopening ordinary admission, and can only complete the recorded forward activation/verification/cleanup. Startup recovery uses the same idempotent path and enters localized safe mode if it still cannot establish health.

Page ViewModels and supervisors depend on host-singleton repository facades, not concrete generation-scoped repositories. A facade pins a generation for the duration of each operation. Clear-data marks the old generation draining, rejects new leases, and waits for existing leases during pre-gate quiescence. The old scope and its rollback material stay alive through paused new-generation attachment, durable manifest promotion, in-memory swap, and target verification. Before the commit marker, any failure disposes prepared attachments, swaps the facade and manifest back to the verified old generation, and resumes its prior supervisors; after the marker, rollback is forbidden and recovery completes the new generation forward. The old scope may be disposed only after that marker. No consumer retains a stale concrete repository reference or observes the pre-commit staged generation.

### 5.5 NavigationService

One navigation registry owns:

- tag;
- page type/factory;
- localized label and icon key;
- shell visibility;
- tray visibility;
- optional anchor/action parameter;
- selection behavior.

Shell, tray, Master tiles, and internal links all call `INavigationService`. Connections is registered as a supported page. A contract test requires every Page to be registered or explicitly marked intentionally unrouted.

### 5.6 LogStorageCoordinator

Log storage uses a fair serialized write queue with capacity 4096 and independent read connections/snapshots. Enqueue waits at most 100 ms before returning a typed overload result; data is never silently dropped. Export and VACUUM run through an explicit maintenance gate that pauses new maintenance work without holding the general read path or a CLR monitor across SQLite calls.

Foreground reads use a two-second SQLite busy timeout. A deterministic integration test holds maintenance at its longest barrier and requires an independent sampling/trigger read to finish within one second. A 10,000-operation mixed read/write/export stress test requires zero starvation, zero silent loss, and every operation to complete within its declared two-second foreground or 30-second maintenance deadline. Queue depth, enqueue latency, read latency, maintenance duration, overloads, and failed writes are observable.

## 6. Trigger Architecture and Semantics

### 6.1 Components

| Component | Responsibility |
|---|---|
| `ITriggerRepository` | Versioned atomic load/save, corruption quarantine, migration |
| `TriggerScheduler` | Periodic ticks and runtime event ingestion |
| `TriggerContextProvider` | Fully asynchronous context creation |
| `TriggerMatcher` | Pure condition evaluation and re-arm decisions |
| `TriggerExecutionGate` | Per-task serialization and durable idempotency token |
| `TriggerActionExecutor` | Executes typed actions and returns results |
| `TriggerEditorViewModel` | Multi-condition/action editing and validation |

When triggers are disabled, the scheduler and startup pipeline return before requesting any evaluation context. When enabled, `TriggerContextProvider` fetches controller and storage data asynchronously. Controller timeout, malformed JSON, SQLite, and IO failures are represented as typed unavailable fields/degraded context when the affected condition can safely evaluate false; failures that prevent a sound decision return a typed evaluation failure. Neither case escapes through a UI event handler.

### 6.2 Versioned condition data

The new trigger document has a schema version and typed parameter objects. Legacy `Kind/Threshold/Value` records migrate on load and are saved only after successful validation.

Traffic scope values are:

- `RollingWindow`: traffic in a configured duration; legacy `Scheduled` maps here, default five minutes;
- `CurrentSession`: traffic since this application runtime started; legacy `Startup` maps here;
- `AllTime`: persisted cumulative traffic; legacy `Cumulative` maps here.

Unknown kinds or invalid enum numbers quarantine only the affected task and produce a diagnostic; they do not crash startup or silently become defaults.

### 6.3 Firing semantics

- Multiple conditions use logical AND.
- `SystemTime` fires at most once per local calendar day when evaluation first occurs at or after the target time.
- `AppEntered`, `ProxyStarted`, and `NotificationRaised` match only the corresponding event instance.
- Rate, rolling-window traffic, runtime, active-connections, and session thresholds fire on a false-to-true edge and re-arm after returning false.
- All-time cumulative traffic fires once for the current task revision. Editing its threshold or explicitly resetting task state re-arms it.
- One task cannot execute concurrently from periodic and runtime event paths.
- A repository transaction commits the condition latch plus an execution outbox before dispatch. Each action record contains execution ID, task revision, action index, idempotency key, desired effect, and `Pending/Running/HandedOff/Succeeded/Failed/Uncertain` state.
- Current actions are made effect-idempotent: state-setting actions verify desired final state before retry; connection close is safe to repeat; notifications deduplicate by execution/action ID. A future non-idempotent action is rejected at registration unless it supplies an idempotency or compensation contract. `ExitApplication` follows the lifecycle handoff protocol below rather than awaiting shutdown inside the trigger participant.
- After a crash, `Pending/Running` actions are reconciled against external final state and then marked succeeded or retried; `HandedOff` actions use their handoff protocol and process epoch. An effect that cannot be queried or deduplicated becomes `Uncertain`, blocks later actions in that execution, and surfaces a diagnostic instead of guessing.
- Trigger-generated notifications carry provenance and cannot recursively re-enter the same task unless the user configured a distinct notification condition that permits it.

`ProcessLifetimeRunner` is owned by WinUI `App`, lives outside the service provider, and is never a trigger/background quiescence participant. AppHost receives only its non-owned `ILifecycleRequestSink`. `ExitApplication` must be the final action in an execution; editor/domain validation rejects later actions. One trigger transaction changes it to `HandedOff` and inserts a lifecycle request keyed by execution/action ID and the current process epoch. The action executor publishes that ID to the sink, returns, and releases its repository facade pin, `TriggerExecutionGate`, supervisor in-flight lease, and every other host-owned lease; only after an explicit release acknowledgement may the outer runner invoke `ShutdownAsync` from its own task. When shutdown returns `PreparedForHostDisposal`, the runner lets that call stack unwind and then stops/disposes AppHost, so host disposal can await the trigger supervisor and lifecycle publisher without waiting on the task currently disposing it. If shutdown fails while the process remains alive, the runner records the typed failure and the action becomes `Failed` or `Uncertain` according to final-state verification. If the process terminates after durable handoff, the next primary launch treats a prior-epoch exit as satisfied and marks it `Succeeded` without exiting the new process. Handoff insertion, complete lease-release acknowledgement, shutdown start, host disposal, and process-boundary recovery are all idempotent.

### 6.4 Persistence

The target repository stores trigger definitions, latches/revisions, executions, per-action outbox state, and lifecycle handoff/process-epoch records transactionally in `Triggers.db` (SQLite WAL). Writes commit through SQLite transactions. A last-known-good backup is produced through the SQLite Backup API into a same-volume temporary file, flushed, validated, and atomically promoted; raw WAL/shm files are never copied as a backup.

Legacy `Triggers.json` is read only through a migration adapter. A valid document is imported in one database transaction and retained as a timestamped migration backup until the next successful launch. Invalid JSON/IO is quarantined with a visible diagnostic; a valid database/backup is preferred, otherwise the repository opens an empty recoverable collection. No trigger persistence exception can escape static initialization because trigger services have no static initialization.

Crash tests terminate the worker during legacy migration, database commit, backup promotion, before dispatch, after external effect, before/after each action-state commit, and at every exit handoff/release/shutdown-start boundary.

Legacy `LastTriggeredAt` migrates deterministically: a scheduled condition records that local calendar date as consumed; an all-time cumulative condition is consumed for revision 1; edge conditions start disarmed when a prior trigger timestamp exists and re-arm only after an observed false evaluation; event-only conditions carry the timestamp for history but no persistent latch. Migration never invents a completed outbox action.

## 7. Settings and Data Package Semantics

### 7.1 Live apply

Persisted settings use a versioned `SettingsEnvelope` with a monotonic document-level `EnvelopeRevision`, canonical per-key `DesiredEntry(value, KeyDesiredRevision)`, per-key `AppliedState` (`Verified(value, source, observedHash, observedAt)` or `Unknown(reason)`), an ordered `PendingApplications` collection of disjoint batches, and migration history. A batch contains its `LiveReconcile`/`Restart` kind, batch ID, creation sequence, attempt ID/state/error, and immutable entries `(key, KeyDesiredRevision, valueHash)`. `EnvelopeRevision` changes on every envelope transaction but never participates in application-attempt identity; a key's desired revision changes only when that key's value changes. After every envelope transaction, each pending-required key whose `Desired` differs from verified `AppliedState`, or whose `AppliedState` is `Unknown`, is covered by exactly one batch entry matching its current `KeyDesiredRevision` and value hash; no key may overlap or remain uncovered. A registry-classified `Unknown/BlockedProbe` may be deliberately non-applicable, but must retain its explicit diagnostic instead of masquerading as applied. Runtime code consumes only verified applied values. An unknown safety-sensitive external value disables the affected operation or uses the registry entry's explicit safe fallback and exposes degraded health. Presentation renders `Desired` together with its pending/applied/failed/unknown status.

Editing keys in `Pending` or `Failed` batches is one atomic envelope rewrite. A normalized value equal to the current `DesiredEntry` is a no-op and leaves its batch entry byte-for-byte unchanged. Otherwise the rewrite advances `EnvelopeRevision`, assigns a new `KeyDesiredRevision` only to each changed key, and removes only those changed keys from their old batches. Every untouched sibling retains its `DesiredEntry`, batch ID, kind, creation sequence, state, attempt ID, last error, and batch entry byte-for-byte; the old batch is deleted only when empty. If the new value equals verified `AppliedState`, no replacement batch is created. Reverting an unknown key sets `Desired` to the registry's safe fallback and either creates a new batch when the external baseline is compensable or retains explicit `Unknown/BlockedProbe` with no automatic batch when that fallback is locally enforced and the key is registry-classified as non-applicable. Every other changed key enters a new batch with a new creation sequence, key desired revision, and attempt ID. Editing a `Running` batch is rejected with a typed busy result until its journal reaches a recoverable terminal state, so a possibly side-effecting attempt is never split. Revert uses the same split operation and never discards unrelated failed work.

Batch processing order is total and stable: `LiveReconcile` precedes `Restart`, then creation sequence, then batch ID. Import performs the same split against every pre-existing batch in its single transaction and groups newly touched keys by application kind and imported transaction, retaining each entry's own key desired revision. Validation proves the coverage/disjointness invariant before promotion. Crash tests at every atomic-envelope promotion cut point observe either the complete old partition or the complete new partition, including edits and reverts of one key in a multi-key `Pending` or `Failed` batch; they never observe a lost sibling, duplicated key, reset attempt ID, or uncovered mismatch. An unrelated envelope transaction cannot alter an existing attempt's identity or make it run again.

The default is live application with final-state verification:

- language, theme, accent, tray composition, notification preferences, and regional display refresh immediately;
- sampling enable/interval restarts the sampling hosted service;
- mixed port, TUN, active profile, and mode use `NetworkStateCoordinator` and commit only after health checks;
- StartupTask changes query the final Windows state and commit only when it matches the request;
- startup restore/fallback registration is accessed through an injected Windows adapter; registry/path failures return typed results and cannot abort Settings page construction;
- each connection-test target owns its own default URL. Invalid edits keep the editor open with field-level localized validation and never collapse all targets to the legacy single URL;
- settings that genuinely require process restart are validated, then atomically persist a new `Desired` revision plus a `PendingApplications` restart batch while preserving the old verified `AppliedState`; this durable target returns `RestartRequired` and is never presented as already active.

The UI shows pending, applied, failed/rolled back, or restart-required state. It never displays a desired value as already applied while an external operation is still in flight.

On primary startup, after journal recovery and before repositories/hosted services consume process-bound settings, `StartupCoordinator` reconciles auto-eligible batches in fixed `LiveReconcile` then `Restart` order under the mutation coordinator. Each batch is a separate transaction, and processing stops on the first failure. Automatic deduplication is keyed by batch ID, attempt ID, and the sorted immutable `(key, KeyDesiredRevision, valueHash)` entries; `EnvelopeRevision` is irrelevant. An automatic attempt runs at most once for that identity, and crash recovery resumes the same journaled attempt rather than creating another. Success verifies external/process state, atomically copies only the batch keys into verified `AppliedState`, removes the batch, and then writes the commit marker. Failure starts against the retained verified baseline or explicit safe fallback, keeps `Desired`, records the batch as `Failed` with a typed diagnostic, and requires an explicit retry (new attempt ID), edit, or revert instead of entering a restart loop. Revert atomically copies the affected verified applied values back to `Desired`; an unknown key can only be reset to its declared safe default. Import may include restart-only fields: its one transaction verifies live participants, advances `AppliedState` only for those fields, and commits restart-only fields in a separate pending batch without claiming their external effect is verified.

### 7.2 Import

Import stages and validates the entire package before mutation. It validates enum membership, ranges, paths, schema versions, file hashes, and duplicate entries. It then builds one combined `SettingsMutationPlan`; the single top-level mutation executes:

1. enters the pre-gate exclusive admission/drain protocol and quiesces writers, with reverse resume/reopen on failure;
2. snapshots settings, files, caches, runtime, and Windows state;
3. journals all file/settings/cache/network/hosted-service steps;
4. applies each staged participant exactly once without nested public coordinator calls;
5. verifies files, settings, profile/core generation, network state, and supervisor attachments;
6. atomically promotes and flushes the durable target persistence (including desired/applied-state/pending-application state), re-reads and verifies its target hash, and then flushes the single commit marker;
7. releases locks, publishes one aggregate event, and resumes writers.

Any error rolls back every layer. Rollback errors are reported with recovery instructions. `MasterHeroStatusLayout` and every current user setting are generated into the package descriptor set; legacy compatibility keys are not exported as independent current settings.

### 7.3 Reset and clear data

Group reset and global reset differ only by generated change-set scope. They share the same application path and cannot bypass callbacks.

Clear-data quiesces every producer, stages a default-only generation, applies default runtime state, atomically makes that generation authoritative, commits, and only then destroys the unreachable old generation before resuming default-enabled hosted services. The operation succeeds only when the new generation contains exactly the intentionally recreated canonical files and no cleared record can reappear through a stale facade, backup promotion, or producer write.

## 8. Presentation and MVVM

### 8.1 ViewModel rules

- Commands are asynchronous, cancellable where meaningful, and expose `IsBusy`, validation, and localized error state.
- Command execution is tracked; unexpected exceptions go to `IApplicationErrorSink` and the page error state.
- External state changes commit the displayed value only after success; failures restore the last applied value.
- ViewModels depend only on application interfaces and immutable presentation services.
- Localization refresh is event-driven for every active ViewModel.
- Page ViewModels implement `IAsyncNavigationAware`/`IAsyncDisposable`. `OnNavigatedFromAsync` cancels page commands, releases generation leases, and unsubscribes events before disposal. Frame caching is disabled by default; explicitly cached pages must prove idempotent activation/deactivation and never retain a window/data-generation reference.
- UI-bound notifications are marshalled through an injected window dispatcher and ignored after scope disposal. Tray navigation first activates or creates the window scope, then resolves the current `INavigationService`; it never targets a disposed window.

### 8.2 Code-behind rules

Allowed:

- XamlRoot and window handle acquisition;
- native window messages;
- visual state/animation hooks that cannot be expressed in XAML;
- file/folder/color picker invocation behind an injected UI service;
- forwarding framework events to commands.

Not allowed:

- direct repository/service singleton access;
- construction or mutation of domain trigger/settings objects;
- business validation or unit conversion;
- application startup/shutdown/network orchestration;
- unguarded `async void` work beyond framework event forwarding.

### 8.3 UI corrections

- Connections is added to adaptive shell and tray navigation.
- Long-list pages use finite Grid `*` viewports and preserve virtualization at 800x600.
- Proxy selection has pending/applied state, serializes requests, and rolls back on failure.
- Master tiles use Button/ToggleButton semantics with keyboard and UI Automation patterns.
- Accent foreground is selected from measured contrast and covers all interaction states/high contrast. Normal text must meet WCAG 2.2 AA 4.5:1; large text and non-text UI indicators must meet 3:1; high-contrast mode uses system high-contrast brushes rather than custom accent calculations.
- Every glyph-only button has a localized automation name and tooltip.
- Modal UI uses ContentDialog or a complete focus trap, Escape handling, background inertness, dialog UIA semantics, and focus restoration.
- Navigation always updates selected item and supports anchors/actions.
- Close confirmation has a single in-flight task and cannot call `ShowAsync` concurrently.
- Connections rows adapt below their current fixed-width requirement.
- Profiles/Links commands use real `CanExecute` state and surface selection/operation errors instead of silently returning.
- Initial and minimum window sizes are capped to the active monitor work area at the current DPI.
- Modal layout recalculates against the current window size and never freezes the first-open dimensions into permanent minimums.

Settings is decomposed into Appearance, Startup, Proxy, Windows Integration, Notification/Trigger, Tray/Region, Data Management, and Diagnostics section ViewModels. Master Control composes independent mode, status, and tile providers; Trigger editing is a separate editor ViewModel. Non-generated ViewModels are limited to one use-case family, at most eight constructor dependencies, and 600 physical lines; non-generated page code-behind is limited to 250 lines (350 for the native-window adapter). A CI architecture test enforces these boundaries and forbids moving logic into partial files merely to evade them.

### 8.4 Safe UI verification mode

The composition root supports a dedicated UI-smoke configuration selected only by an explicit test launch argument. It injects in-memory settings, fake mihomo, fake Windows proxy/StartupTask/service adapters, deterministic sample data, and a no-op external launcher. It does not change production code paths through conditional compilation; production and smoke modes resolve the same application interfaces with different infrastructure registrations. This mode enables real WinUI layout, keyboard, UIA, and navigation checks without touching the developer machine's proxy, registry, services, or user data.

## 9. Localization

### 9.1 Resource organization

`LocalizationService` remains an injected application service, but raw resources move into one source file per released language. Raw dictionaries are observable by completeness tests before fallback. Fallback remains English for corrupt/development builds only.

### 9.2 Completeness

- English, Simplified Chinese, Traditional Chinese, French, Russian, and German have the exact same explicit key set.
- All dynamic Trigger/Master/Hero keys are generated into the validation set.
- Composite-format placeholders and plural/quantity parameters match across languages.
- No released language may pass CI through English fallback or an untranslated-key allowlist.
- Existing semantic drift, including transparent-proxy behavior descriptions, is corrected.

### 9.3 Culture

The selected concrete language determines formatting culture for localized dates, times, numbers, byte rates, and strings. Auto-detect resolves to a supported concrete language and culture. An unsupported persisted enum is treated as corrupt input: it is diagnosed, repaired to the documented default concrete language, and then normal lookup proceeds. Runtime English fallback remains a last-resort lookup safety net, but it never counts as an explicit translation and cannot make a completeness gate pass.

## 10. Error Handling and Observability

- No sync-over-async (`GetAwaiter().GetResult`, `.Result`, `.Wait`) in application paths.
- No untracked fire-and-forget task. Long-running hosted tasks are owned by AppHost; the sole outer lifetime loop is tracked, awaited, and disposed by WinUI `App`; short tasks are awaited by commands/coordinators.
- Expected failures return typed results with stable error codes and localized presentation keys.
- Unexpected exceptions are logged with operation ID, component, transition phase, and rollback outcome.
- UI never renders raw `Exception.Message`; details remain in logs and an optional copyable diagnostic view.
- File writes that represent state use atomic replace/backup or database transactions.
- Cancellation is propagated during read-only and pre-side-effect work. After a mutation's first side effect, caller cancellation requests bounded compensation; rollback/recovery uses the independent token and typed outcome defined in 4.6, never the cancelled caller token.
- Process execution reads stdout/stderr asynchronously, has a real timeout, kills the process tree on cancellation/timeout, and re-queries final SCM state.
- Concurrent process output uses a thread-safe channel/buffer.
- Hosted loops are supervised through an injected clock/jitter source. Consecutive failures use delays of 1, 2, 5, 10, and 30 seconds, then remain capped at 30 seconds; production applies deterministic-per-service ±10% jitter while tests inject zero jitter. The first failure changes `Healthy` to `Retrying`; the fifth consecutive failure or 60 seconds since the first failure (whichever occurs first) changes it to `Degraded`. Degraded loops continue probing at the capped interval. One complete successful iteration changes `Retrying/Degraded` to `Recovering`; a second consecutive success at the normal configured interval changes it to `Healthy`, while a recovery failure returns to `Degraded` with a 30-second next probe. Intentional disable/quiesce/stop is `Stopped`, not failure.
- Health publishes state, consecutive failure/success counts, first/last failure times, next-attempt time, stable error code, and last-success time. Fake-clock tests assert the exact `1/2/5/10/30/30` sequence, fifth-failure degradation, 60-second boundary, two-success recovery, recovery relapse, jitter bounds, and zero work after stop; no exception may terminate the supervisor task.

## 11. Installer, Supply Chain, and Release

- Package manifest is the single source for MSIX Publisher. Development certificate generation reads it and verifies Subject, code-signing EKU, expiration, and thumbprint before use.
- Production signing material is external to the repository and selected by CI configuration.
- Installer records the exact development certificate thumbprint it installs and removes only that certificate during uninstall after confirming no installed package needs it.
- Mihomo version is pinned. Downloaded artifacts require SHA-256 or upstream signature verification before execution or packaging.
- CI emits three distinct signed attestations: standard SLSA build provenance, an SPDX 2.3 SBOM predicate, and a repository-defined release-metadata predicate. The custom predicate binds the source revision/ref/workflow run, every NuGet/Cargo lock path and hash, SDK/toolchain/action-pin inputs, pinned mihomo version/source/hash/signature identity, generated version, package/installer/SBOM hashes, and safe-to-publish Authenticode certificate/timestamp identity fields.
- One generated version file feeds manifest, app display, Rust installer, and build outputs.
- The stale Python installer entry point is removed or converted to a thin wrapper over the canonical Rust/MSIX build.
- Sandbox `all` requires every default scenario to pass; skipped is failure unless the scenario was explicitly excluded by the caller.
- The mihomo service runs as the virtual account `NT SERVICE\ClashSharpMihomo`, uses a restricted service SID, and declares only `SeChangeNotifyPrivilege` through the required-privileges service configuration. Sandbox must prove Rule/TUN operation under that token; adding a privilege requires a reviewed policy change and a failing capability test that proves necessity.
- Service binaries live under `%ProgramFiles%\ClashSharp`: Administrators and SYSTEM have Full Control, the service SID and Users have Read/Execute, and neither Users nor the interactive app may write. Generated privileged runtime configuration lives under `%ProgramData%\ClashSharp\Runtime`: Administrators and SYSTEM have Full Control, the service SID has Modify, and Users have no access. Inheritance is disabled and tests compare effective ACLs to this matrix.
- The medium-integrity full-trust UI never opens the privileged service endpoint. A separately packaged `ClashSharp.PrivilegedBroker` runs as an AppContainer out-of-process app-service task, accepts only the exact installed `CallerPackageFamilyName`, and owns the service binding. Missing AppContainer identity, broker activation failure, or identity mismatch disables privileged operations; there is no same-user or development named-pipe fallback in production.
- Broker-to-service communication is a versioned MIDL local-RPC interface registered at one fixed endpoint only on `ncalrpc`. The broker is a distinct package identity from the UI. `RpcServerRegisterIf3` uses `RPC_IF_ALLOW_LOCAL_ONLY | RPC_IF_ALLOW_SECURE_ONLY | RPC_IF_SEC_NO_CACHE` and an explicit dual-principal interface security descriptor: one ACE grants the exact installed user's SID required by the normal-token half of AppContainer access, and a separate ACE grants the exact broker AppContainer/package SID required by the restricted-token half. It grants no UI package, `Users`, `Authenticated Users`, or `ALL APPLICATION PACKAGES` access. These two token halves are the only DACL-level conjunction; package and capability allow ACEs within the AppContainer half would be additive, not logical AND, so the design does not rely on such ACEs to compose identity. The uncached security callback is authoritative: on every call it obtains an authorization context with `RpcGetAuthorizationContextForClient`, verifies the exact user and `AuthzContextInfoAppContainerSid` together with protected package/publisher registration, checks `RPC_C_AUTHN_WINNT` plus packet-privacy level, and rejects a missing or different AppContainer identity. It never trusts a PID, opens a client process/token, or requires `SeDebugPrivilege`/`SeImpersonatePrivilege`; the service's only enabled privilege remains `SeChangeNotifyPrivilege`.
- Installer provisioning resolves the exact service SID for `NT SERVICE\ClashSharpMihomo`, stores its binary SID in write-protected, broker-readable metadata, and fails install/repair if it differs from the SCM/account lookup. The broker calls `RpcBindingSetAuthInfoEx` with a null `ServerPrincName`, `RPC_C_AUTHN_WINNT`, `RPC_C_AUTHN_LEVEL_PKT_PRIVACY`, and `RPC_SECURITY_QOS_V5`: version 5, `RPC_C_QOS_CAPABILITIES_MUTUAL_AUTH`, `RPC_C_QOS_IDENTITY_DYNAMIC`, `RPC_C_IMP_LEVEL_IDENTIFY`, `Sid` equal to that exact service SID, and a `ServerSecurityDescriptor` whose server-identity DACL accepts only that SID. The V5 SID/security-descriptor check—not NTLM's backward-compatible mutual-authentication claim or an SPN derived from the machine account—is the authoritative server identity. The service registers matching authentication information. Dynamic identity makes a copied or recreated binding present the token of the thread making each call, so an unpackaged same-user helper still fails the AppContainer callback; the exact server-SID check makes a squatted endpoint or forged service response fail before any privileged result is accepted. Local-only registration exposes no named-pipe/TCP transport or remote endpoint.
- Each authenticated RPC session has a fresh nonce and strictly increasing sequence. A request contains session ID, sequence, operation ID, method/version, and DTO hash; the service atomically consumes the sequence and operation ID before an idempotent effect. Its response echoes the session ID, sequence, operation ID, and request hash and includes the typed result plus observed-state hash. The broker rejects any mismatch. Windows mutual authentication plus packet privacy binds both directions of this transcript, so the design introduces no separate application cryptographic secret or ACL for such a secret.
- The user app never supplies an executable/config/work-directory path to the service. The broker sends only bounded-size versioned DTOs after its own caller-identity/schema validation; the service independently validates enum/range/transition whitelists and writes its own protected runtime configuration. Arbitrary YAML keys, external executable paths, path traversal, unknown methods/versions, oversized payloads, incorrectly bound/replayed messages, and reused operation IDs are rejected. The threat boundary excludes code already executing inside the signed UI or broker, but even a compromised allowed client cannot select an executable or privileged filesystem path.
- Service installation verifies account, exact service SID, required privileges, restricted SID, quoted binary path, expected user/package/AppContainer identities, dual-principal RPC DACL, QOS V5 server-identity descriptor, and filesystem ACLs. Sandbox runs `sc qc`, service-token/SID/privilege inspection, RPC policy/identity inspection, `icacls`, Rule/TUN smoke, and standard-user overwrite/rename/delete attempts against binary and runtime configuration before stop, while stopped, and after restart; every tamper attempt must fail. It proves the broker succeeds while the full-trust UI direct call, unpackaged same-SID helper, wrong package/AppContainer, copied/recreated binding from a helper, endpoint squatting while the service is stopped, forged/mismatched response transcript, replayed/injected writer, tampered package/service-SID metadata, and remote protocol attempt are rejected without privileged side effects, then proves the authenticated path recovers after service restart with only the declared service privilege. The endpoint-squatting test runs an executable same-user fake server and proves QOS V5 rejects it even if `RPC_C_AUTHN_WINNT` reports the mutual-authentication capability.
- Patch-level .NET/Rust dependency updates are applied after their own tests. Major updates such as Windows App SDK or xUnit v3 require a compatibility task and cannot be silently bundled into unrelated fixes.
- Dependabot runs weekly on Monday for NuGet, Cargo, and GitHub Actions. Security updates open immediately; patch/minor updates may be grouped per ecosystem; every major update is isolated with a compatibility checklist and cannot be auto-merged.

## 12. Testing Strategy

### 12.1 Unit tests

- Pure trigger matcher, migration, re-arm, daily schedule, and action retry semantics.
- Settings registry completeness, parsing, enum membership, and generated descriptors.
- Hosted-supervisor fake-clock backoff and health-state transitions.
- Network transition planning and rollback decisions.
- Navigation registry completeness and selection behavior.
- Localization raw completeness and placeholder signatures.
- ViewModel commands, busy/error state, selection rollback, and multi-condition editing.

### 12.2 Integration tests

- Tests use `ProjectReference` to `ClashSharp.Core` and `ClashSharp.Infrastructure`; no production source links or `UNIT_TESTS` forks.
- Trigger persistence with corrupt legacy JSON/database, valid SQLite backup, denied IO, interrupted migration/transaction/backup promotion, and recoverable empty fallback.
- Settings apply/import/reset/clear-data transactions with injected failures at every phase, envelope-versus-per-key desired revisions, desired/applied-state/pending-application persistence, atomic multi-key pending/failed batch edit/revert/import splitting, attempt deduplication across unrelated envelope transactions and startup, legacy external-state mismatch/unavailable probes, deterministic alias-conflict migration, generation/paused-supervisor swap cut points, post-marker activation failure, same-process recovery-only retry, and verified rollback/forward recovery.
- Network coordinator with fake mihomo/controller/Windows proxy and deterministic barriers for concurrency.
- Runtime lifecycle with blocked in-flight sampling/trigger/audit work, a trigger action paused before mutation-gate admission, partial quiescence timeout, reverse resume, awaited healthy quiescence, and `ExitApplication` handed off only after every repository/facade/trigger lease is released. A disposal barrier remains blocked through `ShutdownAsync`, lets its stack unwind, then proves the App-owned runner can await `AppHost.StopAsync`/`DisposeAsync` without a queue-worker self-join. Separate committed and uncommitted `RecoveryOnly` exit tests prove no second journal or exit-policy mutation occurs and the next launch resumes the frozen recovery obligation. Deterministic retry/exit barriers cover a retry waiting for the recovery lease, holding the lease before the mutation gate, holding the gate before its first side effect, after phase-intent, after the side effect, after phase-complete flush, after successful journal deletion, and after a failed retained generation; every case has one `CompleteRecoveryAttempt` transition, zero host-disposal overlap, and no transition out of `ClosedForShutdown`.
- Process runner timeout/cancellation and concurrent stdout/stderr.
- AppHost registration and startup ordering contract.
- A two-process test starts a primary instance in RuleTakeover, launches a secondary instance, exits the secondary, and proves the primary core plus Windows proxy registry values never changed. A process-independent fake registry/barrier records any secondary side effect.
- Mutation tests interleave every pair of Apply/Import/Reset/ClearData/network transition/trigger action/shutdown/startup-recovery operations and assert admission closes before destructive gate acquisition, fair serialization, fixed lock order, no reentrancy, no stale rollback overwrite, full pre-gate waiter drainage, and no deadlock. A dedicated call-graph test starts shutdown from `ExitApplication` inside the trigger participant and proves handoff releases every host-owned lease before the App-owned runner quiesces and disposes the host.
- Crash recovery tests kill and restart a helper process at every durable-journal, lifecycle-handoff, and trigger-outbox cut point, including side-effect-before-phase-complete, durable-target-before-marker, marker-before-supervisor-resume, and marker-before-cleanup. They prove the protected recovery root survives clear-data, prior-epoch exit handoff does not exit the new process, and `Baseline/Desired/Partial/Unknown` probes follow the specified recovery branch. A separate same-process barrier leaves ordinary admission in `RecoveryOnly`, advances and fails a first retry, releases its gate/lease, succeeds on a second retry using the refreshed journal generation, proves unrelated mutations remain rejected, and reopens admission only after verified activation and cleanup.

The assembly-level global xUnit parallelization disable is removed. Pure tests run in parallel by default with unique temporary roots. Only tests that prove a documented process-global Windows constraint may use a named non-parallel collection. CI runs targeted concurrency suites repeatedly and a contract test fails if an unapproved global disable reappears.

Source-text tests are limited to a dedicated `ArchitectureContracts` area for artifacts that cannot be exercised through a production API (manifest, XAML resource declarations, generated version/signing metadata). They may assert stable schema/semantic facts but not source line order, local variable names, or implementation substrings. Existing source-string tests are classified; behavior assertions move to production assembly tests, and every retained contract records why runtime verification is impossible.

### 12.3 Presentation tests

- XAML build and Binding Diagnostics smoke.
- Every Page route contract, including Connections.
- 800x600 and high-DPI long-list reachability.
- Keyboard-only Master tile and glyph-button navigation.
- UI Automation names/patterns and accent contrast matrix.
- Proxy selection failure/out-of-order completion.
- Close dialog reentrancy and focus restoration.
- Safe UI-smoke launch proves that presentation verification performs no registry, proxy, service, StartupTask, or user-data mutation.

### 12.4 End-to-end gates

Repository pins the exact stable .NET SDK in `global.json` with `rollForward: disable`/`allowPrerelease: false`, the Rust toolchain in `rust-toolchain.toml`, NuGet dependencies in `packages.lock.json`, Cargo dependencies in the existing lock files, and GitHub Actions by immutable commit SHA.

Required PR jobs on a clean `windows-2025` runner are:

1. `dotnet`: `dotnet restore ClashSharp/ClashSharp.slnx --locked-mode`; format verification; x64 Debug and Release builds with warnings as errors; full Release tests against production assemblies; repeated concurrency tests.
2. `rust`: restore/cache the repository-pinned tool version by running `cargo install cargo-audit --version 0.22.2 --locked` from `eng/tool-versions.json`, require `cargo audit --version` to report exactly `0.22.2`, then for Installer and SandboxTest run `cargo fetch --locked`, `cargo fmt --check`, `cargo clippy --all-targets --locked -- -D warnings`, `cargo test --all-targets --locked`, and machine-readable `cargo audit`; the report records the advisory-database revision.
3. `dependency-policy`: machine-readable NuGet/RustSec results, lock consistency, exact `eng/tool-versions.json` bootstrap/version checks, deprecated/outdated report, action-SHA policy, and exception validation.
4. `package-contract`: manifest/version/publisher/certificate-subject checks, full-trust UI plus AppContainer broker identity/extension validation, canonical installer build, SBOM and custom release-metadata schema validation, and unsigned package layout verification.
5. `sandbox-contract`: harness tests proving every default scenario is required and that skipped/timeout/missing-report is failure; a workflow-contract test also enforces the declared least-privilege permission matrix.

Any known vulnerability fails regardless of severity unless listed in `eng/security-exceptions.json` with ecosystem, package/advisory ID, justification, owner, and an expiry no more than 30 days away. Expired/malformed exceptions fail. Deprecated/outdated packages generate a published report and weekly dependency PR; an approved major-version deferral has owner, compatibility reason, and review date.

Required release jobs are:

1. `msix-release` on a clean hosted Windows runner: locked restore/build, external production signing, signature verification, installer creation, SPDX 2.3 JSON SBOM, and a canonical `release-manifest.json` validated by `eng/attestation/release-metadata-v1.schema.json`. The manifest contains schema version; repository/ref/commit/workflow run; generated version; every NuGet/Cargo lock path/hash and aggregate; `global.json`, Rust toolchain, and action-pin input hashes; pinned mihomo version/source/hash/signature; every publishable path/size/SHA-256; SBOM hash; and Authenticode subject, issuer, serial, thumbprint, digest algorithm, timestamp authority, and timestamp. It uploads one immutable `release-bundle` and exposes the upload artifact ID, artifact digest, and release-manifest digest as job outputs.
2. `sandbox-e2e`, with `needs: msix-release`, on a clean self-hosted runner labelled `windows-sandbox`: it downloads that exact artifact ID, verifies the artifact and every release-manifest digest before installation, and records the same artifact ID/digest/manifest digest in its JSON report. Preflight requires the Windows Sandbox feature enabled, virtualization available, no prior ClashSharp package/service/certificate, and sufficient disk. It executes install-only, launch-no-proxy, startup-with-proxy-config, cleanup-uninstall, service identity/ACL/tamper, AppContainer-broker local-RPC adversarial authentication, and optional explicitly selected real-proxy. Default skipped, digest mismatch, missing report, timeout, or residual state is failure. Overall timeout is 30 minutes and cleanup evidence is always uploaded.
3. `attestation`, with `needs: [msix-release, sandbox-e2e]`: it downloads the same artifact ID, re-verifies the release and Sandbox-report digest binding, and uses the current `actions/attest` action pinned by commit SHA in three explicit modes: build provenance, SPDX 2.3 SBOM, and custom predicate type `https://github.com/Water-Run/ClashSharp/attestations/release-metadata/v1` whose predicate is the schema-validated release manifest. GitHub OIDC/Sigstore uses the Fulcio/Rekor public-good trust root for a public repository and GitHub's private Sigstore instance otherwise. Verification runs `gh attestation verify` once for provenance and once for each explicit predicate type. All three verified in-toto subject digest sets must exactly equal the package/installer set in `release-manifest.json` and Sandbox; the custom predicate is also field-compared against current lock hashes, mihomo metadata, signing certificate/timestamp output, version, source revision, and artifact hashes.
4. `publish`, with `needs: [msix-release, sandbox-e2e, attestation]`: it downloads the original immutable artifact ID, re-verifies the artifact/manifest digests and all three attestations, and promotes those exact bytes. Publication may not rebuild, re-sign, repackage, regenerate an SBOM, or upload any package/installer whose digest was not exercised by Sandbox.

Workflow-level permissions default to `contents: read`. Build/test/Sandbox jobs receive no write or OIDC permission. The attestation job alone declares `contents: read`, `id-token: write`, and `attestations: write`; the GitHub Release publish job declares only `contents: write` plus `attestations: read`, with no OIDC/attestation write permission. A workflow contract test parses the YAML and fails on missing attestation permissions or broader job permissions.

PR jobs have a 20-minute timeout except ordinary unit jobs (10 minutes). Release artifacts include command logs, test/TRX/JUnit reports, Sandbox JSON/report/screenshots, dependency reports, signature output, release manifest, SBOM, all three attestations, and cleanup evidence. Branch protection requires all PR jobs; tag publication requires every release job and the immutable artifact-ID/digest handoff.

## 13. Migration Sequence

1. Repository policy and CI baseline: `.gitattributes`, project split skeleton, production ProjectReferences, analyzer/build gates.
2. AppHost and lifetime contracts; single-instance-first StartupCoordinator.
3. NetworkStateCoordinator and shutdown/quiesce lifecycle.
4. Trigger repository, typed model migration, async context, scheduler/executor, editor VM.
5. Settings registry and transactional apply/import/reset/clear-data.
6. Navigation registry and Connections restoration.
7. Page-by-page removal of static singletons and code-behind domain logic.
8. Localization resource split, translations, culture, and live refresh.
9. Accessibility/adaptive UI corrections and visual verification.
10. Installer/signing/supply-chain/version unification and real Sandbox gates.
11. Log storage concurrency, service identity/ACL hardening, dependency maintenance, and remaining P3 UI/quality work.
12. Remove compatibility adapters, source-link test entries, `UNIT_TESTS` forks, stale tooling, and obsolete services.
13. Whole-system audit against every finding and release criterion.

Each step must end in a buildable state, dedicated regression tests, task-scoped review, and a commit. Later steps may not postpone a failing invariant introduced by an earlier step.

### 13.1 Durable stabilization ledger

`docs/architecture/stabilization-ledger.md` is version-controlled and contains one row for every audit/traceability ID with: severity, subsystem owner (`Application`, `Runtime`, `Presentation`, `Localization`, or `Release`), status (`Open`, `In Progress`, `Evidence Pending`, `Closed`), implementation-plan task, regression/manual evidence path, closure commit, reviewer, and closure date. Every implementation task updates the ledger in the same commit as its evidence. `Closed` is rejected by CI when evidence or closure commit is missing. New follow-up debt receives a new ID and owner rather than disappearing into prose.

## 14. Compatibility and Migration

- A version-controlled settings schema manifest gives every legacy alias one canonical key, an explicit precedence, `deprecatedInSchema`, and `lastReadableSchema`; `lastReadableSchema` must cover at least the next released schema. Aliases are read-only and are never emitted by current saves/exports. Removing an alias requires the current schema to be greater than `lastReadableSchema`, a retained old-package compatibility fixture, a release note, and an intentional manifest change reviewed in isolation.
- Settings resolution is deterministic. A present, valid canonical value is authoritative; each invalid or differently normalized alias is ignored only with a stable conflict diagnostic and is preserved in the migration backup. A present but invalid canonical value fails migration instead of silently falling back. When the canonical key is absent, every present alias must parse and validate and all must normalize to the same value; invalid or conflicting aliases fail with a diagnostic rather than being skipped or chosen silently. Manifest precedence selects the recorded migration source when equivalent aliases coexist.
- The settings registry classifies every key as `Internal`, `ExternallyObserved`, or `RestartBound` and supplies its probe plus safe fallback. Legacy migration runs under the mutation coordinator before normal consumers start. A legacy value always becomes `Desired`; an internal key becomes verified `AppliedState` only after parse, persistence round-trip, and consumer-contract validation. An externally observed key is read from the real Windows/mihomo/hosted-service adapter: the observation becomes verified `AppliedState`, and a mismatch adds the key to a `LiveReconcile` pending batch against that exact baseline. A restart-bound key uses a verifiable effective-process observation or a last-known-good value; otherwise it remains `Unknown` with the explicit safe fallback and enters a separate `Restart/InitialMigration` batch. A failed external probe produces `Unknown/BlockedProbe` and forbids automatic mutation until a later probe establishes a compensable baseline.
- Migration snapshots the source document and hash in the recovery root, builds and validates the complete target `SettingsEnvelope`, records `(migrationId, fromSchema, toSchema, sourceHash)` inside that envelope, flushes a same-volume temporary file, and atomically replaces the source while retaining the backup. The embedded record makes retry idempotent. Expected probe unavailability is persisted as `Unknown/BlockedProbe`; any parse, conflict, validation, probe-contract violation, write, flush, or promotion failure leaves the original authoritative document untouched and opens the last-known-good baseline or explicit safe fallback with a visible repair/export diagnostic. Tests cover legacy desired values that disagree with Windows proxy, StartupTask, mihomo, and hosted-service state, unavailable probes, and process termination at every migration promotion cut point.
- Existing `Triggers.json` is migrated to the versioned document without losing task IDs, names, enabled state, conditions, actions, or last-trigger metadata.
- Existing data packages remain importable through a versioned compatibility reader. New exports use the current schema.
- Profile catalogs, mihomo configuration, and logs retain current paths unless a migration transaction includes backup and rollback.
- User-visible behavior changes are documented in release notes, especially trigger edge semantics and live application of proxy settings.

## 15. Acceptance Criteria

Completion requires evidence for every item below:

1. Every audit P1/P2/P3 finding has an implemented change or an evidence-backed determination that the audited behavior does not exist. Every automatable finding has a regression test that failed against the baseline and passes after the change; non-automatable findings have recorded manual or clean-environment evidence. A design note alone never satisfies completion.
2. No View/ViewModel source directly accesses application-service `.Instance` members.
3. No application path contains sync-over-async or untracked fire-and-forget.
4. Single-instance ordering and network transition concurrency tests pass.
5. Trigger corruption, migration, daily/edge semantics, multi-condition preservation, and concurrent evaluation tests pass.
6. Settings import/reset/clear-data failure-injection matrix proves full rollback and quiescence.
7. Connections is reachable and all navigation sources stay selected consistently.
8. Raw i18n completeness is zero-missing for every released language and culture-format tests pass.
9. Accessibility/adaptive UI checks pass in automated tests and a recorded manual WinUI verification.
10. Tests reference production assemblies and no longer compile private production copies.
11. Safe UI-smoke mode proves zero external system mutation while exercising real WinUI presentation.
12. Service identity/ACL/local-RPC broker/tamper checks and log-storage concurrency/maintenance tests pass.
13. Debug/Release build, .NET suite, Rust gates (including pinned `cargo-audit 0.22.2` bootstrap), format, dependency audits, MSIX clean-runner, workflow-permission contract, and Sandbox scenarios all pass.
14. Every P3 finding has a change/test or an explicit evidence-backed closure record.
15. Final code review finds no unresolved Critical or Important issue.
16. Disabled-trigger startup creates no context/HTTP request; enabled degraded-context tests cover timeout, malformed JSON, SQLite, and IO without UI-thread blocking.
17. Port/TUN/Profile/StartupTask success, denial/mismatch, cancellation, rollback, and final displayed-state matrices pass.
18. Import-driven language changes refresh all active ViewModels in one cycle; reviewed translation semantics and localized error surfaces contain no raw exception message.
19. Fake-clock supervised sampling/scheduling tests prove the exact `1/2/5/10/30/30` retry sequence, fifth-failure/60-second degradation, two-success recovery, relapse, ±10% production jitter bounds, complete health fields, and zero work after stop.
20. Version consistency, canonical installer selection, SBOM contents, standard provenance, SPDX predicate, and custom release-metadata predicate pass schema, subject-digest, lock/mihomo/signing/version/source field verification on a clean runner.
21. A real two-process regression proves a secondary instance performs zero shared-data/network/service side effects and cannot change the primary instance's proxy/core state.
22. Durable-journal kill/restart tests at intent, external side effect, phase completion, durable-target promotion, commit marker, supervisor activation, and cleanup prove startup reaches exactly the verified baseline or committed target; intent-only `Partial/Unknown`, corrupt journal, rollback failure, protected recovery-root survival, committed activation failure, repeated recovery crashes, and same-process recovery-only retry follow the specified safe outcomes. A failed first retry refreshes the journal generation and releases all recovery resources so a second retry succeeds without restart; retry never opens ordinary admission and reopens it only after verified activation/cleanup. Retry/exit interleavings before lease/gate entry, before and after the first recovery side effect, at phase flush, at journal deletion, and on retained failure prove the atomic shutdown-pending handoff, frozen final generation, zero recovery/disposal overlap, and terminal `ClosedForShutdown` state.
23. Apply/Import/Reset/ClearData/network/trigger-action/shutdown/recovery pairwise interleaving tests prove admission/drain precedes destructive gate ownership, one fair non-reentrant mutation owner, the fixed lock order, no stale rollback overwrite, and no deadlock. Participant-originated `ExitApplication` durably hands off and releases every repository/facade/trigger lease before the App-owned lifetime runner begins shutdown; `ShutdownAsync` unwinds before that runner stops/disposes AppHost.
24. Normal shutdown preserves all user settings/profile/trigger records and never deletes, resets, or truncates logs (shutdown append records are permitted); clear-data alone replaces the data generation and cannot begin deletion after quiescence timeout. Partial quiescence timeout/cancellation restores prior producer health and UI mutation admission, or reports a typed degraded state; after commit, prepared-supervisor resume either verifies health and reopens admission or retains the journal in `CommittedDegraded` and `RecoveryOnly` for bounded, operation-ID-authorized forward recovery without restarting the process. Exiting from committed or uncommitted `RecoveryOnly` creates no competing journal/exit-policy mutation and the next launch resumes the preserved obligation.
25. Caller cancellation from the first side effect until the commit marker completes bounded compensation with a typed, final-state-verified outcome; after the marker it cannot roll back and committed activation/cleanup proceeds independently. Any replay-capable retained journal keeps admission in `RecoveryOnly`, rejects a later operation B, and is removed before B can begin. No rollback/cleanup uses the cancelled caller token.
26. Trigger outbox crash tests reconcile every current action without duplicate effective notification/state change or silently lost later action; lifecycle `HandedOff` state is epoch-safe, and uncertain effects block and diagnose.
27. Page/window navigation lifecycle tests prove cancellation, event unsubscription, dispatcher safety, generation release, tray window recreation, and zero updates after disposal.
28. CI architecture gates enforce ViewModel/code-behind/dependency limits, no volatile date headers, no global xUnit parallel disable, and the retained source-contract allowlist with reasons.
29. WCAG contrast thresholds, log-storage timing/deadline thresholds, service privilege/ACL matrix, and dependency exception expiry rules pass exactly as specified.
30. The stabilization ledger has an owner, current status, implementation task, evidence, closure commit, reviewer, and date for every finding; CI rejects incomplete closure rows.
31. Restart-required settings persist distinct desired/applied-state/pending batches, resume one journaled automatic attempt per immutable per-key desired revision set, retain a working baseline after failure, and support explicit retry/revert without a restart loop. Editing, reverting, or importing one key in a multi-key `Pending` or `Failed` batch advances `EnvelopeRevision` but atomically preserves every untouched sibling's `KeyDesiredRevision`, batch identity/state/attempt, gives only changed work a new key revision/attempt, and maintains exact one-batch coverage in stable `LiveReconcile`/`Restart` order across crashes and unrelated transactions. Legacy internal/external/restart keys establish `Verified` or `Unknown` state by their declared probes; canonical/alias conflicts and interrupted migration never silently overwrite settings.
32. Sandbox, all three attestations, and publication consume the exact immutable artifact ID and digest set emitted by `msix-release`; schema or lock/mihomo/signing/version/source mismatch, rebuild, digest substitution, or report mismatch fails before publication.
33. The distinct-package AppContainer broker succeeds through packet-private `ncalrpc` whose `RPC_SECURITY_QOS_V5` SID/server-security-descriptor check binds the server to the exact `ClashSharpMihomo` service SID and whose dual-principal interface DACL plus uncached callback binds the client to the exact user and broker AppContainer. The full-trust UI direct call, unpackaged same-user helper, wrong package/AppContainer, copied/recreated binding, stopped-service endpoint squatter (including NTLM's false mutual-authentication claim), forged or mismatched response transcript, replayed/injected writer, tampered package/service-SID metadata, and remote protocol call are rejected per RPC call without privileged side effects; service restart restores the authenticated path and the service token exposes only `SeChangeNotifyPrivilege`.

## 16. Design Decisions

- Comprehensive architectural change is authorized for long-term maintainability.
- Migration is incremental rather than a big-bang rewrite.
- Removing UI-layer static service access is a hard target.
- Settings are live-applied by default and commit only after verified external success.
- Trigger thresholds use edge semantics; scheduled time runs once per local day; multiple conditions are ANDed.
- Connections remains a supported user-facing feature.
- Released translations must be explicit; fallback cannot satisfy completeness gates.

## 17. Audit Traceability

This table is normative. An implementation task may supersede a proposed type name, but it may not remove the mapped behavior or verification evidence.

| Audit item | Governing design | Required evidence |
|---|---|---|
| P1-01 single-instance/proxy race | 5.1, 5.2, 12.2 | startup-order barriers plus real two-process RuleTakeover/secondary-exit zero-side-effect test |
| P1-02 corrupt/non-atomic trigger storage | 6.1, 6.4 | corrupt legacy JSON/database, valid SQLite backup, denied IO and interrupted migration/commit/backup tests |
| P1-03 ignored scope/repeated execution | 6.2, 6.3 | all scopes, daily/edge, periodic/runtime concurrency, outbox, and participant-originated exit-handoff tests |
| P1-04 multi-condition data loss | 6.1, 6.3, 8.1 | open/save round-trip preserving all AND conditions |
| P1-05 sync trigger context/UI freeze | 2.1(19), 5.1, 6.1, 10 | disabled short-circuit plus timeout/invalid JSON/SQLite/IO async responsiveness tests |
| P1-06 reset/clear runtime split | 4.5, 5.3, 5.4, 7.3 | admission/drain, partial-quiescence resume, paused attachment, committed activation failure, and in-flight-writer matrix |
| P1-07 partial import/runtime/cache split | 5.3, 7.1, 7.2, 14 | full snapshot, applied-state/legacy probe, cache reload, runtime final-state and rollback tests |
| P1-08 TUN/port only saved | 2.1(22), 5.2, 7.1 | success/failure/cancellation live transition, health-check, rollback and displayed-state tests |
| P1-09 Connections unreachable | 5.5, 8.3 | route registry and real navigation smoke |
| P1-10 MSIX subject mismatch | 11 | clean-runner subject validation/package/sign/install test |
| P1-11 test copies/no CI | 3, 12 | ProjectReference-only tests and required CI workflow |
| P2-I18N-01 fallback hides missing keys | 9.1, 9.2 | raw zero-missing matrix before fallback |
| P2-I18N-02 culture mismatch | 9.3 | language/culture date-number-byte formatting matrix |
| P2-I18N-03 mixed language after import | 2.1(21), 7.1, 8.1, 9.3 | active shell/page one-cycle live-refresh integration test |
| P2-I18N-04 semantic drift/raw exceptions | 2.1(21), 9.2, 10 | reviewed strings and UI error-key/no-raw-exception tests |
| P2-SET-01 StartupTask false success | 2.1(22), 7.1 | Other/denied/final-state-mismatch rollback and UI-state tests |
| P2-SET-02 undefined numeric enum | 5.3, 7.2 | every enum rejects numeric undefined input before mutation |
| P2-SET-03 URL feedback/default mix-up | 7.1 | per-target default and field-validation tests |
| P2-RUN-01 sampling loop silently dies | 2.1(20), 5.4, 10 | fake-clock exact backoff/degraded/recovery/stop sequence and health-field tests |
| P2-RUN-02 `sc.exe` timeout/cancel | 10 | hung helper timeout, process-tree kill, SCM re-query tests |
| P2-RUN-03 fire-and-forget/optimistic UI | 8.1, 10 | error sink, pending/applied/rollback command tests |
| P2-RUN-04 concurrent diagnostic buffer | 10 | concurrent stdout/stderr stress test |
| P2-UI-01 unbounded list viewport | 8.3, 8.4 | 200-item 800x600 last-item reachability test |
| P2-UI-02 proxy selection race | 8.1, 8.3 | failure rollback and out-of-order completion test |
| P2-UI-03 pointer-only Master tile | 8.3 | keyboard and TogglePattern test |
| P2-UI-04 accent contrast | 8.3 | WCAG 4.5:1 text / 3:1 large or non-text matrix for all states/high-contrast |
| P2-UI-05 unnamed glyph buttons | 8.3 | localized UIA name/tooltip inventory test |
| P2-UI-06 incomplete modal overlay | 8.3 | focus trap, Escape, inert background, UIA and focus-restore tests |
| P2-UI-07 navigation selection drift | 5.5, 8.3 | shell/tray/internal tag+anchor selection tests |
| P2-UI-08 close dialog reentrancy | 8.3 | repeated-close single-dialog test |
| P2-UI-09 Connections width overflow | 8.3, 8.4 | 800x600 and DPI adaptive-row test |
| P2-REL-01 trust/download integrity | 2.1(23), 11, 12.4 | certificate cleanup, tampered-download rejection, immutable artifact handoff, Sandbox binding, and three-attestation subject/schema/metadata verification |
| P2-REL-02 skipped Sandbox success | 11, 12.4 | default skipped scenario returns non-zero; all scenarios executed |
| P2-REL-03 version/build drift | 2.2, 11 | single-version generated-output and canonical installer tests |
| P2-QA-01 format/spec gates | 4.4, 12.4 | clean-checkout format/analyzer/documentation, pinned tool bootstrap, and workflow-permission gates |
| P3 dependency governance | 2.2, 11, 12.4 | weekly Dependabot, pinned `cargo-audit`, patch updates, security immediacy, and recorded major-version decisions |
| Additional: disabled Profiles/Links commands | 8.1, 8.3 | `CanExecute` transition and visible error tests |
| Additional: work-area/overlay sizing | 8.3, 8.4 | DPI/work-area and resize tests |
| P3 log-storage global lock | 5.6 | 1s deterministic read, 2s foreground/30s maintenance deadlines, no-starvation stress |
| P3 code size/static singleton debt | 3, 4, 8 | no presentation `.Instance`; section boundaries, dependency and 600/250/350-line architecture gates |
| P3 nullable/docs/analyzer/header drift | 4.4 | CodingStyle/build consistency and no volatile author/file/date banner gate |
| P3 source-text-heavy tests/no parallelism | 12.2 | retained-contract reason inventory, production behavior replacements, default parallel and repeated concurrency suites |
| P3 debt owner/status tracking | 13.1 | complete owner/status/task/evidence/commit/reviewer/date ledger with CI validation |
| Security candidate: service identity/ACL | 11, 12.4 | exact virtual-account/SID/privilege/filesystem policy, AppContainer dual-principal RPC DACL, QOS V5 exact-service-SID/server-descriptor authentication, dynamic per-call Authz/transcript binding, direct/helper/squatter/forgery/replay/remote rejection, Rule/TUN capability, and stop/running/restart tamper matrix |
