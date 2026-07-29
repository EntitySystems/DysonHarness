using Harness.UI.Files;

namespace Harness.UI.Components.Files;

public sealed record FileTreeFolderContextMenuArgs(
    DysonFileTreeNode Node,
    double ClientX,
    double ClientY);
