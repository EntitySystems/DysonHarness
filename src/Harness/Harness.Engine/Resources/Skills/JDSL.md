# JDSL — JsonDynamicStructuredLanguageToolchain

Agent usage guide for batching multi-step MCP flows in **one** tool call.

Full engine spec: [`docs/engine/json-dynamic-toolchain.md`](../../../../../docs/engine/json-dynamic-toolchain.md).

## What it is for

Call `JsonDynamicStructuredLanguageToolchain` with a JSON **program** that chains existing session catalog tools (`WebFetch`, `CompleteTask`, `WaitForSeconds`, file tools, …). Success/failure branching uses each nested result’s **`IsError`** only (`OnSuccess` / `OnFailure`), plus optional `ContinueWith` and `Loop`. Use the JDSL-only intrinsic `JDSL:ReturnOutput` when the caller model should receive a specific value as the tool result.

## Use cases

Prefer JDSL when several catalog calls should run in **one** turn with branching or bounded polling. Prefer ordinary sequential tool calls when you need model judgment between steps.

**Loop note:** a loop exits when `Condition` returns `IsError` (normal exit, not program failure). For “wait until ready,” use a condition tool that **fails while not ready** (e.g. `BrowserWaitForSelector`, `Grep` with no match if that yields error, `ReadFile` of a sentinel), often paired with `WaitForSeconds` in `Action` — not an infinite `WaitForSeconds`-only condition.

- **Try until one succeeds** — nested `ShellExecute` (or similar) with `OnFailure` → next attempt; optional final `OnFailure` → `CompleteTask` / report.

```json
{
  "FunctionCall": {
    "Function": "MCP:ShellExecute",
    "Arguments": { "command": "cmd-a" },
    "OnFailure": {
      "FunctionCall": {
        "Function": "MCP:ShellExecute",
        "Arguments": { "command": "cmd-b" },
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

- **Poll until ready** — `Loop` whose `Condition` fails until ready; `Action` does `WaitForSeconds` (and/or a light probe); `MaxIterations` as the timeout budget.

```json
{
  "Loop": {
    "Condition": {
      "FunctionCall": {
        "Function": "MCP:BrowserWaitForSelector",
        "Arguments": { "selector": "#ready", "timeoutMs": 500 }
      }
    },
    "Action": {
      "FunctionCall": {
        "Function": "MCP:WaitForSeconds",
        "Arguments": { "seconds": 1 }
      }
    },
    "MaxIterations": 10
  }
}
```

- **Local then online** — `Grep` / `ReadFile` / `ListDirectory` → `OnFailure` → `FreeSearch` / `WebFetch` / `SearchWithSynthesis`.
- **Fallback file paths** — try `ReadFile` on primary path → `OnFailure` alternate path(s) (config, README, lockfile variants).
- **Create-if-missing then write** — `ReadFile` / `ListDirectory` fails → `CreateDirectory` / `CreateFile` → `ContinueWith` `WriteFile`.
- **Navigate, wait, extract (browser)** — `BrowserNavigate` → `OnSuccess` `BrowserWaitForSelector` → `ContinueWith` `BrowserGetHtml` / `BrowserTakeScreenshot`.
- **Long-running shell: start then wait** — Prefer `StartLongRunningShell` → `WaitForLongRunningShellCompletion` (required `timeoutMs`) over poll / `WaitForSeconds` loops. Bounded `Loop` probes (`ReadLongRunningShellTail` + `WaitForSeconds`) are still ok when waiting for a log marker rather than process exit.
- **Research with source fallback** — `FreeSearch` → `OnFailure` `FreeSearchAdvanced` → `OnFailure` `WebFetch` a known docs URL; forward `fromResult:$0` into `CompleteTask`.
- **Return a value to the caller** — fetch / compute, then `JDSL:ReturnOutput` with `output` (literal or `fromResult:$0`). Stops the program; UI keeps the full flow envelope; the model transcript sees only that output.

```json
{
  "FunctionCall": {
    "Function": "WebFetch",
    "Arguments": { "url": "https://example.com", "summarize": true },
    "OnSuccess": {
      "FunctionCall": {
        "Function": "JDSL:ReturnOutput",
        "Arguments": { "output": "fromResult:$0" }
      }
    }
  }
}
```

- **Build / test then branch** — `ShellExecute` build or test → `OnSuccess` mark todo / `CompleteTask`; `OnFailure` `Grep` logs or `ContinueWork`-style follow-up (catalog tools only).
- **Subagent fire-and-collect** — `StartSubagent` → `ContinueWith` `WaitForSubagent` → `OnSuccess` `InspectSubagentLog` / `SubmitSubagentReport` handling via refs where useful.

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
| `FunctionCall` | nested **object** with `Function` | Optional `Arguments` (object), `OnSuccess`, `OnFailure`, `ContinueWith`. `Function` may be catalog `MCP:ToolName` / `ToolName`, or intrinsic `JDSL:ReturnOutput` |
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

### `JDSL:ReturnOutput`

JDSL-only (not in the MCP catalog). `Function` must be exactly `JDSL:ReturnOutput` — `MCP:ReturnOutput` and bare `ReturnOutput` fail as unknown tools.

- Required `Arguments.output` (literal or ref). Non-string values are serialized to text.
- Success: sets program `finalContent` + `returned: true`, skips that node’s children, and stops the program (no further ancestor `ContinueWith` / loop iterations). Does not count toward the nested MCP invocation cap.
- Persist/UI `Content` stays the full envelope; the caller-model transcript receives only `finalContent`.

## Branching and Loop

- After a FunctionCall: `IsError` ? `OnFailure` : `OnSuccess`, then `ContinueWith` (unless a nested result set `EndsCurrentTurn`, or `JDSL:ReturnOutput` already returned).
- Outer toolchain `IsError` only for parse/cap/unknown-tool/self-call, or a leaf error with no `OnFailure` that handled it.
- **Loop:** while `Condition` is `!IsError` and under `MaxIterations`, run `Action`. Condition `IsError` exits the loop normally (not a program failure).

## Caps and catalog rules

- Max action nesting depth **8**
- Max nested tool invocations **50** (`JDSL:ReturnOutput` does not increment)
- `MaxIterations` **1–20** (missing ⇒ **5**)
- Catalog-only for MCP tools: `MCP:ToolName` or `ToolName` must exist in the session catalog; plus intrinsic `JDSL:ReturnOutput`
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
