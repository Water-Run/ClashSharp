# `.NET 10`: C# Single-File Coding Style (Prompt)

When writing C# code, you must follow this coding style (targeting rules within a single .cs file only; repository structure, CI, and solution-level constraints are out of scope).

**Runtime / Framework Level:** `.NET 10`.
**Language Level:** `C# 14` (leverage the latest syntax and expressiveness available in the highest C# version).
**Nullable:** `#nullable enable` is mandatory; never silently accept null — whether null is permitted must be explicit in both the type signature and the documentation.
**Style Guiding Principle:** Code is documentation. Apart from XML doc comments, avoid writing any comments; non-XML comments (`//` or `/* */`) are forbidden in principle and are permitted only when a critical "why" cannot be expressed through code structure alone, using minimal wording that must be traceable (e.g., a design-decision ID / issue number / change-reason tag — never output real links).

**XML Doc Comment Coverage (Mandatory — Zero Omissions):** Every namespace, type (`class` / `struct` / `record` / `interface` / `enum` / `delegate`), enum member, constructor, method (including extension methods), property, field (including `private`), event, operator, and explicit interface implementation member in the file must have an XML doc comment. "It's private so it doesn't matter" is never a valid reason to omit documentation; even internal details must carry a concise `<summary>` that clearly delineates their responsibility boundary. **Any member lacking a complete XML doc comment is a build-breaking violation of this specification.** Automated review must treat a missing or incomplete doc comment with the same severity as a compilation error.

**Required Doc Comment Content (Constrained by Member Kind):**

Namespace / File Header: Must provide file-level information (file responsibility, author, last-modified date) and the semantic boundary of the namespace. Because C# has no independent "file-level XML doc" syntax, file-header information is carried in the namespace's XML doc. Author is always `WaterRun`; last-modified date is written as `2026-01-27` (example — use the actual current date), with the rule that "every modification to this file must update the date to today and keep the author as `WaterRun`".

Type (`class` / `struct` / `record` / `interface` / `enum` / `delegate`): Must include `<summary>` (one sentence: "what it is + purpose"), and a `<remarks>` section stating key invariants, thread-safety conclusion (default: not thread-safe), side-effect boundary, and usage constraints. If the type is an abstraction layer (interface / base class), describe the contract rather than implementation details.

Method / Constructor (including `async`): Must include `<summary>`, a `<param>` for every parameter (semantics, unit/range, whether null is allowed, boundary conditions), `<returns>` (return-value semantics; omit for `void`), and an `<exception>` for every foreseeable exception (exception type + trigger condition). Generic methods must include a `<typeparam>` for each type parameter. Async methods must state cancellation semantics (`CancellationToken` behavior) and completion semantics (idempotency, reentrancy, I/O presence) in `<remarks>`.

Property: Must include `<summary>` and `<value>`, describing read/write semantics, default value, whether lazily computed, thread safety, and side effects. If the setter validates or throws, the exception must be declared in `<exception>`.

Field / Constant: Must include `<summary>`, stating the field's purpose, lifetime, whether read-only, whether cached, and whether thread-related. A `const` must state its semantic meaning and unit/range.

Event: Must include `<summary>` and `<remarks>`, stating when it fires, thread context, whether reentrancy is possible, and subscribe/unsubscribe considerations.

Operator / Conversion: Must include `<summary>`, parameter and return semantics, possible exceptions, and invariant impact.

Examples and References: Public APIs should include `<example>`. When referencing other symbols, use `<see cref="..."/>` / `<seealso/>`; avoid mentioning type names as plain text so that tooling can resolve them.

**XML Doc Comment Templates (Must Follow This Style — Content in English, Terse, Auditable, No Filler)**

File header (carrying file-level information):

```csharp
/*
 * Application Configuration Management    -- one-line summary
 * Provides a unified access point for user settings and hard-coded constants, with persistence support  -- extended description
 * 
 * @author: WaterRun   -- author
 * @file: Static/AppConfig.cs  -- file path relative to source root
 * @date: 2026-01-27  -- last-modified date, updated to today on every change
 */
```

Type:

```csharp
/// <summary>One-sentence description of the type's purpose.</summary>
/// <remarks>
/// Invariants: …
/// Thread safety: …
/// Side effects: …
/// </remarks>
```

Method / Constructor (including async):

```csharp
/// <summary>Action + purpose.</summary>
/// <param name="x">Semantics, range, whether null is allowed, boundary conditions.</param>
/// <typeparam name="T">Type-parameter semantics (if applicable).</typeparam>
/// <returns>Return-value semantics (if applicable).</returns>
/// <exception cref="ArgumentNullException">Trigger condition.</exception>
/// <exception cref="ArgumentOutOfRangeException">Trigger condition.</exception>
/// <exception cref="InvalidOperationException">Trigger condition.</exception>
/// <remarks>
/// Cancellation semantics: … (if applicable)
/// Thread / reentrancy: … (if applicable)
/// </remarks>
```

Property:

```csharp
/// <summary>Property purpose.</summary>
/// <value>Value semantics, range, default, whether null is allowed.</value>
/// <exception cref="ArgumentException">Setter validation trigger condition (if applicable).</exception>
```

Enum and Enum Members:

```csharp
/// <summary>Overall meaning and usage scenario of the enum.</summary>
public enum X
{
    /// <summary>Meaning of this enum value.</summary>
    Value = 0,
}
```

**Naming Conventions (Mandatory):**

Public types and public members use `PascalCase`. Parameters and local variables use `camelCase`. Private fields use `_camelCase`. Interfaces are prefixed with `I` (`IUserRepository`). Exception types are suffixed with `Exception`. Attribute types are suffixed with `Attribute`. Async methods are suffixed with `Async`. Boolean names use readable predicates (`isEnabled` / `hasValue` / `canRetry`). Collections use plural names (`items` / `users` / `entries`). Avoid meaningless abbreviations and Hungarian notation.

The file name must match the file's primary public type name. In principle, a single `.cs` file contains only one public top-level type; all other helper types must be `internal` or `file`-local and tightly coupled to the primary type.

**Layout and Readability (Mandatory):**

File-scoped namespaces are required. `using` directives are placed at the top of the file in a stable sort order. Control flow must prefer guard-clause early returns to reduce nesting. All `if` / `for` / `foreach` / `while` / `switch` branches must use braces — never rely on indentation to guess logic. Expression splitting follows semantic boundaries; avoid excessively long call chains that hinder auditing. Use an explicit type declaration when the type carries business semantics; use `var` when the right-hand side makes the type obvious and readability is not impaired.

**Modern C# Syntax Requirements (Prefer When Not Degrading Maintainability):**

Prefer pattern matching (`is` / `not`, `switch` expressions) to express branching intent. Prefer target-typed `new`, collection expressions (`[..]`), `required` members, `init`-only properties, and `record` types (for value semantics and immutable data) to increase expressiveness. Prefer primary constructors (suitable for dependency injection and pure data carriers) but never at the cost of invariant validation or documentation completeness — if a primary constructor cannot enforce invariants or host complete XML doc comments for each parameter, use a traditional constructor body instead. Use the `file` modifier for file-local types to delimit implementation detail boundaries. Use `throw` expressions and `ArgumentNullException.ThrowIfNull` to express contract failures where applicable. Async code must use `Task` / `Task<T>`; `async void` is forbidden except in event handlers. Cancellable operations must accept a `CancellationToken` (typically as the last parameter) and define cancellation semantics in their documentation. Never silently swallow exceptions to "keep the flow going" — errors must be expressed through exceptions or explicit result types.

Prefer LINQ for collection querying, projection, filtering, aggregation, and transformation wherever it yields clearer intent than imperative loops. Favor method-syntax LINQ (`.Where(…).Select(…)`) for simple pipelines and query-syntax LINQ (`from … where … select …`) when multiple `let`, `join`, or `group` clauses improve readability. Avoid materializing intermediate collections unnecessarily — work with `IEnumerable<T>` lazily when possible and materialize (`.ToList()`, `.ToArray()`, `.ToFrozenSet()`) only at consumption boundaries. Every LINQ pipeline must remain auditable: if a chain exceeds roughly five operators, extract named local functions or intermediate variables to preserve clarity.

**Contracts and Exceptions (Mandatory):**

All non-nullable reference-type parameters must be validated at runtime. Range and state constraints must be expressed through semantically clear exceptions and declared with `<exception>` trigger conditions. Exceptions must never serve as routine control flow. For foreseeable failures, prefer `TryXxx` patterns or explicit result types. Never leak internal implementation details into public exception messages. Logs must never record sensitive information (keys, tokens, PII, payment data).

**Output Requirements:** When generating code you must strictly follow the specification above. When explaining the specification, remain calm and objective and do not output engineering-level content unrelated to this specification. Do not organize the final specification text with bullet points or list items (though this prompt itself may exist as a constraint description). Generated code should make good use of LINQ. All doc-comment content must be written in English.
