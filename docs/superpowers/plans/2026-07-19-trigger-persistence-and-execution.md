# Trigger Persistence and Execution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. This repository session does not authorize subagent delegation, so execute locally in the existing isolated worktree.

**Goal:** Replace the synchronous JSON-backed Trigger monolith with typed trigger semantics, transactional SQLite persistence, deterministic scheduling, durable action reconciliation, lifecycle-safe exit handoff, and a multi-condition editor that preserves every AND condition.

**Architecture:** `ClashSharp.Core` owns immutable trigger definitions, typed condition parameters, validation, and pure matching/re-arm decisions. `ClashSharp.Application` owns repository contracts, scheduler/execution orchestration, per-task serialization, degraded context results, outbox reconciliation, and lifecycle handoff contracts. `ClashSharp.Infrastructure` owns `Triggers.db`, WAL transactions, backup/promotion, legacy JSON migration, quarantine, and process-crash recovery. WinUI supplies platform adapters and a temporary host-composed facade; Trigger editor domain state moves into a dedicated ViewModel. No trigger path may perform synchronous controller/storage I/O, mutate files directly, or start detached work.

**Tech Stack:** .NET 10 / C# 14, xUnit, Microsoft.Data.Sqlite 10, SQLite WAL/Backup API, `System.Threading.Channels`, `System.Text.Json`, Microsoft.Extensions.DependencyInjection, WinUI 3.

**Normative constraints:** `docs/superpowers/specs/2026-07-18-architecture-stabilization-design.md` sections 4.3–4.6, 5.1, 5.4, 6.1–6.4, 8.1–8.2, 10, 12, 14, 15, and 17. This phase must close `P1-02`, `P1-03`, `P1-04`, and `P1-05`. It may add evidence to lifecycle and presentation rows, but it must not claim the Phase 05 data-generation transaction or the Phase 07 full presentation migration.

---

## Phase invariants

- Trigger definitions are immutable, revisioned, validated, and stored from production assemblies; invalid enum numbers or parameters never silently become defaults.
- Multiple conditions are logical AND. Event conditions match only their event instance; scheduled conditions consume one local calendar date; threshold conditions use false-to-true edges; all-time totals consume one task revision.
- Disabled trigger startup and evaluation return before requesting any controller, SQLite statistics, or other context data.
- Context creation is asynchronous. Expected HTTP/JSON/SQLite/IO faults become typed unavailable fields or a typed evaluation failure, never UI-thread blocking or escaped background exceptions.
- A task has at most one evaluation/execution in flight across periodic and runtime-event paths.
- The latch transition, execution row, and complete ordered action outbox are committed in one SQLite transaction before the first external action.
- Every current action is effect-idempotent, safely repeatable, or deduplicated. `Uncertain` blocks later actions and is surfaced; no executor guesses success.
- `ExitApplication` is the final action. Durable handoff and lease-release acknowledgement precede the App-owned lifetime runner; the trigger participant never waits for its own host disposal.
- `Triggers.db` uses WAL and transactional writes. Backups use SQLite Backup into a same-volume temporary database, validation, flush, and atomic promotion; WAL/SHM files are never copied.
- Legacy `Triggers.json` is migration-only. Valid tasks retain IDs, names, enabled state, every condition/action, and timestamp semantics; bad documents/tasks are quarantined with stable diagnostics and cannot crash static initialization.
- Trigger scheduling is an awaited `IRuntimeParticipant` with owned tasks/channels and health. No timer callback or event path starts detached async work.
- Trigger editing is owned by `TriggerEditorViewModel`; opening and saving a task round-trips every condition and ordered action.

## Checkpoint strategy

Keep every checkpoint buildable and independently reviewable:

1. typed domain and pure firing semantics;
2. SQLite repository, backup, and legacy migration;
3. durable execution/outbox and lifecycle handoff;
4. supervised scheduler and asynchronous context;
5. editor migration, compatibility removal, and phase evidence.

---

### Task 1: Establish immutable typed trigger definitions and validation

**Files:**

