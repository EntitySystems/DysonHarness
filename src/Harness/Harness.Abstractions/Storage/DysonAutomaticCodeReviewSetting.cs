namespace DysonHarness;

/// <summary>
/// Persist tokens and compatibility mapping for
/// <see cref="DysonAppSettingKeys.AutomaticCodeReview"/>.
/// Settings-layer only — not a task-lifecycle evaluator.
/// </summary>
public static class DysonAutomaticCodeReviewSetting
{
    public const string None = "none";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string ReportOnly = "report_only";
    public const string AutomaticallyFix = "automatically_fix";

    /// <summary>Display-only / unsupported. Must not start a review.</summary>
    public const string High = "high";

    /// <summary>True when the new key is missing and legacy keys should be mapped once.</summary>
    public static bool NeedsLegacyMigration(string? automaticCodeReview) =>
        string.IsNullOrWhiteSpace(automaticCodeReview);

    /// <summary>
    /// Normalize a persisted (or form) value.
    /// Unknown / empty becomes <see cref="None"/>. <see cref="High"/> is kept for display.
    /// </summary>
    public static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            None => None,
            Low => Low,
            Medium => Medium,
            High => High,
            _ => None,
        };

    /// <summary>
    /// Legacy Boolean false/missing → <see cref="None"/>.
    /// True + <c>low</c> → <see cref="Low"/>; true + anything else → <see cref="Medium"/>.
    /// </summary>
    public static string FromLegacy(string? endOfTaskAutoReview, string? selfReviewIntensity)
    {
        if (!string.Equals(endOfTaskAutoReview, "true", StringComparison.OrdinalIgnoreCase))
            return None;

        return string.Equals(selfReviewIntensity?.Trim(), Low, StringComparison.OrdinalIgnoreCase)
            ? Low
            : Medium;
    }

    /// <summary>
    /// Prefer the new key when present; otherwise map obsolete
    /// <see cref="DysonAppSettingKeys.EndOfTaskAutoReview"/> +
    /// <see cref="DysonAppSettingKeys.SelfReviewIntensity"/>.
    /// </summary>
    public static string Resolve(
        string? automaticCodeReview,
        string? endOfTaskAutoReview,
        string? selfReviewIntensity) =>
        NeedsLegacyMigration(automaticCodeReview)
            ? FromLegacy(endOfTaskAutoReview, selfReviewIntensity)
            : Normalize(automaticCodeReview);

    /// <summary>True only for selectable persist values (<see cref="None"/> / <see cref="Low"/> / <see cref="Medium"/>).</summary>
    public static bool TrySelect(string? value, out string selected)
    {
        var normalized = Normalize(value);
        if (normalized is None or Low or Medium)
        {
            selected = normalized;
            return true;
        }

        selected = None;
        return false;
    }

    /// <summary>True only for <see cref="Low"/> and <see cref="Medium"/>. <see cref="None"/> and <see cref="High"/> do not start a review.</summary>
    public static bool ShouldEnqueueReview(string? value)
    {
        var normalized = Normalize(value);
        return normalized is Low or Medium;
    }

    /// <summary>Normalizes the automatic-review action; unknown/missing values report only.</summary>
    public static string NormalizeAction(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            ReportOnly => ReportOnly,
            AutomaticallyFix => AutomaticallyFix,
            _ => ReportOnly,
        };

    /// <summary>Accepts only selectable automatic-review action tokens.</summary>
    public static bool TrySelectAction(string? value, out string selected)
    {
        selected = NormalizeAction(value);
        return value?.Trim().ToLowerInvariant() is ReportOnly or AutomaticallyFix;
    }

    /// <summary>
    /// Reads/persists the automatic-review action. Older settings default to the safe
    /// report-only behavior without requiring a schema migration.
    /// </summary>
    public static async Task<Result<string, string>> ResolveActionAsync(
        IDysonSubjectSettingsRepository settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var current = await settings
            .GetSettingAsync(DysonAppSettingKeys.AutomaticCodeReviewAction, cancellationToken)
            .ConfigureAwait(false);
        if (current.IsError)
            return Result<string, string>.AsError(current.Error);

        var action = NormalizeAction(current.Value);
        if (!string.IsNullOrWhiteSpace(current.Value))
            return Result<string, string>.AsValue(action);

        var persist = await settings
            .SetSettingAsync(DysonAppSettingKeys.AutomaticCodeReviewAction, action, cancellationToken)
            .ConfigureAwait(false);
        return persist.IsError
            ? Result<string, string>.AsError(persist.Error)
            : Result<string, string>.AsValue(action);
    }

    /// <summary>
    /// Reads <see cref="DysonAppSettingKeys.AutomaticCodeReview"/>. When absent, maps the
    /// obsolete keys and persists the result so later reads stay on the new key.
    /// </summary>
    public static async Task<Result<string, string>> ResolveAsync(
        IDysonSubjectSettingsRepository settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var current = await settings
            .GetSettingAsync(DysonAppSettingKeys.AutomaticCodeReview, cancellationToken)
            .ConfigureAwait(false);
        if (current.IsError)
            return Result<string, string>.AsError(current.Error);

        if (!NeedsLegacyMigration(current.Value))
            return Result<string, string>.AsValue(Normalize(current.Value));

        var autoReview = await settings
            .GetSettingAsync(DysonAppSettingKeys.EndOfTaskAutoReview, cancellationToken)
            .ConfigureAwait(false);
        if (autoReview.IsError)
            return Result<string, string>.AsError(autoReview.Error);

        var intensity = await settings
            .GetSettingAsync(DysonAppSettingKeys.SelfReviewIntensity, cancellationToken)
            .ConfigureAwait(false);
        if (intensity.IsError)
            return Result<string, string>.AsError(intensity.Error);

        var mapped = FromLegacy(autoReview.Value, intensity.Value);
        var persist = await settings
            .SetSettingAsync(DysonAppSettingKeys.AutomaticCodeReview, mapped, cancellationToken)
            .ConfigureAwait(false);
        if (persist.IsError)
            return Result<string, string>.AsError(persist.Error);

        return Result<string, string>.AsValue(mapped);
    }
}
