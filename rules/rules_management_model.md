# Meta Agent drones: Sonnet 5.5 for ops and implementation, Opus 5.5 only for planning

Every drone a root Meta Agent starts with `StartAsyncMetaAgentDrone` or `BeginBuildPlan` gets a model chosen by task type. Effort is `high`, except `medium` for simple ops.

- **Management/ops tasks:** `modelSlug` `claude-sonnet-5-5`.
  - Management/ops: git ops (commit, push, merge, pull, branch, preview merges), starting and stopping services, restarting the local UI, running builds or test suites as an operation, watching CI, releases, shutdown, tooling repair.
  - `reasoningEffort` `high` for complex ops: releases, CI watching with reruns, merges with conflicts, multi-step pushes.
  - `reasoningEffort` `medium` for simple ops: restart the UI, run one build or test suite, a single git command.
- **Implementation and coordinators:** `modelSlug` `claude-sonnet-5-5`, `reasoningEffort` `high`.
  - Implementation: feature implementation, multi-file or cross-layer changes, debugging root causes, and every coordinator / `BeginBuildPlan` build.
  - The workers a coordinator runs with `StartSubagent` omit both model fields and inherit the parent, so under a Sonnet coordinator they are `claude-sonnet-5-5` `high` too.
- **Planning:** `modelSlug` `claude-opus-5-5`, `reasoningEffort` `high`.
  - Planning: purpose=plan drones, plan revisions. Opus is only for planning and authoring plans.
- **Simple tasks:** `modelSlug` `claude-sonnet-5-5`, `reasoningEffort` `high`.
  - Simple tasks: small single-file code edits, rule and doc wording edits, merge-conflict resolvers.
- When unsure: plan authoring goes to Opus; everything else (implementation, ops) goes to Sonnet (`high` if in doubt).
- Both fields are mandatory on every `StartAsyncMetaAgentDrone` and every root `BeginBuildPlan` spawn.
- Never omit `modelSlug` or `reasoningEffort`. Omitting `modelSlug` does not inherit the parent. It selects the Settings Meta Agent Drone default, which can be a different model. That is how a drone runs as the wrong model.
- Never set either field on `StartSubagent`, or on `StartAsyncExploreAgent`, `StartAsyncBugReviewAgent`, or `StartAsyncSecurityReviewAgent`. Those calls omit both fields and inherit the parent model.
- `ChangeSubagentAIModel` is only for an explicit user request, or for correcting a root drone to the role-table model (Sonnet for implementation, Opus for planning, effort per the table); never use it routinely on workers, explores, or reviews.
- Slugs are exactly `claude-opus-5-5` and `claude-sonnet-5-5`. Efforts are `high`, plus `medium` only for simple ops. Do not invent another slug or effort.
- `agentId` reuse on `BeginBuildPlan` does not take a model. The drone already running keeps its model.
- An explicit user request for a specific model overrides this rule.