- Create: `ClashSharp/ClashSharp.Core/Domain/Triggers/TriggerEventKind.cs`
- Create: `ClashSharp/ClashSharp.Core/Domain/Triggers/TriggerCondition.cs`
- Create: `ClashSharp/ClashSharp.Core/Domain/Triggers/TriggerConditionParameters.cs`
- Create: `ClashSharp/ClashSharp.Core/Domain/Triggers/TriggerAction.cs`
- Create: `ClashSharp/ClashSharp.Core/Domain/Triggers/TriggerTaskDefinition.cs`
- Create: `ClashSharp/ClashSharp.Core/Domain/Triggers/TriggerDefinitionValidator.cs`
- Create: `ClashSharp/ClashSharp.Tests/Unit/Triggers/TriggerDefinitionValidatorTests.cs`
- Modify: `ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj`

- [x] **Step 1: Write RED domain tests**

Cover stable IDs, positive revisions, immutable copied collections, every condition parameter shape, positive finite thresholds/windows, valid local times, enum membership, nonempty conditions/actions, unique condition identities, ordered actions, and `ExitApplication` only as the final action. Prove undefined numeric enums and mismatched parameter types are rejected before persistence or mutation.

- [x] **Step 2: Implement the production Core model**

Use typed parameter records rather than `Kind/Threshold/Value` scalar bags. Model `RollingWindow`, `CurrentSession`, and `AllTime` explicitly. Keep notification severity trigger-specific or move a platform-neutral severity enum into Core; Core must not reference WinUI.

- [x] **Step 3: Prove assembly ownership**

Tests reference the production Core assembly only. Do not add source links or `UNIT_TESTS` branches. Run the domain tests, Core Release build, format verification, and `git diff --check`.

- [x] **Step 4: Review and checkpoint**

Use the requesting/receiving-code-review checklists, rerun focused tests, and commit `feat: add typed trigger domain`.

---

### Task 2: Implement pure AND, edge, daily, and revision firing semantics

**Files:**

- Create: `ClashSharp/ClashSharp.Core/Domain/Triggers/TriggerEvaluationContext.cs`
- Create: `ClashSharp/ClashSharp.Core/Domain/Triggers/TriggerConditionState.cs`
- Create: `ClashSharp/ClashSharp.Core/Domain/Triggers/TriggerTaskState.cs`
- Create: `ClashSharp/ClashSharp.Core/Domain/Triggers/TriggerMatchDecision.cs`
- Create: `ClashSharp/ClashSharp.Core/Domain/Triggers/TriggerMatcher.cs`
- Create: `ClashSharp/ClashSharp.Tests/Unit/Triggers/TriggerMatcherTests.cs`

- [x] **Step 1: Write RED matcher tests**

Prove all conditions are ANDed; event conditions match only their exact event; `SystemTime` fires once per supplied local date at or after the target; rate/window/runtime/connection/session thresholds fire on false-to-true and re-arm after false; all-time traffic fires once per task revision; editing the threshold increments revision and re-arms; disabled/empty tasks do not fire. Inject time/date and never read process clocks in the matcher.

- [x] **Step 2: Implement a pure transition function**

Return both the decision and complete proposed next state without I/O. Distinguish unavailable condition data from false data. A sound-decision failure must be typed, not represented as a default zero.

- [x] **Step 3: Verify deterministic semantics**

Run the matcher suite repeatedly, Core Release build, format verification, and diff check.

- [x] **Step 4: Review and checkpoint**

Commit `feat: define deterministic trigger matching` after addressing all Critical/Important review findings.

---

