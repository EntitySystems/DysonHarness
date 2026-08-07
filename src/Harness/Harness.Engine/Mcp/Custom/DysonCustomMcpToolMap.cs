using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>Catalog name ↔ (serverId, remote tool name) for custom MCP tools.</summary>
public sealed partial class DysonCustomMcpToolMap
{
    private readonly Dictionary<string, (string ServerId, string RemoteName)> _byCatalog =
        new(StringComparer.Ordinal);
    private readonly Dictionary<(string ServerId, string RemoteName), string> _byRemote =
        new();

    [GeneratedRegex(@"[^A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeChars();

    public IReadOnlyDictionary<string, (string ServerId, string RemoteName)> ByCatalog => _byCatalog;

    public void Clear()
    {
        _byCatalog.Clear();
        _byRemote.Clear();
    }

    public static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "tool";

        var cleaned = UnsafeChars().Replace(value.Trim(), "_");
        if (cleaned.Length == 0)
            cleaned = "tool";
        if (!char.IsLetter(cleaned[0]) && cleaned[0] != '_')
            cleaned = "_" + cleaned;
        return cleaned;
    }

    public static string CatalogName(string serverId, string remoteToolName) =>
        $"{SanitizeSegment(serverId)}__{SanitizeSegment(remoteToolName)}";

    /// <summary>
    /// Registers a mapping. Returns false if the catalog name collides with a built-in
    /// or another mapping (caller should skip / rename).
    /// </summary>
    public bool TryAdd(string serverId, string remoteToolName, string catalogName, ISet<string>? reservedNames)
    {
        if (string.IsNullOrWhiteSpace(catalogName))
            return false;
        if (reservedNames is not null && reservedNames.Contains(catalogName))
            return false;
        if (_byCatalog.ContainsKey(catalogName))
            return false;

        _byCatalog[catalogName] = (serverId, remoteToolName);
        _byRemote[(serverId, remoteToolName)] = catalogName;
        return true;
    }

    public bool TryResolve(string catalogName, out string serverId, out string remoteToolName)
    {
        if (_byCatalog.TryGetValue(catalogName, out var pair))
        {
            serverId = pair.ServerId;
            remoteToolName = pair.RemoteName;
            return true;
        }

        serverId = "";
        remoteToolName = "";
        return false;
    }

    public bool IsCustomTool(string catalogName) => _byCatalog.ContainsKey(catalogName);

    public IReadOnlyList<string> CatalogNamesForServer(string serverId) =>
        _byCatalog
            .Where(kv => string.Equals(kv.Value.ServerId, serverId, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToArray();
}
