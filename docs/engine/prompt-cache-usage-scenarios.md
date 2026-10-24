# Prompt-cache usage: before and after plan 151

Simulated quota-relevant tokens for one hierarchy: root Meta Agent, Meta Agent Drones, and classic worker Drones those meta drones spawn. Three columns:

- **old double-catalog** — tool schemas in `tools[]` and again inside the system text (that duplicate is already gone on this checkout).
- **current** — this checkout: `tools[]` only, tool rounds collapsed when the turn is replayed, optimizer on every prompt once the turn count sticks, rename mandate only while the user turn is in flight.
- **plan 151** — per-round replay without previous-turn reasoning, optimizer in batches, rename mandate kept on that same user message. Not built here; this file only models it.

## Cost index

Not a bill. Weights are a comparable Anthropic-style index on quota-relevant input tokens:

| bucket | weight | what it is |
|---|---:|---|
| cache read | 0.1 | prefix tokens byte-identical to this agent's previous request |
| cache write | 1.25 | prefix tokens that changed (the miss that replaces the old tail, up to the old length) |
| uncached input | 1.0 | new suffix past the previous request's length |

Gross input = cache read + cache write + uncached. That is what the usage panel calls "in" (cached tokens stay inside the gross). The first request of every agent is a full cache write. Each agent has its own cache. A hit requires the byte prefix to match **that** agent's previous request. CLIProxy is assumed to cache when that prefix is stable; there is no "proxy does not cache" column.

Index = `0.1 * read + 1.25 * write + 1.0 * uncached`. A pure append (in-flight tool hop, or a plan-151 next prompt with no reasoning) is read on the whole previous request and uncached on the new hop only. A mid-prompt edit is a write on the replaced tail.

## How the bytes were measured

Tokenizer: `DysonTiktokenTokenCounter` (`src/Harness/Harness.Engine/Context/DysonTiktokenTokenCounter.cs`), `cl100k_base`. One-shot measurement, not a live session trace.

Catalogs from `DysonSessionToolsetBuilder.Build` (`src/Harness/Harness.Engine/Mcp/DysonSessionToolsetBuilder.cs`) with `BrowserControl = DysonNullBrowserControl` (non-null, so browser tools stay), `AvailableShells = DysonShell.DefaultConfiguredShellsForCurrentPlatform()` (Windows: Pwsh, PowerShell, Cmd), `ImageGenerationProvider` null (`GenerateImage` removed), voice off. `tools[]` bytes are `OpenAiCompatibleHttp.BuildToolsArray(...).ToJsonString()` (`src/Harness/Harness.Engine/Providers/OpenAi/OpenAiCompatibleHttp.cs`).

- Root Meta Agent: depth 0. `ApplyModeCatalog` → `DysonMetaAgentTools.ApplyAllowlist`. **61** tools, **11,574** tokens.
- Meta Agent Drone: depth 1 (`DysonAgentSession.ComputeDepth`), `omitRootTaskCompletionTools: true`, then `ApplyDroneAllowlist` (drops `AskQuestionFromParent` / `PromptUserDialogFromParent`, adds plan and review tools). **78** tools, **15,767** tokens.
- Classic worker: mode `Drone`, depth 2 (spawned by the meta drone, not the root). `ConfigureInterAgentTools(2)` drops `AskQuestion`, `PromptUserDialog`, both FromParent ask/dialog tools, and `SubscribeToLongRunningShellCompletion`. Child gate drops `CompleteTask` / `ConfirmTaskComplete` / `ContinueWork` and keeps `SubmitSubagentReport`. No meta allowlist, so the rest of `CreateDefault` stays. **74** tools, **14,661** tokens.

System text tokens are the mode directive (`DysonAgentSystemPrompts.ForMode` / `WithBrowser` — the browser sentence is root Meta Agent only), the `BuildSystemText` reply-format line (`OpenAiCacheFriendlyTranscriptBuilder`), and the work-root open-rules block (`DysonOpenRules.BuildSystemPromptBlockAsync`, **2,607** tokens: Root `AGENTS.md` plus AutoInclude rules). Models catalog and the git-status block are **not** included; they would add the same stable tokens to every column and would not change plan-151 deltas.