### Task 3: Define repository, execution, outbox, and diagnostic contracts

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Triggers/ITriggerRepository.cs`
- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerRepositorySnapshot.cs`
- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerExecution.cs`
- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerOutboxAction.cs`
- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerOutboxState.cs`
- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerDiagnostic.cs`
- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerPersistenceResult.cs`
- Create: `ClashSharp/ClashSharp.Tests/Architecture/TriggerContractTests.cs`

- [x] **Step 1: Write RED contract tests**

Require schema version, task revision/order, latch version, execution ID, action index, deterministic idempotency key, desired effect, `Pending/Running/HandedOff/Succeeded/Failed/Uncertain`, process epoch, typed diagnostics, optimistic expected-state input, and cancellation-aware async APIs.

- [x] **Step 2: Implement narrow application contracts**

Separate definition CRUD/snapshots from atomic match-and-enqueue and outbox transition/recovery. The repository transaction API must accept the expected task revision/latch version and either commit the proposed state plus full outbox or report a typed conflict; callers may not partially create an execution.

- [x] **Step 3: Add lifecycle handoff records without implementation shortcuts**

Represent handoff insertion, release acknowledgement, shutdown start, completion/failure, and process epoch explicitly. Do not overload ordinary action failure strings.

- [x] **Step 4: Review and checkpoint**

Run Application tests/build and commit `feat: define durable trigger contracts`.

---

### Task 4: Build the SQLite WAL trigger repository and atomic backup

**Files:**

- Modify: `ClashSharp/ClashSharp.Infrastructure/ClashSharp.Infrastructure.csproj`
- Modify: `ClashSharp/ClashSharp.Infrastructure/packages.lock.json`
- Create: `ClashSharp/ClashSharp.Infrastructure/Triggers/SqliteTriggerRepository.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Triggers/TriggerDatabaseSchema.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Triggers/TriggerDefinitionCodec.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Triggers/TriggerBackupManager.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Triggers/ITriggerPersistenceFaultInjector.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/SqliteTriggerRepositoryTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/TriggerBackupRecoveryTests.cs`

- [ ] **Step 1: Write RED repository tests**

Cover empty initialization, schema/version metadata, WAL mode, ordered immutable definition round-trip, transactional replace/delete/reorder, atomic latch+execution+all-actions enqueue, optimistic conflict, legal outbox transitions, restart recovery, corrupt primary with valid backup, corrupt primary and backup safe-empty diagnostic, busy/denied IO, and independent concurrent reads.

- [ ] **Step 2: Implement the normalized schema**

Store definitions, typed condition/action payloads, task/condition latches, executions, ordered outbox actions, lifecycle handoffs, process epochs, and diagnostics. Use transactions and foreign keys. Never expose a live connection or mutable entity collection.

- [ ] **Step 3: Implement safe backup and promotion**

Use SQLite Backup into a same-volume temporary database, open/validate it, flush the file, then atomically promote. Fault-inject before backup, after backup, after validation, and before/after promotion. Never copy `-wal` or `-shm` files.

- [ ] **Step 4: Verify package locks and checkpoint**

Update locks intentionally, then run locked restore, Infrastructure/Integration tests, Release build, format, diff check, review, and commit `feat: persist triggers transactionally`.

---

### Task 5: Migrate and quarantine legacy `Triggers.json` deterministically

**Files:**

- Create: `ClashSharp/ClashSharp.Infrastructure/Triggers/LegacyTriggerDocument.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Triggers/LegacyTriggerMigrationReader.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Triggers/TriggerMigrationCoordinator.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/LegacyTriggerMigrationTests.cs`
- Create: `ClashSharp/ClashSharp.TriggerProbe/ClashSharp.TriggerProbe.csproj`
- Create: `ClashSharp/ClashSharp.TriggerProbe/Program.cs`
- Modify: `ClashSharp/ClashSharp.slnx`
- Modify: `ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj`

- [ ] **Step 1: Write RED compatibility fixtures**

Cover legacy array and document shapes, all kinds/scopes/actions, multiple conditions/actions, IDs/names/enabled/order, duplicate normalization diagnostics, undefined enums, invalid parameters, truncated/malformed JSON, denied IO, a valid existing database taking precedence, and idempotent repeated launch.

- [ ] **Step 2: Implement deterministic timestamp migration**

Map legacy `Scheduled` to a five-minute `RollingWindow`, `Startup` to `CurrentSession`, and `Cumulative` to `AllTime`. Convert `LastTriggeredAt` to daily consumption, revision-1 all-time consumption, disarmed edge state, or event history exactly as the design specifies. Never invent a completed outbox action.

- [ ] **Step 3: Quarantine safely**

Quarantine invalid whole documents or only invalid tasks as applicable, retain stable diagnostics, import valid data in one SQLite transaction, and retain a timestamped source backup until a later successful launch. No static initializer may touch storage.

- [ ] **Step 4: Add real crash-cut tests**

Use the framework-dependent Trigger probe to terminate migration before commit and around backup promotion. Restart against the same unique temporary root and prove the authority is either the untouched legacy source/old valid database or the complete new database—never a partial mix.

- [ ] **Step 5: Review and checkpoint**

Run migration/crash tests repeatedly and commit `feat: migrate legacy trigger storage`.

---

### Task 6: Create fully asynchronous typed context acquisition

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Triggers/ITriggerContextProvider.cs`
- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerContextResult.cs`
- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerDataField.cs`
- Create: `ClashSharp/ClashSharp/Service/TriggerContextProviderAdapter.cs`
- Modify: `ClashSharp/ClashSharp/Service/TriggerEvaluationContextFactory.cs`
- Create: `ClashSharp/ClashSharp.Tests/Unit/Triggers/TriggerContextProviderTests.cs`

