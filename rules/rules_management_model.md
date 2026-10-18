# Meta Agent drones use Grok 4.7 high

Every drone a root Meta Agent starts with `StartAsyncMetaAgentDrone` uses Grok 4.7 at high effort. That includes management (start or stop a process, run a build or test suite as an operation, restart the local UI, install or repair tooling) and authoring (feature implementation, plan writing, rule writing, and a coordinator that writes code).

- On every `StartAsyncMetaAgentDrone`, set `modelSlug` to `grok-4.7` and `reasoningEffort` to `high`.
- Never set either field on `StartSubagent`, or on `StartAsyncExploreAgent`, `StartAsyncBugReviewAgent`, or `StartAsyncSecurityReviewAgent`. Those calls omit both fields and inherit the parent model.
- Slug is exactly `grok-4.7`. Effort is exactly `high`. Do not invent another slug.
