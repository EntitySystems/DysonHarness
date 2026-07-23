---
name: csharp
description: >-
  Modern C# / .NET practices for DysonHarness (Harness.Engine): coding
  conventions, null safety, async/TAP, naming, shallow architecture (1–2
  abstraction layers), and Result-pattern APIs. Use when writing, reviewing,
  or refactoring C#, .NET, or Harness.Engine code in this repository.
---

# C# / .NET (DysonHarness)

Apply this checklist when writing or reviewing C# in this repo (`net10.0`, namespace `DysonHarness`). Prefer clarity and shallow design over indirection.

## Architecture (repo rule)

- Prefer concrete types and direct calls over indirection.
- Cap at **1–2 layers of abstraction** for a given concern (e.g. session → provider is fine; session → facade → adapter → gateway → client is not).
- Do not introduce interfaces, wrappers, factories, or DI layers “for testability/future-proofing” unless a second implementation or boundary exists now.
- Avoid over-engineering: no premature generic frameworks, marker interfaces, or deep inheritance trees.

## Language & style

- Use modern language features; avoid outdated constructs.
- Prefer language keywords (`string`, `int`, `nint`) over BCL type names (`System.String`, …).
- Prefer `int` over unsigned types unless the domain requires unsigned.
- Use `var` only when the type is obvious from the right-hand side.
- Prefer string interpolation for short strings; `StringBuilder` for looped/large concatenation; prefer raw string literals (`"""`) over heavy escaping.
- Prefer collection expressions (`[]`) for collection initialization.
- Prefer `new()` / target-typed `new` and object initializers when they clarify construction.
- Prefer `using` declarations over try/finally solely for `Dispose`.
- Prefer `Func<>` / `Action<>` over custom delegate types unless a named delegate adds clarity.
- Catch only exceptions you can handle; prefer specific exception types; do not swallow.
- Prefer LINQ when it improves readability for collection work.
- Prefer primary constructors / `required` / `init` when they reduce boilerplate without hiding intent—do not force on every type.
- Prefer `&&` / `||` over `&` / `|` for boolean logic (short-circuit).

## Null safety

- Keep nullable reference types enabled (`<Nullable>enable</Nullable>`).
- Annotate intent with `T?`; prefer `is null` / `is not null`, `?.`, `??`, `??=`.
- Treat nullability warnings as defects to fix, not suppress casually.

## Async / TAP (library code)

`Harness.Engine` is a class library—write async with library callers in mind.

- Prefer `async`/`await` for I/O; avoid sync-over-async wrappers.
- Avoid `async void` except event handlers; prefer `async Task` / `Task<T>`.
- Async all the way; do not mix blocking `.Result` / `.Wait()` with async.
- Name awaitable-returning methods with `Async` suffix.
- Pass `CancellationToken` on public async APIs.
- Prefer `ConfigureAwait(false)` unless the continuation must resume on a captured context (CA2007).

## Naming

- PascalCase: types, namespaces, public members, constants.
- camelCase: locals, parameters; private/instance fields prefixed with `_`.
- Interfaces start with `I`.
- Meaningful names; avoid cryptic abbreviations.

## .NET 10 / C# 14 (adopt when useful)

- Prefer `field`-backed properties when custom accessors need the backing field.
- Prefer null-conditional assignment (`?.` on LHS) when it clarifies null-guarded sets.
- Use other C# 14 features only when they simplify existing code—not for novelty.

## Result pattern (repo rule)

- Public expected-failure paths return `Result` / `VoidResult` / `ValueResult` per [rules/rules_csharp.md](../../rules/rules_csharp.md).
- Exceptions remain for unexpected programmer bugs (`ArgumentNullException.ThrowIfNull`, etc.).

## DB / EF

- Entity timestamps and EF queries: use `DateTime` (UTC via `DateTime.UtcNow`), never `DateTimeOffset` — see [rules/rules_csharp.md](../../rules/rules_csharp.md).

## Sources

- [.NET Coding Conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- [Identifier names](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/identifier-names)
- [Nullable reference types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/nullable-reference-types)
- [Null safety](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/null-safety/)
- [TAP / async and await](https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/task-asynchronous-programming-model)
- [Async best practices](https://learn.microsoft.com/en-us/archive/msdn-magazine/2013/march/async-await-best-practices-in-asynchronous-programming)
- [Consuming TAP / ConfigureAwait](https://learn.microsoft.com/en-us/dotnet/standard/asynchronous-programming-patterns/consuming-the-task-based-asynchronous-pattern)
- [CA2007](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca2007)
- [What's new in C# 14](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14)
- [.NET 10](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview)