| role | mode | depth | tools | `tools[]` tokens | system text tokens | cold prefix |
|---|---|---:|---:|---:|---:|---:|
| root Meta Agent | Meta Agent | 0 | 61 | 11,574 | 7,379 | 18,953 |
| Meta Agent Drone | Meta Agent Drone | 1 | 78 | 15,767 | 5,590 | 21,357 |
| classic worker Drone | Drone | 2 | 74 | 14,661 | 4,568 | 19,229 |

Cold prefix = system text + `tools[]`. It is cache-written on that agent's first call and cache-read after, until something in it changes. Children do not share it.

The old column adds a second copy of that role's `tools[]` token count into the system text. `FormatToolsForPrompt` is gone from this checkout, so this is an **estimate**: duplicate size = measured `tools[]` tokens, not a recovered copy of the old prose.

### Thresholds the simulator actually uses

- Optimizer today (`DysonContextOptimizer.ShouldOptimize`): `turns.Count >= 10` **or** unoptimized eligible text outside the keep window `> 10,000`. `Optimize` compacts every not-yet-optimized turn in `[0, count - 2)`. The count check stays true forever, so after it trips, every later prompt compacts the one turn that just left the window.
- Plan 151: delete the sticky count check. Trigger when the number of non-excluded, not-yet-optimized turns outside that same window is `>= 10`, **or** their eligible text is over 10,000. One pass still compacts everything currently outside the window. `OptimizeContextIfNeeded` runs at the start of `PromptWithTurnAsync`, **before** `AddTurn` (`OpenAiCompatibleAgentSession`), so the new turn is not in the count yet.
- Eligible text follows `BuildUnoptimizedEligibleText`: tool name, arguments JSON, and `FormatResponseLog`, not the wire JSON. This sim uses the measured per-round eligible size below.
- Rename mandate (`DysonSessionInitialization.RenameSessionReviewMandate`, **53** tokens) is appended by `FormatTurnUserContent` only while `incompleteCurrent` is true, on chrome-skipped slots 1, 9, 17, …. Plan 151 removes that bail. Plan-mode `PlanFirstTurnMandate` (**79** tokens) is the same bug; these scenarios are not Plan mode, so it does not fire.
- DropTurnContext (`DysonDropContextFlow`): no plan-151 code change. Scenario B forces one extra call at user turn 16, then marks the oldest 6 turns excluded. The real inject also needs the session over its max-target token cap (`EvaluateInject`); that cap is not modeled. Keep-4 and the 5-user-turn throttle are why a single inject is the right shape.
- Responses reasoning (`AppendToolRoundResponses`) is on the in-flight hop only. Plan 151 does not store it. Scenario G is the only Responses run. A–F are Completions-shaped (no reasoning items).

### Repeated sample (estimated text, measured token counts)

Every tool round is one `ReadFile` call plus a 672-character result. Wire tokens **285**. Optimizer-eligible tokens **186**. Compact line **48** plus a **25**-token harness prefix once per compacted turn. Assistant reply **24**. Root user message **35** (`[turnId]` + a 13-token task). A meta drone's stored first instruction (report mandate + drone mandate + `useWorktree is false` line + task) is **756** with the turn header. A coordinated worker's first instruction is **669**. Later user turns are **35**. Those mandates are stored on `Instruction` by `BuildChildFirstPrompt`, so they do not fall off. A user turn is `tool rounds + 1` model calls: the extra call is the closing assistant hop with no tools. In-flight hops are append-only on every column.

## Scenarios

### A. Root only, short

4 user turns, 3 tool rounds. No children. Under the optimizer threshold.

| | old double-catalog | current | plan 151 | plan 151 − current | current − old |
|---|---:|---:|---:|---:|---:|
| calls | 16 | 16 | 16 | +0 | +0 |
| gross input | 517,980 | 332,796 | 333,432 | +636 | -185,184 |
| cache read | 481,168 | 307,558 | 310,794 | +3,236 | -173,610 |
| cache write | 33,268 | 21,694 | 19,041 | -2,653 | -11,574 |
| uncached | 3,544 | 3,544 | 3,597 | +53 | +0 |
| cost index | 93,245.8 | 61,417.3 | 58,477.7 | -2,939.7 | -31,828.5 |

