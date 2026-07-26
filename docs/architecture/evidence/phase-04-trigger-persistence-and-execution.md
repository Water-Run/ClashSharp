# Phase 04 Trigger Persistence and Execution Evidence

**Date:** 2026-07-19 through 2026-07-26

**Branch:** `codex/architecture-stabilization-phase-01`

**Plan:** `docs/superpowers/plans/2026-07-19-trigger-persistence-and-execution.md`

## Delivered architecture

Trigger definitions are now immutable production-domain values in `ClashSharp.Core`. Matching is a pure, deterministic decision over typed condition parameters and persisted state. Invalid enum values, parameters, scopes, and action ordering are rejected instead of being converted to defaults.

`ITriggerRepository`, context, evaluation, execution, outbox, scheduler, and lifecycle-handoff contracts live in `ClashSharp.Application`. Every storage and context operation is asynchronous, accepts a final `CancellationToken`, and returns typed persistence or degraded-context outcomes. Trigger evaluation is serialized per task, runtime events enter through a bounded channel, periodic work is awaited, and the scheduler is an owned `IRuntimeParticipant` with supervised health.

`ClashSharp.Infrastructure` owns `Triggers.db`, WAL transactions, integrity checks, backup validation and promotion, and read-only legacy migration. The legacy `Triggers.json` file is no longer a runtime authority. Migration is restartable and quarantines malformed documents or tasks with stable diagnostics. Action side effects use a durable outbox and probe-before-retry reconciliation. Exit is transferred through a durable, epoch-scoped lifecycle handoff rather than stopping or disposing the host from trigger work.

Trigger CRUD now uses an async definition store. `TriggerEditorViewModel` and its condition/action child view models own draft state, validation, conversion, ordering, and save coordination. The WinUI code-behind only binds controls and dialogs; the deleted `TriggerService`, mutable presentation `TriggerTask`, normalizer, and synchronous context factory cannot return without failing an architecture test.

## Checkpoints

- `cd73da27cfba31c38e2fc0e10127b299c8f96a09` — typed trigger domain
- `7c6a1870ef983011414d7198948971f27126b4a2` — deterministic matching
- `957dbbcb7815e9f7f0b9c2807cbeba4c7cba377e` — durable application contracts
- `ec1d65ea05c39e6c9b6b8b3b553e5545359805c8` — transactional SQLite persistence
- `372e6e43a0b81529a5291d1488d491b22eb1d6a6` — restartable legacy migration
- `e4b4fb8f8156f2c1e8e4b424f94fb7924f0b4428` — asynchronous degraded context
- `5ee07e2e55a9f38c51d88477749347120c6edba3` — serialized evaluation
- `7266195c1b6e157a5b0ebe3f0df1832ba1074963` — durable outbox reconciliation
- `72251fc433ff400eaa86a7a63f0aaf954beb6ca2` — lifecycle-safe exit handoff
- `1b1e01b35e72b0ab20bf5912b1b0e166c9f6e039` — supervised scheduler
- `7d3779b6f0d93d1857684b42b087f28d56e46ced` — ViewModel-owned trigger editing

The last checkpoint contains the complete production state used to close the four Phase 04 audit rows.

## Audit-row evidence

| Row | Automated evidence | Closure result |
|---|---|---|
| `P1-02` corrupt/non-atomic storage | `SqliteTriggerRepositoryTests`, `TriggerBackupRecoveryTests`, `LegacyTriggerMigrationTests`, `TriggerPersistenceCrashTests`, and `TriggerArchitectureTests` cover corrupt JSON/database input, denied and busy I/O, validated backups, atomic generations, quarantine, restartable intent, migration commit cuts, and backup-promotion cuts. | Closed |
| `P1-03` ignored scope/repeated execution | `TriggerMatcherTests`, `TriggerEvaluationConcurrencyTests`, `TriggerActionExecutorTests`, `TriggerOutboxRecoveryTests`, `TriggerExitHandoffTests`, and `TriggerSchedulerTests` cover every traffic scope, daily reset, edge re-arm, event consumption, periodic/runtime concurrency, durable retry, and participant-originated exit. | Closed |
| `P1-04` multi-condition data loss | `TriggerEditorViewModelTests` opens, edits, saves, and reloads heterogeneous condition lists and ordered action lists; add/remove/reorder, duplicate IDs, invalid time/threshold/scope, unique names, conflict refresh, busy/error state, and exit-final ordering are covered. | Closed |
| `P1-05` synchronous context/UI freeze | `TriggerContextProviderTests` covers disabled short-circuit, asynchronous responsiveness, cancellation, timeout, malformed JSON, controller/HTTP failure, SQLite busy/storage failure, and I/O failure as typed unavailable fields. `TriggerArchitectureTests` rejects synchronous context and storage contracts. | Closed |

## Crash and concurrency cuts

The real `ClashSharp.TriggerProbe` process exits with code 86 at four persistence cut points:

- `BeforeMigrationCommit`
- `AfterMigrationCommit`
- `BeforeBackupPromotion`
- `AfterBackupPromotion`

