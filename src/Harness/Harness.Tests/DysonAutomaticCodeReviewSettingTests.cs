using DysonHarness;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert Automatic code review normalize / legacy map / one-shot persist.
/// </summary>
public class DysonAutomaticCodeReviewSettingTests
{
    [Fact]
    public void Run()
    {
        AssertNormalize();
        AssertFromLegacy();
        AssertResolveAndSelect();
        AssertReviewAction();
        AssertResolveAsyncMigratesOnce();
    }

    private static void AssertNormalize()
    {
        if (DysonAutomaticCodeReviewSetting.Normalize(null) != DysonAutomaticCodeReviewSetting.None
            || DysonAutomaticCodeReviewSetting.Normalize(" ") != DysonAutomaticCodeReviewSetting.None
            || DysonAutomaticCodeReviewSetting.Normalize("NOPE") != DysonAutomaticCodeReviewSetting.None)
        {
            throw new InvalidOperationException("Unknown / empty review setting must become none.");
        }

        if (DysonAutomaticCodeReviewSetting.Normalize(" LOW ") != DysonAutomaticCodeReviewSetting.Low
            || DysonAutomaticCodeReviewSetting.Normalize("Medium") != DysonAutomaticCodeReviewSetting.Medium
            || DysonAutomaticCodeReviewSetting.Normalize("HIGH") != DysonAutomaticCodeReviewSetting.High)
        {
            throw new InvalidOperationException("Known review tokens must normalize case-insensitively.");
        }
    }

    private static void AssertFromLegacy()
    {
        if (DysonAutomaticCodeReviewSetting.FromLegacy(null, "low") != DysonAutomaticCodeReviewSetting.None
            || DysonAutomaticCodeReviewSetting.FromLegacy("false", "medium") != DysonAutomaticCodeReviewSetting.None
            || DysonAutomaticCodeReviewSetting.FromLegacy("TRUE", "low") != DysonAutomaticCodeReviewSetting.Low
            || DysonAutomaticCodeReviewSetting.FromLegacy("true", "medium") != DysonAutomaticCodeReviewSetting.Medium
            || DysonAutomaticCodeReviewSetting.FromLegacy("true", "high") != DysonAutomaticCodeReviewSetting.Medium
            || DysonAutomaticCodeReviewSetting.FromLegacy("true", "weird") != DysonAutomaticCodeReviewSetting.Medium)
        {
            throw new InvalidOperationException("Legacy Boolean + intensity mapping mismatch.");
        }
    }

    private static void AssertResolveAndSelect()
    {
        if (DysonAutomaticCodeReviewSetting.Resolve("low", "true", "medium")
            != DysonAutomaticCodeReviewSetting.Low)
        {
            throw new InvalidOperationException("Present new key must win over legacy keys.");
        }

        if (DysonAutomaticCodeReviewSetting.Resolve(null, "true", "low")
            != DysonAutomaticCodeReviewSetting.Low)
        {
            throw new InvalidOperationException("Missing new key must map legacy values.");
        }

        if (!DysonAutomaticCodeReviewSetting.TrySelect("medium", out var selected)
            || selected != DysonAutomaticCodeReviewSetting.Medium
            || DysonAutomaticCodeReviewSetting.TrySelect("high", out _))
        {
            throw new InvalidOperationException("TrySelect must accept none/low/medium and reject high.");
        }

        if (DysonAutomaticCodeReviewSetting.ShouldEnqueueReview("none")
            || DysonAutomaticCodeReviewSetting.ShouldEnqueueReview("high")
            || !DysonAutomaticCodeReviewSetting.ShouldEnqueueReview("low")
            || !DysonAutomaticCodeReviewSetting.ShouldEnqueueReview("MEDIUM"))
        {
            throw new InvalidOperationException("Only low/medium should enqueue a review.");
        }
    }

    private static void AssertReviewAction()
    {
        if (DysonAutomaticCodeReviewSetting.NormalizeAction(null) != DysonAutomaticCodeReviewSetting.ReportOnly
            || DysonAutomaticCodeReviewSetting.NormalizeAction("unexpected") != DysonAutomaticCodeReviewSetting.ReportOnly
            || DysonAutomaticCodeReviewSetting.NormalizeAction(" AUTOMATICALLY_FIX ")
                != DysonAutomaticCodeReviewSetting.AutomaticallyFix
            || !DysonAutomaticCodeReviewSetting.TrySelectAction("report_only", out var reportOnly)
            || reportOnly != DysonAutomaticCodeReviewSetting.ReportOnly
            || !DysonAutomaticCodeReviewSetting.TrySelectAction("automatically_fix", out var fix)
            || fix != DysonAutomaticCodeReviewSetting.AutomaticallyFix
            || DysonAutomaticCodeReviewSetting.TrySelectAction("unexpected", out _))
        {
            throw new InvalidOperationException(
                "Automatic-review actions must normalize to report_only and accept only report_only/automatically_fix.");
        }

        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var keepAlive = conn;
        var settings = DysonTempDb.Settings(accessor);
        var first = DysonAutomaticCodeReviewSetting.ResolveActionAsync(settings).GetAwaiter().GetResult();
        if (first.IsError || first.Value != DysonAutomaticCodeReviewSetting.ReportOnly)
            throw new InvalidOperationException("Missing review action must persist report_only.");

        settings.SetSettingAsync(
                DysonAppSettingKeys.AutomaticCodeReviewAction,
                DysonAutomaticCodeReviewSetting.AutomaticallyFix)
            .GetAwaiter()
            .GetResult();
        var second = DysonAutomaticCodeReviewSetting.ResolveActionAsync(settings).GetAwaiter().GetResult();
        if (second.IsError || second.Value != DysonAutomaticCodeReviewSetting.AutomaticallyFix)
            throw new InvalidOperationException("Persisted automatically_fix action must take precedence.");
    }

    private static void AssertResolveAsyncMigratesOnce()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _keepAlive = conn;
        var settings = DysonTempDb.Settings(accessor);

        settings.SetSettingAsync(DysonAppSettingKeys.EndOfTaskAutoReview, "true")
            .GetAwaiter().GetResult();
        settings.SetSettingAsync(DysonAppSettingKeys.SelfReviewIntensity, "low")
            .GetAwaiter().GetResult();

        var first = DysonAutomaticCodeReviewSetting.ResolveAsync(settings).GetAwaiter().GetResult();
        if (first.IsError || first.Value != DysonAutomaticCodeReviewSetting.Low)
            throw new InvalidOperationException("First resolve must persist mapped low: " + (first.Error ?? first.Value));

        settings.SetSettingAsync(DysonAppSettingKeys.EndOfTaskAutoReview, "false")
            .GetAwaiter().GetResult();

        var second = DysonAutomaticCodeReviewSetting.ResolveAsync(settings).GetAwaiter().GetResult();
        if (second.IsError || second.Value != DysonAutomaticCodeReviewSetting.Low)
            throw new InvalidOperationException("Second resolve must keep the new key, not re-read legacy.");

        var stored = settings.GetSettingAsync(DysonAppSettingKeys.AutomaticCodeReview)
            .GetAwaiter().GetResult();
        if (stored.IsError || stored.Value != DysonAutomaticCodeReviewSetting.Low)
            throw new InvalidOperationException("New key must be persisted after first resolve.");
    }
}