Plan 151 changes the index by -2,939.7 (4.8% saved vs current). Cold prefixes (first call of each agent) are still the largest single write. Plan 151 only cheapens the later hops, once a prefix exists to reuse.

### B. Root only, long

24 user turns, 6 tool rounds. Optimizer runs. One scripted DropTurnContext at the start of user turn 16 (oldest 6 turns excluded).

| | old double-catalog | current | plan 151 | plan 151 − current | current − old |
|---|---:|---:|---:|---:|---:|
| calls | 169 | 169 | 169 | +0 | +0 |
| gross input | 6,594,721 | 4,638,715 | 5,165,529 | +526,814 | -1,956,006 |
| cache read | 6,446,751 | 4,502,319 | 5,078,004 | +575,685 | -1,944,432 |
| cache write | 106,393 | 94,819 | 44,900 | -49,919 | -11,574 |
| uncached | 41,577 | 41,577 | 42,625 | +1,048 | +0 |
| cost index | 819,243.4 | 610,332.7 | 606,550.4 | -3,782.2 | -208,910.7 |

Plan 151 changes the index by -3,782.2 (0.6% saved vs current). Current optimizer passes: 14, turns compacted: 21. Plan 151 passes: 2, turns compacted: 18. After the threshold, current rewrites one older turn on every prompt. Plan 151 waits for a full outside-window batch or the 10,000-token cap, then compacts everything outside the keep-2 window in one pass. Fewer passes means more full tool JSON stays in history: gross input and cache read go up, cache write goes down, and the two nearly cancel. On the current index, cache read at weight 0.1 is 450,231.9 and cache write at weight 1.25 is 118,523.8. Read is the larger term, so the extra uncompacted history eats most of the write savings. The scripted DropTurnContext is the same on every column: one extra full-size call, then a hard break at the first excluded turn (the oldest turn). That cliff is shared, which is why the net index move stays small.

### C. Root plus two meta drones

Root: 8 user turns, 2 tool rounds. Each of two meta drones: 6 user turns, 4 tool rounds. No workers.

| | old double-catalog | current | plan 151 | plan 151 − current | current − old |
|---|---:|---:|---:|---:|---:|
| calls | 84 | 84 | 84 | +0 | +0 |
| gross input | 3,280,703 | 2,056,907 | 2,060,670 | +3,763 | -1,223,796 |
| cache read | 3,138,042 | 1,957,354 | 1,978,054 | +20,700 | -1,180,688 |
| cache write | 123,577 | 80,469 | 63,373 | -17,096 | -43,108 |
| uncached | 19,084 | 19,084 | 19,243 | +159 | +0 |
| cost index | 487,359.5 | 315,405.7 | 296,264.7 | -19,141.0 | -171,953.8 |

Plan 151 changes the index by -19,141.0 (6.1% saved vs current). Cold prefixes (first call of each agent) are still the largest single write. Plan 151 only cheapens the later hops, once a prefix exists to reuse.

### D. Full tree, light

Root: 6 turns × 2 rounds. One meta drone: 5 turns × 3 rounds. That drone spawns 3 workers. Each worker: 4 turns × 8 rounds.

| | old double-catalog | current | plan 151 | plan 151 − current | current − old |
|---|---:|---:|---:|---:|---:|
| calls | 146 | 146 | 146 | +0 | +0 |
| gross input | 5,622,473 | 3,515,413 | 3,521,349 | +5,936 | -2,107,060 |
| cache read | 5,384,384 | 3,348,648 | 3,384,172 | +35,524 | -2,035,736 |
| cache write | 202,237 | 130,913 | 101,060 | -29,853 | -71,324 |
| uncached | 35,852 | 35,852 | 36,117 | +265 | +0 |
| cost index | 827,086.7 | 534,358.1 | 500,859.2 | -33,498.9 | -292,728.6 |

