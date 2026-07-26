# Settings and Data Generations Implementation Plan

**Goal:** Replace the mutable `ApplicationDataContainer` settings singleton and direct import/reset/delete paths with one versioned settings registry, immutable desired/applied/pending envelope, atomic generation-scoped persistence, and a single journaled `SettingsCoordinator` for apply, import, reset, and clear-data.

**Architecture:** `ClashSharp.Core` owns stable setting keys, moved setting enums, canonical values, the metadata registry, immutable envelope records, validation, and pure batch-edit/revert rules. `ClashSharp.Application` owns asynchronous repository/facade contracts, generation leases, settings planning, mutation/quiescence orchestration, pending-batch reconciliation, and typed results. `ClashSharp.Infrastructure` owns atomic JSON/envelope persistence, generation manifests, same-volume staging/promotion, versioned legacy/package readers, hashes, and crash fault injection. WinUI supplies Windows/mihomo/hosted-service adapters and temporarily routes the existing Settings page through a host-composed compatibility factory; it does not remain a persistence or mutation owner.

**Tech stack:** .NET 10.0.201, C# 14, WinUI 3 / Windows App SDK, `System.Text.Json`, xUnit, the Phase 03 mutation journal/admission pipeline, and framework-dependent settings crash probes.

**Normative constraints:** `docs/superpowers/specs/2026-07-18-architecture-stabilization-design.md` sections 2.1(6, 21, 22), 4.3–4.6, 5.1–5.4, 7.1–7.3, 8.1, 10, 12, 14, 15, and 17. This phase must close `P1-07`, `P2-SET-01`, `P2-SET-02`, and `P2-SET-03`, and may close the remaining Phase 05 portions of `P1-06` and `P1-08` only with their complete matrices. It must not claim the Phase 07 full Settings-page decomposition or the Phase 08 one-cycle localization refresh.

## Global constraints

- `SettingsRegistry` is the only current key/default/parser/validator/package/reset/application metadata authority. No parallel `KnownKeys`, package descriptor, reset list, or enum-option list may be introduced.
- Undefined numeric enum values, noncanonical booleans/numbers, invalid ranges, unsafe paths, duplicate package entries, and canonical/alias conflicts fail before mutation.
- Runtime consumers read verified `AppliedState`, never optimistic `Desired`. Presentation can show `Desired` only together with applied/pending/failed/unknown state.
- Every envelope transaction preserves the coverage invariant: each applicable desired/applied mismatch or unknown applied value appears in exactly one pending batch entry with the current key revision and value hash.
- Editing/reverting/importing one key may not mutate untouched siblings, batch identity, creation sequence, attempt ID/state/error, or key revision. A running batch is immutable until its attempt reaches a recoverable terminal state.
- Apply/import/reset/clear-data submit exactly one top-level mutation. A participant may plan or execute under the supplied `MutationContext`; it may not call a public coordinator, acquire the mutation gate, commit independently, or publish early.
- Destructive admission/drain precedes participant quiescence, which precedes the fair mutation gate. Quiescence never waits while holding the mutation gate.
- Caller cancellation before the first side effect cancels cleanly. From the first side effect to the commit marker, bounded independent compensation runs. After the marker, only bounded forward activation/cleanup is legal.
- The recovery root is outside replaceable generations and is never enumerated by clear-data. Manifest and target promotion must be same-volume, flushed, atomic, hash-verified, and reparse-safe.
- Old settings/data packages remain readable through explicit versioned compatibility readers. Aliases are read-only and never emitted by current saves or exports.
- Existing WinUI code may temporarily consume a host-composed settings presentation facade, but no new `.Instance`, source link, `UNIT_TESTS` fork, blocking wait, or fire-and-forget work is permitted.
- Every task starts RED, reaches a focused GREEN checkpoint, runs format/build/diff verification, receives structured review when it changes a durable/public/concurrent boundary, and commits before the next task.

---

### Task 1: Establish the single typed settings registry

**Files:**

