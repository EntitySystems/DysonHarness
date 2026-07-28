# JsonDynamicStructuredLanguageToolchain

Session MCP tool that interprets a strict nested JSON program (`Entry` / `FunctionCall` / `Loop`) and dispatches nested calls only to **existing catalog tools**. Branching uses nested `DysonToolCallResult.IsError` only.

**Source of truth:** C# schema types in [`DysonJsonDynamicToolchainSchema.cs`](../../src/Harness/Harness.Engine/Mcp/DysonJsonDynamicToolchainSchema.cs). Agent-facing guide: [`Resources/Skills/JDSL.md`](../../src/Harness/Harness.Engine/Resources/Skills/JDSL.md) — load with `LoadSkill(name: "JDSL", loadIndexOnly: true)` or composer `/skill-jdsl`.

## Catalog

| Field | Value |
| ----- | ----- |
| Name | `JsonDynamicStructuredLanguageToolchain` |
| Input | `{ "program": object \| string }` (+ harness `stage`) |
| Executor | `DysonWorkspaceToolExecutor` → `DysonJsonDynamicToolchainInterpreter` |
| Result | `DysonJsonDynamicToolchainResult` JSON (`status`, `flow`, `steps`, `finalContent`, `error`) |

## Program schema (strict)

PascalCase wire. Flat `"FunctionCall": "MCP:…"` is a **parse error**.

- `Entry.Arguments` — optional **object** of named locals (arrays rejected)
- `Entry.Actions` — ActionNode: exactly one of nested `FunctionCall` **object** or `Loop` **object**
- `FunctionCall.Function` — `"MCP:ToolName"` or `"ToolName"` (required)
- `FunctionCall.Arguments` — named object only; values may be literals or ref strings
- `OnSuccess` / `OnFailure` / `ContinueWith` — optional ActionNodes
- `Loop.Condition` / `Loop.Action` — required ActionNodes
- `Loop.MaxIterations` — optional int; default **5**; runtime clamp **1–20**; typo `MaxInterations` rejected

### Refs

| Ref | Meaning |
|-----|---------|
| `fromArg:<name>` | `Entry.Arguments[<name>]` |
| `fromResult:$0` | `Content` of the FunctionCall that just completed |
| `fromResult:<jsonPath>` | JSON path into that Content (`a.b.0`) |

### Caps

- Max action nesting depth **8**
- Max nested invocations **50**
- No self-call of this tool
- Nested `Stage` inherits outer call; `CallId` = `{parent}/n{i}`
- Nested `EndsCurrentTurn` stops after that node’s success/failure path (skips `ContinueWith`) and surfaces on the outer result

## Evaluation

```mermaid
flowchart TD
  Model["Model calls tool"] --> Exec["DysonWorkspaceToolExecutor"]
  Exec --> Parse["ParseProgram via schema"]
  Parse --> Interp["DysonJsonDynamicToolchainInterpreter"]
  Interp --> Dispatch["ExecuteAsync nested catalog tools"]
  Interp --> Branch["OnSuccess / OnFailure / ContinueWith / Loop"]
```

- **FunctionCall:** resolve args → dispatch → `IsError` ? `OnFailure` : `OnSuccess` → then `ContinueWith` (unless `EndsCurrentTurn`)
- **Loop:** while Condition is `!IsError` and under `MaxIterations`, run Action; Condition `IsError` exits normally
- **Outer IsError:** parse/cap/unknown-tool/self-call, or unhandled leaf error (no successful `OnFailure` path)

## Flow result (UI)

`flow` mirrors the program tree. Taken nodes have `executed: true`; sibling untaken branches remain with `executed: false` for the flow modal (green vs grey borders). See [UI README](../ui/README.md) tool variants.

## Tests

`DysonJsonDynamicToolchainTests` in `Harness.Tests` — schema reject-flat, flow executed flags, branches, loops, refs, caps.
