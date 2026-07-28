# Mid-refactor Stabilization and Functional Parity Review

**Recorded:** 2026-07-28

**Branch:** `codex/architecture-stabilization-phase-01`

**Comparison baseline:** `f3fce05da96a8d47c9cdc11a51a12e4cd5f0fc46`, the clean `HEAD` at the start of this stabilization pass

## Scope and validation boundary

This checkpoint stabilizes the current architecture refactor; it does not claim that the product or the wider refactor is complete. The acceptance target is the same implemented feature level as the comparison baseline, with safer ownership boundaries and room for later feature work.

The workstation was in active use, so this pass deliberately did not launch or control the WinUI application and did not take screenshots. UI conclusions are based on XAML/source inspection, WinUI compilation, architecture tests, and behavioral tests. A real interactive visual smoke remains separate evidence and is not implied by this report.

## Immediate regressions addressed

### Black startup window and apparent hangs

- Primary-instance ownership is decided before the application creates its shell.
- A minimal visible startup shell is created before host startup work, with navigation hidden and disabled until runtime readiness.
- Fatal primary startup keeps the diagnostic shell visible instead of leaving a black or missing window.
- Optional theme, title-bar, minimum-size, window-procedure, and tray setup is capability-gated; ordinary native failures cannot repeatedly prevent the shell from being constructed. Process-fatal and cancellation exceptions are still propagated through a recursive exception-graph policy.
- Startup and lifecycle diagnostics are queued to one owned FIFO consumer. Their caller-side boundary performs no SQLite/file I/O and does not read exception-controlled text; shutdown uses bounded flush and completion.
- The lifetime-request consumer is explicitly awaited and fault-observed. Dispatcher rejection, scheduler exceptions, and a dispatcher that can no longer resume continuations follow bounded retry or a non-UI ownership-release fallback.
- A lifetime request that temporarily blocks startup completion retains the latest startup context and resumes it only while the host is still running. A stopped host cannot be presented as resumable, and terminal exit explicitly abandons deferred context.
- Post-startup `AppEntered`, conflict checks, and startup-guide work is explicitly scheduled once after runtime readiness. It no longer depends on a `Loaded` event that the early visible shell may already have raised.
- Startup guidance no longer runs the full system-check set twice on the UI thread or builds a second full-window dark overlay. Checks are collected once through the page/window lifetime and passed to one managed dialog presentation.
- Redirected activation restores and activates an existing minimized window.
- `MinimizeToTray` hides the only window only after `NIM_MODIFY` or `NIM_ADD` confirms a usable icon. Explorer `TaskbarCreated` rebuilds the icon, and failed recovery restores a previously hidden window.
- Window-procedure restoration clears native state and its delegate only after confirmed success. Failed restoration retains the previous procedure and roots the managed callback for the process lifetime; cancellation or optional-resource cleanup failures cannot skip this boundary.

### Master-control layout and missing drop-down content

- The control area again uses a same-row hero and mode presentation rather than placing the primary cards one below another.
- The `ScrollViewer` now stretches its content to the real constrained viewport and responsive decisions use `ContentHost.SizeChanged`. The previous fixed scroll-bar-width subtraction and manual `ContentHost.Width` assignment were removed, so DPI, theme, and scroll-bar variations cannot move the layout across a guessed breakpoint.
- The shell pins the documented `NavigationView` adaptive thresholds and pane widths. At the application's 800 DIP minimum window width, compact navigation still leaves the master page above its 620 DIP side-by-side breakpoint.
- At normal width, the four modes retain the original compact vertical control rail on the right. Its rows use content sizing, so the controls cannot acquire the former star-sized gaps. Below 620 DIP of actual content, the hero and control rail stack and the four controls switch to a compact 2-by-2 fallback.
- The eight hero-status values fill horizontally and wrap after two columns. Their container widths are recalculated from the measured grid width, so the card does not fall back to a one-item vertical list at the supported minimum window size.
- Hero-status selectors have an explicit item template, selected-value mapping, and a scrollable flyout so item text is rendered consistently.
- Programmatic reset and flyout rebuilding are protected by a presentation selection gate, preventing `SelectionChanged` re-entry from persisting an unintended intermediate layout.

### Unexpected checks in the tile editor

- Visible tiles now have one canonical, ordered `MasterInfoTileLayout` setting instead of mixing persisted state with a default-visibility flag.
- The editor initializes each check from the tile's actual `IsVisible` state.
- Saving and drag reordering persist one normalized order; unknown and duplicate identifiers are filtered.
- An explicitly empty layout continues to mean “hide all”. An unknown-only corrupt value falls back to the safe eight-tile default.
- Import/export validates unsafe and oversized values before writes. Older packages that do not contain the new field preserve the current layout.

