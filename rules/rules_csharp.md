# C# rules

## Result pattern

For C# in this repository:

- Prefer `Result<TValue, TError>`, `VoidResult<TError>`, and `ValueResult<TValue>` (namespace `DysonHarness`) over throwing for expected failures.
- Every **public** method that can fail or return a value must return an appropriate Result type (or `Task<...>` of one for async).
- Use `VoidResult<TError>` for side-effect-only public APIs; `Result<TValue, TError>` when both value and error matter; `ValueResult<TValue>` when only success-value vs error-flag is needed.
- Do not use exceptions for ordinary control flow; reserve exceptions for truly unexpected bugs.

Types live under [`src/Harness/Harness.Engine/`](../src/Harness/Harness.Engine/).

## Modern C# practice

Follow [`skills/csharp/SKILL.md`](../skills/csharp/SKILL.md) for modern C# / .NET conventions (language features, null safety, async/TAP, naming, .NET 10 / C# 14).

## Abstraction cap

- Prefer concrete types and direct calls over indirection.
- Cap at **1–2 layers of abstraction** for a given concern unless necessity is demonstrated (e.g. a real second implementation or boundary exists now).
- Do not add interfaces, wrappers, factories, or DI layers “for testability/future-proofing” without that necessity.
