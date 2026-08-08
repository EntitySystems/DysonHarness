using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>Trusted active-workspace context used to form a project plugin install target.</summary>
public sealed record DysonPluginProjectContext
{
    public required Guid WorkDirectoryId { get; init; }
    public required string WorkDirectoryName { get; init; }
    public required IDysonWorkspaceFileSystem FileSystem { get; init; }
}
