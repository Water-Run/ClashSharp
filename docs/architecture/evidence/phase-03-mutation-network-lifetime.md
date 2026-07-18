# Phase 03 Mutation, Network, and Lifetime Evidence

**Date:** 2026-07-19

**Branch:** `codex/architecture-stabilization-phase-01`

**Plan:** `docs/superpowers/plans/2026-07-19-mutation-network-and-lifetime.md`

## TDD evidence

The first `MutationAdmissionContractTests` run was performed after adding behavior tests but before adding any mutation production types. The Release test build failed with `CS0234` because `ClashSharp.ApplicationModel.Mutations` did not exist. This establishes the RED baseline against the referenced `ClashSharp.Application` assembly.

The first production implementation uses an explicit FIFO waiter queue, cancellation-safe waiter removal, an unforgeable `MutationContext` ownership token, logical-flow reentrancy detection, and an admission state machine for ordinary, destructive, recovery-only, and terminal shutdown leases. The focused suite passed 6 tests and then passed ten consecutive Release repetitions (60 executions total). The Application Release build completed with 0 warnings and 0 errors, and solution format verification reported 0 changed files.

The first `FileMutationJournalStoreTests` build then failed with `CS0234`/`CS0246` because neither the Infrastructure recovery namespace nor the production journal contracts existed. The implemented store persists a canonical JSON payload inside a versioned SHA-256 envelope, validates expected hashes and consecutive generations, flushes a same-directory temporary file before atomic promotion, rejects reparse/escaping/relative paths, restricts the Windows recovery-root ACL, and requires matching operation identity plus latest hash for deletion. Fault injection at all three exposed write cut points proves that the authoritative file is exactly the old valid generation before promotion or the new valid generation after promotion. The first path-policy expansion exposed a validation-order regression—constructor normalization hid relative input—and the new test failed until raw input was validated first. The focused journal suite now passes 10 tests.

The mutation/journal foundation checkpoint passed locked restore, solution format verification, a full Release solution build with 0 warnings and 0 errors, and the complete Release suite with 706 passed, 0 failed, and 0 skipped. The combined 16 mutation foundation tests also passed five consecutive repetitions.

## Pending evidence

Mutation execution/recovery, network routing, runtime supervision, process execution, lifecycle handoff, full repeated concurrency verification, and ledger closure evidence remain pending as the phase proceeds.
