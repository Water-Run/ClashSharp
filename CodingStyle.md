# ClashSharp Coding Style

ClashSharp targets .NET 10 and C# 14. Repository build configuration is authoritative when this document and executable policy disagree.

## Language and nullability

Nullable analysis is enabled at project level. Do not add redundant per-file `#nullable enable` directives. Public signatures express nullability accurately, validate inputs at trust boundaries, and use explicit result types for expected operational failures.

## Documentation

Every production project emits an XML documentation file and treats `CS1591` as an error. Every public type, constructor, method, event, field, property, enum value, record parameter, and WinUI dependency-property identifier therefore has concise English XML documentation. Parameter tags describe the actual parameter contract and remain synchronized with signature changes; malformed comments are build failures, not optional prose warnings.

Document internal and private boundaries whenever their invariant, authority, ownership, cancellation, thread-safety, security decision, native interop contract, replay behavior, or side effect is not completely clear from structure and naming. Prefer a precise statement of information that callers or maintainers must preserve; do not narrate syntax or add placeholder comments merely to satisfy a counter. Do not add volatile author, file-path, or last-modified-date banners; source control is authoritative.

## Structure and naming

Use file-scoped namespaces, braces for every control-flow body, PascalCase public symbols, camelCase locals and parameters, `_camelCase` private fields, `I`-prefixed interfaces, and `Async` suffixes for awaitable methods. A file normally contains one primary type whose name matches the file.

## Dependencies and side effects

Use constructor injection. Presentation code must not resolve application services through static `.Instance` access. Constructors validate immutable arguments only; they do not start tasks, open user data, mutate Windows or mihomo state, or register long-lived handlers. Every background operation has explicit ownership, cancellation, observation, and awaited shutdown.

The main application follows MVVM dependency direction: `Core` owns domain contracts, `Application` owns use cases and ports, `Infrastructure` implements ports, ViewModels depend on application-facing abstractions, and WinUI views/code-behind only translate platform interaction into ViewModel commands or presentation services. Core, Application, Infrastructure, and ViewModels do not reference WinUI. Composition roots are the only locations allowed to assemble concrete adapters.

## Async and errors

Avoid `async void` except platform event handlers. Cancellable I/O accepts a final `CancellationToken`. Never swallow exceptions or expose raw exception text to the UI. Route unexpected asynchronous failures to the application error sink and log only non-sensitive diagnostic data.

## Native interop

Every `DllImport` or `LibraryImport` of a Windows system library is immediately constrained with `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`. Keep native declarations private or behind a narrowly scoped adapter, preserve the native error contract explicitly, and represent owned handles with `SafeHandle` implementations. Do not let raw handles, mutable native buffers, or platform error codes cross application or presentation boundaries.

## PowerShell

Tracked functions provide adjacent comment-based help with a concrete `.SYNOPSIS`, `.DESCRIPTION`, and one `.PARAMETER` entry for every parameter. Prefer advanced-script parameter validation, `-LiteralPath`, terminating errors, and exact allowlists at packaging or deletion boundaries. Parse every tracked `.ps1` and `.psm1` with the PowerShell AST in verification; generated AppPackages and sandbox run output are not source.

## Formatting

Human-authored repository text uses UTF-8, LF, final newlines, and trimmed trailing whitespace; generated lock files retain their tool-produced representation. `dotnet format --verify-no-changes` is the C# formatting contract. Prefer readable modern C# syntax and extract named steps when a LINQ or fluent chain becomes difficult to audit.

## Modern C# and LINQ

Use C# 14 features when they make an invariant more explicit: collection expressions for fixed snapshots, records for immutable value contracts, property/list patterns and switch expressions for closed state machines, `using`/`await using` declarations for deterministic ownership, and `ArgumentNullException.ThrowIfNull` or equivalent boundary guards. Do not introduce a feature only to shorten code when it obscures authority, lifetime, or diagnostics.

Prefer LINQ for side-effect-free filtering, projection, grouping, set construction, and exact cardinality checks. Prefer an explicit loop when order contains mutation cut points, cancellation must be checked per element, asynchronous work is intentionally sequential, early exit carries a diagnostic, or intermediate state is security-sensitive. Never use deferred enumeration across a disposed resource or mutable authority boundary; materialize an immutable snapshot first.

## WinUI 3 presentation

Use semantic controls instead of pointer-only containers. Every glyph-only action exposes a localized `AutomationProperties.Name` and tooltip and retains native keyboard/focus behavior. Bind visible text and state through ViewModels; keep code-behind limited to XAML lifetime, dialogs, navigation, focus, drag/drop, and other WinUI-only mechanics.

Prefer `x:Bind` for stable, compile-time-known page properties when conversion can be verified without changing lifetime or template semantics. Keep `{Binding}` for intentionally dynamic `DataContext` composition and template item contexts; do not perform a blanket migration. Page or window operations own linked cancellation sources, cancel and dispose them on unload/close, marshal only UI work through the DispatcherQueue, and route unexpected `async void` event failures through the application error boundary.
