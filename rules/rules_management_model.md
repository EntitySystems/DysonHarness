# Management drones use Grok 4.7 high

Management means operations, not authoring: start or stop a process or service; run a build or test suite as an operation (`dotnet build`, `dotnet test`, CI watch); restart the local UI; install or repair local tooling.

- For a management drone, `StartAsyncMetaAgentDrone` sets `modelSlug` to `grok-4.7` and `reasoningEffort` to `high`.
- For every other drone, omit both so the parent model is inherited. Not management: feature implementation, plan writing, bug review, security review, research, or a coordinator that writes code and only runs tests at the end.
- Slug is exactly `grok-4.7`. Effort is exactly `high`. Do not invent another slug.