- Create: `ClashSharp/ClashSharp.Core/Settings/SettingKey.cs`
- Create: `ClashSharp/ClashSharp.Core/Settings/SettingValue.cs`
- Create: `ClashSharp/ClashSharp.Core/Settings/SettingDefinition.cs`
- Create: `ClashSharp/ClashSharp.Core/Settings/SettingsRegistry.cs`
- Create: `ClashSharp/ClashSharp.Core/Settings/SettingsResetScope.cs`
- Move into Core while preserving namespace/value identity:
  - `ClashSharp/ClashSharp/Model/AppLanguage.cs`
  - `ClashSharp/ClashSharp/Model/AppThemeMode.cs`
  - `ClashSharp/ClashSharp/Model/AppAccentColorMode.cs`
  - `ClashSharp/ClashSharp/Model/StartupBehaviorMode.cs`
  - `ClashSharp/ClashSharp/Model/CloseBehaviorMode.cs`
  - `ClashSharp/ClashSharp/Model/MainlandChinaFeatureMode.cs`
  - `ClashSharp/ClashSharp/Model/NotificationLevel.cs`
  - `ClashSharp/ClashSharp/Model/ClashDataPackageScope.cs`
- Modify: `ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj`
- Create: `ClashSharp/ClashSharp.Tests/Unit/Settings/SettingsRegistryTests.cs`

- [ ] **Step 1: Write RED registry completeness and parsing tests**

Reference the production Core assembly. Require one unique definition for every current `AppSettingsService` property/key, explicit enum values, canonical defaults, stable categories, reset scopes, package inclusion, sensitive flag, authority classification (`Internal`, `ExternallyObserved`, `RestartBound`), and application kind. Test every enum with its valid names and reject `-2`, `999`, numeric enum text, undefined boxed values, malformed booleans, invalid ports/intervals/colors/URLs, whitespace IDs, and duplicate keys.

- [ ] **Step 2: Implement canonical values and definitions**

Use invariant canonical text as the durable representation. `SettingDefinition.Normalize(string)` must return a typed success/failure without throwing for user input. Store a default canonical value, schema version, aliases, parser/validator, import/export flag, reset scopes, authority/application kind, safe fallback, localization category, and sensitive flag. Collections are defensively copied and read-only.

- [ ] **Step 3: Make the registry the only metadata source**

Register all current settings, including three distinct connection-test URLs and `MasterHeroStatusLayout`. Do not add a production restart-bound key merely to exercise the engine; use a synthetic definition in later domain tests when no current setting truly requires restart. Delete moved WinUI model files and their test source links.

- [ ] **Step 4: Verify and checkpoint**

Run registry tests, Core tests, Debug/Release builds, format, and diff check. Review default/enum compatibility and commit `feat: define canonical settings registry`.

---

### Task 2: Model immutable desired, applied, and pending state

**Files:**

- Create: `ClashSharp/ClashSharp.Core/Settings/SettingDesiredEntry.cs`
- Create: `ClashSharp/ClashSharp.Core/Settings/SettingAppliedState.cs`
- Create: `ClashSharp/ClashSharp.Core/Settings/SettingsApplicationBatch.cs`
- Create: `ClashSharp/ClashSharp.Core/Settings/SettingsMigrationRecord.cs`
- Create: `ClashSharp/ClashSharp.Core/Settings/SettingsEnvelope.cs`
- Create: `ClashSharp/ClashSharp.Core/Settings/SettingsEnvelopeValidator.cs`
- Create: `ClashSharp/ClashSharp.Core/Settings/SettingsEnvelopeEditor.cs`
- Create: `ClashSharp/ClashSharp.Tests/Unit/Settings/SettingsEnvelopeTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Unit/Settings/SettingsEnvelopeEditorTests.cs`

- [ ] **Step 1: Write RED envelope invariant tests**

Require positive schema/envelope/key revisions, complete desired/applied maps, unique ordered batches, disjoint entries, current key revision/value hash, total `LiveReconcile` then `Restart` ordering, stable attempt identity independent of `EnvelopeRevision`, and explicit `Unknown` reason/safe behavior. Reject missing, duplicate, overlapping, stale-hash, stale-revision, undefined enum, and unregistered-key states.

- [ ] **Step 2: Implement immutable state and validation**

Use sealed records/classes with defensive copies. Batch identity consists of batch ID, kind, creation sequence, attempt ID, state, last error, and sorted immutable `(key, keyDesiredRevision, valueHash)` entries. Compute hashes from registry-normalized canonical text using SHA-256.

- [ ] **Step 3: Implement atomic pure edit/revert/import partitioning**

`SettingsEnvelopeEditor.ApplyChanges` performs one pure rewrite. A no-op returns the original instance. Changed keys alone receive new desired revisions and leave old batches; untouched siblings remain byte-for-byte equivalent. Reject edits to `Running` batches. Revert uses verified applied state or the registry safe fallback and preserves unrelated work. Group new work by application kind and transaction without merging existing batches.

