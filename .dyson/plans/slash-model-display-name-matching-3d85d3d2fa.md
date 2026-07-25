# Plan: slash model lookup by display name

## Goal
Change the composer `/` (slash command) model picker so that it no longer resolves model commands by raw API slug. Instead it should match against display alias, provider name, and the composed label, and when multiple models tie it should order alphabetically by provider name then display alias and surface the provider name clearly in suggestions.

## Background
- `src/Harness/Harness.UI/Components/Chat/ComposerSlashCommands.cs` is a static helper with `Filter`, `TryResolve`, `RankModels`, `ScoreModel`, and `SelfCheck`.
- `Composer.razor` calls `Filter` on every keystroke and `TryResolve` at send time to apply mode/model/new-session commands.
- `ModelSlugPicker.razor` already filters by `DisplayAlias`, `ProviderName`, `Slug`, and `Label`, but that is a separate UI surface and not the scope here.
- `DysonModelStore.FindSlugByNameAsync` is used by engine sessions to resolve `StartSubagent.modelSlug`; it currently supports exact slug or display alias. That behavior is intentionally different (exact programmatic identifiers) and is **out of scope** unless explicitly requested.

## Recommended approach
Keep changes small and localized to `ComposerSlashCommands.cs` and its self-check.

### 1. Change `ScoreModel` to ignore raw slug
Current:
```csharp
private static int ScoreModel(ModelOption m, string filter)
{
    if (string.Equals(m.Slug, filter, StringComparison.OrdinalIgnoreCase)
        || string.Equals(m.DisplayAlias, filter, StringComparison.OrdinalIgnoreCase))
        return 300;
    if (m.Slug.StartsWith(filter, StringComparison.OrdinalIgnoreCase)
        || m.DisplayAlias.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
        return 200;
    if (filter.Length >= 3
        && (m.Slug.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || m.DisplayAlias.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || m.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || m.ProviderName.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        return 100;
    return 0;
}
```

New behavior:
- Remove all `m.Slug` comparisons from the scoring path.
- Exact match on `DisplayAlias` → 300.
- Starts-with on `DisplayAlias` → 200.
- For filters of 3+ characters: contains on `DisplayAlias`, `ProviderName`, or `Label` → 100.
- (Optional but recommended) keep a very low Contains on `Slug` at score 50 only when filter length >= 3, so a user who genuinely remembers the raw slug still gets a result — but the user explicitly asked to remove slug matching, so implement **no slug matching** unless later changed.

### 2. Update `RankModels` ordering
Current ordering is by score descending then `DisplayAlias` ascending. Update to:
1. Score descending.
2. `ProviderName` ascending (`StringComparer.OrdinalIgnoreCase`).
3. `DisplayAlias` ascending.

This satisfies the requirement to pick alphabetically by provider, then alias, when scores tie.

### 3. Update `TryResolve` model matching
Replace the exact-match block that currently compares `m.Slug` or `m.DisplayAlias` with the new display-name-only resolution:
- Try exact `DisplayAlias` match first (score 300).
- If no exact match, fall back to the first result of `Filter(token, models)` (which will now use the new ranking).
- Do **not** match on raw `Slug`.

### 4. Update suggestions to show provider name and display alias only
The current `ModelOption.Label` is formatted as `"{DisplayAlias} · {ProviderName} / {Slug}"` and `Filter` uses it as the suggestion description. Change the label generation in `Composer.razor` and the `Suggestion` description in `ComposerSlashCommands.Filter` to a slug-free format: `"{DisplayAlias} · {ProviderName}"`.

Rationale: the user should never see the raw API slug in slash suggestions; the display name plus provider name is enough to disambiguate models across providers.

### 5. Update `SelfCheck`
The current self-check asserts `/gpt-fast` resolves to the GPT model. Because slug matching is removed, replace that assertion with a display-name-based query, e.g.:
- `Filter("/gpt fast")` or `Filter("/GPT Fast")` returns the GPT model.
- `TryResolve("/gpt fast", ...)` returns the GPT model.
- Add an assertion that `TryResolve("/gpt-fast")` no longer resolves (since slug-only input should fail).
- Keep the `/new` and `/ask` assertions unchanged.

### 6. Verify no other callers break
Only `Composer.razor` and `Program.cs` (`SelfCheck`) call into `ComposerSlashCommands`. The engine session resolution paths (`DysonModelStore.FindSlugByNameAsync`) are intentionally unchanged.

## Files to change
- `src/Harness/Harness.UI/Components/Chat/ComposerSlashCommands.cs`
  - `ScoreModel` remove slug matching.
  - `RankModels` add provider-name tie-breaker.
  - `TryResolve` remove slug exact match.
  - `SelfCheck` update test cases.

## Out of scope
- `ModelSlugPicker.razor` search behavior.
- `DysonModelStore.FindSlugByNameAsync` (programmatic slug/alias resolution for `StartSubagent.modelSlug`).
- Changing the model option label format.
- Adding fuzzy/Levenshtein matching; keep simple ordinal contains/starts-with.

## Verification
1. `Program.cs` startup self-check passes.
2. Manual UI check: typing `/claude` filters to models whose display alias/provider/label contains "claude"; typing `/gpt-fast` no longer resolves a model.
3. When two providers have a model alias "GPT-4o", the suggestion list orders them by provider name alphabetically.

## Risks / follow-ups
- Power users who memorized raw slugs will need to use display names. This is intentional per the request.
- If a display alias is empty, the composed label still includes the slug; matching will fall through to provider/label only.
- This does not address ambiguity when the same provider has duplicate display aliases; that is a data-model concern, not a slash-command concern.