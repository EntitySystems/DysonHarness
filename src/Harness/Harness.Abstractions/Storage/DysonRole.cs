namespace DysonHarness;

/// <summary>Intended hosting roles (assignment / auth mapping not implemented yet).</summary>
public enum DysonRole
{
    /// <summary>Own subject data; use shared providers; manage own favorites/settings/sessions/workdirs/shells.</summary>
    Member = 0,

    /// <summary>Member plus manage shared model providers (and later deploy-wide settings).</summary>
    Admin = 1,
}