- [ ] **Step 4: Stress and checkpoint**

Generate randomized valid envelopes and thousands of edit/revert/import sequences; validate after every transition and prove unrelated envelope changes do not alter attempt identities. Commit `feat: model settings desired and applied state`.

---

### Task 3: Introduce data-generation identities, leases, and atomic manifests

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Data/IDataGenerationStore.cs`
- Create: `ClashSharp/ClashSharp.Application/Data/DataGenerationDescriptor.cs`
- Create: `ClashSharp/ClashSharp.Application/Data/DataGenerationLease.cs`
- Create: `ClashSharp/ClashSharp.Application/Data/DataGenerationScope.cs`
- Create: `ClashSharp/ClashSharp.Application/Data/DataGenerationManager.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Data/FileDataGenerationStore.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Data/DataGenerationPathPolicy.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Data/IDataGenerationFaultInjector.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/DataGenerationManagerTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/FileDataGenerationStoreTests.cs`

- [ ] **Step 1: Write RED lease/swap/path tests**

Require a nonempty stable generation ID, monotonically increasing generation number, canonical root under `Data/v1/generations/<id>`, and a current-manifest outside all generation directories. Reject relative, escaping, reparse, cross-volume staging, duplicate, stale-generation, and invalid manifest/hash inputs.

- [ ] **Step 2: Implement pin/drain/stage semantics**

`AcquireAsync` pins the current immutable scope for one operation. `BeginDrainAsync` rejects later leases and awaits existing leases without a mutation-gate dependency. Staging constructs a paused scope that is invisible to ordinary consumers. Promotion and in-memory swap are separate explicit steps; rollback can restore the old descriptor before the commit marker.

- [ ] **Step 3: Implement flushed atomic manifest promotion**

Write a versioned SHA-256 envelope to a same-directory temporary file, flush to disk, inject before/after promotion faults, atomically replace, re-read, and verify. Never delete the prior generation from this store; cleanup is a post-commit manager operation.

- [ ] **Step 4: Run concurrency/fault matrix and checkpoint**

Cover in-flight lease drain, cancellation, stale facade attempts, pre/post-promotion faults, rollback, and old-scope disposal only after explicit commit. Commit `feat: establish data generation ownership`.

---

### Task 4: Persist settings envelopes atomically inside a generation

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Settings/ISettingsRepository.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/SettingsPersistenceResult.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Settings/JsonSettingsRepository.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Settings/SettingsEnvelopeCodec.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Settings/ISettingsPersistenceFaultInjector.cs`
- Create: `ClashSharp/ClashSharp.SettingsProbe/ClashSharp.SettingsProbe.csproj`
- Create: `ClashSharp/ClashSharp.SettingsProbe/Program.cs`
- Modify: `ClashSharp/ClashSharp.slnx`
- Modify: project lock files
- Create: `ClashSharp/ClashSharp.Tests/Integration/JsonSettingsRepositoryTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/SettingsPersistenceCrashTests.cs`

- [ ] **Step 1: Write RED repository/codec tests**

Require async cancellation-aware open/read/replace, optimistic expected revision, canonical deterministic JSON, schema/hash verification, exact enum and numeric rejection, denied/busy I/O typing, corrupt-primary quarantine, valid backup recovery, and no exception escaping static construction.

- [ ] **Step 2: Implement one-generation repository**

The repository is constructed from a pinned generation descriptor and cannot resolve global paths. Writes use a flushed same-directory candidate and atomic replace; backups are validated before promotion. Return typed `Succeeded`, `Conflict`, `Invalid`, `Unavailable`, or `Corrupt` outcomes with stable diagnostics.

- [ ] **Step 3: Add real termination cuts**

The probe terminates before and after envelope promotion and backup promotion. Restart against the same generation proves exactly the complete old or complete new envelope, never a partial document, and cleans orphan candidates.

- [ ] **Step 4: Repeat crash probes and checkpoint**

Run every cut repeatedly, verify zero leaked probe process, locked restore, builds, format, and commit `feat: persist versioned settings envelopes`.

---

### Task 5: Migrate legacy settings deterministically

**Files:**

