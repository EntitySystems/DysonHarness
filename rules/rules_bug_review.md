# Meta Agent drones: Dyson Bug Review before merge

Every Meta Agent Drone started with `useWorktree` true, including `BeginBuildPlan` coordinators, whose worktree has commits runs a Dyson Bug Review of its diff before it merges. Always: no exception for small changes.

## When

After the final build, tests and commit, and **before** the final `SubmitSubagentReport` with status `completed`. That report triggers the merge.

## How

- The drone itself calls `StartAsyncBugReviewAgent` (the Dyson Bug Review agent). Not a classic `StartSubagent` Drone or Explore, and not the root Meta Agent.
- The brief gives the commit range (`base SHA..HEAD`), the goal or `planId`, the changed files and the acceptance criteria.
- Omit `modelSlug` and `reasoningEffort`. The review inherits the parent model (see [rules_management_model.md](rules_management_model.md)).
- Then `WaitForSubagent` until the review finishes. Do not keep editing while it runs.

## Act on findings

- Fix confirmed bugs (correctness, data loss, security, concurrency, regressions) in the same worktree.
- Rebuild, re-run the affected tests (the full suite when engine code changed), and commit.
- Re-review at most once, and only when the fixes were substantial.
- List false positives and out-of-scope findings with a one-line reason each.

## Report

The final `SubmitSubagentReport` has a "Bug review" section: the reviewer `agentId`, the findings, what was fixed (commit SHAs), and what was not fixed and why.

If the review cannot start or fails, retry once. If it still fails, `SubmitSubagentReport` with status `failed` and say the review could not run, so the root decides. Never merge unreviewed in silence.

## Root Meta Agent

Names this rule in every `useWorktree` true brief and in `BeginBuildPlan` `extraInstructions`, and checks that the report has the Bug review section.

## Not in scope

`useWorktree` false drones (ops, merge-conflict resolvers, plan drones), Explores, and reviews themselves.
