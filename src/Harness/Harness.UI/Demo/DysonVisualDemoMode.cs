using DysonHarness;
using Microsoft.Extensions.Configuration;

namespace Harness.UI.Demo;

/// <summary>
/// Process-wide Demo Mode: scripted mock replay through the real engine + UI.
/// Enabled by <c>--demo</c> / <c>--visual-demo</c>, <c>DYSON_VISUAL_DEMO=1</c>,
/// or config <c>Dyson:VisualDemo</c>.
/// </summary>
/// <remarks>
/// ponytail: static <see cref="Current"/> so <see cref="DemoDysonAgentSession"/> can
/// read the flag without a new constructor/DI layer. Host Create() assigns it once.
/// Upgrade: thread the instance through session create if a second demo flavor appears.
/// </remarks>
public sealed class DysonVisualDemoMode
{
    public const string FlagName = "--demo";
    public const string AlternateFlagName = "--visual-demo";
    public const string EnvironmentVariableName = "DYSON_VISUAL_DEMO";
    public const string ConfigurationKey = "Dyson:VisualDemo";

    public static DysonVisualDemoMode Disabled { get; } = new(enabled: false);

    /// <summary>Assigned by the web host at process start. Tests may replace and restore.</summary>
    public static DysonVisualDemoMode Current { get; set; } = Disabled;

    private int _autoPlayStarted;

    public DysonVisualDemoMode(bool enabled) => Enabled = enabled;

    public bool Enabled { get; }

    /// <summary>
    /// Root persistence id of the auto-played showcase session. Children inherit via parent walk.
    /// Null means every demo session uses the script (tests / before auto-play assigns an id).
    /// </summary>
    public Guid? AutoPlaySessionId { get; set; }

    public static DysonVisualDemoMode FromCommandLine(
        IEnumerable<string>? args,
        IConfiguration? configuration = null)
    {
        if (args is not null)
        {
            foreach (var arg in args)
            {
                if (string.Equals(arg, FlagName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, AlternateFlagName, StringComparison.OrdinalIgnoreCase))
                {
                    return new DysonVisualDemoMode(enabled: true);
                }
            }
        }

        if (IsTruthy(Environment.GetEnvironmentVariable(EnvironmentVariableName)))
            return new DysonVisualDemoMode(enabled: true);

        if (IsTruthy(configuration?[ConfigurationKey]))
            return new DysonVisualDemoMode(enabled: true);

        return Disabled;
    }

    public bool AppliesTo(DysonAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Enabled)
            return false;
        if (AutoPlaySessionId is null)
            return true;

        var root = session;
        while (root.Parent is not null)
            root = root.Parent;
        return root.PersistenceId == AutoPlaySessionId;
    }

    /// <summary>First caller wins so two circuits cannot both auto-prompt.</summary>
    public bool TryBeginAutoPlay() =>
        Enabled && Interlocked.CompareExchange(ref _autoPlayStarted, 1, 0) == 0;

    public void ResetAutoPlay()
    {
        Volatile.Write(ref _autoPlayStarted, 0);
        AutoPlaySessionId = null;
    }

    public static bool IsTruthy(string? value) =>
        value is not null
        && (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
}