- Create: `ClashSharp/ClashSharp.Infrastructure/Settings/settings-schema.json`
- Modify: `ClashSharp/ClashSharp.Infrastructure/ClashSharp.Infrastructure.csproj`
- Create: `ClashSharp/ClashSharp.Application/Settings/ILegacySettingsSource.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Settings/SettingsSchemaManifest.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Settings/SettingsMigrationCoordinator.cs`
- Create: `ClashSharp/ClashSharp/Service/WindowsApplicationDataSettingsSource.cs`
- Modify: `ClashSharp/ClashSharp.SettingsProbe/Program.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/LegacySettingsMigrationTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/SettingsMigrationCrashTests.cs`

- [ ] **Step 1: Write RED canonical/alias/probe tests**

Cover valid canonical precedence, invalid canonical failure, equivalent aliases, conflicting aliases, invalid aliases, manifest-selected equivalent source, unknown keys, every undefined enum number, and all current defaults. Preserve the source snapshot/hash and stable conflict diagnostics.

- [ ] **Step 2: Implement classification-aware migration**

Legacy values always become `Desired`. Internal values become verified only after canonical round-trip and consumer validation. External values use injected real probes; mismatch creates `LiveReconcile` against the observed baseline. Probe failure creates `Unknown/BlockedProbe` without automatic mutation. Restart-bound values use effective-process/last-known-good observation or an explicit `Restart/InitialMigration` batch.

- [ ] **Step 3: Make migration idempotent and atomic**

Snapshot the legacy source in the protected recovery root, include `(migrationId, fromSchema, toSchema, sourceHash)` in the envelope, and promote once. Any parse, probe-contract, validation, write, flush, or promotion failure leaves the legacy source authoritative and exposes a repair/export diagnostic.

- [ ] **Step 4: Kill/restart and checkpoint**

Terminate before/after migration promotion and prove one complete migration record, source preservation until finalization, stable backup, and safe retry. Commit `feat: migrate legacy settings safely`.

---

### Task 6: Expose a host-owned asynchronous settings facade and startup boundary

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Settings/ISettingsStore.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/SettingsStore.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/SettingsSnapshot.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/ISettingsStartupInitializer.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/SettingsStartupInitializer.cs`
- Create: `ClashSharp/ClashSharp/AppHost/Startup/SettingsInitializationStartupStep.cs`
- Create: `ClashSharp/ClashSharp/AppHost/Compatibility/SettingsReadCompatibilityFacade.cs`
- Modify: `ClashSharp/ClashSharp/AppHost/ClashSharpAppHostFactory.cs`
- Modify: startup order tests
- Create: `ClashSharp/ClashSharp.Tests/Unit/Settings/SettingsStoreTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/SettingsStartupTests.cs`

- [ ] **Step 1: Write RED cache/startup tests**

Require initialization after journal recovery and generation selection but before localization/network/trigger/sampling consumers. Reads before successful initialization return typed unavailable state or explicit registry fallback, never legacy optimistic values. Concurrent reads observe one immutable snapshot; failed refresh retains the last verified snapshot.

- [ ] **Step 2: Implement repository-pinning facade**

Every read/write operation acquires a generation lease, resolves that generation's repository, and releases the lease after I/O. `Current` is a host-owned immutable cache of desired/applied/pending state. No consumer receives a concrete generation repository.

- [ ] **Step 3: Compose migration and compatibility reads**

Register generation manager, settings repository factory, migration coordinator, store, and startup step in `AppHost`. The compatibility facade supplies synchronous read-only snapshots only where Phase 07 constructor migration is not yet in scope; it exposes no setter and performs no I/O.

- [ ] **Step 4: Verify startup isolation and checkpoint**

Prove a secondary process never initializes/migrates settings and a failed primary initialization stops later consumers with a typed localized startup result. Commit `feat: initialize settings through the app host`.

---

### Task 7: Add participant quiescence to destructive mutation execution

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Mutations/IMutationQuiescenceCoordinator.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/MutationQuiescencePolicy.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/MutationQuiescenceSession.cs`
- Modify: `ClashSharp/ClashSharp.Application/Mutations/ApplicationMutationCoordinator.cs`
- Modify: `ClashSharp/ClashSharp.Application/Mutations/MutationRequest.cs`
- Modify: `ClashSharp/ClashSharp.Application/Lifecycle/QuiescenceSession.cs`
- Modify: `ClashSharp/ClashSharp/AppHost/ClashSharpAppHostFactory.cs`
- Modify: `ClashSharp/ClashSharp.Tests/Integration/ApplicationMutationCoordinatorTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/DestructiveMutationQuiescenceTests.cs`

