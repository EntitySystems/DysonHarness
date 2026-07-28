using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

/// <summary>CRUD + seed for <see cref="DysonConfiguredShellEntity"/>.</summary>
public sealed class DysonConfiguredShellStore(DysonDbAccessor accessor)
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));

    /// <summary>
    /// When the table is empty, seeds platform defaults (Windows: Pwsh / PowerShell / Cmd).
    /// Other platforms seed nothing until Bash/Zsh runners exist.
    /// </summary>
    public Task<VoidResult<string>> EnsureDefaultsAsync(CancellationToken cancellationToken = default)
        => _accessor.RunAsync(EnsureDefaultsCoreAsync, cancellationToken);

    public Task<Result<IReadOnlyList<DysonConfiguredShellEntity>, string>> ListAsync(
        CancellationToken cancellationToken = default)
        => _accessor.RunAsync(ListCoreAsync, cancellationToken);

    /// <summary>Enabled shells as session specs, ordered by <see cref="DysonConfiguredShellEntity.SortOrder"/>.</summary>
    public Task<Result<IReadOnlyList<DysonConfiguredShellSpec>, string>> ListEnabledSpecsAsync(
        CancellationToken cancellationToken = default)
        => _accessor.RunAsync(ListEnabledSpecsCoreAsync, cancellationToken);

    public Task<Result<Guid, string>> CreateAsync(
        string name,
        string executablePath,
        bool isEnabled = true,
        IReadOnlyList<string>? fixedArgs = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = (name ?? "").Trim();
        var trimmedPath = (executablePath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
            return Task.FromResult(Result<Guid, string>.AsError("Shell name is required."));
        if (string.IsNullOrWhiteSpace(trimmedPath))
            return Task.FromResult(Result<Guid, string>.AsError("Executable path is required."));

        return _accessor.RunAsync(
            (db, ct) => CreateCoreAsync(db, trimmedName, trimmedPath, isEnabled, fixedArgs, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> UpdateAsync(
        Guid id,
        string name,
        string executablePath,
        bool isEnabled,
        IReadOnlyList<string>? fixedArgs = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = (name ?? "").Trim();
        var trimmedPath = (executablePath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
            return Task.FromResult(new VoidResult<string>("Shell name is required."));
        if (string.IsNullOrWhiteSpace(trimmedPath))
            return Task.FromResult(new VoidResult<string>("Executable path is required."));

        return _accessor.RunAsync(
            (db, ct) => UpdateCoreAsync(db, id, trimmedName, trimmedPath, isEnabled, fixedArgs, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
        => _accessor.RunAsync((db, ct) => DeleteCoreAsync(db, id, ct), cancellationToken);

    /// <summary>Space-separated UI tokens → argv list (empty ⇒ null / heuristics).</summary>
    public static IReadOnlyList<string>? ParseFixedArgsText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }

    /// <summary>JSON array column → argv list (null/empty/invalid ⇒ null).</summary>
    public static IReadOnlyList<string>? ParseFixedArgs(string? fixedArgsJson)
    {
        if (string.IsNullOrWhiteSpace(fixedArgsJson))
            return null;

        try
        {
            var arr = JsonSerializer.Deserialize<string[]>(fixedArgsJson);
            return arr is { Length: > 0 } ? arr : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Argv list → JSON array column (null/empty ⇒ null).</summary>
    public static string? ToFixedArgsJson(IReadOnlyList<string>? fixedArgs)
    {
        if (fixedArgs is null || fixedArgs.Count == 0)
            return null;

        var cleaned = fixedArgs
            .Select(a => (a ?? "").Trim())
            .Where(a => a.Length > 0)
            .ToArray();
        return cleaned.Length == 0 ? null : JsonSerializer.Serialize(cleaned);
    }

    /// <summary>JSON column → space-separated UI text.</summary>
    public static string FixedArgsToText(string? fixedArgsJson)
    {
        var args = ParseFixedArgs(fixedArgsJson);
        return args is null ? "" : string.Join(' ', args);
    }

    private static async Task<VoidResult<string>> EnsureDefaultsCoreAsync(
        DysonDbContext db,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await db.ConfiguredShells.AnyAsync(cancellationToken).ConfigureAwait(false))
                return VoidResult<string>.Success;

            if (!OperatingSystem.IsWindows())
                return VoidResult<string>.Success;

            var now = DateTime.UtcNow;
            db.ConfiguredShells.AddRange(
                new DysonConfiguredShellEntity
                {
                    Id = Guid.NewGuid(),
                    Name = "Pwsh",
                    ExecutablePath = "pwsh",
                    IsEnabled = true,
                    SortOrder = 0,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                },
                new DysonConfiguredShellEntity
                {
                    Id = Guid.NewGuid(),
                    Name = "PowerShell",
                    ExecutablePath = "powershell.exe",
                    IsEnabled = true,
                    SortOrder = 1,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                },
                new DysonConfiguredShellEntity
                {
                    Id = Guid.NewGuid(),
                    Name = "Cmd",
                    ExecutablePath = "cmd.exe",
                    IsEnabled = true,
                    SortOrder = 2,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                });

            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return new VoidResult<string>($"Failed to seed configured shells: {ex.Message}");
        }
    }

    private static async Task<Result<IReadOnlyList<DysonConfiguredShellEntity>, string>> ListCoreAsync(
        DysonDbContext db,
        CancellationToken cancellationToken)
    {
        try
        {
            var list = await db.ConfiguredShells
                .AsNoTracking()
                .OrderBy(s => s.SortOrder)
                .ThenBy(s => s.Name)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<DysonConfiguredShellEntity>, string>.AsValue(list);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonConfiguredShellEntity>, string>.AsError(
                $"Failed to list configured shells: {ex.Message}");
        }
    }

    private static async Task<Result<IReadOnlyList<DysonConfiguredShellSpec>, string>> ListEnabledSpecsCoreAsync(
        DysonDbContext db,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await db.ConfiguredShells
                .AsNoTracking()
                .Where(s => s.IsEnabled)
                .OrderBy(s => s.SortOrder)
                .ThenBy(s => s.Name)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<DysonConfiguredShellSpec> list = rows
                .Select(s => new DysonConfiguredShellSpec(s.Name, s.ExecutablePath, ParseFixedArgs(s.FixedArgsJson)))
                .ToList();

            return Result<IReadOnlyList<DysonConfiguredShellSpec>, string>.AsValue(list);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonConfiguredShellSpec>, string>.AsError(
                $"Failed to list enabled shells: {ex.Message}");
        }
    }

    private static async Task<Result<Guid, string>> CreateCoreAsync(
        DysonDbContext db,
        string trimmedName,
        string trimmedPath,
        bool isEnabled,
        IReadOnlyList<string>? fixedArgs,
        CancellationToken cancellationToken)
    {
        try
        {
            var duplicate = await db.ConfiguredShells
                .AnyAsync(s => s.Name.ToLower() == trimmedName.ToLower(), cancellationToken)
                .ConfigureAwait(false);
            if (duplicate)
                return Result<Guid, string>.AsError($"A shell named '{trimmedName}' already exists.");

            var maxOrder = await db.ConfiguredShells
                .Select(s => (int?)s.SortOrder)
                .MaxAsync(cancellationToken)
                .ConfigureAwait(false);

            var now = DateTime.UtcNow;
            var entity = new DysonConfiguredShellEntity
            {
                Id = Guid.NewGuid(),
                Name = trimmedName,
                ExecutablePath = trimmedPath,
                FixedArgsJson = ToFixedArgsJson(fixedArgs),
                IsEnabled = isEnabled,
                SortOrder = (maxOrder ?? -1) + 1,
                CreatedUtc = now,
                UpdatedUtc = now,
            };

            db.ConfiguredShells.Add(entity);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return Result<Guid, string>.AsValue(entity.Id);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<Guid, string>.AsError($"Failed to create configured shell: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> UpdateCoreAsync(
        DysonDbContext db,
        Guid id,
        string trimmedName,
        string trimmedPath,
        bool isEnabled,
        IReadOnlyList<string>? fixedArgs,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await db.ConfiguredShells
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
                return new VoidResult<string>($"Configured shell '{id}' not found.");

            var duplicate = await db.ConfiguredShells
                .AnyAsync(
                    s => s.Id != id && s.Name.ToLower() == trimmedName.ToLower(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicate)
                return new VoidResult<string>($"A shell named '{trimmedName}' already exists.");

            existing.Name = trimmedName;
            existing.ExecutablePath = trimmedPath;
            existing.FixedArgsJson = ToFixedArgsJson(fixedArgs);
            existing.IsEnabled = isEnabled;
            existing.UpdatedUtc = DateTime.UtcNow;

            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return new VoidResult<string>($"Failed to update configured shell: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> DeleteCoreAsync(
        DysonDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await db.ConfiguredShells
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
                return new VoidResult<string>($"Configured shell '{id}' not found.");

            db.ConfiguredShells.Remove(existing);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return new VoidResult<string>($"Failed to delete configured shell: {ex.Message}");
        }
    }
}