## Architecture, MVVM, and C# review

- All six data-backed pages (`Profiles`, `Links`, `Logs`, `Rules`, `Statistics`, and `Proxies`) now have explicit `Loaded`/`Unloaded` lifetimes. Their view-model constructors only retain dependencies and initialize safe empty state; initial file, SQLite, profile, and runtime reads are cancellable.
- Logs search has a 250 ms replaceable debounce, so superseded keystrokes do not start SQLite work. A later page load cannot be overwritten by an older canceled snapshot.
- Log cleanup and storage-backed cleanup previews now execute behind the asynchronous load boundary. Preview work uses a separate 150 ms latest-wins session, and cleanup produces its replacement snapshot in the same background operation before committing it to the UI.
- Profile import, validation, and activation and subscription-link add/check/update operations use the page lifetime token. The transitional synchronous JSON catalog is isolated behind asynchronous presentation adapters; leaving a page prevents stale completion from updating its view model.
- Page-loading commands now own and expose their asynchronous execution. Unloading the Master page cancels and awaits its current load, while a later reload receives a fresh lifetime.
- The Master view model checks cancellation after awaited boundaries and before committing UI, tray, core, or runtime state.
- Startup-conflict detection, dialog presentation, and repair now share the owning page or window cancellation token. Blocking operating-system probes are moved from the UI context, and a canceled page does not continue repair or write dialog state.
- Runtime snapshot capture is limited to current in-memory values. File, registry, process, and SQLite work occurs after the asynchronous boundary.
- Same-profile configuration import and validation share one keyed asynchronous transaction across existence checks, complete reads, external validation, metrics, staging, commit, and rollback. Each transaction owns unique staging and backup paths; different profiles can still proceed concurrently.
- Settings change notifications are raised after the settings lock is released, and connection snapshots resolve external profile state before entering the SQLite lock, eliminating a proven lock inversion.
- Naturally exited mihomo processes are disposed before replacement. Expected SQLite/runtime failures are translated into stable presentation results instead of leaking infrastructure details to the UI.
- Launch-at-sign-in verifies the actual Windows result after enable/disable. Platform refusal or an indeterminate state raises a typed failure; application and trigger actions persist the preference only after Windows confirms the transition.
- Localization catalog lookup supports worker-thread reads while language mutation and its notification remain UI-owned.
- Profile, link, and log view models depend on narrow presentation contracts. Concrete storage and platform adapters live under `Presentation/Adapters`, not under `ViewModel`.
- The entire `ViewModel` directory is independent of the `Service` namespace. Eight cross-layer snapshots/results were moved to `Model`, consumer-owned layout and action contracts live in one-type files, and the concrete application-action bridge remains in `Presentation/Adapters`.
- Master and Settings constructors no longer read persisted settings, runtime state, supported-language globals, or layout services. Their first explicit load establishes state; repeated loads do not rebuild collections or duplicate subscriptions.
- The `View` directory contains only the 11 code-behind files paired with its 11 XAML pages. Code-behind is limited to visual events, selection/focus, picker/dialog coordination, and page lifetime ownership; service resolution stays in composition.
- Startup steps receive their settings, localization, logging, audit, conflict, and network dependencies through host construction. A repository gate prevents `Service.Instance` resolution from returning inside `*StartupStep` implementations.
- Presentation adapters are one primary type per matching file. Volatile author/date/file banners and redundant per-file nullable directives were removed from the C# source tree and are now guarded by repository tests.
- Application shutdown is modeled with request identity and explicit persistence confirmation. Ordinary and durable requests have deterministic admission, retry, promotion, and terminal behavior; an unconfirmed durable owner reserves only its own identity without blocking ordinary exit.
- Headless shutdown is bounded. Host disposal failure is typed and follows a controlled ownership-release path rather than retrying a disposed host.
- Best-effort notification, diagnostic, native-shell, and UI cleanup paths no longer swallow direct or wrapped cancellation, out-of-memory, stack-overflow, or access-violation failures.
- Startup-shell, post-startup scheduling, deferred-completion, dispatcher rejection, hero-selection, async-command, snapshot, tile-layout, tray recovery, lifetime, and durable-retry behavior have dedicated regression tests and architecture wiring gates.
- New startup and tile-editor text is present in all six supported localization catalogs; fallback and packaging tests cover completeness.
- The localization catalogs contain the same complete key set for Simplified Chinese, Traditional Chinese, English, Russian, French, and German.

## Functional-parity inventory

The following inventory was extracted from the baseline commit and the final working tree with the same source patterns:

