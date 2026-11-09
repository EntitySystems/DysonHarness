namespace Harness.UI.Components.Pages;

/// <summary>
/// When the shell may force a move to <c>/meta/{id}</c>. Only an explicit user selection of a
/// work directory qualifies: the panel, the dropdown, and the on-screen <c>SwitchWorkdir</c>
/// path. Restoring a remembered workdir, and any path that loads a session, never does.
/// </summary>
public static class HomeMetaNavigation
{
    public static bool ShouldRedirectToMeta(bool userSelectedWorkDirectory) => userSelectedWorkDirectory;
}
