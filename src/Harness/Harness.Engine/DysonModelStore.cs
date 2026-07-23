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
            var now = DateTimeOffset.UtcNow;
            if (provider.Id == Guid.Empty)
                provider.Id = Guid.NewGuid();

            provider.CreatedUtc = now;
            provider.UpdatedUtc = now;
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

            existing.DisplayName = provider.DisplayName;
            existing.ProviderKind = provider.ProviderKind;
            existing.BaseUrl = provider.BaseUrl;
            existing.ApiKey = provider.ApiKey;
            existing.UpdatedUtc = DateTimeOffset.UtcNow;

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to update model provider: {ex.Message}");
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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayAlias);

        try
        {
            var providerExists = await _db.ModelProviders
                .AnyAsync(p => p.Id == providerId, cancellationToken)
                .ConfigureAwait(false);

            if (!providerExists)
                return Result<Guid, string>.AsError($"Model provider '{providerId}' not found.");

            if (isDefault)
                await ClearDefaultsAsync(cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var entity = new DysonModelSlugEntity
            {
                Id = Guid.NewGuid(),
                ProviderId = providerId,
                Slug = slug,
                DisplayAlias = displayAlias,
                IsDefault = isDefault,
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
                .FirstOrDefaultAsync(s => s.Id == slug.Id, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
                return new VoidResult<string>($"Model slug '{slug.Id}' not found.");

            if (slug.IsDefault && !existing.IsDefault)
                await ClearDefaultsAsync(cancellationToken).ConfigureAwait(false);

            existing.Slug = slug.Slug;
            existing.DisplayAlias = slug.DisplayAlias;
            existing.IsDefault = slug.IsDefault;
            existing.UpdatedUtc = DateTimeOffset.UtcNow;

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
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
                return new VoidResult<string>($"Model slug '{id}' not found.");

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
                .FirstOrDefaultAsync(s => s.IsDefault, cancellationToken)
                .ConfigureAwait(false);

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

            await ClearDefaultsAsync(cancellationToken).ConfigureAwait(false);
            existing.IsDefault = true;
            existing.UpdatedUtc = DateTimeOffset.UtcNow;

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to set default model slug: {ex.Message}");
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

    private async Task ClearDefaultsAsync(CancellationToken cancellationToken)
    {
        var defaults = await _db.ModelSlugs
            .Where(s => s.IsDefault)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in defaults)
            item.IsDefault = false;
    }
}
