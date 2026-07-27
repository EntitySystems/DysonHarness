using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonModelStore(DysonDbContext db)
{
    private readonly DysonDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<Result<IReadOnlyList<DysonModelProviderEntity>, string>> ListProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var list = await _db.ModelProviders
                .AsNoTracking()
                .Include(p => p.Slugs)
                .OrderBy(p => p.DisplayName)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var provider in list)
            {
                provider.Slugs = provider.Slugs
                    .OrderByDescending(s => s.IsDefault)
                    .ThenBy(s => s.DisplayAlias)
                    .ToList();
            }

            return Result<IReadOnlyList<DysonModelProviderEntity>, string>.AsValue(list);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<DysonModelProviderEntity>, string>.AsError(
                $"Failed to list model providers: {ex.Message}");
        }
    }

    public async Task<Result<DysonModelProviderEntity, string>> GetProviderAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entity = await _db.ModelProviders
                .AsNoTracking()
                .Include(p => p.Slugs)
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
                return Result<DysonModelProviderEntity, string>.AsError($"Model provider '{id}' not found.");

            entity.Slugs = entity.Slugs
                .OrderByDescending(s => s.IsDefault)
                .ThenBy(s => s.DisplayAlias)
                .ToList();

            return Result<DysonModelProviderEntity, string>.AsValue(entity);
        }
        catch (Exception ex)
        {
            return Result<DysonModelProviderEntity, string>.AsError(
                $"Failed to get model provider: {ex.Message}");
        }
    }

    public async Task<Result<Guid, string>> CreateProviderAsync(
        DysonModelProviderEntity provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        try
        {
            var now = DateTime.UtcNow;
            if (provider.Id == Guid.Empty)
                provider.Id = Guid.NewGuid();

            provider.CreatedUtc = now;
            provider.UpdatedUtc = now;
            provider.OpenAiApiMode = DysonOpenAiApiModes.Normalize(provider.OpenAiApiMode);
            provider.Slugs = [];

            _db.ModelProviders.Add(provider);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<Guid, string>.AsValue(provider.Id);
        }
        catch (Exception ex)
        {
            return Result<Guid, string>.AsError($"Failed to create model provider: {ex.Message}");
        }
    }

    public async Task<VoidResult<string>> UpdateProviderAsync(
        DysonModelProviderEntity provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        try
        {
            var existing = await _db.ModelProviders
                .FirstOrDefaultAsync(p => p.Id == provider.Id, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
                return new VoidResult<string>($"Model provider '{provider.Id}' not found.");

            if (!string.IsNullOrWhiteSpace(existing.ManagedSource))
            {
                return new VoidResult<string>(
                    $"Provider '{existing.DisplayName}' is managed ({existing.ManagedSource}) and cannot be edited.");
            }

            existing.DisplayName = provider.DisplayName;
            existing.ProviderKind = provider.ProviderKind;
            existing.BaseUrl = provider.BaseUrl;
            existing.ApiKey = provider.ApiKey;
            existing.OpenAiApiMode = DysonOpenAiApiModes.Normalize(provider.OpenAiApiMode);
            existing.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to update model provider: {ex.Message}");
        }
    }

    /// <summary>
    /// Insert-or-update a managed provider by <paramref name="managedSource"/> (id-stable),
    /// merging slugs by name (preserves <see cref="DysonModelSlugEntity.Id"/> and
    /// <see cref="DysonModelSlugEntity.IsEnabled"/>). Empty <paramref name="slugs"/> clears the catalog.
    /// </summary>
    public async Task<Result<Guid, string>> UpsertManagedProviderAsync(
        string managedSource,
        string displayName,
        string baseUrl,
        string apiKey,
        string openAiApiMode,
        IReadOnlyList<ManagedSlugSpec> slugs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(slugs);

        try
        {
            var source = managedSource.Trim();
            var now = DateTime.UtcNow;
            var existing = await _db.ModelProviders
                .Include(p => p.Slugs)
                .FirstOrDefaultAsync(p => p.ManagedSource == source, cancellationToken)
                .ConfigureAwait(false);

            string? priorDefaultSlug = null;
            if (existing is not null)
                priorDefaultSlug = existing.Slugs.FirstOrDefault(s => s.IsDefault)?.Slug;

            if (existing is null)
            {
                existing = new DysonModelProviderEntity
                {
                    Id = Guid.NewGuid(),
                    DisplayName = displayName.Trim(),
                    ProviderKind = DysonProviderKinds.OpenAICompatible,
                    BaseUrl = baseUrl.Trim(),
                    ApiKey = apiKey,
                    OpenAiApiMode = DysonOpenAiApiModes.Normalize(openAiApiMode),
                    ManagedSource = source,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                    Slugs = [],
                };
                _db.ModelProviders.Add(existing);
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                existing.DisplayName = displayName.Trim();
                existing.ProviderKind = DysonProviderKinds.OpenAICompatible;
                existing.BaseUrl = baseUrl.Trim();
                existing.ApiKey = apiKey;
                existing.OpenAiApiMode = DysonOpenAiApiModes.Normalize(openAiApiMode);
                existing.UpdatedUtc = now;
            }

            var providerId = existing.Id;
            var bySlug = existing.Slugs.ToDictionary(s => s.Slug, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var defaultAssigned = false;

            foreach (var spec in slugs)
            {
                if (string.IsNullOrWhiteSpace(spec.Slug) || string.IsNullOrWhiteSpace(spec.DisplayAlias))
                    continue;

                var slugKey = spec.Slug.Trim();
                seen.Add(slugKey);

                var isDefault = !defaultAssigned
                    && priorDefaultSlug is not null
                    && string.Equals(priorDefaultSlug, slugKey, StringComparison.OrdinalIgnoreCase);
                if (isDefault)
                {
                    await ClearDefaultsAsync(cancellationToken).ConfigureAwait(false);
                    defaultAssigned = true;
                }

                if (bySlug.TryGetValue(slugKey, out var row))
                {
                    row.DisplayAlias = spec.DisplayAlias.Trim();
                    row.IsDefault = isDefault;
                    row.ReasoningModes = StringListJsonValueConverter.Normalize(spec.ReasoningModes);
                    row.UpdatedUtc = now;
                    // Keep Id + IsEnabled + DefaultReasoningEffort across Verify sync.
                }
                else
                {
                    _db.ModelSlugs.Add(new DysonModelSlugEntity
                    {
                        Id = Guid.NewGuid(),
                        ProviderId = providerId,
                        Slug = slugKey,
                        DisplayAlias = spec.DisplayAlias.Trim(),
                        IsDefault = isDefault,
                        IsEnabled = true,
                        DefaultReasoningEffort = NormalizeReasoningEffort(spec.DefaultReasoningEffort),
                        ReasoningModes = StringListJsonValueConverter.Normalize(spec.ReasoningModes),
                        CreatedUtc = now,
                        UpdatedUtc = now,
                    });
                }
            }

            foreach (var obsolete in existing.Slugs.Where(s => !seen.Contains(s.Slug)).ToList())
                _db.ModelSlugs.Remove(obsolete);

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<Guid, string>.AsValue(providerId);
        }
        catch (Exception ex)
        {
            return Result<Guid, string>.AsError($"Failed to upsert managed provider: {ex.Message}", ex);
        }
    }

    public async Task<VoidResult<string>> DeleteProviderAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _db.ModelProviders
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
                return new VoidResult<string>($"Model provider '{id}' not found.");

            _db.ModelProviders.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to delete model provider: {ex.Message}");
        }
    }

    public async Task<Result<Guid, string>> AddSlugAsync(
        Guid providerId,
        string slug,
        string displayAlias,
        bool isDefault = false,
        string? defaultReasoningEffort = null,
        IEnumerable<string>? reasoningModes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayAlias);

        try
        {
            var provider = await _db.ModelProviders
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == providerId, cancellationToken)
                .ConfigureAwait(false);

            if (provider is null)
                return Result<Guid, string>.AsError($"Model provider '{providerId}' not found.");

            if (!string.IsNullOrWhiteSpace(provider.ManagedSource))
            {
                return Result<Guid, string>.AsError(
                    $"Provider '{provider.DisplayName}' is managed ({provider.ManagedSource}); slugs are synced via Verify.");
            }

            if (isDefault)
                await ClearDefaultsAsync(cancellationToken).ConfigureAwait(false);

            var now = DateTime.UtcNow;
            var entity = new DysonModelSlugEntity
            {
                Id = Guid.NewGuid(),
                ProviderId = providerId,
                Slug = slug,
                DisplayAlias = displayAlias,
                IsDefault = isDefault,
                DefaultReasoningEffort = NormalizeReasoningEffort(defaultReasoningEffort),
                ReasoningModes = StringListJsonValueConverter.Normalize(reasoningModes),
                CreatedUtc = now,
                UpdatedUtc = now,
            };

            _db.ModelSlugs.Add(entity);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<Guid, string>.AsValue(entity.Id);
        }
        catch (Exception ex)
        {
            return Result<Guid, string>.AsError($"Failed to add model slug: {ex.Message}");
        }
    }

    public async Task<VoidResult<string>> UpdateSlugAsync(
        DysonModelSlugEntity slug,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slug);

        try
        {
            var existing = await _db.ModelSlugs
                .Include(s => s.Provider)
                .FirstOrDefaultAsync(s => s.Id == slug.Id, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
                return new VoidResult<string>($"Model slug '{slug.Id}' not found.");

            if (!string.IsNullOrWhiteSpace(existing.Provider?.ManagedSource))
            {
                return new VoidResult<string>(
                    $"Slug belongs to managed provider ({existing.Provider.ManagedSource}) and cannot be edited.");
            }

            if (slug.IsDefault && !existing.IsDefault)
                await ClearDefaultsAsync(cancellationToken).ConfigureAwait(false);

            existing.Slug = slug.Slug;
            existing.DisplayAlias = slug.DisplayAlias;
            existing.IsDefault = slug.IsDefault;
            existing.DefaultReasoningEffort = NormalizeReasoningEffort(slug.DefaultReasoningEffort);
            existing.ReasoningModes = StringListJsonValueConverter.Normalize(slug.ReasoningModes);
            existing.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to update model slug: {ex.Message}");
        }
    }

    public async Task<VoidResult<string>> RemoveSlugAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _db.ModelSlugs
                .Include(s => s.Provider)
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
                return new VoidResult<string>($"Model slug '{id}' not found.");

            if (!string.IsNullOrWhiteSpace(existing.Provider?.ManagedSource))
            {
                return new VoidResult<string>(
                    $"Slug belongs to managed provider ({existing.Provider.ManagedSource}) and cannot be removed.");
            }

            _db.ModelSlugs.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to remove model slug: {ex.Message}");
        }
    }

    public async Task<Result<DysonModelSlugEntity?, string>> GetDefaultSlugAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entity = await _db.ModelSlugs
                .AsNoTracking()
                .Include(s => s.Provider)
                .FirstOrDefaultAsync(s => s.IsDefault && s.IsEnabled, cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
            {
                entity = await _db.ModelSlugs
                    .AsNoTracking()
                    .Include(s => s.Provider)
                    .OrderBy(s => s.DisplayAlias)
                    .FirstOrDefaultAsync(s => s.IsEnabled, cancellationToken)
                    .ConfigureAwait(false);
            }

            return Result<DysonModelSlugEntity?, string>.AsValue(entity);
        }
        catch (Exception ex)
        {
            return Result<DysonModelSlugEntity?, string>.AsError(
                $"Failed to get default model slug: {ex.Message}");
        }
    }

    public async Task<VoidResult<string>> SetDefaultSlugAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _db.ModelSlugs
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
                return new VoidResult<string>($"Model slug '{id}' not found.");

            if (!existing.IsEnabled)
            {
                return new VoidResult<string>(
                    "Enable the model slug before setting it as default.");
            }

            await ClearDefaultsAsync(cancellationToken).ConfigureAwait(false);
            existing.IsDefault = true;
            existing.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to set default model slug: {ex.Message}");
        }
    }

    /// <summary>
    /// Enable/disable a managed provider slug. Manual providers are rejected.
    /// </summary>
    public async Task<VoidResult<string>> SetSlugEnabledAsync(
        Guid id,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _db.ModelSlugs
                .Include(s => s.Provider)
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
                return new VoidResult<string>($"Model slug '{id}' not found.");

            if (string.IsNullOrWhiteSpace(existing.Provider?.ManagedSource))
            {
                return new VoidResult<string>(
                    "Enable/disable is only available for managed provider slugs.");
            }

            existing.IsEnabled = enabled;
            existing.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to set model slug enabled: {ex.Message}");
        }
    }

    /// <summary>
    /// Set default reasoning effort for a managed provider slug. Manual providers are rejected.
    /// Blank/whitespace <paramref name="effort"/> clears to null (omit).
    /// </summary>
    public async Task<VoidResult<string>> SetSlugDefaultReasoningEffortAsync(
        Guid id,
        string? effort,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _db.ModelSlugs
                .Include(s => s.Provider)
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
                return new VoidResult<string>($"Model slug '{id}' not found.");

            if (string.IsNullOrWhiteSpace(existing.Provider?.ManagedSource))
            {
                return new VoidResult<string>(
                    "Default reasoning effort can only be set for managed provider slugs.");
            }

            existing.DefaultReasoningEffort = NormalizeReasoningEffort(effort);
            existing.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to set model slug default reasoning effort: {ex.Message}");
        }
    }

    public async Task<Result<DysonModelSlugEntity, string>> GetSlugAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entity = await _db.ModelSlugs
                .AsNoTracking()
                .Include(s => s.Provider)
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
                return Result<DysonModelSlugEntity, string>.AsError($"Model slug '{id}' not found.");

            return Result<DysonModelSlugEntity, string>.AsValue(entity);
        }
        catch (Exception ex)
        {
            return Result<DysonModelSlugEntity, string>.AsError(
                $"Failed to get model slug: {ex.Message}");
        }
    }

    /// <summary>
    /// Case-insensitive exact match on <see cref="DysonModelSlugEntity.Slug"/>, then
    /// <see cref="DysonModelSlugEntity.DisplayAlias"/> (picker label fields).
    /// </summary>
    public async Task<Result<DysonModelSlugEntity, string>> FindSlugByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var needle = name.Trim();
            var slugs = await _db.ModelSlugs
                .AsNoTracking()
                .Include(s => s.Provider)
                .Where(s => s.IsEnabled)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var bySlug = slugs.FirstOrDefault(s =>
                string.Equals(s.Slug, needle, StringComparison.OrdinalIgnoreCase));
            if (bySlug is not null)
                return Result<DysonModelSlugEntity, string>.AsValue(bySlug);

            var byAlias = slugs.FirstOrDefault(s =>
                string.Equals(s.DisplayAlias, needle, StringComparison.OrdinalIgnoreCase));
            if (byAlias is not null)
                return Result<DysonModelSlugEntity, string>.AsValue(byAlias);

            return Result<DysonModelSlugEntity, string>.AsError(
                $"Model slug '{needle}' not found.");
        }
        catch (Exception ex)
        {
            return Result<DysonModelSlugEntity, string>.AsError(
                $"Failed to find model slug: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<Guid>, string>> ListFavoriteSlugIdsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ids = await _db.ModelFavorites
                .AsNoTracking()
                .OrderBy(f => f.CreatedUtc)
                .Select(f => f.ModelSlugId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<Guid>, string>.AsValue(ids);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<Guid>, string>.AsError(
                $"Failed to list favorite model slugs: {ex.Message}");
        }
    }

    public async Task<Result<bool, string>> IsFavoriteAsync(
        Guid modelSlugId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var isFavorite = await _db.ModelFavorites
                .AsNoTracking()
                .AnyAsync(f => f.ModelSlugId == modelSlugId, cancellationToken)
                .ConfigureAwait(false);

            return Result<bool, string>.AsValue(isFavorite);
        }
        catch (Exception ex)
        {
            return Result<bool, string>.AsError(
                $"Failed to check favorite model slug: {ex.Message}");
        }
    }

    public async Task<VoidResult<string>> AddFavoriteAsync(
        Guid modelSlugId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var slugExists = await _db.ModelSlugs
                .AnyAsync(s => s.Id == modelSlugId, cancellationToken)
                .ConfigureAwait(false);

            if (!slugExists)
                return new VoidResult<string>($"Model slug '{modelSlugId}' not found.");

            var already = await _db.ModelFavorites
                .AnyAsync(f => f.ModelSlugId == modelSlugId, cancellationToken)
                .ConfigureAwait(false);

            if (already)
                return VoidResult<string>.Success;

            _db.ModelFavorites.Add(new DysonModelFavoriteEntity
            {
                Id = Guid.NewGuid(),
                ModelSlugId = modelSlugId,
                CreatedUtc = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to add favorite model slug: {ex.Message}");
        }
    }

    public async Task<VoidResult<string>> RemoveFavoriteAsync(
        Guid modelSlugId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _db.ModelFavorites
                .FirstOrDefaultAsync(f => f.ModelSlugId == modelSlugId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
                return VoidResult<string>.Success;

            _db.ModelFavorites.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to remove favorite model slug: {ex.Message}");
        }
    }

    public async Task<Result<int, string>> RepairMisTaggedProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var candidates = await _db.ModelProviders
                .Where(p => p.ProviderKind == DysonProviderKinds.Demo)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var now = DateTime.UtcNow;
            var updated = 0;

            foreach (var provider in candidates)
            {
                if (!DysonProviderKinds.HasCredentials(provider.BaseUrl, provider.ApiKey))
                    continue;

                provider.ProviderKind = DysonProviderKinds.OpenAICompatible;
                provider.OpenAiApiMode = DysonOpenAiApiModes.Normalize(provider.OpenAiApiMode);
                provider.UpdatedUtc = now;
                updated++;
            }

            if (updated > 0)
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Result<int, string>.AsValue(updated);
        }
        catch (Exception ex)
        {
            return Result<int, string>.AsError(
                $"Failed to repair mis-tagged providers: {ex.Message}");
        }
    }

    private async Task ClearDefaultsAsync(CancellationToken cancellationToken)
    {
        var defaults = await _db.ModelSlugs
            .Where(s => s.IsDefault)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in defaults)
            item.IsDefault = false;
    }

    private static string? NormalizeReasoningEffort(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
