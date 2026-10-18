# Grok 4.7 high is only for Meta Agent management drones

Management means operations, not authoring: start or stop a process or service; run a build or test suite as an operation (`dotnet build`, `dotnet test`, CI watch); restart the local UI; install or repair local tooling.

- Only a root Meta Agent calling `StartAsyncMetaAgentDrone` may set `modelSlug` to `grok-4.7` and `reasoningEffort` to `high`, and only when that drone's task is management.
- A Meta Agent drone that is not management omits both fields so the parent model is inherited. Not management: feature implementation, plan writing, bug review, security review, research, or a coordinator that writes code and only runs tests at the end.
- Never set either field on `StartSubagent`, on a coordinator's workers, or on `StartAsyncExploreAgent`, `StartAsyncBugReviewAgent`, or `StartAsyncSecurityReviewAgent`. Those calls omit both fields and inherit the parent model.
- Slug is exactly `grok-4.7`. Effort is exactly `high`. Do not invent another slug.