Plan 151 changes the index by -33,498.9 (6.3% saved vs current). Cold prefixes (first call of each agent) are still the largest single write. Plan 151 only cheapens the later hops, once a prefix exists to reuse. Worker catalogs are not shared with the meta drone or the root. A worker cold prefix is 19,229 tokens before the first user message.

### E. Full tree, heavy

Root: 12 turns × 4 rounds. Two meta drones: 8 turns × 5 rounds each. Each meta drone spawns 4 workers (8 workers). Each worker: 6 turns × 12 rounds.

| | old double-catalog | current | plan 151 | plan 151 − current | current − old |
|---|---:|---:|---:|---:|---:|
| calls | 780 | 780 | 780 | +0 | +0 |
| gross input | 34,452,373 | 23,095,837 | 23,212,054 | +116,217 | -11,356,536 |
| cache read | 33,683,578 | 22,487,438 | 22,784,545 | +297,107 | -11,196,140 |
| cache write | 565,021 | 404,625 | 222,981 | -181,644 | -160,396 |
| uncached | 203,774 | 203,774 | 204,528 | +754 | +0 |
| cost index | 4,278,408.1 | 2,958,299.1 | 2,761,708.8 | -196,590.3 | -1,320,109.0 |

Plan 151 changes the index by -196,590.3 (6.6% saved vs current). Cold prefixes (first call of each agent) are still the largest single write. Plan 151 only cheapens the later hops, once a prefix exists to reuse. Optimizer passes — current 2 (9 turns compacted), plan 151 0 (0 turns compacted). Worker catalogs are not shared with the meta drone or the root. A worker cold prefix is 19,229 tokens before the first user message.

### F. Many short workers

Root: 3 turns × 1 round. One meta drone: 2 turns × 1 round. 8 workers, each 1 turn × 2 rounds. Cold-start case.

| | old double-catalog | current | plan 151 | plan 151 − current | current − old |
|---|---:|---:|---:|---:|---:|
| calls | 34 | 34 | 34 | +0 | +0 |
| gross input | 1,176,809 | 692,433 | 692,751 | +318 | -484,376 |
| cache read | 823,557 | 483,810 | 485,774 | +1,964 | -339,747 |
| cache write | 347,196 | 202,567 | 200,815 | -1,752 | -144,629 |
| uncached | 6,056 | 6,056 | 6,162 | +106 | +0 |
| cost index | 522,406.7 | 307,645.8 | 305,758.2 | -1,887.6 | -214,761.0 |

Plan 151 changes the index by -1,887.6 (0.6% saved vs current). Cold child prefixes dominate: each worker's first call is a full cache write of its own system text plus the full worker `tools[]`, and a one-turn worker never replays history, so stable replay has nothing to save. Worker catalogs are not shared with the meta drone or the root. A worker cold prefix is 19,229 tokens before the first user message.

### G. Reasoning miss only

One Responses Meta Agent, 6 turns, 4 tool rounds, reasoning on every in-flight hop. No children, no DropTurnContext.

| | old double-catalog | current | plan 151 | plan 151 − current | current − old |
|---|---:|---:|---:|---:|---:|
| calls | 30 | 30 | 30 | +0 | +0 |
| gross input | 1,027,870 | 680,650 | 681,975 | +1,325 | -347,220 |
| cache read | 982,897 | 647,251 | 648,611 | +1,360 | -335,646 |
| cache write | 36,645 | 25,071 | 25,036 | -35 | -11,574 |
| uncached | 8,328 | 8,328 | 8,328 | +0 | +0 |
| cost index | 152,424.0 | 104,391.9 | 104,484.1 | +92.2 | -48,032.1 |

Plan 151 changes the index by +92.2 (-0.1% saved vs current). Current collapses the whole tool block on every later prompt. Plan 151 keeps the per-round blocks and still misses once per turn, at the first reasoning object that was on the in-flight hop and is absent from history. Replaying that reasoning too would land at index 99,715.4; the accepted miss costs about 4,768.8 on the index.

## Summary

Ranked by how much plan 151 cuts the cost index versus current (positive = plan 151 is cheaper). Percent is that cut divided by the current index.

