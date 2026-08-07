using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>Typed helpers for work-directory <see cref="JsonNode"/> config documents.</summary>
public static class DysonWorkDirectoryConfig
{
    public const string McpActiveKey = "mcpActive";

    /// <summary>Default document when no DB row exists (MCP on by default).</summary>
    public static JsonObject CreateDefault() =>
        new() { [McpActiveKey] = true };

    /// <summary>
    /// Reads <c>mcpActive</c>. Missing key or null document ⇒ <c>true</c>.
    /// Non-boolean values ⇒ <c>true</c> (opt-out only via explicit <c>false</c>).
    /// </summary>
    public static bool TryGetMcpActive(JsonNode? config)
    {
        if (config is null)
            return true;

        var node = config[McpActiveKey];
        if (node is null)
            return true;

        return node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.True => true,
            _ => true,
        };
    }

    /// <summary>Sets <c>mcpActive</c> on a mutable object (creates object if needed).</summary>
    public static JsonObject WithMcpActive(JsonNode? config, bool mcpActive)
    {
        var obj = config as JsonObject ?? config?.DeepClone() as JsonObject ?? CreateDefault();
        obj[McpActiveKey] = mcpActive;
        return obj;
    }
}
