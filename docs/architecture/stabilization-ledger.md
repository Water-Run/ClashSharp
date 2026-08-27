# ClashSharp Stabilization Ledger

> This ledger preserves architecture-closure evidence; it is not the current project-status summary. Current status is mapped in [`2026-08-27-project-development-map.md`](../reviews/2026-08-27-project-development-map.md), and current release blockers and execution order are tracked only in [`2026-08-27-production-readiness-execution-plan.md`](../reviews/2026-08-27-production-readiness-execution-plan.md).

The architecture stabilization design and its audit traceability table are normative. A row becomes `Closed` only when its regression or manual evidence, closure commit, reviewer, and closure date are all recorded. `—` means evidence does not yet exist; it is never a substitute for closure proof.

| ID | Severity | Owner | Status | Plan task | Evidence | Closure commit | Reviewer | Closure date |
|---|---|---|---|---|---|---|---|---|
| P1-01 | P1 | Application | In Progress | Phases 02 and 03 | `ApplicationStartupContractTests`; `SecondaryInstanceIsolationTests` (10 repeated real two-process runs); `docs/architecture/evidence/phase-02-apphost-startup.md`; `docs/architecture/evidence/phase-03-mutation-network-lifetime.md`; packaged real-app smoke pending | — | structured self-review | — |
| P1-02 | P1 | Runtime | Closed | Phase 04 | `SqliteTriggerRepositoryTests`; `TriggerBackupRecoveryTests`; `LegacyTriggerMigrationTests`; `TriggerPersistenceCrashTests`; `TriggerArchitectureTests`; `docs/architecture/evidence/phase-04-trigger-persistence-and-execution.md` | `7d3779b6f0d93d1857684b42b087f28d56e46ced` | independent structured review (`task11_review`) and structured self-review | 2026-07-26 |
| P1-03 | P1 | Runtime | Closed | Phase 04 | `TriggerMatcherTests`; `TriggerEvaluationConcurrencyTests`; `TriggerActionExecutorTests`; `TriggerOutboxRecoveryTests`; `TriggerExitHandoffTests`; `TriggerSchedulerTests`; `docs/architecture/evidence/phase-04-trigger-persistence-and-execution.md` | `7d3779b6f0d93d1857684b42b087f28d56e46ced` | independent structured review (`task11_review`) and structured self-review | 2026-07-26 |
| P1-04 | P1 | Presentation | Closed | Phase 04 | `TriggerEditorViewModelTests`; `TriggerArchitectureTests`; Debug/Release WinUI builds; `docs/architecture/evidence/phase-04-trigger-persistence-and-execution.md` | `7d3779b6f0d93d1857684b42b087f28d56e46ced` | independent structured review (`task11_review`) and structured self-review | 2026-07-26 |
| P1-05 | P1 | Runtime | Closed | Phase 04 | `TriggerContextProviderTests`; `TriggerArchitectureTests`; 10 repeated trigger/mutation/lifecycle/supervision runs; `docs/architecture/evidence/phase-04-trigger-persistence-and-execution.md` | `7d3779b6f0d93d1857684b42b087f28d56e46ced` | independent structured review (`task11_review`) and structured self-review | 2026-07-26 |
| P1-06 | P1 | Application | In Progress | Phases 03 and 05 | `ApplicationMutationCoordinatorTests`; `RuntimeLifecycleCoordinatorTests`; `AppDataMaintenanceServiceTests`; `docs/architecture/evidence/phase-03-mutation-network-lifetime.md`; Phase 05 settings transaction matrix pending | — | structured self-review | — |
| P1-07 | P1 | Application | In Progress | Phase 05 | admitted import/runtime mutation paths exist; generation-backed settings authority and complete replay/rollback matrix pending | — | — | — |
| P1-08 | P1 | Runtime | In Progress | Phases 03 and 05 | `NetworkMutationConcurrencyTests`; `NetworkTakeoverServiceTests`; Master/Settings rollback tests; `docs/architecture/evidence/phase-03-mutation-network-lifetime.md`; Phase 05 verified settings generation pending | — | structured self-review | — |
| P1-09 | P1 | Presentation | Evidence Pending | Phase 06 | typed `ShellRoute.Connections`, shell/tray entry, page factory and `MainWindowCompositionArchitectureTests`; current candidate CI/closure review pending | — | — | — |
| P1-10 | P1 | Release | Evidence Pending | Phase 10 | manifest-derived Publisher binding and `InstallerBuildScriptTests`; clean signed candidate/closure review pending | — | — | — |
| P1-11 | P1 | Release | In Progress | Phase 01 Task 3; Phase 12 | `RepositoryTopologyTests`; `docs/architecture/evidence/phase-01-repository-foundation.md` | — | — | — |
| P2-I18N-01 | P2 | Localization | Evidence Pending | Phase 08 | six explicit localization catalogs and resource completeness tests; current candidate CI/semantic review pending | — | — | — |
| P2-I18N-02 | P2 | Localization | Open | Phase 08 | — | — | — | — |
| P2-I18N-03 | P2 | Localization | Evidence Pending | Phase 08 | import/reset localized-property refresh paths exist; one-cycle shell/Settings integration proof pending | — | — | — |
| P2-I18N-04 | P2 | Localization | Open | Phase 08 | — | — | — | — |
| P2-SET-01 | P2 | Application | In Progress | Phase 05 | typed StartupTask failure and UI fallback paths exist; verified settings transaction closure pending | — | — | — |
| P2-SET-02 | P2 | Application | Evidence Pending | Phase 05 | enum normalization uses parse plus defined-value validation; Phase 05 matrix/closure review pending | — | — | — |
| P2-SET-03 | P2 | Presentation | In Progress | Phase 05 | validation paths exist, but Settings code-behind does not consistently surface false validation results | — | — | — |
| P2-RUN-01 | P2 | Runtime | Closed | Phase 03 | `SupervisedLoopTests`; `ConnectionSamplingServiceTests`; `docs/architecture/evidence/phase-03-mutation-network-lifetime.md` | `bd9d0ae3b1fd6bd0b2436977424e7f39aa5772fd` | structured self-review | 2026-07-23 |
| P2-RUN-02 | P2 | Runtime | Closed | Phase 03 | `WindowsProcessRunnerTests`; `MihomoServiceManagerTests`; 10 repeated process-tree/cancellation smoke runs with zero residue; `docs/architecture/evidence/phase-03-mutation-network-lifetime.md` | `bd9d0ae3b1fd6bd0b2436977424e7f39aa5772fd` | structured self-review | 2026-07-23 |
| P2-RUN-03 | P2 | Presentation | In Progress | Phases 03 and 07 | `AsyncRelayCommandTests`; Master/Settings pending-applied-rollback tests; presentation `.Instance` freeze gate; `docs/architecture/evidence/phase-03-mutation-network-lifetime.md`; full Phase 07 migration pending | — | structured self-review | — |
| P2-RUN-04 | P2 | Runtime | Closed | Phase 03 | `ConcurrentBoundedTextBufferTests`; real concurrent stdout/stderr core diagnostic test; `docs/architecture/evidence/phase-03-mutation-network-lifetime.md` | `bd9d0ae3b1fd6bd0b2436977424e7f39aa5772fd` | structured self-review | 2026-07-23 |
| P2-UI-01 | P2 | Presentation | Open | Phase 09 | — | — | — | — |
| P2-UI-02 | P2 | Presentation | Open | Phase 09 | — | — | — | — |
| P2-UI-03 | P2 | Presentation | Open | Phase 09 | — | — | — | — |
| P2-UI-04 | P2 | Presentation | Open | Phase 09 | — | — | — | — |
| P2-UI-05 | P2 | Presentation | Open | Phase 09 | — | — | — | — |
| P2-UI-06 | P2 | Presentation | Open | Phase 09 | — | — | — | — |
| P2-UI-07 | P2 | Presentation | Evidence Pending | Phase 06 | centralized typed routes and navigation selection contracts; candidate CI/closure review pending | — | — | — |
| P2-UI-08 | P2 | Presentation | Evidence Pending | Phase 09 | `_closePromptActive` reentrancy gate exists; repeated-close runtime evidence pending | — | — | — |
| P2-UI-09 | P2 | Presentation | In Progress | Phase 06 | Connections uses adaptive star/auto layout; 800×600 and 200% text-scale evidence pending | — | — | — |
| P2-REL-01 | P2 | Release | In Progress | Phase 10 | fixed hashes, trust anchor, signer/provenance and TOCTOU protection exist; certificate lifecycle/SBOM/release proof pending | — | — | — |
| P2-REL-02 | P2 | Release | Open | Phase 10 | — | — | — | — |
| P2-REL-03 | P2 | Release | In Progress | Phase 10 | controlled `build.ps1` and manifest-derived identity exist; canonical version and release metadata pending | — | — | — |
| P2-QA-01 | P2 | Release | Evidence Pending | Phase 01 Tasks 2, 4, and 5 | `.github/workflows/ci.yml`; `docs/architecture/evidence/phase-01-repository-foundation.md` | — | — | — |
| P3 dependency governance | P3 | Release | In Progress | Phases 01 and 11 | `eng/dependency-audit-exceptions.json`; `docs/architecture/evidence/phase-01-repository-foundation.md` | — | — | — |
| Additional: disabled Profiles/Links commands | Additional | Presentation | Open | Phase 07 | — | — | — | — |
| Additional: work-area/overlay sizing | Additional | Presentation | Open | Phase 09 | — | — | — | — |
| P3 log-storage global lock | P3 | Runtime | Open | Phase 11 | — | — | — | — |
| P3 code size/static singleton debt | P3 | Presentation | In Progress | Phases 02, 07, and 12 | `ClashSharp.Application`; `ClashSharp/ClashSharp/AppHost`; `docs/architecture/evidence/phase-02-apphost-startup.md` | — | — | — |
| P3 nullable/docs/analyzer/header drift | P3 | Application | In Progress | Phase 01 Task 2; Phase 12 | `CodingStyle.md`; `Directory.Build.props`; `.gitattributes`; Phase 01 zero-warning build | — | — | — |
| P3 source-text-heavy tests/no parallelism | P3 | Release | Open | Phase 12 | — | — | — | — |
| P3 debt owner/status tracking | P3 | Release | In Progress | Phases 01 and 13 | `docs/architecture/stabilization-ledger.md` | — | — | — |
| Security candidate: service identity/ACL | Security | Runtime | Open | Phase 11 | — | — | — | — |
