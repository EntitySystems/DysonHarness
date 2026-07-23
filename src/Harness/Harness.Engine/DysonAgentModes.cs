namespace DysonHarness;

public static class DysonAgentModes
{
    public const string Ask = "Ask";
    public const string Plan = "Plan";
    public const string Work = "Work";
    public const string Explore = "Explore";
    public const string Drone = "Drone";
    public const string SecurityReview = "Security Review";
    public const string BugReview = "Bug Review";
    /// <summary>Category label; lookup uses Config.CustomAgents keys, not this literal.</summary>
    public const string Custom = "Custom";
}
