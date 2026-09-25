using System.Globalization;

namespace DysonHarness;

/// <summary>
/// Display-path scheme for DB-backed meta plans in the file viewer
/// (<c>metaplan:{planId}/{slug}.md</c>). The <c>.md</c> suffix is load-bearing
/// for markdown rendering and comment anchors. Not a filesystem path.
/// </summary>
public static class DysonMetaPlanDisplayPath
{
    public const string Prefix = "metaplan:";

    /// <summary>
    /// True when the list/overlay Build action may run: not already
    /// <see cref="DysonPlanStatus.Building"/>, and a Meta Agent runtime is attached.
    /// </summary>
    public static bool CanBuild(DysonPlanStatus status, bool sessionReady) =>
        status != DysonPlanStatus.Building && sessionReady;

    public static string Format(long planId, string? title)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(planId);
        var slug = DysonFileManager.SanitizeSlug(title);
        return $"{Prefix}{planId}/{slug}.md";
    }

    public static bool TryParse(string? relativePath, out long planId)
    {
        planId = 0;
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var path = relativePath.Trim().Replace('\\', '/');
        if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = path[Prefix.Length..];
        if (rest.Length == 0)
            return false;

        var slash = rest.IndexOf('/');
        var idPart = slash < 0 ? rest : rest[..slash];
        if (idPart.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            idPart = idPart[..^3];

        return long.TryParse(idPart, NumberStyles.None, CultureInfo.InvariantCulture, out planId)
            && planId > 0;
    }

    /// <summary>
    /// Session message that asks the Meta Agent to call <c>BeginBuildPlan(planId)</c>.
    /// Does not spawn a drone and does not set status — the tool does both.
    /// </summary>
    public static string FormatBuildPrompt(long planId, string? title)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(planId);
        var name = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        return $"Begin build of plan `{planId}` ({name}). Call BeginBuildPlan with that planId.";
    }
}