- [ ] **Step 1: Write RED lock-order and restoration tests**

Instrument admission, in-flight ordinary leases, participant quiescence, fair-gate ownership, plan validation, side effects, and resume. Require `close/drain → quiesce → gate → journal`; no participant may acquire the gate while quiescing. Cover timeout/cancellation/throw after partial quiescence and reverse resume before admission reopens.

- [ ] **Step 2: Extend the generic mutation owner**

A destructive request supplies a quiescence policy resolved by the coordinator. The coordinator owns session lifetime and calls it only after the destructive lease drains and before gate acquisition. Ordinary mutations retain existing behavior. Do not let Settings pre-quiesce outside the mutation owner.

- [ ] **Step 3: Define pre/post-commit completion**

Pre-commit failure resumes prior participants in reverse order with the independent recovery token. Committed success activates the prepared target then resumes. Post-marker activation failure retains recovery, keeps admission `RecoveryOnly`, and never restores the old target.

- [ ] **Step 4: Interleave and checkpoint**

Run destructive/ordinary/shutdown/trigger/sampling interleavings ten times. Review for gate inversion, cancellation misuse, and duplicate resume, then commit `feat: quiesce destructive mutations safely`.

---

### Task 8: Build the single SettingsCoordinator and composite recovery resolver

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Settings/ISettingsCoordinator.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/SettingsChangeSet.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/SettingsApplyResult.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/SettingsMutationPlan.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/SettingsMutationPlanBuilder.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/SettingsEnvelopeMutationParticipant.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/SettingsCoordinator.cs`
- Create: `ClashSharp/ClashSharp.Application/Packaging/DataPackage.cs`
- Create: `ClashSharp/ClashSharp.Application/Mutations/CompositeMutationRecoveryPlanResolver.cs`
- Modify: `ClashSharp/ClashSharp/AppHost/ClashSharpAppHostFactory.cs`
- Create: `ClashSharp/ClashSharp.Tests/Unit/Settings/SettingsMutationPlanBuilderTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/SettingsCoordinatorTests.cs`

- [ ] **Step 1: Write RED single-owner tests**

Specify the four public async operations from the design. Apply validates and normalizes the complete change set before admission. Import/reset/clear require quiescence. One request creates one operation ID, plan, journal, target promotion, result, and aggregate publication. Reject nested coordinator calls and stale/foreign mutation contexts.

```csharp
Task<SettingsApplyResult> ApplyAsync(
    SettingsChangeSet changes,
    CancellationToken cancellationToken);
Task<SettingsApplyResult> ImportAsync(
    DataPackage package,
    CancellationToken cancellationToken);
Task<SettingsApplyResult> ResetAsync(
    SettingsResetScope scope,
    CancellationToken cancellationToken);
Task<SettingsApplyResult> ClearDataAsync(CancellationToken cancellationToken);
```

- [ ] **Step 2: Implement planning without side effects**

Build the next envelope with the pure editor, capture baseline/desired hashes, collect ordered participant contributions, and calculate `RequiresQuiescence`. Stage/verify/compensate delegates receive the active context and frozen values only.

- [ ] **Step 3: Implement envelope target promotion and recovery routing**

The envelope participant promotes only after all external participants verify. Register a composite resolver keyed by exact operation type for network and settings plans; unknown or mismatched operations fail safe. Result creation reads the verified committed snapshot before journal deletion.

- [ ] **Step 4: Failure-inject and checkpoint**

Cover pre-journal, participant stage/apply/verify, target promotion, result creation, activation, and cleanup failures plus cancellation on both sides of the marker. Commit `feat: coordinate settings mutations transactionally`.

---

### Task 9: Add verified live, external, and restart application handlers

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Settings/ISettingApplicationHandler.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/SettingApplicationHandlerRegistry.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/InternalSettingApplicationHandler.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/NetworkSettingsApplicationHandler.cs`
- Create: `ClashSharp/ClashSharp/Service/StartupTaskSettingsApplicationHandler.cs`
- Create: `ClashSharp/ClashSharp/Service/SamplingSettingsApplicationHandler.cs`
- Create: `ClashSharp/ClashSharp/Service/TriggerSettingsApplicationHandler.cs`
- Create: `ClashSharp/ClashSharp/Service/AppearanceSettingsApplicationHandler.cs`
- Create: `ClashSharp/ClashSharp.Application/Network/INetworkMutationParticipantFactory.cs`
- Modify: `ClashSharp/ClashSharp.Application/Network/NetworkStateCoordinator.cs`
- Modify: `ClashSharp/ClashSharp.Application/Network/NetworkPlan.cs`
- Modify: `ClashSharp/ClashSharp.Application/Network/INetworkStateAdapter.cs`
- Modify: `ClashSharp/ClashSharp/AppHost/ClashSharpAppHostFactory.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/SettingsApplicationHandlerTests.cs`
- Extend: `ClashSharp/ClashSharp.Tests/Integration/NetworkMutationConcurrencyTests.cs`

