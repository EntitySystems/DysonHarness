namespace DysonHarness;

/// <summary>
/// RBAC gate for persistence mutations that need more than subject filtering
/// (especially shared model providers). Stub until roles bind to users.
/// </summary>
public interface IDysonAccessEvaluator
{
    /// <summary>Roles foreshadowed for the current principal (may be empty until auth exists).</summary>
    IReadOnlyList<DysonRole> Roles { get; }

    bool Can(DysonPermission permission);
}
