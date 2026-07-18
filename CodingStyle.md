# ClashSharp Coding Style

ClashSharp targets .NET 10 and C# 14. Repository build configuration is authoritative when this document and executable policy disagree.

## Language and nullability

Nullable analysis is enabled at project level. Do not add redundant per-file `#nullable enable` directives. Public signatures express nullability accurately, validate inputs at trust boundaries, and use explicit result types for expected operational failures.

## Documentation

Document public contracts and non-obvious application interfaces with concise English XML documentation. Private members need documentation only when their invariant, ownership, cancellation, thread-safety, or side-effect behavior is not clear from structure and naming. Do not add volatile author, file-path, or last-modified-date banners; source control is authoritative.

## Structure and naming

Use file-scoped namespaces, braces for every control-flow body, PascalCase public symbols, camelCase locals and parameters, `_camelCase` private fields, `I`-prefixed interfaces, and `Async` suffixes for awaitable methods. A file normally contains one primary type whose name matches the file.

## Dependencies and side effects

Use constructor injection. Presentation code must not resolve application services through static `.Instance` access. Constructors validate immutable arguments only; they do not start tasks, open user data, mutate Windows or mihomo state, or register long-lived handlers. Every background operation has explicit ownership, cancellation, observation, and awaited shutdown.

## Async and errors

Avoid `async void` except platform event handlers. Cancellable I/O accepts a final `CancellationToken`. Never swallow exceptions or expose raw exception text to the UI. Route unexpected asynchronous failures to the application error sink and log only non-sensitive diagnostic data.

## Formatting

Human-authored repository text uses UTF-8, LF, final newlines, and trimmed trailing whitespace; generated lock files retain their tool-produced representation. `dotnet format --verify-no-changes` is the C# formatting contract. Prefer readable modern C# syntax and extract named steps when a LINQ or fluent chain becomes difficult to audit.
