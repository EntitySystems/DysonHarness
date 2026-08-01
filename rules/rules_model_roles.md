---
description: Which models to use for harness orchestration roles
mode: AgentOptional
providers: [cursor]
---

# Model roles

Rules for picking inference models when running Dyson Harness agent sessions and subagents.

## Defaults

- Use the session model unless a role below says otherwise (`StartSubagent` without `modelSlug` inherits the parent model).
- Cheaper/faster models are fine for mechanical work; reserve stronger models for judgment-heavy work.

## Roles

- **Explore (read-only mapping):** any fast model works — it only reads and reports. Low effort is acceptable.
- **Drone (implementation):** use a strong coding model (e.g. the `*-code` variants). Keep the default effort; code quality drops noticeably on cheap models.
- **Orchestrator (Work root):** keep the user's selected model — it owns routing, briefs, and merge decisions.
- **Verification / browser E2E:** any model; the bottleneck is the harness browser, not reasoning.

## Constraints

- `StartSubagent.modelSlug` must be the same provider kind as the parent session.
- Do not hard-code model slugs into product code, docs, or skills — models rotate; reference roles, not names.
