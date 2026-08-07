using System.Text;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary><c>${env:VAR}</c> expansion and optional <c>envFile</c> loading for custom MCP configs.</summary>
public static partial class DysonCustomMcpEnv
{
    [GeneratedRegex(@"\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex EnvTokenRegex();

    /// <summary>
    /// Loads KEY=VALUE pairs from a dotenv-style file (comments and blank lines skipped).
    /// Relative paths resolve against <paramref name="workRoot"/>.
    /// </summary>
    public static Result<Dictionary<string, string>, string> LoadEnvFile(
        string workRoot,
        string? envFilePath)
    {
        if (string.IsNullOrWhiteSpace(envFilePath))
            return Result<Dictionary<string, string>, string>.AsValue(
                new Dictionary<string, string>(StringComparer.Ordinal));

        string fullPath;
        try
        {
            fullPath = Path.IsPathRooted(envFilePath)
                ? Path.GetFullPath(envFilePath.Trim())
                : Path.GetFullPath(Path.Combine(workRoot, envFilePath.Trim()));
        }
        catch (Exception ex)
        {
            return Result<Dictionary<string, string>, string>.AsError($"Invalid envFile path: {ex.Message}");
        }

        if (!File.Exists(fullPath))
            return Result<Dictionary<string, string>, string>.AsError($"envFile not found: {fullPath}");

        try
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rawLine in File.ReadAllLines(fullPath, Encoding.UTF8))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                var eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (value.Length >= 2
                    && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                {
                    value = value[1..^1];
                }

                if (key.Length > 0)
                    map[key] = value;
            }

            return Result<Dictionary<string, string>, string>.AsValue(map);
        }
        catch (Exception ex)
        {
            return Result<Dictionary<string, string>, string>.AsError($"Failed to read envFile: {ex.Message}");
        }
    }

    /// <summary>
    /// Expands <c>${env:NAME}</c> using process env first, then <paramref name="fileEnv"/> overrides for lookup.
    /// Unresolved tokens stay as-is (callers may still fail later on auth).
    /// </summary>
    public static string Expand(string? value, IReadOnlyDictionary<string, string>? fileEnv = null)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? "";

        return EnvTokenRegex().Replace(value, match =>
        {
            var name = match.Groups[1].Value;
            if (fileEnv is not null && fileEnv.TryGetValue(name, out var fromFile))
                return fromFile;

            return Environment.GetEnvironmentVariable(name) ?? match.Value;
        });
    }

    public static Dictionary<string, string> ExpandMap(
        IReadOnlyDictionary<string, string>? source,
        IReadOnlyDictionary<string, string>? fileEnv = null)
    {
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        if (source is null)
            return result;

        foreach (var (key, value) in source)
            result[key] = Expand(value, fileEnv);

        return result;
    }
}
