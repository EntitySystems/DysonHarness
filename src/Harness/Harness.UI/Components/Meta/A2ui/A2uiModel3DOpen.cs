using DysonHarness;

namespace Harness.UI.Components.Meta.A2ui;

/// <summary>The Model3D the surface's large view shows: node id (closes when the node goes), title, card and its preview URL.</summary>
public sealed record A2uiModel3DOpen(string NodeId, string Title, DysonA2uiModel3DCard Card, string PreviewUrl);