- [ ] **Step 1: Write RED context tests**

Prove disabled triggers short-circuit before provider invocation. For enabled work, cover controller timeout, malformed JSON, SQLite busy/error, IO failure, caller cancellation, partial field degradation, and a typed unsound-decision failure. Use completion barriers to prove methods remain asynchronous and the calling thread is not blocked.

- [ ] **Step 2: Implement platform-neutral result semantics**

Each field is available or unavailable with a stable reason. The provider may return a degraded context when affected conditions safely evaluate false; missing data required for a sound state transition returns a typed failure. Preserve caller cancellation.

- [ ] **Step 3: Replace cached/synchronous context reads**

Await controller/storage APIs and remove the static synchronous factory. Keep any unavoidable legacy access inside a host-registered compatibility adapter, not View/ViewModel.

- [ ] **Step 4: Review and checkpoint**

Run context/disabled-path tests, Release build, format, and commit `feat: acquire trigger context asynchronously`.

---

### Task 7: Add per-task serialization and atomic evaluation-to-outbox execution

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerExecutionGate.cs`
- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerEvaluator.cs`
- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerExecutionCoordinator.cs`
- Create: `ClashSharp/ClashSharp.Tests/Unit/Triggers/TriggerExecutionGateTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/TriggerEvaluationConcurrencyTests.cs`

- [ ] **Step 1: Write RED concurrency tests**

Interleave periodic and runtime events for the same task with deterministic barriers. Prove one execution, no lost re-arm, no duplicate outbox, no task-level concurrency, different tasks may progress independently, and definition revision conflicts trigger a safe reload rather than stale state overwrite.

- [ ] **Step 2: Implement evaluation orchestration**

Acquire a per-task gate, reload definition/state, obtain only the context fields required by enabled tasks, run the pure matcher, and atomically commit proposed state plus the complete action outbox before dispatch. Release repository leases before external actions when possible.

- [ ] **Step 3: Integrate mutation admission**

Trigger actions waiting to submit mutations use ordinary admission and remain durably pending if cancellation occurs before mutation-gate entry. Never reacquire the mutation gate from a participant that already owns it.

- [ ] **Step 4: Review and checkpoint**

Run concurrency tests ten times and commit `feat: serialize trigger evaluation`.

---

### Task 8: Execute and reconcile idempotent durable actions

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Triggers/ITriggerActionRuntime.cs`
- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerActionExecutor.cs`
- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerActionReconciler.cs`
- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerActionResult.cs`
- Create: `ClashSharp/ClashSharp/Service/TriggerActionRuntimeAdapter.cs`
- Create: `ClashSharp/ClashSharp.Tests/Unit/Triggers/TriggerActionExecutorTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/TriggerOutboxRecoveryTests.cs`

- [ ] **Step 1: Write RED action-state tests**

Cover every current action and every legal state transition. State-setting actions probe desired final state before retry; connection close is safe to repeat; notifications deduplicate by execution/action ID; a failed retry remains diagnosable; `Uncertain` blocks later actions; an unsupported non-idempotent action is rejected at definition validation.

- [ ] **Step 2: Implement ordered outbox processing**

Transition `Pending → Running` before dispatch and commit a verified terminal state afterward. On startup, reconcile `Pending/Running` against external final state before retry. Continue only after `Succeeded`; stop on `Failed`, `HandedOff`, or `Uncertain` according to the typed policy.

- [ ] **Step 3: Add crash-cut coverage**

Terminate before dispatch, after external effect, and before/after each action-state commit. Restart and prove no duplicate effective notification/state change and no silently lost later action.

- [ ] **Step 4: Review and checkpoint**

Run outbox recovery repeatedly and commit `feat: reconcile trigger action outbox`.

---

### Task 9: Implement epoch-safe `ExitApplication` handoff

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Triggers/ITriggerLifecycleHandoff.cs`
- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerLifecycleHandoffCoordinator.cs`
- Modify: `ClashSharp/ClashSharp.Application/Lifecycle/ApplicationLifetimeRequestChannel.cs`
- Modify: `ClashSharp/ClashSharp.Application/Hosting/ProcessLifetimeRunner.cs`
- Modify: `ClashSharp/ClashSharp/Service/ApplicationLifecycleService.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/TriggerExitHandoffTests.cs`

- [ ] **Step 1: Write RED handoff barriers**

Prove durable `HandedOff` insertion, publication keyed by execution/action/process epoch, release acknowledgement only after repository pin/execution gate/supervisor lease release, shutdown starting from the App-owned runner, `ShutdownAsync` unwinding before host stop/disposal, shutdown failure classification, idempotent duplicate publication, and prior-epoch recovery marking success without exiting the new process.

- [ ] **Step 2: Implement non-owned lifetime publication**

The trigger participant returns after handoff and never awaits host shutdown. The outer runner waits for explicit release acknowledgement, then invokes lifecycle shutdown and records completion/failure through a safe non-self-owned channel.

- [ ] **Step 3: Add recovery-only and crash boundaries**

Cover uncommitted/committed `RecoveryOnly`, handoff-before-release, release-before-shutdown-start, shutdown-start-before-disposal, and process termination after durable handoff. No case may create a competing journal or exit the next process epoch.

- [ ] **Step 4: Review and checkpoint**

Run lifecycle/trigger tests ten times and commit `feat: hand off trigger exit safely`.

---

### Task 10: Replace detached Trigger scheduling with one supervised runtime participant

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Triggers/TriggerScheduler.cs`
- Create: `ClashSharp/ClashSharp.Application/Triggers/ITriggerSchedulerClock.cs`
- Create: `ClashSharp/ClashSharp/Service/TriggerSchedulerAdapters.cs`
- Modify: `ClashSharp/ClashSharp/AppHost/Startup/TriggerSupervisorStartupStep.cs`
- Modify: `ClashSharp/ClashSharp/AppHost/Compatibility/LegacyRuntimeParticipants.cs`
- Modify: `ClashSharp/ClashSharp/AppHost/ClashSharpAppHostFactory.cs`
- Create: `ClashSharp/ClashSharp.Tests/Unit/Triggers/TriggerSchedulerTests.cs`

