using DysonHarness;

namespace Harness.UI.Components.Meta.A2ui;

/// <summary>The Image the surface's large view shows: node id (closes when the node goes), alt, caption, card and its own preview URL.</summary>
public sealed record A2uiImageOpen(string NodeId, string Alt, string Caption, DysonA2uiImageCard Card, string PreviewUrl);
