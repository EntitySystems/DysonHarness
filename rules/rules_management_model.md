# Meta Agent drones use Grok 4.7 high

Every drone a root Meta Agent starts with `StartAsyncMetaAgentDrone` or `BeginBuildPlan` uses Grok 4.7 at high effort. That includes management (start or stop a process, run a build or test suite as an operation, restart the local UI, install or repair tooling) and authoring (feature implementation, plan writing, rule writing, and a coordinator that writes code).

- On every `StartAsyncMetaAgentDrone`, and on every root `BeginBuildPlan` spawn, set `modelSlug` to `grok-4.7` and `reasoningEffort` to `high`. Both fields are mandatory.
- Never omit `modelSlug` or `reasoningEffort`. Omitting `modelSlug` does not inherit the parent. It selects the Settings Meta Agent Drone default, which can be a different model (it has been `claude-opus-5-5`). That is how a drone, including a build drone, runs as the wrong model.
- Never set either field on `StartSubagent`, or on `StartAsyncExploreAgent`, `StartAsyncBugReviewAgent`, or `StartAsyncSecurityReviewAgent`. Those calls omit both fields and inherit the parent model.
- Slug is exactly `grok-4.7`. Effort is exactly `high`. Do not invent another slug.
- `agentId` reuse on `BeginBuildPlan` does not take a model. The drone already running keeps its model.
