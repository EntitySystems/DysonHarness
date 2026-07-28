# JDSL — JsonDynamicStructuredLanguageToolchain

Agent usage guide for batching multi-step MCP flows in **one** tool call.

Full engine spec: [`docs/engine/json-dynamic-toolchain.md`](../../../../../docs/engine/json-dynamic-toolchain.md).

## What it is for

Call `JsonDynamicStructuredLanguageToolchain` with a JSON **program** that chains existing session catalog tools (`WebFetch`, `CompleteTask`, `WaitForSeconds`, file tools, …). Success/failure branching uses each nested result’s **`IsError`** only (`OnSuccess` / `OnFailure`), plus optional `ContinueWith` and `Loop`.

## Strict shape (nested only)

PascalCase wire. Flat `"FunctionCall": "MCP:…"` strings are **rejected**.

```json
{
  "Entry": {
    "Arguments": { "url": "https://example.com" },
    "Actions": {
      "FunctionCall": {
        "Function": "MCP:WebFetch",
        "Arguments": {
          "url": "fromArg:url",
          "fullHtml": false,
          "summarizePrompt": "One-paragraph page summary"
        },
        "OnSuccess": {
          "FunctionCall": {
            "Function": "MCP:CompleteTask",
            "Arguments": { "summary": "fromResult:$0" }
          }
        },
        "OnFailure": {
          "FunctionCall": {
            "Function": "MCP:CompleteTask",
            "Arguments": { "summary": "fromResult:$0" }
          }
        }
      }
    }
  }
}
```

MCP input:

```json
{ "program": { /* Entry … */ }, "stage": 0 }
```

`program` may also be a JSON **string** of the same object.

### Nodes

| Kind | Required | Notes |
|------|----------|--------|
| `FunctionCall` | nested **object** with `Function` | Optional `Arguments` (object), `OnSuccess`, `OnFailure`, `ContinueWith` |
| `Loop` | nested **object** with `Condition` + `Action` | Optional `MaxIterations` (default 5, clamp 1–20). Typo `MaxInterations` rejected |

Exactly one of `FunctionCall` or `Loop` per action node. No sibling keys at the action level.

## Arguments and refs

Named MCP parameters only (no positional arrays).

| Ref | Meaning |
|-----|---------|
| `fromArg:<name>` | `Entry.Arguments[<name>]` |
| `fromResult:$0` | `Content` of the FunctionCall that just finished (works in both `OnSuccess` and `OnFailure`) |
| `fromResult:<jsonPath>` | Path into that Content when it is JSON (`a.b.0`) |

Unresolved refs make that FunctionCall fail (`IsError`); the program fails only if unhandled / caps / parse.

## Branching and Loop

- After a FunctionCall: `IsError` ? `OnFailure` : `OnSuccess`, then `ContinueWith` (unless a nested result set `EndsCurrentTurn`).
- Outer toolchain `IsError` only for parse/cap/unknown-tool/self-call, or a leaf error with no `OnFailure` that handled it.
- **Loop:** while `Condition` is `!IsError` and under `MaxIterations`, run `Action`. Condition `IsError` exits the loop normally (not a program failure).

## Caps and catalog rules

- Max action nesting depth **8**
- Max nested tool invocations **50**
- `MaxIterations` **1–20** (missing ⇒ **5**)
- Catalog-only: `MCP:ToolName` or `ToolName` must exist in the session catalog
- **No self-call** of `JsonDynamicStructuredLanguageToolchain`
- Nested `stage` inherits the outer call; synthetic call ids `{parent}/n{i}`

## Corrected example (real tools)

WebFetch defaults to a **summarized** body (`fullHtml` omitted/false). Forward that text with `fromResult:$0`:

```json
{
  "Entry": {
    "Actions": {
      "FunctionCall": {
        "Function": "MCP:WebFetch",
        "Arguments": {
          "url": "https://example.com",
          "fullHtml": false,
          "summarizePrompt": "One-paragraph page summary"
        },
        "OnSuccess": {
          "FunctionCall": {
            "Function": "MCP:CompleteTask",
            "Arguments": { "summary": "fromResult:$0" }
          }
        },
        "OnFailure": {
          "FunctionCall": {
            "Function": "MCP:CompleteTask",
            "Arguments": { "summary": "fromResult:$0" }
          }
        }
      }
    }
  }
}
```

Loop sketch:

```json
{
  "Entry": {
    "Actions": {
      "Loop": {
        "Condition": {
          "FunctionCall": {
            "Function": "MCP:WaitForSeconds",
            "Arguments": { "seconds": 1 }
          }
        },
        "Action": {
          "FunctionCall": {
            "Function": "MCP:GetDateTime",
            "Arguments": { "timezone": "utc" }
          }
        },
        "MaxIterations": 3
      }
    }
  }
}
```

## UI

Expanding the tool row shows a short summary; **View flow** opens a modal with the program-shaped tree — green border = taken (`executed: true`), grey = ignored branches.