Restart against the same directory proves exactly one complete imported generation, a complete old/new backup authority, cleanup of temporary candidates, and no copied backup WAL/SHM files.

Outbox reconciliation additionally injects failures before and after pending-to-running, running-to-terminal, and running-to-pending retry commits. A simulated crash after the desired effect but before durable success proves probe-before-retry prevents duplicate effect application. Lifecycle tests inject failures before and after handoff publication, release acknowledgement, shutdown-start, and terminal commits for both current and prior process epochs.

The first ten-run combined pressure attempt exposed a Windows SQLite test-cleanup race on iteration 8: the temporary `Triggers.db` could remain briefly unavailable immediately after the final awaited operation. Production repository connections already use `Pooling=false` and deterministic disposal. Test cleanup now retries only `IOException`/`UnauthorizedAccessException` for five bounded attempts and still surfaces a persistent leak. The exact failing handoff cut then passed 10 consecutive focused runs, followed by 10 consecutive clean combined runs.

## Executable coding and architecture policy

`TriggerArchitectureTests` adds nine gates that:

- keep `Triggers.json` migration-only and the migration reader read-only;
- reject direct file mutation from trigger application or presentation code;
- reject timer callbacks, detached async work, and `async void` in trigger runtime layers;
- require asynchronous, cancellation-aware context and storage contracts;
- reject the removed monolith, trigger source links, trigger `UNIT_TESTS` forks, and duplicate mutable presentation models;
- freeze remaining `.Instance` debt to the two named compatibility adapters;
- verify composition-root registration of the repository, definition store, context, executor, notification sink, lifecycle handoff, scheduler, and startup initializer.

The detectors strip comments and literals before evaluating executable source and include known-bad mutation samples for blocking waits, member-call discards, `Task.Factory.StartNew`, continuations, writable legacy streams, and registration text hidden in comments or strings.

`RepositoryTopologyTests` lowers the presentation service-locator baseline from 208 to 167 occurrences and removes `Triggers.xaml.cs` from that debt inventory. The test project references the real WinUI assembly for editor tests; it no longer compiles trigger production source links or a trigger-specific conditional fork.

## Review findings

Structured review of the final production checkpoint found that a failed notification callback could incorrectly block durable business-action progress, that context-specific definition-load diagnostics were being flattened, and that the production executor allowed an omitted notification sink. The executor now contains best-effort notification failure (including reporter failure), preserves typed storage error codes with a load-specific message, and requires an explicit sink; tests use the null object deliberately.

The review also strengthened notification tests from count-only assertions to exact platform arguments, receipt idempotency keys, log fault injection, and disabled-notification short-circuit behavior. A second independent pass reported no remaining Critical, Important, or Minor finding in the production checkpoint.

The Task 12 closure review then found three Important evidence/gate defects: implementation-level sync-over-async and several detached/write forms could escape the source rules; the editor test inspected the written value without creating a new reader; and the ledger/roadmap were marked complete before the required post-review matrix. The gates now scan executable implementation text and carry mutation-style detector tests, the editor creates a fresh list/ViewModel and reloads/rebuilds the saved graph, and closure remained provisional until verification finished. Independent re-review closed all three Important findings. Its only Minor observation—comments or strings could cause conservative detector false positives—was also fixed before the final matrix. No known Critical, Important, or Minor review finding remains.

## Verification

With `CI=true` and `Platform=x64` on 2026-07-26, after the closure review and all review fixes:

- forced locked restore succeeded after intentionally updating the test lock file for its new WinUI project reference;
- solution format verification changed no files;
- Debug and Release solution builds each completed with 0 warnings and 0 errors;
- the complete Release suite passed 1,048 tests with 0 failed and 0 skipped;
- the trigger/mutation/lifecycle/supervision filter passed 335 tests in each of 10 consecutive runs, for 3,350 executions;
- the 12 migration/outbox crash-probe cases passed in each of 3 consecutive runs, for 36 executions;
- the cleanup-race regression passed 2 theory cases in each of 10 consecutive focused runs, for 20 executions;
- the Windows process table contained no `ClashSharp.TriggerProbe`, `ClashSharp.ProcessProbe`, or `ClashSharp.StartupProbe` residue;
- `git diff --check` succeeded.

## Retained debt

- Phase 05 still owns settings/data generation transactions. Trigger actions use the Phase 03 mutation boundary, but this phase does not claim verified multi-setting import/reset/clear generations.
- Phase 07 still owns complete constructor injection and code-behind removal. `TriggerPresentationCompatibilityFactory` and `TriggerSchedulerAdapters` contain the three frozen trigger `.Instance` occurrences; no other trigger service locator is permitted.
- The test project now references the WinUI project so it can verify real internal presentation types. Removing unrelated production source links and global `UNIT_TESTS` compatibility remains Phase 12 work.
- The fired-notification branch is intentionally best effort and must never block the business-action outbox. A separately durable notification retry lane, if required later, must be modeled as its own outbox rather than sharing business-action state.
