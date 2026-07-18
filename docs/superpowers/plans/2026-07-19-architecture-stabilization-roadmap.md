# ClashSharp Architecture Stabilization Roadmap

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the approved architecture-stabilization design through small, buildable, independently reviewable checkpoints until all 33 acceptance criteria and every audit traceability row have evidence.

**Architecture:** Migrate vertically from the current WinUI monolith into `ClashSharp.Core`, `ClashSharp.Infrastructure`, and a presentation-only WinUI executable. Establish executable repository policy first, then introduce lifetime and mutation ownership before moving persistence, settings, triggers, and presentation behavior. Keep compatibility readers and adapters only while their replacement path is covered by regression tests.

**Tech Stack:** .NET 10.0.201, C# 14, WinUI 3 / Windows App SDK, xUnit, Microsoft.Data.Sqlite, Rust 1.95.0, GitHub Actions, Windows Sandbox.

## Global Constraints

- The normative design is `docs/superpowers/specs/2026-07-18-architecture-stabilization-design.md`.
- Every phase starts with a dedicated implementation plan and failing characterization or contract test.
- Every phase ends with a clean Release build, relevant regression tests, ledger evidence, review, and a commit.
- Startup, settings, network, trigger, lifecycle, and recovery changes may not bypass the lifetime and mutation contracts introduced by earlier phases.
- Existing settings, triggers, profiles, and data packages remain readable until the versioned compatibility rules permit removal.
- No new production source-link entry, `UNIT_TESTS` fork, presentation `.Instance` lookup, volatile date banner, unpinned action, or unowned fire-and-forget task may be introduced.
- Manual UI, Sandbox, installer, signing, service, and RPC checks complement automation; they never replace automatable tests.

---

## Phase Sequence

| Phase | Independently testable outcome | Primary traceability coverage | Depends on |
|---|---|---|---|
| 01 Repository foundation and topology | Pinned toolchains, LF policy, build/analyzer gates, baseline CI, real Core/Infrastructure references, first model tested from its production assembly | P1-11, P2-QA-01, P3 nullable/docs/analyzer/header drift | Frozen design |
| 02 Composition root and startup ownership | Side-effect-free `AppHost`; primary-instance arbitration precedes host construction, data access, and external mutation | P1-01, P3 code size/static singleton debt (composition portion) | 01 |
| 03 Mutation, network, and process lifetime | One admission barrier/coordinator/journal path; bounded recovery; no self-join shutdown; supervised network/runtime transitions | P1-06, P1-08, P2-RUN-01, P2-RUN-02, P2-RUN-03, P2-RUN-04 | 02 |
| 04 Trigger persistence and execution | SQLite repository, typed conditions, async degraded context, deterministic scheduler/executor/outbox, all-condition editor | P1-02, P1-03, P1-04, P1-05 | 03 |
| 05 Settings and data generations | Registry/envelope revisions, verified live apply, import/reset/clear transactions, restart/pending state, migration compatibility | P1-07, P2-SET-01, P2-SET-02, P2-SET-03 and the settings half of P1-06/P1-08 | 03, 04 |
| 06 Navigation and Connections vertical slice | One route registry for shell/tray/internal navigation; Connections is reachable, cancellable, page-scoped, and adaptive | P1-09, P2-UI-07, P2-UI-09 | 02, 03 |
| 07 Presentation MVVM migration | Page-by-page constructor injection; domain editing/validation/async state leaves code-behind; disabled commands restored | Additional Profiles/Links, P2-RUN-03, P3 code size/static singleton debt | 05, 06 |
| 08 Localization and culture | Explicit released-language resources, raw completeness gate, semantic review, selected-culture formatting, one-cycle live refresh | P2-I18N-01, P2-I18N-02, P2-I18N-03, P2-I18N-04 | 05, 07 |
| 09 Accessibility and adaptive UI | Keyboard/UIA/focus/high-contrast fixes, bounded viewports, work-area sizing, dialog/overlay correctness | P2-UI-01 through P2-UI-06, P2-UI-08, Additional work-area/overlay sizing | 06, 07, 08 |
| 10 Packaging, installer, and supply chain | Single version source, manifest-derived signing subject, verified download/cleanup, SBOM/provenance/release metadata, executable Sandbox scenarios | P1-10, P2-REL-01, P2-REL-02, P2-REL-03 | 01, 05 |
| 11 Service security, storage concurrency, and dependency governance | AppContainer broker, per-call authorized RPC, service SID/QOS V5 hardening, ACLs, bounded log maintenance, dependency cadence | Security service identity/ACL, P3 log-storage global lock, P3 dependency governance | 03, 10 |
| 12 Compatibility-debt removal | No production source links, `UNIT_TESTS`, presentation service locators, stale entry points, or unjustified global test serialization | P1-11, P3 source-text-heavy tests/no parallelism, P3 debt owner/status tracking | 04-11 |
| 13 Whole-system release audit | All 33 acceptance criteria and all ledger rows close with reproducible automated/manual evidence | Every audit row and release criterion | 01-12 |

## Detailed Plan Files

- [x] Phase 01: `docs/superpowers/plans/2026-07-19-repository-foundation-and-project-topology.md`
- [x] Phase 02: `docs/superpowers/plans/2026-07-19-apphost-and-startup-ownership.md`
- [ ] Phase 03: `docs/superpowers/plans/2026-07-19-mutation-network-and-lifetime.md`
- [ ] Phase 04: `docs/superpowers/plans/2026-07-19-trigger-persistence-and-execution.md`
- [ ] Phase 05: `docs/superpowers/plans/2026-07-19-settings-and-data-generations.md`
- [ ] Phase 06: `docs/superpowers/plans/2026-07-19-navigation-and-connections.md`
- [ ] Phase 07: `docs/superpowers/plans/2026-07-19-presentation-mvvm-migration.md`
- [ ] Phase 08: `docs/superpowers/plans/2026-07-19-localization-and-culture.md`
- [ ] Phase 09: `docs/superpowers/plans/2026-07-19-accessibility-and-adaptive-ui.md`
- [ ] Phase 10: `docs/superpowers/plans/2026-07-19-packaging-installer-and-supply-chain.md`
- [ ] Phase 11: `docs/superpowers/plans/2026-07-19-service-security-storage-and-dependencies.md`
- [ ] Phase 12: `docs/superpowers/plans/2026-07-19-compatibility-debt-removal.md`
- [ ] Phase 13: `docs/superpowers/plans/2026-07-19-whole-system-release-audit.md`

Only the current phase receives an executable task-level plan. Later plan files are created after the preceding contracts and production topology are real, so their file paths, interfaces, and test seams describe the actual repository rather than guessed future state.