- [ ] **Step 1: Write RED scheduler/lifecycle tests**

Use a fake clock and channels, not delays. Prove disabled startup requests no context; enabled periodic ticks and runtime events are queued deterministically; task exceptions become health/diagnostics; quiesce rejects new events and awaits in-flight work; resume preserves prior running state; stop owns and awaits every task; no event is dropped during active evaluation.

- [ ] **Step 2: Implement one owned scheduler**

Use one bounded channel and owned task or the existing supervised primitive. Runtime event publication is synchronous enqueue only; periodic time is awaited; start/quiesce/resume/stop are idempotent. Remove all three TriggerService detached continuations and timer callbacks.

- [ ] **Step 3: Compose startup and recovery in order**

Initialize/migrate/reconcile the repository before scheduler start. Register the scheduler directly as `IRuntimeParticipant`; delete `LegacyTriggerRuntimeParticipant` when no longer needed. Startup failure is typed and visible.

- [ ] **Step 4: Review and checkpoint**

Run scheduler/lifecycle tests ten times, full Release build, and commit `feat: supervise trigger scheduling`.

---

### Task 11: Migrate Trigger CRUD and the editor without losing conditions

**Files:**

- Create: `ClashSharp/ClashSharp/ViewModel/TriggerEditorViewModel.cs`
- Create: `ClashSharp/ClashSharp/ViewModel/TriggerConditionEditorViewModel.cs`
- Create: `ClashSharp/ClashSharp/ViewModel/TriggerActionEditorViewModel.cs`
- Modify: `ClashSharp/ClashSharp/ViewModel/TriggersViewModel.cs`
- Modify: `ClashSharp/ClashSharp/View/Triggers.xaml`
- Modify: `ClashSharp/ClashSharp/View/Triggers.xaml.cs`
- Delete: `ClashSharp/ClashSharp/Model/TriggerTask.cs`
- Delete: `ClashSharp/ClashSharp/Service/TriggerTaskNormalizer.cs`
- Delete: `ClashSharp/ClashSharp/Service/TriggerEvaluationContextFactory.cs`
- Delete: `ClashSharp/ClashSharp/Service/TriggerService.cs`
- Modify: `ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj`
- Create: `ClashSharp/ClashSharp.Tests/Unit/ViewModel/TriggerEditorViewModelTests.cs`

