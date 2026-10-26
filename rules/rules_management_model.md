# Meta Agent drones: Opus 5.5 for complex work, Sonnet 5.5 for simple work

Every drone a root Meta Agent starts with `StartAsyncMetaAgentDrone` or `BeginBuildPlan` gets a model chosen by task complexity. Effort is always `high`.

- **Complex tasks and planning:** `modelSlug` `claude-opus-5-5`, `reasoningEffort` `high`.
  - Complex tasks: feature implementation, multi-file or cross-layer changes, coordinator / `BeginBuildPlan` builds, debugging root causes.
  - Planning: purpose=plan drones, plan revisions.
- **Simple tasks:** `modelSlug` `claude-sonnet-5-5`, `reasoningEffort` `high`.
  - Simple tasks: management/ops (start or stop processes, run builds or tests as an operation, restart the local UI, commit, push, merge, CI watch, shutdown), small single-file edits, rule and doc wording edits, merge-conflict resolvers.
- When unsure whether a task is simple, use Opus.
- Both fields are mandatory on every `StartAsyncMetaAgentDrone` and every root `BeginBuildPlan` spawn.
- Never omit `modelSlug` or `reasoningEffort`. Omitting `modelSlug` does not inherit the parent. It selects the Settings Meta Agent Drone default, which can be a different model. That is how a drone runs as the wrong model.
- Never set either field on `StartSubagent`, or on `StartAsyncExploreAgent`, `StartAsyncBugReviewAgent`, or `StartAsyncSecurityReviewAgent`. Those calls omit both fields and inherit the parent model.
- Slugs are exactly `claude-opus-5-5` and `claude-sonnet-5-5`. Effort is exactly `high`. Do not invent another slug.
- `agentId` reuse on `BeginBuildPlan` does not take a model. The drone already running keeps its model.
- An explicit user request for a specific model overrides this rule.