| rank | scenario | plan 151 saves (index) | vs current | current saves vs old |
|---:|---|---:|---:|---:|
| 1 | E. Full tree, heavy | 196,590.3 | 6.6% | 1,320,109.0 |
| 2 | D. Full tree, light | 33,498.9 | 6.3% | 292,728.6 |
| 3 | C. Root plus two meta drones | 19,141.0 | 6.1% | 171,953.8 |
| 4 | B. Root only, long | 3,782.2 | 0.6% | 208,910.7 |
| 5 | A. Root only, short | 2,939.7 | 4.8% | 31,828.5 |
| 6 | F. Many short workers | 1,887.6 | 0.6% | 214,761.0 |
| 7 | G. Reasoning miss only | -92.2 | -0.1% | 48,032.1 |

Biggest save: **E. Full tree, heavy** (196,590.3, 6.6%). Smallest save: **G. Reasoning miss only** (-92.2, -0.1%).

Cold workers dwarf the plan on the short-worker tree, and they do not on the long one. Scenario F cold-write index is 251,018.8 against a plan-151 save of 1,887.6 (a one-turn worker never replays a collapsed history). Scenario E cold-write index is 278,726.2 and plan 151 still saves 196,590.3, because six turns of 12-round worker history get rewritten on every later prompt today and only appended after plan 151. The already-shipped catalog dedupe (current vs old) saves 214,761.0 on F and 1,320,109.0 on E, almost all of it the duplicated `tools[]` sitting in the cached prefix at the 0.1 read weight after the first call, plus a 1.25 write once per agent.

## What plan 151 does not fix

- **Cold child caches.** A new meta drone or worker pays a full cache write of its own system text and `tools[]`. Nothing in plan 151 shares the parent's prefix. Scenario F is almost entirely this.
- **Full worker catalogs.** Section 5 of plan 151 leaves Explore / Drone / review catalogs on `CreateDefault` minus structural gates. The worker `tools[]` measured here is larger than the root Meta Agent allowlist.
- **DropTurnContext.** Still one extra full-size call when over the cap, then a prefix break at the first excluded turn. Bytes before that turn stay valid. Same before and after the plan (scenario B).
- **One Responses reasoning miss per completed turn.** In-flight hops still send reasoning and cache against each other. The next user prompt omits those items, so the prefix breaks at the first reasoning object. Completions never sent them, so A–F do not pay this. Legacy turns with no `ToolRoundCallCounts` stay one collapsed chunk.
- **The token-cap compaction.** A fat unoptimized tail outside the keep-2 window still compacts in one pass once it exceeds 10,000 eligible tokens. That pass is a real prefix edit. Plan 151 only stops the *every prompt* edit that the sticky turn-count check causes.
- **Open-rules, models, and git blocks** are outside the plan. Open-rules is in the measured prefix. A git-status block that changes every prompt would break the prefix in front of the transcript on every column; it is not modeled.

## Do not treat these as measured

- **Cost weights** (0.1 / 1.25 / 1.0) are an index, not a provider invoice. Gross input is not the index.
- **Round size.** One synthetic `ReadFile` round (672-character result) repeated for every hop. Real shells, greps, and file slices are often larger. Heavier results scale the plan-151 savings up and do not scale the cold prefix. Collapsed history is charged the **same token count** as the per-round sum; only the cache key differs. The JSON envelope is a bit smaller in reality.
- **User and assistant sizes** are short on purpose so the tables show prefix and tool-history effects, not chatter. Child first-turn mandates are the measured `BuildChildFirstPrompt` strings plus the sample task.
- **Reasoning item** is a synthetic JSON object (62 tokens), not a captured Responses trace. Scenario G's miss scales with the real item.
- **Drop shape** is scripted (one call, six oldest turns), not `EvaluateInject`'s max-target comparison. The resume is a 32-token stand-in.
- **Duplicate catalog** size equals current `tools[]` tokens. The old `FormatToolsForPrompt` text was not recovered.
- **No** comments, images, context files, retries, or summarizer calls. Optimizer keep-recent is 2. Drop keep-recent (4) only matters because the excluded turns are the oldest ones, so the break is at the start of the transcript.