- [ ] **Step 1: Write RED per-handler final-state matrices**

Cover success, denial/mismatch, cancellation, timeout, rollback, and typed unknown for mode, TUN, mixed port, profile, StartupTask, sampling, and trigger enablement. StartupTask `Disabled/Enabled/DisabledByUser/Other/error` must query final state and never report false success. Verify frozen settings values are used after journaling.

- [ ] **Step 2: Contribute participants, never nested operations**

Network settings use the existing adapter's under-context planning path and join the settings mutation. Startup/sampling/trigger/appearance handlers implement probe/stage/apply/verify/compensate/activate/cleanup directly. Internal live values publish only after committed verification.

- [ ] **Step 3: Preserve honest desired/applied presentation**

Only verified live keys advance `AppliedState`. Restart work keeps prior applied state and creates a durable restart batch. Unknown safety-sensitive state uses the registry fallback and exposes degraded status.

- [ ] **Step 4: Run pairwise concurrency and checkpoint**

Run settings/network/trigger/shutdown pairwise interleavings and final-state matrices ten times. Commit `feat: verify settings runtime application`.

---

### Task 10: Reconcile pending batches with retry and revert semantics

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Settings/SettingsBatchIdentity.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/SettingsBatchReconciler.cs`
- Create: `ClashSharp/ClashSharp.Application/Settings/ISettingsBatchAttemptStore.cs`
- Create: `ClashSharp/ClashSharp/AppHost/Startup/SettingsBatchReconciliationStartupStep.cs`
- Modify: `ClashSharp/ClashSharp.Application/Settings/SettingsCoordinator.cs`
- Modify: `ClashSharp/ClashSharp/AppHost/ClashSharpAppHostFactory.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/SettingsBatchReconciliationTests.cs`

- [ ] **Step 1: Write RED ordering/deduplication tests**

Process `LiveReconcile` before `Restart`, then creation sequence and batch ID. Identity is batch ID, attempt ID, and sorted immutable key revision/value hash entries; unrelated `EnvelopeRevision` changes cannot re-run an attempt. Stop startup reconciliation on first failure.

- [ ] **Step 2: Implement success/failure state transitions**

Mark `Running` atomically before effects. Success advances applied state for only batch keys and removes the batch after verification. Failure retains desired, records typed failure, and requires explicit retry/edit/revert. Explicit retry assigns a new attempt ID.

- [ ] **Step 3: Implement safe revert and crash recovery**

Revert uses verified applied values or declared fallback for unknown keys. It never discards unrelated failed work. A crash resumes the journaled attempt rather than generating another automatic attempt.

- [ ] **Step 4: Repeat and checkpoint**

Run batch edit/retry/revert/startup tests ten times and commit `feat: reconcile pending settings batches`.

---

### Task 11: Make profiles, triggers, logs, and configuration generation-aware

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Data/IGenerationRepositoryFacade.cs`
- Create: `ClashSharp/ClashSharp.Application/Data/GenerationRepositoryFacade.cs`
- Modify/create generation-aware factories/facades for:
  - settings repository
  - profile catalog
  - trigger repository
  - log storage
  - mihomo configuration
- Modify: `ClashSharp/ClashSharp/AppHost/ClashSharpAppHostFactory.cs`
- Modify: startup consumers to resolve facades instead of concrete generation repositories
- Create: `ClashSharp/ClashSharp.Tests/Integration/GenerationRepositoryFacadeTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/GenerationSupervisorAttachmentTests.cs`

- [ ] **Step 1: Write RED stale-reference and attachment tests**

Block operations with active leases, begin drain, prove later leases reject, prepare a new paused attachment, and verify no scheduling/publication occurs before commit. After swap, new operations use only the new generation. A retained old concrete repository reference must not be injectable into consumers.

