using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// Executes workspace-scoped MCP tools against a work directory root, plus RenameSession.
/// Other catalog tools return a not-implemented stub result.
/// </summary>
public sealed class DysonWorkspaceToolExecutor
{
    private readonly DysonAgentSession _session;
    private readonly string _workRoot;
    private readonly DysonSessionStore? _store;

    public DysonWorkspaceToolExecutor(
        DysonAgentSession session,
        string workDirectoryAbsolutePath,
        DysonSessionStore? store = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectoryAbsolutePath);
        _workRoot = Path.GetFullPath(workDirectoryAbsolutePath);
        _store = store;
    }

    public async Task<DysonToolCallResult> ExecuteAsync(
        DysonToolCall call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        try
        {
            return call.ToolName switch
            {
                "RenameSession" => await RenameSessionAsync(call, cancellationToken).ConfigureAwait(false),
                "ReadFile" => await ReadFileAsync(call, cancellationToken).ConfigureAwait(false),
                "CreateFile" => await CreateFileAsync(call, cancellationToken).ConfigureAwait(false),
                "WriteFile" => await WriteFileAsync(call, cancellationToken).ConfigureAwait(false),
                "Grep" => await GrepAsync(call, cancellationToken).ConfigureAwait(false),
                "ListDirectory" => await ListDirectoryAsync(call, cancellationToken).ConfigureAwait(false),
                "CreateDirectory" => await CreateDirectoryAsync(call, cancellationToken).ConfigureAwait(false),
                _ => Stub(call),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error(call, "Tool execution was cancelled.");
        }
        catch (Exception ex)
        {
            return Error(call, $"{call.ToolName} failed: {ex.Message}");
        }
    }

    private static DysonToolCallResult Stub(DysonToolCall call) =>
        new()
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = false,
            Content = $"{call.ToolName} is not implemented yet.",
        };

    private static DysonToolCallResult Ok(DysonToolCall call, string content) =>
        new()
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = false,
            Content = content,
        };

    private static DysonToolCallResult Error(DysonToolCall call, string content) =>
        new()
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = true,
            Content = content,
        };

    private async Task<DysonToolCallResult> RenameSessionAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        string? title = null;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            if (doc.RootElement.TryGetProperty("title", out var titleProp))
                title = titleProp.GetString();
        }
        catch (JsonException)
        {
            return Error(call, "RenameSession: invalid JSON arguments.");
        }

        var rename = await _session.RenameAsync(title ?? "", cancellationToken).ConfigureAwait(false);
        if (rename.IsError)
            return Error(call, rename.Error);

        if (_store is not null && _session.PersistenceId != Guid.Empty)
        {
            var persist = await _store.UpdateSessionMetaAsync(
                new DysonSessionMetaUpdate
                {
                    SessionId = _session.PersistenceId,
                    Title = _session.DisplayTitle,
                },
                cancellationToken).ConfigureAwait(false);

            if (persist.IsError)
                return Error(call, persist.Error);

            var renamedLog = DysonSessionLogPayload.CreateEntry(
                _session.PersistenceId,
                DysonSessionLogKind.SessionRenamed,
                new DysonSessionLogSessionRenamed(_session.DisplayTitle!));

            await _store.AppendLogAsync(renamedLog, cancellationToken).ConfigureAwait(false);
        }

        return Ok(call, $"Renamed session to \"{_session.DisplayTitle}\".");
    }

    private async Task<DysonToolCallResult> ReadFileAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Error(call, path.Error);

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Error(call, resolved.Error);

        if (!File.Exists(resolved.Value))
            return Error(call, $"File not found: {path.Value}");

        var lines = await File.ReadAllLinesAsync(resolved.Value, cancellationToken).ConfigureAwait(false);
        var offset = GetInt(doc.RootElement, "offset") ?? 1;
        var limit = GetInt(doc.RootElement, "limit");
        if (offset < 1)
            offset = 1;

        var start = Math.Min(offset - 1, lines.Length);
        var take = limit is null ? lines.Length - start : Math.Max(0, limit.Value);
        var slice = lines.Skip(start).Take(take);

        var sb = new StringBuilder();
        var lineNo = start + 1;
        foreach (var line in slice)
        {
            sb.Append(lineNo);
            sb.Append('|');
            sb.AppendLine(line);
            lineNo++;
        }

        return Ok(call, sb.Length == 0 ? "(empty)" : sb.ToString().TrimEnd());
    }

    private async Task<DysonToolCallResult> CreateFileAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Error(call, path.Error);

        var content = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        var overwrite = doc.RootElement.TryGetProperty("overwrite", out var o)
            && o.ValueKind == JsonValueKind.True;

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Error(call, resolved.Error);

        if (File.Exists(resolved.Value) && !overwrite)
            return Error(call, $"File already exists: {path.Value}");

        var dir = Path.GetDirectoryName(resolved.Value);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(resolved.Value, content, cancellationToken).ConfigureAwait(false);
        return Ok(call, $"Created {path.Value} ({content.Length} chars).");
    }

    private async Task<DysonToolCallResult> WriteFileAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Error(call, path.Error);

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Error(call, resolved.Error);

        if (!File.Exists(resolved.Value)
            && !(doc.RootElement.TryGetProperty("content", out var fullContentProp)
                 && fullContentProp.ValueKind == JsonValueKind.String))
        {
            return Error(call, $"File not found: {path.Value}");
        }

        if (doc.RootElement.TryGetProperty("content", out var contentProp)
            && contentProp.ValueKind == JsonValueKind.String
            && !doc.RootElement.TryGetProperty("old_text", out _)
            && !doc.RootElement.TryGetProperty("edits", out _))
        {
            var dir = Path.GetDirectoryName(resolved.Value);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var full = contentProp.GetString() ?? "";
            await File.WriteAllTextAsync(resolved.Value, full, cancellationToken).ConfigureAwait(false);
            return Ok(call, $"Wrote full content to {path.Value} ({full.Length} chars).");
        }

        var text = await File.ReadAllTextAsync(resolved.Value, cancellationToken).ConfigureAwait(false);
        var edits = new List<(string Old, string New)>();

        if (doc.RootElement.TryGetProperty("old_text", out var oldProp)
            && doc.RootElement.TryGetProperty("new_text", out var newProp))
        {
            edits.Add((oldProp.GetString() ?? "", newProp.GetString() ?? ""));
        }

        if (doc.RootElement.TryGetProperty("edits", out var editsArr)
            && editsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var edit in editsArr.EnumerateArray())
            {
                if (!edit.TryGetProperty("old_text", out var o) || !edit.TryGetProperty("new_text", out var n))
                    continue;
                edits.Add((o.GetString() ?? "", n.GetString() ?? ""));
            }
        }

        if (edits.Count == 0)
            return Error(call, "WriteFile: provide content, or old_text/new_text, or edits[].");

        var applied = 0;
        foreach (var (oldText, newText) in edits)
        {
            if (string.IsNullOrEmpty(oldText))
                return Error(call, "WriteFile: old_text must be non-empty.");

            var idx = text.IndexOf(oldText, StringComparison.Ordinal);
            if (idx < 0)
                return Error(call, $"WriteFile: old_text not found in {path.Value}.");

            text = string.Concat(text.AsSpan(0, idx), newText, text.AsSpan(idx + oldText.Length));
            applied++;
        }

        await File.WriteAllTextAsync(resolved.Value, text, cancellationToken).ConfigureAwait(false);
        return Ok(call, $"Applied {applied} edit(s) to {path.Value}.");
    }

    private async Task<DysonToolCallResult> GrepAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var pattern = RequireString(doc.RootElement, "pattern");
        if (pattern.IsError)
            return Error(call, pattern.Error);

        var searchPath = doc.RootElement.TryGetProperty("path", out var pathProp)
            ? pathProp.GetString() ?? "."
            : ".";
        var glob = doc.RootElement.TryGetProperty("glob", out var globProp)
            ? globProp.GetString()
            : null;
        var caseInsensitive = doc.RootElement.TryGetProperty("caseInsensitive", out var ci)
            && ci.ValueKind == JsonValueKind.True;
        var maxMatches = GetInt(doc.RootElement, "maxMatches") ?? 100;

        var resolved = ResolveUnderWorkRoot(searchPath);
        if (resolved.IsError)
            return Error(call, resolved.Error);

        Regex regex;
        try
        {
            regex = new Regex(
                pattern.Value,
                (caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None)
                | RegexOptions.Compiled,
                TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return Error(call, $"Invalid regex: {ex.Message}");
        }

        IEnumerable<string> files;
        if (File.Exists(resolved.Value))
        {
            files = [resolved.Value];
        }
        else if (Directory.Exists(resolved.Value))
        {
            var option = SearchOption.AllDirectories;
            files = string.IsNullOrWhiteSpace(glob)
                ? Directory.EnumerateFiles(resolved.Value, "*", option)
                : Directory.EnumerateFiles(resolved.Value, glob, option);
        }
        else
        {
            return Error(call, $"Path not found: {searchPath}");
        }

        var sb = new StringBuilder();
        var matches = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsUnderWorkRoot(file))
                continue;

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            var rel = Path.GetRelativePath(_workRoot, file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i]))
                    continue;

                sb.Append(rel);
                sb.Append(':');
                sb.Append(i + 1);
                sb.Append(':');
                sb.AppendLine(lines[i]);
                matches++;
                if (matches >= maxMatches)
                    return Ok(call, sb.ToString().TrimEnd() + $"\n… capped at {maxMatches} matches");
            }
        }

        return Ok(call, matches == 0 ? "No matches." : sb.ToString().TrimEnd());
    }

    private Task<DysonToolCallResult> ListDirectoryAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Task.FromResult(Error(call, path.Error));

        var recursive = doc.RootElement.TryGetProperty("recursive", out var r)
            && r.ValueKind == JsonValueKind.True;

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Task.FromResult(Error(call, resolved.Error));

        if (!Directory.Exists(resolved.Value))
            return Task.FromResult(Error(call, $"Directory not found: {path.Value}"));

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var entries = Directory.EnumerateFileSystemEntries(resolved.Value, "*", option)
            .Take(500)
            .Select(e =>
            {
                var rel = Path.GetRelativePath(_workRoot, e);
                var kind = Directory.Exists(e) ? "dir" : "file";
                return $"{kind}\t{rel}";
            });

        var text = string.Join('\n', entries);
        return Task.FromResult(Ok(call, string.IsNullOrEmpty(text) ? "(empty)" : text));
    }

    private Task<DysonToolCallResult> CreateDirectoryAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Task.FromResult(Error(call, path.Error));

        var createParents = !doc.RootElement.TryGetProperty("createParents", out var cp)
            || cp.ValueKind != JsonValueKind.False;

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Task.FromResult(Error(call, resolved.Error));

        if (createParents)
            Directory.CreateDirectory(resolved.Value);
        else
            Directory.CreateDirectory(resolved.Value); // CreateDirectory always creates parents on .NET

        return Task.FromResult(Ok(call, $"Created directory {path.Value}."));
    }

    private Result<string, string> ResolveUnderWorkRoot(string path)
    {
        try
        {
            var combined = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(_workRoot, path));

            if (!IsUnderWorkRoot(combined))
                return Result<string, string>.AsError($"Path escapes work directory: {path}");

            return Result<string, string>.AsValue(combined);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid path: {ex.Message}");
        }
    }

    private bool IsUnderWorkRoot(string fullPath)
    {
        var root = _workRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(fullPath);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                _workRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return true;
        }

        return full.StartsWith(
            root,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string ArgsOrEmpty(DysonToolCall call) =>
        string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;

    private static Result<string, string> RequireString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return Result<string, string>.AsError($"Missing required string field '{name}'.");

        var value = prop.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return Result<string, string>.AsError($"Field '{name}' must be non-empty.");

        return Result<string, string>.AsValue(value);
    }

    private static int? GetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop))
            return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var i))
            return i;
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
            return parsed;
        return null;
    }
}
