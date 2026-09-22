namespace DysonHarness;

/// <summary>
/// Root Meta Agent scratch notes: leaf jail and the one cap scan.
/// Storage is <c>.dyson/scratch/*.md</c>. Tool descriptions and the directive do not name that location.
/// </summary>
internal static class DysonScratchNotes
{
    internal const string RelativeDirectory = ".dyson/scratch";
    internal const int MaxNotes = 20;
    internal const int MaxTokensPerNote = 1000;
    internal const int MaxTokensTotal = 20_000;

    internal const string RejectedNameMessage = "Note name must be a single name ending in .md.";
    internal const string ExistsReason = "A note with that name already exists.";
    internal const string MissingReason = "No note with that name exists.";
    internal const string CountReason = "20 notes already exist.";
    internal const string PerNoteReason = "That note would be over 1000 tokens.";
    internal const string TotalReason = "All notes together would be over 20000 tokens.";
    internal const string StorageFailureMessage = "The note could not be saved.";

    internal enum Kind
    {
        Probe,
        Create,
        Update,
    }

    internal readonly record struct Note(string Name, int Tokens);

    internal readonly record struct Check(
        bool Allowed,
        string? Reason,
        int NoteCount,
        int TotalTokens,
        int? NoteTokens,
        IReadOnlyList<Note> Notes);

    internal static Result<string, string> ResolveLeaf(IDysonWorkspaceFileSystem fs, string name)
    {
        ArgumentNullException.ThrowIfNull(fs);
        if (!IsSingleMdLeaf(name))
            return Result<string, string>.AsError(RejectedNameMessage);

        var relative = RelativeDirectory + "/" + name;
        var resolved = fs.ResolvePath(relative);
        if (resolved.IsError)
            return Result<string, string>.AsError(RejectedNameMessage);

        var rel = fs.GetRelativePath(resolved.Value);
        if (rel.IsError || !RelativeIsExactLeaf(rel.Value, name))
            return Result<string, string>.AsError(RejectedNameMessage);

        return Result<string, string>.AsValue(relative);
    }

    internal static async Task<Result<Check, string>> CheckWriteAsync(
        IDysonWorkspaceFileSystem fs,
        IDysonTokenCounter tokens,
        string? leafName,
        string? resultingText,
        Kind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(tokens);

        string? relative = null;
        if (!string.IsNullOrWhiteSpace(leafName))
        {
            var leaf = ResolveLeaf(fs, leafName);
            if (leaf.IsError)
                return Result<Check, string>.AsError(leaf.Error);
            relative = leaf.Value;
        }
        else if (kind != Kind.Probe)
        {
            return Result<Check, string>.AsError("A note name is required.");
        }

        var rosterResult = await ReadRosterAsync(fs, tokens, cancellationToken).ConfigureAwait(false);
        if (rosterResult.IsError)
            return Result<Check, string>.AsError(rosterResult.Error);

        var notes = rosterResult.Value;
        var total = 0;
        foreach (var note in notes)
            total += note.Tokens;

        var exists = false;
        var inRoster = false;
        var oldTokens = 0;
        if (relative is not null)
        {
            var existsResult = await fs.FileExistsAsync(relative, cancellationToken).ConfigureAwait(false);
            if (existsResult.IsError)
                return Result<Check, string>.AsError(HideStorageFailure(existsResult.Error));
            exists = existsResult.Value;

            foreach (var note in notes)
            {
                if (!string.Equals(note.Name, leafName, StringComparison.OrdinalIgnoreCase))
                    continue;
                inRoster = true;
                oldTokens = note.Tokens;
                break;
            }

            if (exists && !inRoster)
            {
                var read = await fs.ReadAllTextAsync(relative, cancellationToken).ConfigureAwait(false);
                if (read.IsError)
                    return Result<Check, string>.AsError(HideStorageFailure(read.Error));
                oldTokens = tokens.CountTokens(read.Value);
            }
        }

        int? noteTokens = relative is null
            ? null
            : resultingText is not null
                ? tokens.CountTokens(resultingText)
                : exists
                    ? oldTokens
                    : tokens.CountTokens("");

        var counted = noteTokens ?? 0;
        var allowed = true;
        string? reason = null;

        if (relative is null)
        {
            // No-arg probe: another small note is possible when the count is under the cap.
            if (notes.Count >= MaxNotes)
                Deny(ref allowed, ref reason, CountReason);
        }
        else if (kind == Kind.Update && !exists)
        {
            Deny(ref allowed, ref reason, MissingReason);
        }
        else if (kind == Kind.Create && exists)
        {
            Deny(ref allowed, ref reason, ExistsReason);
        }
        else if (exists && kind != Kind.Create)
        {
            if (counted > MaxTokensPerNote)
                Deny(ref allowed, ref reason, PerNoteReason);
            var projected = inRoster ? total - oldTokens + counted : total + counted;
            if (projected > MaxTokensTotal)
                Deny(ref allowed, ref reason, TotalReason);
        }
        else
        {
            if (exists)
                Deny(ref allowed, ref reason, ExistsReason);
            if (counted > MaxTokensPerNote)
                Deny(ref allowed, ref reason, PerNoteReason);
            if (notes.Count >= MaxNotes)
                Deny(ref allowed, ref reason, CountReason);
            if (total + counted > MaxTokensTotal)
                Deny(ref allowed, ref reason, TotalReason);
        }

        return Result<Check, string>.AsValue(new Check(
            allowed,
            reason,
            notes.Count,
            total,
            noteTokens,
            notes));
    }