- [ ] **Step 2: Route repository operations through pinned facades**

Each facade acquires one generation lease for the whole operation and resolves that generation's concrete repository. Supervisors prepare paused generation attachments with readiness probes and explicit dispose/resume operations.

- [ ] **Step 3: Migrate flat paths transactionally**

The first generation migration snapshots existing profile/trigger/log/config files, validates staged copies, and promotes the generation manifest. Compatibility paths are read once and retained as rollback material; later writes use generation paths only.

- [ ] **Step 4: Stress and checkpoint**

Run in-flight writer/action, swap, stale facade, failed readiness, rollback, and post-marker forward activation tests. Commit `feat: make repositories generation aware`.

---

### Task 12: Replace direct XML import with a staged versioned package transaction

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Packaging/IDataPackageReader.cs`
- Create: `ClashSharp/ClashSharp.Application/Packaging/IDataPackageExporter.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Packaging/LegacyXmlDataPackageReader.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Packaging/JsonDataPackageExporter.cs`
- Create: `ClashSharp/ClashSharp.Infrastructure/Packaging/DataPackageStager.cs`
- Delete or reduce to a read-only compatibility wrapper: `ClashSharp/ClashSharp/Service/ClashDataPackageService.cs`
- Modify: `ClashSharp/ClashSharp.Application/Settings/SettingsCoordinator.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/DataPackageCompatibilityTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/DataPackageImportTransactionTests.cs`

- [ ] **Step 1: Write RED full-package validation tests**

Cover schema/version/scope, registry-generated descriptors, every current setting including hero layout, duplicate keys/files, hashes, base64, enum membership, ranges, aliases, relative/reparse/escaping paths, oversized payloads, and legacy v1 fixtures. Validate everything before admission or staging writes.

- [ ] **Step 2: Stage one immutable import plan**

Read the package once, normalize settings through the registry, hash every file, and stage a full candidate generation. Do not mutate settings or target files while parsing. Current exports emit one current schema and canonical keys only.

- [ ] **Step 3: Execute one combined mutation**

Import joins envelope, files, network, hosted services, caches, and generation attachments in one settings plan. Each participant executes once. Pre-marker failure restores every layer; post-marker failure recovers forward. Publish one aggregate settings/data event after locks release.

- [ ] **Step 4: Fault-inject and checkpoint**

Inject at every participant and promotion cut, verify cache/runtime/final files, and retain legacy readability. Commit `feat: import data packages transactionally`.

---

### Task 13: Implement reset and generation-safe clear-data

**Files:**

- Create: `ClashSharp/ClashSharp.Application/Data/ClearDataMutationPlanBuilder.cs`
- Create: `ClashSharp/ClashSharp.Application/Data/ClearDataRecoveryPlanResolver.cs`
- Modify: `ClashSharp/ClashSharp.Application/Settings/SettingsCoordinator.cs`
- Delete or reduce to compatibility forwarding only:
  - `ClashSharp/ClashSharp/Service/AppDataMaintenanceService.cs`
  - `ClashSharp/ClashSharp/AppHost/Compatibility/LegacyAppDataMaintenanceRuntimeAdapter.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/SettingsResetTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/ClearDataGenerationTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/ClearDataCrashRecoveryTests.cs`

- [ ] **Step 1: Write RED reset/clear matrices**

Group/global reset generate registry-derived change sets and follow ordinary settings application. Clear-data covers admission drain, partial quiescence timeout/cancellation, in-flight log/profile/trigger writes, paused new attachments, default network state, manifest promotion, in-memory swap, target verification, commit marker, cleanup, and resume.

- [ ] **Step 2: Implement default-only generation staging**

Create exactly the registry defaults and intentionally recreated canonical files. Keep the old generation authoritative and alive through external verification and paused new-generation readiness. No source directory deletion occurs pre-marker.

- [ ] **Step 3: Implement rollback/forward-only cut**

Before the marker, dispose prepared attachments, restore old manifest/facade, and reverse-resume prior participants. After the marker, delete only the unreachable old generation and resume the prepared target. Activation/cleanup failure returns committed-degraded, retains the journal, and keeps admission `RecoveryOnly`.

- [ ] **Step 4: Kill/restart and checkpoint**

Terminate at stage, external effect, manifest promotion, facade swap, marker, supervisor resume, and cleanup. Prove the recovery root survives and the outcome is exactly the verified old or default generation. Commit `feat: clear data by replacing generations`.

---

### Task 14: Route Settings presentation through typed coordinator results

**Files:**

- Modify: `ClashSharp/ClashSharp/ViewModel/SettingsViewModel.cs`
- Modify: `ClashSharp/ClashSharp/View/Settings.xaml.cs`
- Modify: `ClashSharp/ClashSharp/View/Settings.xaml`
- Create: `ClashSharp/ClashSharp/AppHost/Compatibility/SettingsPresentationCompatibilityFactory.cs`
- Modify/delete writable portions of:
  - `ClashSharp/ClashSharp/Service/AppSettingsService.cs`
  - `ClashSharp/ClashSharp/ViewModel/SettingsServiceAdapters.cs`
  - `ClashSharp/ClashSharp/AppHost/Compatibility/SettingsRuntimeMutationAdapter.cs`
- Modify: `ClashSharp/ClashSharp.Tests/Unit/ViewModel/SettingsViewModelTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Integration/SettingsPresentationStateTests.cs`

- [ ] **Step 1: Write RED truthful-state and validation tests**

Require pending/applied/failed/unknown/restart status, one tracked command per operation, cancellation propagation, localized stable errors, and restoration of the last verified displayed state. Cover StartupTask denial/mismatch, port/TUN/profile failure, per-target URL defaults/field errors, undefined enum indices, import/reset/clear, and no optimistic applied claim.

- [ ] **Step 2: Replace direct property writes**

Settings methods build typed change sets and await `ISettingsCoordinator`. The ViewModel reads immutable snapshots and observes one aggregate committed event. Code-behind only binds dialogs/file pickers and awaits commands. Do not perform the Phase 07 section-ViewModel split in this task.

- [ ] **Step 3: Remove the writable legacy singleton path**

`AppSettingsService` may remain only as an explicitly named read compatibility facade if a Phase 07 consumer still needs it. It exposes no setter, reset, import, delete, network, or hosted-service mutation. Lower per-file `.Instance` baselines and reject future settings service locators.

- [ ] **Step 4: Verify XAML and checkpoint**

Run Settings ViewModel/XAML Debug/Release builds and presentation matrices ten times. Review for raw exception text, reentrancy, stale state, and code-behind logic, then commit `refactor: route settings through transactional coordinator`.

---

### Task 15: Add architecture gates, evidence, and Phase 05 closure

**Files:**

- Create: `ClashSharp/ClashSharp.Tests/Architecture/SettingsArchitectureTests.cs`
- Modify: `ClashSharp/ClashSharp.Tests/Architecture/RepositoryTopologyTests.cs`
- Create: `docs/architecture/evidence/phase-05-settings-and-data-generations.md`
- Modify: `docs/architecture/stabilization-ledger.md`
- Modify: `docs/superpowers/plans/2026-07-19-architecture-stabilization-roadmap.md`
- Modify: this plan

- [ ] **Step 1: Add executable policy**

Reject parallel key/default/package/reset lists, mutable settings persistence in presentation, direct import/reset/clear file mutation, sync context/storage calls, nested public coordinator use, concrete generation repositories held by consumers, flat-path writes after migration, writable aliases, new service locators, and detached work. Verify DI/startup order and registry coverage through behavior/reflection where possible; record every unavoidable source contract for Phase 12 removal.

- [ ] **Step 2: Run the complete final matrix**

Run forced locked restore, format verification, Debug and Release solution builds, full Release tests, settings/mutation/network/lifecycle/generation filters ten consecutive times, every migration/import/clear crash probe repeatedly, pairwise concurrency stress, zero leaked probe processes, and `git diff --check`.

- [ ] **Step 3: Close ledger rows only with complete evidence**

Close `P1-07`, `P2-SET-01`, `P2-SET-02`, and `P2-SET-03`; close the remaining `P1-06`/`P1-08` portions only if every required matrix is present. Record exact counts, repetitions, cut points, checkpoint commits, reviewer, date, and retained Phase 07/08/12 compatibility debt.

- [ ] **Step 4: Review and checkpoint**

Use `superpowers:requesting-code-review`, address all Critical/Important findings with `superpowers:receiving-code-review`, rerun the complete matrix, mark Phase 05 complete, commit implementation plus evidence, and preserve the worktree for Phase 06.
