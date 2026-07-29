namespace DysonHarness;

/// <summary>Permissions checked via <see cref="IDysonAccessEvaluator"/>.</summary>
public enum DysonPermission
{
    /// <summary>
    /// Mutate data owned by the current subject.
    /// Own-data checks may stay implicit via subject filter; permission exists for host overrides.
    /// </summary>
    ManageOwnSubjectData = 0,

    /// <summary>Create / update / delete shared model providers (<see cref="DysonSubjects.Shared"/>).</summary>
    ManageSharedProviders = 1,
}