- [ ] **Step 1: Write RED editor round-trip tests**

Open a task with at least three different conditions and multiple ordered actions, edit one field, save, reload, and prove every untouched condition/action remains byte-for-byte equivalent in domain meaning and order. Cover add/remove/reorder, validation, duplicate conditions, invalid time/threshold/scope, name uniqueness, busy/error state, and Exit-final enforcement.

- [ ] **Step 2: Implement the dedicated editor ViewModel**

Own draft collections, conversion, validation, and async save in the ViewModel. Code-behind may only translate control events/bind dialogs and must not construct domain definitions, select defaults, convert units, or truncate conditions.

- [ ] **Step 3: Remove the legacy monolith and source links**

Route CRUD through an injected async facade backed by `ITriggerRepository`. Remove Trigger production source links and the Trigger `UNIT_TESTS` fork. Keep temporary presentation lookup only under a host-registered compatibility factory and reduce the per-file `.Instance` baseline.

- [ ] **Step 4: Verify XAML and checkpoint**

Run editor/ViewModel tests, WinUI Debug/Release builds, format, diff check, structured review, and commit `refactor: move trigger editing into view models`.

---

### Task 12: Add architecture gates, evidence, and Phase 04 closure

**Files:**

- Create: `ClashSharp/ClashSharp.Tests/Architecture/TriggerArchitectureTests.cs`
- Modify: `ClashSharp/ClashSharp.Tests/Architecture/RepositoryTopologyTests.cs`
- Create: `docs/architecture/evidence/phase-04-trigger-persistence-and-execution.md`
- Modify: `docs/architecture/stabilization-ledger.md`
- Modify: `docs/superpowers/plans/2026-07-19-architecture-stabilization-roadmap.md`

- [ ] **Step 1: Add executable architecture gates**

Reject `Triggers.json` writes outside the migration reader, direct Trigger file I/O, timer-based/detached Trigger work, synchronous Trigger context/storage APIs, the removed monolith/source links/`UNIT_TESTS` branch, mutable trigger definitions in presentation, and new Trigger service locators. Verify DI registrations place repository, scheduler, executor, and lifecycle adapters in the composition root.

- [ ] **Step 2: Run clean final verification**

Run locked forced restore, format verification, Debug and Release solution builds, the complete Release suite, and the trigger/mutation/lifecycle/supervision filter ten consecutive times. Run all migration/outbox crash probes repeatedly and perform a zero-leaked-probe process-table check. Finish with `git diff --check`.

- [ ] **Step 3: Update the ledger precisely**

Close `P1-02`, `P1-03`, `P1-04`, and `P1-05` only when their required corruption/migration, scope/edge/outbox/handoff, multi-condition, and async degraded-context evidence is complete. Record checkpoint commits, reviewer method, dates, exact test counts/repetitions, crash cut points, and retained Phase 05/07 compatibility debt.

- [ ] **Step 4: Review and checkpoint**

Use `superpowers:requesting-code-review`, address Critical/Important findings with `superpowers:receiving-code-review`, rerun the complete matrix, mark Phase 04 complete in the roadmap, commit implementation plus evidence, and preserve the worktree for Phase 05.
