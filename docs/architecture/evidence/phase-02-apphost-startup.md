# Phase 02 AppHost and Startup Ownership Evidence

**Recorded:** 2026-07-19

**Branch:** `codex/architecture-stabilization-phase-01`

**Plan:** `docs/superpowers/plans/2026-07-19-apphost-and-startup-ownership.md`

## TDD evidence

The first `ApplicationStartupContractTests` run occurred after the empty `ClashSharp.Application` project and production references existed but before its startup contracts existed. It failed with `CS0234` and `CS0246` for the missing `ClashSharp.ApplicationModel.Startup` and `Hosting` types. After the production launch, host, pipeline, and lifetime contracts were implemented, the focused contract suite passed.

A later review identified that `StartupCoordinator` used `ConfigureAwait(false)`, which could continue window startup on a thread-pool thread after asynchronous proxy recovery. `StartAsync_AsynchronousStep_PreservesCallerSynchronizationContext` first failed with `PostCount == 0`; removing context suppression from the launch pipeline made it pass.

The two-process regression first failed because `ClashSharp.StartupProbe.dll` did not exist. After the probe was added as a build-only test dependency, the test exposed and diagnosed a thread-affine named-Mutex release error. The probe now uses a named Semaphore, which preserves cross-process exclusion across asynchronous continuations.

## Production ownership chain

- `ClashSharp.Application.dll` owns `ApplicationBootstrapper`, `AppHost`, `StartupCoordinator`, typed startup results, and `ProcessLifetimeRunner`.
- `ApplicationBootstrapper` awaits `IPrimaryInstanceBootstrap.AcquireAsync` before invoking its lazy host factory.
- `AppHost.Build` creates and validates the DI provider without resolving the startup coordinator or startup steps; the regression test observes zero constructor calls before `StartAsync`.
- WinUI uses `AppInstance.FindOrRegisterForKey`. A secondary process awaits `RedirectActivationToAsync` and exits without host/window construction. Redirected activation is marshalled to the primary dispatcher and activates the existing window, including one pending activation before window attachment.
- Primary startup runs ordered compatibility steps for localization, helper restore, awaited stale-proxy recovery, audit, triggers, window, and sampling. Proxy recovery completes before window construction, removing the previous untracked recovery-versus-startup-mode race.
- The old process-enumeration/kill dialog, service, tests, and localization keys were removed from `MainWindow`.
- WinUI `App` owns the outer lifetime runner. Ordinary window and helper exits await host stop and async disposal. A primary fatal startup keeps a visible diagnostic shell; redirected or intentionally headless outcomes use bounded shutdown attempts, while terminal host-disposal failure follows a controlled ownership-release and process-exit path.

## Verification evidence

The Application project remains platform-neutral at `net10.0`. Locked restore succeeds for the seven-project solution. A Release solution build completed with 0 warnings and 0 errors. The full Release test run passed 690 tests, 0 failed, and 0 skipped.

The real two-process test creates a unique named Semaphore and shared trace. It starts a primary helper, waits until that process records `host-start`, launches a secondary helper, then proves the trace contains exactly one `host-build`, exactly one `host-start`, exactly one `secondary-redirected`, and no `secondary-mutation`. Every wait is bounded and cleanup kills any surviving helper process. The final verification repeated this process-level regression five consecutive times without a failure or leaked helper.

Implementation checkpoint: `76bea68e71ed08a20f4c4eff17a17b90bac5e6ba` (`refactor: make startup ownership first`).

## Pending evidence

`P1-01` remains `In Progress`: Phase 03 now routes recovery, startup-mode application, and shutdown through the mutation/network and runtime-lifecycle coordinators. The remaining closure item is a packaged real-app two-instance smoke against the actual Windows App SDK, proxy, and core state. Static `.Instance` access is contained behind compatibility and composition boundaries for this path, but later presentation phases must still remove the remaining service locators.
