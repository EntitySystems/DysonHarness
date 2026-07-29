namespace DysonHarness;

/// <summary>
/// Local default and cloud interim evaluator: all permissions allowed.
/// Replace with claims-based evaluator when users/roles exist.
/// </summary>
public sealed class DysonPermissiveAccessEvaluator : IDysonAccessEvaluator
{
    public IReadOnlyList<DysonRole> Roles { get; } = [DysonRole.Member, DysonRole.Admin];

    public bool Can(DysonPermission permission) => true;
}
