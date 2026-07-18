# Phase 03 Mutation, Network, and Lifetime Evidence

**Date:** 2026-07-19

**Branch:** `codex/architecture-stabilization-phase-01`

**Plan:** `docs/superpowers/plans/2026-07-19-mutation-network-and-lifetime.md`

## TDD evidence

The first `MutationAdmissionContractTests` run was performed after adding behavior tests but before adding any mutation production types. The Release test build failed with `CS0234` because `ClashSharp.ApplicationModel.Mutations` did not exist. This establishes the RED baseline against the referenced `ClashSharp.Application` assembly.

The first production implementation uses an explicit FIFO waiter queue, cancellation-safe waiter removal, an unforgeable `MutationContext` ownership token, logical-flow reentrancy detection, and an admission state machine for ordinary, destructive, recovery-only, and terminal shutdown leases. The focused suite passed 6 tests and then passed ten consecutive Release repetitions (60 executions total). The Application Release build completed with 0 warnings and 0 errors, and solution format verification reported 0 changed files.

The first `FileMutationJournalStoreTests` build then failed with `CS0234`/`CS0246` because neither the Infrastructure recovery namespace nor the production journal contracts existed. The implemented store persists a canonical JSON payload inside a versioned SHA-256 envelope, validates expected hashes and consecutive generations, flushes a same-directory temporary file before atomic promotion, rejects reparse/escaping/relative paths, restricts the Windows recovery-root ACL, and requires matching operation identity plus latest hash for deletion. Fault injection at all three exposed write cut points proves that the authoritative file is exactly the old valid generation before promotion or the new valid generation after promotion. The first path-policy expansion exposed a validation-order regression—constructor normalization hid relative input—and the new test failed until raw input was validated first. The focused journal suite now passes 10 tests.

The mutation/journal foundation checkpoint passed locked restore, solution format verification, a full Release solution build with 0 warnings and 0 errors, and the complete Release suite with 706 passed, 0 failed, and 0 skipped. The combined 16 mutation foundation tests also passed five consecutive repetitions.

The first `ApplicationMutationCoordinatorTests` build failed with `CS0246` for the missing coordinator, plan, participant, and recovery resolver types. The journal-driven engine now distinguishes pre-journal planning failure, pre-side-effect cancellation, bounded compensation after the first side effect, the durable commit marker, and forward-only committed recovery. Tests cover failed first recovery followed by a successful second same-process retry, latest-generation advancement, recovery-plan hash/compensation identity validation, an unexpected pre-existing journal, and terminal shutdown winning over recovery completion.

A structured concurrency review found that switching to `RecoveryOnly` after awaiting the fair gate left a handoff race: the next already-admitted FIFO waiter could enter before admission closed. The transition now occurs inside the gate callback; an explicit two-request barrier test proves the waiter is revoked before its validation or side effects. A second review finding moved verified result creation before journal deletion so a result failure cannot leave recovery-only admission without a journal. An ordinary/destructive interleaving test also proves admission closes, the ordinary operation compensates and drains, and destructive work begins only afterward.

The coordinator was split before checkpointing: `ApplicationMutationCoordinator` is 536 formatted lines, journal generation writing is owned by `MutationJournalWriter`, bounded calls by `MutationStepRunner`, and baseline/forward recovery by `MutationRecoveryExecutor`. The 28 focused mutation tests passed ten consecutive Release repetitions (280 executions). Solution format verification succeeded, the Release solution build completed with 0 warnings and 0 errors, and the complete Release suite passed 718 tests, 0 failed, and 0 skipped.

## Pending evidence

Network routing, runtime supervision, process execution, lifecycle handoff, final repeated concurrency verification, and ledger closure evidence remain pending as the phase proceeds.