| Surface | Baseline | Current | Removed | Added |
| --- | ---: | ---: | --- | --- |
| Application XAML files | 21 | 21 | None | None |
| View pages with paired code-behind | 11 | 11 | None | None |
| Master-control tile identifiers | 58 | 58 | None | None |
| Default visible master tiles | 8 | 8 | None | None |
| Command-type master tiles | 13 | 13 | None | None |
| Master operating modes | 4 | 4 | None | None |
| Hero status slots / selectable options | 8 / 14 | 8 / 14 | None | None |
| Shell page routes | 10 | 10 | None | None |
| Visible XAML navigation tags | 9 | 9 | None | None |
| Canonical settings keys | 31 | 32 | None | `MasterInfoTileLayout` |
| Data-package setting descriptors | 29 | 30 | None | `MasterInfoTileLayout` |
| Settings rows / event handlers | 58 / 26 | 58 / 26 | None | None |
| Settings binding occurrences / unique paths | 142 / 130 | 142 / 130 | None | None |
| Trigger condition / action templates | 11 / 7 | 11 / 7 | None | None |
| Startup steps | 11 | 11 | None | None |
| Startup / close behavior choices | 3 / 3 | 3 / 3 | None | None |

The ten retained routes are `About`, `Links`, `Logs`, `MasterControl`, `Profiles`, `ProxyNodes`, `Rules`, `Settings`, `Statistics`, and `Triggers`. The route/tag count difference already existed at the baseline. `Connections` also lacked a direct shell route before this pass, so it is retained product debt rather than a refactor regression.

Every retained Settings binding path resolves to a current view-model member, and every retained XAML event handler resolves to code-behind. Trigger CRUD, import/export scopes and format, and all 17 executable tray commands remain represented. This establishes static and automated feature-surface parity, not proof of pixel-perfect visual parity or every external Windows integration. No previously represented tile, route, navigation tag, setting key, or exported setting descriptor was removed.

## Verification

All commands used `CI=true` and `Platform=x64`; none launched the application:

```text
git diff --check
  passed with no whitespace or line-ending warnings

dotnet format ClashSharp/ClashSharp.slnx --verify-no-changes --no-restore
  passed: 0 of 862 files required formatting
  note: the formatter's design-time MSBuild load reports that WindowsAppSDKSelfContained needs an explicit architecture;
        the explicit x64 Release build below is the authoritative WinUI compilation

dotnet build ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore -m:1
  passed: 0 warnings, 0 errors

dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build -m:1
  passed: 1,652 / 1,652

critical concurrency/lifetime subset, repeated 5 times
  passed: 162 / 162 in every iteration (810 total executions)
```

Independent reviews rechecked the exit/fatal/native boundaries, concurrency transactions,
presentation ownership, MVVM directory responsibilities, and the visible functional-parity
surface. Their actionable findings were incorporated before the final verification.

## Explicitly retained mid-refactor work

- `P1-01` remains `In Progress`: the packaged real-application two-instance smoke against the Windows App SDK, proxy, and core state is still required.
- Transparent-proxy preference is persisted, but changing it while Rule/Full mode is active is not yet a verified live settings transaction.
- Canonical atomic transactions for multi-setting import, reset, and clear remain Phase 05 work.
- `MasterHeroStatusLayout` is still not included in the data package, matching the baseline behavior.
- The remaining process-wide service locators are frozen at named compatibility/composition boundaries; completing assembly-level migration of legacy UI services to Application/Infrastructure remains Phase 07 work.
- SQLite storage and several legacy service implementations still live in the WinUI executable assembly. Their contracts and current presentation adapters provide a migration seam, but moving those implementations into Infrastructure is intentionally a later phase.
- SQLite reads and cleanup are moved away from the UI thread and searches/previews are debounced, but the existing synchronous SQLite API cannot interrupt a query that has already entered native execution. Callers can abandon stale results immediately; a truly asynchronous cancellable persistence API remains later infrastructure work.
- `SettingsViewModel`, `MasterControlViewModel`, and their code-behind presentation coordinators remain larger than the desired end-state. This checkpoint establishes explicit boundaries and lifecycle tests; further feature-area decomposition remains later mid-refactor work.
- Several tightly coupled legacy `Service` files still co-locate their implementation, narrow internal contracts, and default factory adapters. The presentation and view-model layers now follow the one-primary-type convention; completing the same mechanical split across the legacy service layer is retained cleanup, not a functional blocker.
- The test project still links selected production sources under `UNIT_TESTS` in addition to referencing the WinUI assembly. Release x64 compilation and source architecture gates therefore remain required alongside unit tests; the suite is not represented as an interactive WinUI smoke.
- Connections navigation and broader unfinished product features remain expected mid-development work.

These items are intentionally not marked complete by this stabilization pass.
