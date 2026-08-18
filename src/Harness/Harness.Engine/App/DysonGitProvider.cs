namespace DysonHarness;

/// <summary>
/// Classified git remote host. Not persisted — store slugs via
/// <see cref="DysonGitInfo.ToStoredSlug"/>.
/// </summary>
public enum DysonGitProvider
{
    None = 0,
    GitHub = 1,
    GitLab = 2,
    AzureDevOps = 3,
    CursorOrigin = 4,
    Other = 5,
}
