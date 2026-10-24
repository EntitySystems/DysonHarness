#!/usr/bin/env python3
"""Quota-index simulation: old double-catalog vs current vs plan 151.

Token constants were measured with DysonTiktokenTokenCounter (cl100k_base)
via a one-shot Build() / BuildToolsArray / BuildSystemPromptBlockAsync.
Re-running this script regenerates the markdown; it does not re-measure.

Usage (repo root):
  python docs/engine/prompt-cache-usage-scenarios.py
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

# --- measured (cl100k_base), see report citations ---

WEIGHT_READ = 0.1
WEIGHT_WRITE = 1.25
WEIGHT_UNCACHED = 1.0

KEEP_RECENT = 2  # DysonContextOptimizer.DefaultKeepRecentTurns
MAX_TURNS_BEFORE_OPTIMIZE = 10  # DefaultMaxTurnsBeforeOptimize
MAX_UNOPTIMIZED_TOKENS = 10_000  # DefaultMaxUnoptimizedTokens

ROUND_WIRE = 285  # one ReadFile-shaped completions tool_call + result
ROUND_ELIGIBLE = 186  # BuildUnoptimizedEligibleText shape for that round
COMPACT_PREFIX = 25  # CompactToolHistoryHarnessPrefix + blank line
COMPACT_LINE = 48  # FormatCompactToolLine for that round
ASSISTANT = 24
REASONING = 62  # one synthetic Responses reasoning item
RENAME_EXTRA = 53  # RenameSessionReviewMandate added to a 35-token user message (88-35)
DROP_USER = 275  # [turnId] header (22) + DysonDropContextFlow.Instruction (253)
RESUME = 32
ROOT_USER = 35
META_DRONE_FIRST_USER = 756
WORKER_FIRST_USER = 669
LATER_USER = 35

# systemPlusOpenRulesTokens / toolsTokens / toolCount from the measurer.
ROLES = {
    "meta": {"system": 7379, "tools": 11574, "tools_n": 61, "first_user": ROOT_USER},
    "meta_drone": {"system": 5590, "tools": 15767, "tools_n": 78, "first_user": META_DRONE_FIRST_USER},
    "worker": {"system": 4568, "tools": 14661, "tools_n": 74, "first_user": WORKER_FIRST_USER},
}

OPENRULES = 2607
PLAN_MANDATE = 79


@dataclass
class Turn:
    idx: int
    rounds: int
    base_user: int
    assistant: int
    kind: str = "user"  # user | drop
    excluded: bool = False
    optimized: bool = False


@dataclass
class Chunk:
    key: str
    n: int


@dataclass
class Totals:
    calls: int = 0
    gross: int = 0
    read: int = 0
    write: int = 0
    uncached: int = 0
    cold_write: int = 0
    rewrite_write: int = 0
    hop_uncached: int = 0
    optimize_passes: int = 0
    turns_compacted: int = 0
    prev: list[Chunk] | None = None

    @property
    def index(self) -> float:
        return (
            WEIGHT_READ * self.read
            + WEIGHT_WRITE * self.write
            + WEIGHT_UNCACHED * self.uncached
        )


def compact_tokens(rounds: int) -> int:
    if rounds <= 0:
        return 0
    return COMPACT_PREFIX + rounds * COMPACT_LINE


def is_plan(column: str) -> bool:
    return column.startswith("plan")


def rename_slot(idx: int) -> bool:
    # DysonSessionInitialization.IsRenameReviewTurn: 1-based 1, 9, 17, ...
    one = idx + 1
    return one > 0 and (one - 1) % 8 == 0


def user_view(turn: Turn, column: str, in_flight: bool) -> Chunk:
    extra = RENAME_EXTRA if rename_slot(turn.idx) and (in_flight or is_plan(column)) else 0
    tag = "ren" if extra else "plain"
    return Chunk(f"u{turn.idx}:{tag}", turn.base_user + extra)


def eligible_outside(turns: list[Turn]) -> tuple[int, int]:
    until = max(0, len(turns) - KEEP_RECENT)
    n = 0
    tok = 0
    for t in turns[:until]:
        if t.excluded or t.optimized or t.kind != "user":
            continue
        n += 1
        tok += t.rounds * ROUND_ELIGIBLE
    return n, tok


def should_optimize(turns: list[Turn], column: str) -> bool:
    n, tok = eligible_outside(turns)
    over_tokens = tok > MAX_UNOPTIMIZED_TOKENS
    if is_plan(column):
        # Plan 151: batch count of not-yet-optimized turns outside the keep window,
        # or that eligible text over the token cap. Not a sticky turns.Count check.
        return n >= MAX_TURNS_BEFORE_OPTIMIZE or over_tokens
    if len(turns) >= MAX_TURNS_BEFORE_OPTIMIZE:
        return True
    return over_tokens


def optimize(turns: list[Turn], column: str, totals: Totals) -> None:
    if not should_optimize(turns, column):
        return
    totals.optimize_passes += 1
    until = max(0, len(turns) - KEEP_RECENT)
    for t in turns[:until]:
        if t.excluded or t.optimized or t.kind != "user" or t.rounds <= 0:
            continue
        t.optimized = True
        totals.turns_compacted += 1


def history_chunks(turns: list[Turn], column: str) -> list[Chunk]:
    out: list[Chunk] = []
    replay_reasoning = column == "plan_replay"
    for t in turns:
        if t.excluded:
            continue
        out.append(user_view(t, column, in_flight=False))
        if t.kind == "drop":
            out.append(Chunk(f"asst{t.idx}", t.assistant))
            continue
        if t.optimized:
            out.append(Chunk(f"cmp{t.idx}", compact_tokens(t.rounds)))
            continue
        if is_plan(column):
            for r in range(t.rounds):
                if replay_reasoning:
                    out.append(Chunk(f"reas{t.idx}:{r}", REASONING))
                out.append(Chunk(f"rnd{t.idx}:{r}", ROUND_WIRE))
        elif t.rounds:
            # Same payload size as the per-round sum; different bytes (one glued block).
            out.append(Chunk(f"col{t.idx}:{t.rounds}", t.rounds * ROUND_WIRE))
        if t.assistant:
            out.append(Chunk(f"asst{t.idx}", t.assistant))
    return out


def prefix_chunks(role: str, column: str) -> list[Chunk]:
    spec = ROLES[role]
    system = spec["system"] + (spec["tools"] if column == "old" else 0)
    # Old duplicate lives in the system text, so it is its own stable prefix.
    return [Chunk(f"sys:{column}", system), Chunk(f"tools:{role}", spec["tools"])]


def inflight_request(
    role: str,
    turns: list[Turn],
    column: str,
    turn: Turn,
    hops_done: int,
    reasoning: bool,
) -> list[Chunk]:
    chunks = prefix_chunks(role, column) + history_chunks(turns, column)
    chunks.append(user_view(turn, column, in_flight=True))
    for r in range(hops_done):
        if reasoning:
            chunks.append(Chunk(f"reas{turn.idx}:{r}", REASONING))
        chunks.append(Chunk(f"rnd{turn.idx}:{r}", ROUND_WIRE))
    return chunks


def account(chunks: list[Chunk], prev: list[Chunk] | None) -> dict[str, int]:
    gross = sum(c.n for c in chunks)
    if prev is None:
        return {"read": 0, "write": gross, "uncached": 0, "gross": gross, "kind": 0}
    i = 0
    read = 0
    while (
        i < len(chunks)
        and i < len(prev)
        and chunks[i].key == prev[i].key
        and chunks[i].n == prev[i].n
    ):
        read += chunks[i].n
        i += 1
    new_rest = sum(c.n for c in chunks[i:])
    prev_rest = sum(c.n for c in prev[i:])
    changed = min(new_rest, prev_rest)
    suffix = new_rest - changed
    if read + changed + suffix != gross:
        raise AssertionError(f"split {read}+{changed}+{suffix} != {gross}")
    kind = 1 if changed == 0 else 2  # 1 append, 2 rewrite
    return {"read": read, "write": changed, "uncached": suffix, "gross": gross, "kind": kind}


def charge(totals: Totals, chunks: list[Chunk]) -> None:
    row = account(chunks, totals.prev)
    totals.calls += 1
    totals.gross += row["gross"]
    totals.read += row["read"]
    totals.write += row["write"]
    totals.uncached += row["uncached"]
    if totals.prev is None:
        totals.cold_write += row["write"]
    elif row["kind"] == 1:
        totals.hop_uncached += row["uncached"]
    else:
        totals.rewrite_write += row["write"]
    totals.prev = chunks


def run_agent(
    role: str,
    user_turns: int,
    tool_rounds: int,
    column: str,
    *,
    reasoning: bool = False,
    drop_at: int | None = None,
) -> Totals:
    """drop_at is the 1-based user turn whose prompt is preceded by one DropContext call."""
    totals = Totals()
    turns: list[Turn] = []
    spec = ROLES[role]
    for n in range(1, user_turns + 1):
        optimize(turns, column, totals)
        if drop_at is not None and n == drop_at:
            run_drop(role, turns, column, totals, reasoning)
        base = spec["first_user"] if n == 1 else LATER_USER
        turn = Turn(idx=len(turns), rounds=tool_rounds, base_user=base, assistant=ASSISTANT)
        # N tool-returning hops + one closing hop that emits assistant text.
        for hop in range(tool_rounds + 1):
            charge(totals, inflight_request(role, turns, column, turn, hop, reasoning))
        turns.append(turn)
    return totals


def run_drop(
    role: str,
    turns: list[Turn],
    column: str,
    totals: Totals,
    reasoning: bool,
) -> None:
    # One full-size call (plan 151 keeps this). Then exclude the oldest 6 turns.
    drop = Turn(idx=len(turns), rounds=0, base_user=DROP_USER, assistant=RESUME, kind="drop")
    charge(totals, inflight_request(role, turns, column, drop, 0, reasoning=False))
    victims = [t for t in turns if not t.excluded and t.kind == "user"][:6]
    for t in victims:
        t.excluded = True
    turns.append(drop)
    del reasoning


@dataclass
class Scenario:
    code: str
    title: str
    blurb: str
    # (role, user_turns, tool_rounds, count, drop_at or None, reasoning)
    agents: list[tuple[str, int, int, int, int | None, bool]]


SCENARIOS = [
    Scenario(
        "A",
        "Root only, short",
        "4 user turns, 3 tool rounds. No children. Under the optimizer threshold.",
        [("meta", 4, 3, 1, None, False)],
    ),
    Scenario(
        "B",
        "Root only, long",
        "24 user turns, 6 tool rounds. Optimizer runs. One scripted DropTurnContext at the start of user turn 16 (oldest 6 turns excluded).",
        [("meta", 24, 6, 1, 16, False)],
    ),
    Scenario(
        "C",
        "Root plus two meta drones",
        "Root: 8 user turns, 2 tool rounds. Each of two meta drones: 6 user turns, 4 tool rounds. No workers.",
        [
            ("meta", 8, 2, 1, None, False),
            ("meta_drone", 6, 4, 2, None, False),
        ],
    ),
    Scenario(
        "D",
        "Full tree, light",
        "Root: 6 turns × 2 rounds. One meta drone: 5 turns × 3 rounds. That drone spawns 3 workers. Each worker: 4 turns × 8 rounds.",
        [
            ("meta", 6, 2, 1, None, False),
            ("meta_drone", 5, 3, 1, None, False),
            ("worker", 4, 8, 3, None, False),
        ],
    ),
    Scenario(
        "E",
        "Full tree, heavy",
        "Root: 12 turns × 4 rounds. Two meta drones: 8 turns × 5 rounds each. Each meta drone spawns 4 workers (8 workers). Each worker: 6 turns × 12 rounds.",
        [
            ("meta", 12, 4, 1, None, False),
            ("meta_drone", 8, 5, 2, None, False),
            ("worker", 6, 12, 8, None, False),
        ],
    ),
    Scenario(
        "F",
        "Many short workers",
        "Root: 3 turns × 1 round. One meta drone: 2 turns × 1 round. 8 workers, each 1 turn × 2 rounds. Cold-start case.",
        [
            ("meta", 3, 1, 1, None, False),
            ("meta_drone", 2, 1, 1, None, False),
            ("worker", 1, 2, 8, None, False),
        ],
    ),
    Scenario(
        "G",
        "Reasoning miss only",
        "One Responses Meta Agent, 6 turns, 4 tool rounds, reasoning on every in-flight hop. No children, no DropTurnContext.",
        [("meta", 6, 4, 1, None, True)],
    ),
]

COLUMNS = ("old", "current", "plan")
COLUMN_LABEL = {
    "old": "old double-catalog",
    "current": "current",
    "plan": "plan 151",
}


def run_scenario(s: Scenario, column: str) -> Totals:
    acc = Totals()
    for role, turns, rounds, count, drop_at, reasoning in s.agents:
        for _ in range(count):
            part = run_agent(
                role,
                turns,
                rounds,
                column,
                reasoning=reasoning,
                drop_at=drop_at,
            )
            acc.calls += part.calls
            acc.gross += part.gross
            acc.read += part.read
            acc.write += part.write
            acc.uncached += part.uncached
            acc.cold_write += part.cold_write
            acc.rewrite_write += part.rewrite_write
            acc.hop_uncached += part.hop_uncached
            acc.optimize_passes += part.optimize_passes
            acc.turns_compacted += part.turns_compacted
    return acc


def fmt_int(n: int) -> str:
    return f"{n:,}"


def fmt_idx(n: float) -> str:
    return f"{n:,.1f}"


def delta(a: float, b: float) -> str:
    """a - b, with sign."""
    d = a - b
    sign = "+" if d > 0 else ""
    if isinstance(a, float) or isinstance(b, float):
        return f"{sign}{d:,.1f}"
    return f"{sign}{int(d):,}"


def table(rows: dict[str, Totals]) -> str:
    old, cur, plan = rows["old"], rows["current"], rows["plan"]
    lines = [
        "| | old double-catalog | current | plan 151 | plan 151 − current | current − old |",
        "|---|---:|---:|---:|---:|---:|",
    ]
    specs = [
        ("calls", "calls", False),
        ("gross input", "gross", False),
        ("cache read", "read", False),
        ("cache write", "write", False),
        ("uncached", "uncached", False),
        ("cost index", "index", True),
    ]
    for label, attr, is_idx in specs:
        ov, cv, pv = getattr(old, attr), getattr(cur, attr), getattr(plan, attr)
        show = fmt_idx if is_idx else fmt_int
        # plan − current, current − old
        if is_idx:
            d1, d2 = delta(pv, cv), delta(cv, ov)
        else:
            d1 = delta(float(pv), float(cv)).replace(".0", "") if False else delta(pv, cv)  # type: ignore
            # delta() on ints
            d1 = delta(pv, cv) if not isinstance(pv, float) else delta(pv, cv)
            d2 = delta(cv, ov)
            # force int formatting
            d1 = f"{pv - cv:+,}"
            d2 = f"{cv - ov:+,}"
        lines.append(
            f"| {label} | {show(ov)} | {show(cv)} | {show(pv)} | {d1 if is_idx else d1} | {d2 if is_idx else d2} |"
        )
    # fix the index row deltas which went through the int branch incorrectly
    # Rebuild index row cleanly.
    lines[-1] = (
        f"| cost index | {fmt_idx(old.index)} | {fmt_idx(cur.index)} | {fmt_idx(plan.index)} "
        f"| {delta(plan.index, cur.index)} | {delta(cur.index, old.index)} |"
    )
    return "\n".join(lines)


def pct(save: float, base: float) -> str:
    if base == 0:
        return "n/a"
    return f"{100.0 * save / base:.1f}%"


def dominate(s: Scenario, rows: dict[str, Totals]) -> str:
    cur, plan = rows["current"], rows["plan"]
    save = cur.index - plan.index
    cold_share = (WEIGHT_WRITE * cur.cold_write) / cur.index if cur.index else 0
    rewrite_share = (WEIGHT_WRITE * cur.rewrite_write) / cur.index if cur.index else 0
    bits = [
        f"Plan 151 changes the index by {delta(plan.index, cur.index)} "
        f"({pct(save, cur.index)} saved vs current)."
    ]
    if s.code == "F" or (cold_share > 0.45 and save < 0.05 * cur.index):
        bits.append(
            "Cold child prefixes dominate: each worker's first call is a full cache write of its own "
            "system text plus the full worker `tools[]`, and a one-turn worker never replays history, "
            "so stable replay has nothing to save."
        )
    elif s.code == "G":
        replay = run_scenario(s, "plan_replay")
        miss = plan.index - replay.index
        bits.append(
            f"Current collapses the whole tool block on every later prompt. "
            f"Plan 151 keeps the per-round blocks and still misses once per turn, at the first reasoning "
            f"object that was on the in-flight hop and is absent from history. "
            f"Replaying that reasoning too would land at index {fmt_idx(replay.index)}; "
            f"the accepted miss costs about {fmt_idx(miss)} on the index."
        )
    elif s.code == "B":
        read_ix = WEIGHT_READ * cur.read
        write_ix = WEIGHT_WRITE * cur.write
        bits.append(
            f"Current optimizer passes: {cur.optimize_passes}, turns compacted: {cur.turns_compacted}. "
            f"Plan 151 passes: {plan.optimize_passes}, turns compacted: {plan.turns_compacted}. "
            "After the threshold, current rewrites one older turn on every prompt. "
            "Plan 151 waits for a full outside-window batch or the 10,000-token cap, then compacts "
            "everything outside the keep-2 window in one pass. Fewer passes means more full tool JSON "
            "stays in history: gross input and cache read go up, cache write goes down, and the two "
            "nearly cancel. "
            f"On the current index, cache read at weight 0.1 is {fmt_idx(read_ix)} and cache write at "
            f"weight 1.25 is {fmt_idx(write_ix)}. Read is the larger term, so the extra uncompacted "
            "history eats most of the write savings. "
            "The scripted DropTurnContext is the same on every column: one extra full-size call, then a "
            "hard break at the first excluded turn (the oldest turn). That cliff is shared, which is why "
            "the net index move stays small."
        )
    else:
        if rewrite_share >= cold_share and save > 0:
            bits.append(
                "The current bill is mostly per-turn history rewrite: the next prompt diverges at the "
                "previous turn's first tool call (collapsed block, and a rename mandate stripped off "
                "slots 1, 9, 17). Plan 151 replays the same per-round blocks and keeps that mandate, "
                "so the next prompt cache-reads through the previous final hop and only the new "
                "assistant text plus the new user message are uncached."
            )
        elif cold_share > rewrite_share:
            bits.append(
                "Cold prefixes (first call of each agent) are still the largest single write. "
                "Plan 151 only cheapens the later hops, once a prefix exists to reuse."
            )
        if rows["current"].optimize_passes or rows["plan"].optimize_passes:
            bits.append(
                f"Optimizer passes — current {cur.optimize_passes} ({cur.turns_compacted} turns compacted), "
                f"plan 151 {plan.optimize_passes} ({plan.turns_compacted} turns compacted)."
            )
    if s.code in {"D", "E", "F"}:
        # cold write share of workers roughly: recompute worker cold from role size * count
        bits.append(
            "Worker catalogs are not shared with the meta drone or the root. "
            f"A worker cold prefix is {fmt_int(ROLES['worker']['system'] + ROLES['worker']['tools'])} tokens "
            "before the first user message."
        )
    return " ".join(bits)


def catalog_section() -> str:
    return "\n".join(
        [
            "| role | mode | depth | tools | `tools[]` tokens | system text tokens | cold prefix |",
            "|---|---|---:|---:|---:|---:|---:|",
            f"| root Meta Agent | Meta Agent | 0 | {ROLES['meta']['tools_n']} | {fmt_int(ROLES['meta']['tools'])} | {fmt_int(ROLES['meta']['system'])} | {fmt_int(ROLES['meta']['system'] + ROLES['meta']['tools'])} |",
            f"| Meta Agent Drone | Meta Agent Drone | 1 | {ROLES['meta_drone']['tools_n']} | {fmt_int(ROLES['meta_drone']['tools'])} | {fmt_int(ROLES['meta_drone']['system'])} | {fmt_int(ROLES['meta_drone']['system'] + ROLES['meta_drone']['tools'])} |",
            f"| classic worker Drone | Drone | 2 | {ROLES['worker']['tools_n']} | {fmt_int(ROLES['worker']['tools'])} | {fmt_int(ROLES['worker']['system'])} | {fmt_int(ROLES['worker']['system'] + ROLES['worker']['tools'])} |",
            "",
            "Cold prefix = system text + `tools[]`. It is cache-written on that agent's first call and cache-read after, until something in it changes. Children do not share it.",
        ]
    )


def build() -> str:
    results: dict[str, dict[str, Totals]] = {}
    for s in SCENARIOS:
        results[s.code] = {col: run_scenario(s, col) for col in COLUMNS}

    ranking = sorted(
        SCENARIOS,
        key=lambda s: results[s.code]["current"].index - results[s.code]["plan"].index,
        reverse=True,
    )
    biggest = ranking[0]
    smallest = ranking[-1]
    bcur, bplan = results[biggest.code]["current"], results[biggest.code]["plan"]
    scur, splan = results[smallest.code]["current"], results[smallest.code]["plan"]

    parts: list[str] = []
    parts.append("# Prompt-cache usage: before and after plan 151")
    parts.append("")
    parts.append(
        "Simulated quota-relevant tokens for one hierarchy: root Meta Agent, Meta Agent Drones, "
        "and classic worker Drones those meta drones spawn. Three columns:"
    )
    parts.append("")
    parts.append(
        "- **old double-catalog** — tool schemas in `tools[]` and again inside the system text "
        "(that duplicate is already gone on this checkout)."
    )
    parts.append(
        "- **current** — this checkout: `tools[]` only, tool rounds collapsed when the turn is replayed, "
        "optimizer on every prompt once the turn count sticks, rename mandate only while the user turn is in flight."
    )
    parts.append(
        "- **plan 151** — per-round replay without previous-turn reasoning, optimizer in batches, "
        "rename mandate kept on that same user message. Not built here; this file only models it."
    )
    parts.append("")
    parts.append("## Cost index")
    parts.append("")
    parts.append(
        "Not a bill. Weights are a comparable Anthropic-style index on quota-relevant input tokens:"
    )
    parts.append("")
    parts.append("| bucket | weight | what it is |")
    parts.append("|---|---:|---|")
    parts.append("| cache read | 0.1 | prefix tokens byte-identical to this agent's previous request |")
    parts.append("| cache write | 1.25 | prefix tokens that changed (the miss that replaces the old tail, up to the old length) |")
    parts.append("| uncached input | 1.0 | new suffix past the previous request's length |")
    parts.append("")
    parts.append(
        "Gross input = cache read + cache write + uncached. That is what the usage panel calls \"in\" "
        "(cached tokens stay inside the gross). The first request of every agent is a full cache write. "
        "Each agent has its own cache. A hit requires the byte prefix to match **that** agent's previous request. "
        "CLIProxy is assumed to cache when that prefix is stable; there is no \"proxy does not cache\" column."
    )
    parts.append("")
    parts.append(
        "Index = `0.1 * read + 1.25 * write + 1.0 * uncached`. "
        "A pure append (in-flight tool hop, or a plan-151 next prompt with no reasoning) is read on the whole "
        "previous request and uncached on the new hop only. A mid-prompt edit is a write on the replaced tail."
    )
    parts.append("")
    parts.append("## How the bytes were measured")
    parts.append("")
    parts.append(
        "Tokenizer: `DysonTiktokenTokenCounter` (`src/Harness/Harness.Engine/Context/DysonTiktokenTokenCounter.cs`), "
        "`cl100k_base`. One-shot measurement, not a live session trace."
    )
    parts.append("")
    parts.append(
        "Catalogs from `DysonSessionToolsetBuilder.Build` "
        "(`src/Harness/Harness.Engine/Mcp/DysonSessionToolsetBuilder.cs`) with "
        "`BrowserControl = DysonNullBrowserControl` (non-null, so browser tools stay), "
        "`AvailableShells = DysonShell.DefaultConfiguredShellsForCurrentPlatform()` "
        "(Windows: Pwsh, PowerShell, Cmd), `ImageGenerationProvider` null (`GenerateImage` removed), voice off. "
        "`tools[]` bytes are `OpenAiCompatibleHttp.BuildToolsArray(...).ToJsonString()` "
        "(`src/Harness/Harness.Engine/Providers/OpenAi/OpenAiCompatibleHttp.cs`)."
    )
    parts.append("")
    parts.append(
        "- Root Meta Agent: depth 0. `ApplyModeCatalog` → `DysonMetaAgentTools.ApplyAllowlist`. "
        f"**{ROLES['meta']['tools_n']}** tools, **{fmt_int(ROLES['meta']['tools'])}** tokens."
    )
    parts.append(
        "- Meta Agent Drone: depth 1 (`DysonAgentSession.ComputeDepth`), `omitRootTaskCompletionTools: true`, "
        "then `ApplyDroneAllowlist` (drops `AskQuestionFromParent` / `PromptUserDialogFromParent`, "
        "adds plan and review tools). "
        f"**{ROLES['meta_drone']['tools_n']}** tools, **{fmt_int(ROLES['meta_drone']['tools'])}** tokens."
    )
    parts.append(
        "- Classic worker: mode `Drone`, depth 2 (spawned by the meta drone, not the root). "
        "`ConfigureInterAgentTools(2)` drops `AskQuestion`, `PromptUserDialog`, both FromParent ask/dialog tools, "
        "and `SubscribeToLongRunningShellCompletion`. Child gate drops `CompleteTask` / `ConfirmTaskComplete` / "
        "`ContinueWork` and keeps `SubmitSubagentReport`. No meta allowlist, so the rest of `CreateDefault` stays. "
        f"**{ROLES['worker']['tools_n']}** tools, **{fmt_int(ROLES['worker']['tools'])}** tokens."
    )
    parts.append("")
    parts.append(
        "System text tokens are the mode directive (`DysonAgentSystemPrompts.ForMode` / `WithBrowser` — "
        "the browser sentence is root Meta Agent only), the `BuildSystemText` reply-format line "
        "(`OpenAiCacheFriendlyTranscriptBuilder`), and the work-root open-rules block "
        f"(`DysonOpenRules.BuildSystemPromptBlockAsync`, **{fmt_int(OPENRULES)}** tokens: Root `AGENTS.md` plus "
        "AutoInclude rules). Models catalog and the git-status block are **not** included; they would add the "
        "same stable tokens to every column and would not change plan-151 deltas."
    )
    parts.append("")
    parts.append(catalog_section())
    parts.append("")
    parts.append(
        "The old column adds a second copy of that role's `tools[]` token count into the system text. "
        "`FormatToolsForPrompt` is gone from this checkout, so this is an **estimate**: duplicate size = "
        "measured `tools[]` tokens, not a recovered copy of the old prose."
    )
    parts.append("")
    parts.append("### Thresholds the simulator actually uses")
    parts.append("")
    parts.append(
        f"- Optimizer today (`DysonContextOptimizer.ShouldOptimize`): `turns.Count >= {MAX_TURNS_BEFORE_OPTIMIZE}` "
        f"**or** unoptimized eligible text outside the keep window `> {fmt_int(MAX_UNOPTIMIZED_TOKENS)}`. "
        f"`Optimize` compacts every not-yet-optimized turn in `[0, count - {KEEP_RECENT})`. "
        "The count check stays true forever, so after it trips, every later prompt compacts the one turn that just left the window."
    )
    parts.append(
        f"- Plan 151: delete the sticky count check. Trigger when the number of non-excluded, not-yet-optimized "
        f"turns outside that same window is `>= {MAX_TURNS_BEFORE_OPTIMIZE}`, **or** their eligible text is over "
        f"{fmt_int(MAX_UNOPTIMIZED_TOKENS)}. One pass still compacts everything currently outside the window. "
        "`OptimizeContextIfNeeded` runs at the start of `PromptWithTurnAsync`, **before** `AddTurn` "
        "(`OpenAiCompatibleAgentSession`), so the new turn is not in the count yet."
    )
    parts.append(
        "- Eligible text follows `BuildUnoptimizedEligibleText`: tool name, arguments JSON, and `FormatResponseLog`, "
        "not the wire JSON. This sim uses the measured per-round eligible size below."
    )
    parts.append(
        "- Rename mandate (`DysonSessionInitialization.RenameSessionReviewMandate`, "
        f"**{RENAME_EXTRA}** tokens) is appended by `FormatTurnUserContent` only while `incompleteCurrent` is true, "
        "on chrome-skipped slots 1, 9, 17, …. Plan 151 removes that bail. Plan-mode `PlanFirstTurnMandate` "
        f"(**{PLAN_MANDATE}** tokens) is the same bug; these scenarios are not Plan mode, so it does not fire."
    )
    parts.append(
        "- DropTurnContext (`DysonDropContextFlow`): no plan-151 code change. Scenario B forces one extra call "
        "at user turn 16, then marks the oldest 6 turns excluded. The real inject also needs the session over "
        "its max-target token cap (`EvaluateInject`); that cap is not modeled. Keep-4 and the 5-user-turn throttle are why a single inject is the right shape."
    )
    parts.append(
        "- Responses reasoning (`AppendToolRoundResponses`) is on the in-flight hop only. "
        "Plan 151 does not store it. Scenario G is the only Responses run. A–F are Completions-shaped (no reasoning items)."
    )
    parts.append("")
    parts.append("### Repeated sample (estimated text, measured token counts)")
    parts.append("")
    parts.append(
        "Every tool round is one `ReadFile` call plus a 672-character result. "
        f"Wire tokens **{ROUND_WIRE}**. Optimizer-eligible tokens **{ROUND_ELIGIBLE}**. "
        f"Compact line **{COMPACT_LINE}** plus a **{COMPACT_PREFIX}**-token harness prefix once per compacted turn. "
        f"Assistant reply **{ASSISTANT}**. Root user message **{ROOT_USER}** (`[turnId]` + a 13-token task). "
        f"A meta drone's stored first instruction (report mandate + drone mandate + `useWorktree is false` line + task) "
        f"is **{META_DRONE_FIRST_USER}** with the turn header. A coordinated worker's first instruction is **{WORKER_FIRST_USER}**. "
        f"Later user turns are **{LATER_USER}**. Those mandates are stored on `Instruction` by `BuildChildFirstPrompt`, so they do not fall off. "
        "A user turn is `tool rounds + 1` model calls: the extra call is the closing assistant hop with no tools. "
        "In-flight hops are append-only on every column."
    )
    parts.append("")
    parts.append("## Scenarios")
    parts.append("")

    for s in SCENARIOS:
        rows = results[s.code]
        parts.append(f"### {s.code}. {s.title}")
        parts.append("")
        parts.append(s.blurb)
        parts.append("")
        parts.append(table(rows))
        parts.append("")
        parts.append(dominate(s, rows))
        parts.append("")

    parts.append("## Summary")
    parts.append("")
    parts.append(
        "Ranked by how much plan 151 cuts the cost index versus current "
        "(positive = plan 151 is cheaper). Percent is that cut divided by the current index."
    )
    parts.append("")
    parts.append("| rank | scenario | plan 151 saves (index) | vs current | current saves vs old |")
    parts.append("|---:|---|---:|---:|---:|")
    for i, s in enumerate(ranking, 1):
        cur, plan, old = results[s.code]["current"], results[s.code]["plan"], results[s.code]["old"]
        save = cur.index - plan.index
        parts.append(
            f"| {i} | {s.code}. {s.title} | {fmt_idx(save)} | {pct(save, cur.index)} | {fmt_idx(old.index - cur.index)} |"
        )
    parts.append("")
    parts.append(
        f"Biggest save: **{biggest.code}. {biggest.title}** "
        f"({fmt_idx(bcur.index - bplan.index)}, {pct(bcur.index - bplan.index, bcur.index)}). "
        f"Smallest save: **{smallest.code}. {smallest.title}** "
        f"({fmt_idx(scur.index - splan.index)}, {pct(scur.index - splan.index, scur.index)})."
    )
    parts.append("")
    # Cold vs plan on the heavy tree and the cold-start tree.
    e = results["E"]
    f = results["F"]
    e_save = e["current"].index - e["plan"].index
    f_save = f["current"].index - f["plan"].index
    e_cold = WEIGHT_WRITE * e["current"].cold_write
    f_cold = WEIGHT_WRITE * f["current"].cold_write
    parts.append(
        f"Cold workers dwarf the plan on the short-worker tree, and they do not on the long one. "
        f"Scenario F cold-write index is {fmt_idx(f_cold)} against a plan-151 save of {fmt_idx(f_save)} "
        f"(a one-turn worker never replays a collapsed history). "
        f"Scenario E cold-write index is {fmt_idx(e_cold)} and plan 151 still saves {fmt_idx(e_save)}, "
        f"because six turns of 12-round worker history get rewritten on every later prompt today "
        f"and only appended after plan 151. "
        f"The already-shipped catalog dedupe (current vs old) saves {fmt_idx(f['old'].index - f['current'].index)} "
        f"on F and {fmt_idx(e['old'].index - e['current'].index)} on E, almost all of it the duplicated "
        f"`tools[]` sitting in the cached prefix at the 0.1 read weight after the first call, plus a 1.25 write once per agent."
    )
    parts.append("")
    parts.append("## What plan 151 does not fix")
    parts.append("")
    parts.append(
        "- **Cold child caches.** A new meta drone or worker pays a full cache write of its own system text and `tools[]`. "
        "Nothing in plan 151 shares the parent's prefix. Scenario F is almost entirely this."
    )
    parts.append(
        "- **Full worker catalogs.** Section 5 of plan 151 leaves Explore / Drone / review catalogs on `CreateDefault` "
        "minus structural gates. The worker `tools[]` measured here is larger than the root Meta Agent allowlist."
    )
    parts.append(
        "- **DropTurnContext.** Still one extra full-size call when over the cap, then a prefix break at the first "
        "excluded turn. Bytes before that turn stay valid. Same before and after the plan (scenario B)."
    )
    parts.append(
        "- **One Responses reasoning miss per completed turn.** In-flight hops still send reasoning and cache against "
        "each other. The next user prompt omits those items, so the prefix breaks at the first reasoning object. "
        "Completions never sent them, so A–F do not pay this. Legacy turns with no `ToolRoundCallCounts` stay one collapsed chunk."
    )
    parts.append(
        "- **The token-cap compaction.** A fat unoptimized tail outside the keep-2 window still compacts in one pass "
        "once it exceeds 10,000 eligible tokens. That pass is a real prefix edit. Plan 151 only stops the "
        "*every prompt* edit that the sticky turn-count check causes."
    )
    parts.append(
        "- **Open-rules, models, and git blocks** are outside the plan. Open-rules is in the measured prefix. "
        "A git-status block that changes every prompt would break the prefix in front of the transcript on every column; it is not modeled."
    )
    parts.append("")
    parts.append("## Do not treat these as measured")
    parts.append("")
    parts.append(
        "- **Cost weights** (0.1 / 1.25 / 1.0) are an index, not a provider invoice. Gross input is not the index."
    )
    parts.append(
        "- **Round size.** One synthetic `ReadFile` round (672-character result) repeated for every hop. "
        "Real shells, greps, and file slices are often larger. Heavier results scale the plan-151 savings up and do not scale the cold prefix. "
        "Collapsed history is charged the **same token count** as the per-round sum; only the cache key differs. The JSON envelope is a bit smaller in reality."
    )
    parts.append(
        "- **User and assistant sizes** are short on purpose so the tables show prefix and tool-history effects, not chatter. "
        "Child first-turn mandates are the measured `BuildChildFirstPrompt` strings plus the sample task."
    )
    parts.append(
        "- **Reasoning item** is a synthetic JSON object (62 tokens), not a captured Responses trace. Scenario G's miss scales with the real item."
    )
    parts.append(
        "- **Drop shape** is scripted (one call, six oldest turns), not `EvaluateInject`'s max-target comparison. "
        "The resume is a 32-token stand-in."
    )
    parts.append(
        "- **Duplicate catalog** size equals current `tools[]` tokens. The old `FormatToolsForPrompt` text was not recovered."
    )
    parts.append(
        "- **No** comments, images, context files, retries, or summarizer calls. "
        f"Optimizer keep-recent is {KEEP_RECENT}. Drop keep-recent (4) only matters because the excluded turns are the oldest ones, so the break is at the start of the transcript."
    )
    parts.append("")
    return "\n".join(parts) + "\n"


def main() -> None:
    text = build()
    out = Path(__file__).with_suffix(".md")
    out.write_text(text, encoding="utf-8")
    print(f"wrote {out.as_posix()} ({len(text)} chars)")


if __name__ == "__main__":
    main()