    internal static string HideStorageFailure(string message)
    {
        if (message.Contains(".dyson", StringComparison.OrdinalIgnoreCase)
            || message.Contains('\\')
            || message.Contains('/')
            || message.Contains("Path escapes", StringComparison.Ordinal))
        {
            return StorageFailureMessage;
        }

        return message;
    }

    private static void Deny(ref bool allowed, ref string? reason, string next)
    {
        if (!allowed)
            return;
        allowed = false;
        reason = next;
    }

    private static async Task<Result<IReadOnlyList<Note>, string>> ReadRosterAsync(
        IDysonWorkspaceFileSystem fs,
        IDysonTokenCounter tokens,
        CancellationToken cancellationToken)
    {
        var dir = await fs.DirectoryExistsAsync(RelativeDirectory, cancellationToken).ConfigureAwait(false);
        if (dir.IsError)
            return Result<IReadOnlyList<Note>, string>.AsError(HideStorageFailure(dir.Error));
        if (!dir.Value)
            return Result<IReadOnlyList<Note>, string>.AsValue([]);

        var entries = await fs.EnumerateEntriesAsync(RelativeDirectory, cancellationToken).ConfigureAwait(false);
        if (entries.IsError)
        {
            if (entries.Error.Contains("Directory not found", StringComparison.Ordinal))
                return Result<IReadOnlyList<Note>, string>.AsValue([]);
            return Result<IReadOnlyList<Note>, string>.AsError(HideStorageFailure(entries.Error));
        }

        var notes = new List<Note>();
        foreach (var entry in entries.Value)
        {
            if (entry.IsDirectory)
                continue;
            var leaf = ResolveLeaf(fs, entry.Name);
            if (leaf.IsError)
                continue;

            var read = await fs.ReadAllTextAsync(leaf.Value, cancellationToken).ConfigureAwait(false);
            if (read.IsError)
                return Result<IReadOnlyList<Note>, string>.AsError(HideStorageFailure(read.Error));
            notes.Add(new Note(entry.Name, tokens.CountTokens(read.Value)));
        }

        notes.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return Result<IReadOnlyList<Note>, string>.AsValue(notes);
    }

    private static bool IsSingleMdLeaf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            return false;
        if (Path.IsPathRooted(name) || name.IndexOfAny(['/', '\\']) >= 0)
            return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.'))
            return false;
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            return false;
        return string.Equals(Path.GetExtension(name), ".md", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RelativeIsExactLeaf(string relative, string name)
    {
        var parts = relative.Split('/');
        return parts.Length == 3
            && parts[0].Equals(".dyson", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("scratch", StringComparison.OrdinalIgnoreCase)
            && parts[2].Equals(name, StringComparison.Ordinal);
    }
}
