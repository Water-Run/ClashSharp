# Phase 03 Mutation, Network, and Lifetime Evidence

**Date:** 2026-07-19

**Branch:** `codex/architecture-stabilization-phase-01`

**Plan:** `docs/superpowers/plans/2026-07-19-mutation-network-and-lifetime.md`

## TDD evidence

The first `MutationAdmissionContractTests` run was performed after adding behavior tests but before adding any mutation production types. The Release test build failed with `CS0234` because `ClashSharp.ApplicationModel.Mutations` did not exist. This establishes the RED baseline against the referenced `ClashSharp.Application` assembly.

The first production implementation uses an explicit FIFO waiter queue, cancellation-safe waiter removal, an unforgeable `MutationContext` ownership token, logical-flow reentrancy detection, and an admission state machine for ordinary, destructive, recovery-only, and terminal shutdown leases. The focused suite passed 6 tests and then passed ten consecutive Release repetitions (60 executions total). The Application Release build completed with 0 warnings and 0 errors, and solution format verification reported 0 changed files.

The first `FileMutationJournalStoreTests` build then failed with `CS0234`/`CS0246` because neither the Infrastructure recovery namespace nor the production journal contracts existed. The implemented store persists a canonical JSON payload inside a versioned SHA-256 envelope, validates expected hashes and consecutive generations, flushes a same-directory temporary file before atomic promotion, rejects reparse/escaping/relative paths, restricts the Windows recovery-root ACL, and requires matching operation identity plus latest hash for deletion. Fault injection at all three exposed write cut points proves that the authoritative file is exactly the old valid generation before promotion or the new valid generation after promotion. The first path-policy expansion exposed a validation-order regression—constructor normalization hid relative input—and the new test failed until raw input was validated first. The focused journal suite now passes 10 tests.

The mutation/journal foundation checkpoint passed locked restore, solution format verification, a full Release solution build with 0 warnings and 0 errors, and the complete Release suite with 706 passed, 0 failed, and 0 skipped. The combined 16 mutation foundation tests also passed five consecutive repetitions.

Foundation checkpoint: `428576d495a4505d6ca4fce2c06ff46f2e16d45d` (`feat: establish durable mutation admission`).

The first `ApplicationMutationCoordinatorTests` build failed with `CS0246` for the missing coordinator, plan, participant, and recovery resolver types. The journal-driven engine now distinguishes pre-journal planning failure, pre-side-effect cancellation, bounded compensation after the first side effect, the durable commit marker, and forward-only committed recovery. Tests cover failed first recovery followed by a successful second same-process retry, latest-generation advancement, recovery-plan hash/compensation identity validation, an unexpected pre-existing journal, and terminal shutdown winning over recovery completion.

A structured concurrency review found that switching to `RecoveryOnly` after awaiting the fair gate left a handoff race: the next already-admitted FIFO waiter could enter before admission closed. The transition now occurs inside the gate callback; an explicit two-request barrier test proves the waiter is revoked before its validation or side effects. A second review finding moved verified result creation before journal deletion so a result failure cannot leave recovery-only admission without a journal. An ordinary/destructive interleaving test also proves admission closes, the ordinary operation compensates and drains, and destructive work begins only afterward.

The coordinator was split before checkpointing: `ApplicationMutationCoordinator` is 536 formatted lines, journal generation writing is owned by `MutationJournalWriter`, bounded calls by `MutationStepRunner`, and baseline/forward recovery by `MutationRecoveryExecutor`. The 28 focused mutation tests passed ten consecutive Release repetitions (280 executions). Solution format verification succeeded, the Release solution build completed with 0 warnings and 0 errors, and the complete Release suite passed 718 tests, 0 failed, and 0 skipped.

Mutation engine checkpoint: `0ff078b10254a97c3e8e94116c65c503e5901816` (`feat: add journal-driven mutation engine`).

The first `NetworkMutationConcurrencyTests` build failed with `CS0234`/`CS0246` because the application network namespace and coordinator contracts did not exist. The network vertical slice now plans only while holding an active mutation context, captures immutable mode/TUN/port intent and a classified baseline, serializes concurrent requests through the fair gate before planning, verifies observed external state, and restores both external and durable baseline state after apply failure. Foreign and expired contexts are rejected before adapter access.

The WinUI compatibility boundary is the only remaining caller of the legacy takeover mutator. Startup journal recovery runs first, stale-proxy recovery is a top-level mutation, and configured startup behavior is applied through the same coordinator. `ProxyRecoveryService` is now a read-only endpoint probe; its direct mutation API and obsolete result model were removed. A pre-network startup conflict snapshot prevents an occupied port or external mihomo process from being ignored and prevents the later window dialog from misclassifying the newly started owned core. Explicit proxy-conflict repair also enters the durable coordinator.

Shared trigger, tray, master-control, and startup mode actions await the coordinator and publish only its verified result. The presentation layer no longer writes `CurrentMode`; the durable committer owns that promotion. A failed mode request restores the previously displayed state instead of showing an optimistic `Faulted` mode. TUN preference actions intentionally persist preference only and return no runtime-applied claim until the Phase 05 settings transaction is available.

Structured review found and fixed three important races or policy errors: the legacy executor reread live TUN/port settings after journaling, recovery-required startup behavior was initially downgraded to a warning, and the startup conflict dialog could inspect the process after startup had created its own core. Execution now receives frozen plan values, recovery obligations are fatal startup outcomes, and conflicts are probed once before startup mode application. Aggregate validation also rejects a settings or external-state baseline change before journal creation.

The strengthened real two-process probe records fake `RuleTakeover`, core-start, and system-proxy mutations with process identity. The test proves all three belong to the primary PID and the redirected secondary performs none. The network/engine/admission focused suite passed 22 tests ten consecutive times (220 executions). The complete Release suite passed 724 tests, 0 failed, and 0 skipped; the Release solution build completed with 0 warnings and 0 errors; format verification and `git diff --check` succeeded.

## Pending evidence

Runtime supervision, process execution, lifecycle handoff, final repeated concurrency verification, and ledger closure evidence remain pending as the phase proceeds.
