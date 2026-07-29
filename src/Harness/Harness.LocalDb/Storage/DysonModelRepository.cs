using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonModelRepository(
    DysonDbAccessor accessor,
    IDysonSubjectContext subjectContext,
    IDysonAccessEvaluator access) : IDysonModelRepository
{
    // Mirror Engine DysonProviderKinds (LocalDb must not reference Engine).
    private const string KindDemo = "demo";
    private const string KindOpenAICompatible = "OpenAICompatible";

    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly IDysonSubjectContext _subjectContext =
        subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));
    private readonly IDysonAccessEvaluator _access =
        access ?? throw new ArgumentNullException(nameof(access));

    public Task<Result<IReadOnlyList<DysonModelProviderEntity>, string>> ListProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var list = await VisibleProviders(db, subjectId)
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
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<IReadOnlyList<DysonModelProviderEntity>, string>.AsError(
                    $"Failed to list model providers: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<Result<DysonModelProviderEntity, string>> GetProviderAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var entity = await VisibleProviders(db, subjectId)
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
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<DysonModelProviderEntity, string>.AsError(
                    $"Failed to get model provider: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<Result<Guid, string>> CreateProviderAsync(
        DysonModelProviderEntity provider,
        bool shared = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (shared && !_access.Can(DysonPermission.ManageSharedProviders))
            return Task.FromResult(Result<Guid, string>.AsError("Permission denied: ManageSharedProviders."));

        var subjectId = shared ? DysonSubjects.Shared : _subjectContext.SubjectId;

        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var now = DateTime.UtcNow;
                if (provider.Id == Guid.Empty)
                    provider.Id = Guid.NewGuid();

                provider.SubjectId = subjectId;
                provider.CreatedUtc = now;
                provider.UpdatedUtc = now;
                provider.OpenAiApiMode = DysonOpenAiApiModes.Normalize(provider.OpenAiApiMode);
                provider.Slugs = [];

                db.ModelProviders.Add(provider);
                await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
                return Result<Guid, string>.AsValue(provider.Id);
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<Guid, string>.AsError($"Failed to create model provider: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> UpdateProviderAsync(
        DysonModelProviderEntity provider,
        bool shared = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (shared && !_access.Can(DysonPermission.ManageSharedProviders))
            return Task.FromResult(new VoidResult<string>("Permission denied: ManageSharedProviders."));

        var currentSubject = _subjectContext.SubjectId;
        var expectedSubject = shared ? DysonSubjects.Shared : currentSubject;

        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var existing = await db.ModelProviders
                    .FirstOrDefaultAsync(p => p.Id == provider.Id, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is null)
                    return new VoidResult<string>($"Model provider '{provider.Id}' not found.");

                if (!string.Equals(existing.SubjectId, expectedSubject, StringComparison.Ordinal))
                {
                    return new VoidResult<string>(
                        shared
                            ? $"Model provider '{provider.Id}' is not a shared provider."
                            : $"Model provider '{provider.Id}' not found.");
                }

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

                await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return new VoidResult<string>($"Failed to update model provider: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<Result<Guid, string>> UpsertManagedProviderAsync(
        string managedSource,
        string displayName,
        string baseUrl,
        string apiKey,
        string openAiApiMode,
        IReadOnlyList<ManagedSlugSpec> slugs,
        bool shared = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(slugs);

        if (shared && !_access.Can(DysonPermission.ManageSharedProviders))
            return Task.FromResult(Result<Guid, string>.AsError("Permission denied: ManageSharedProviders."));

        var subjectId = shared ? DysonSubjects.Shared : _subjectContext.SubjectId;

        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var source = managedSource.Trim();
                var now = DateTime.UtcNow;
                var existing = await db.ModelProviders
                    .Include(p => p.Slugs)
                    .FirstOrDefaultAsync(
                        p => p.SubjectId == subjectId && p.ManagedSource == source,
                        cancellationToken)
                    .ConfigureAwait(false);

                string? priorDefaultSlug = null;
                if (existing is not null)
                    priorDefaultSlug = existing.Slugs.FirstOrDefault(s => s.IsDefault)?.Slug;

                if (existing is null)
                {
                    existing = new DysonModelProviderEntity
                    {
                        Id = Guid.NewGuid(),
                        SubjectId = subjectId,
                        DisplayName = displayName.Trim(),
                        ProviderKind = KindOpenAICompatible,
                        BaseUrl = baseUrl.Trim(),
                        ApiKey = apiKey,
                        OpenAiApiMode = DysonOpenAiApiModes.Normalize(openAiApiMode),
                        ManagedSource = source,
                        CreatedUtc = now,
                        UpdatedUtc = now,
                        Slugs = [],
                    };
                    db.ModelProviders.Add(existing);
                    await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    existing.DisplayName = displayName.Trim();
                    existing.ProviderKind = KindOpenAICompatible;
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
                        await ClearDefaultsAsync(db, cancellationToken).ConfigureAwait(false);
                        defaultAssigned = true;
                    }

                    if (bySlug.TryGetValue(slugKey, out var row))
                    {
                        row.DisplayAlias = spec.DisplayAlias.Trim();
                        row.IsDefault = isDefault;
                        row.ReasoningModes = StringListJsonValueConverter.Normalize(spec.ReasoningModes);
                        row.UpdatedUtc = now;
                    }
                    else
                    {
                        db.ModelSlugs.Add(new DysonModelSlugEntity
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
                    db.ModelSlugs.Remove(obsolete);

                await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
                return Result<Guid, string>.AsValue(providerId);
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<Guid, string>.AsError($"Failed to upsert managed provider: {ex.Message}", ex);
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> DeleteProviderAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var currentSubject = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var existing = await db.ModelProviders
                    .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is null)
                    return new VoidResult<string>($"Model provider '{id}' not found.");

                if (string.Equals(existing.SubjectId, DysonSubjects.Shared, StringComparison.Ordinal))
                {
                    if (!_access.Can(DysonPermission.ManageSharedProviders))
                        return new VoidResult<string>("Permission denied: ManageSharedProviders.");
                }
                else if (!string.Equals(existing.SubjectId, currentSubject, StringComparison.Ordinal))
                {
                    return new VoidResult<string>($"Model provider '{id}' not found.");
                }

                db.ModelProviders.Remove(existing);
                await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return new VoidResult<string>($"Failed to delete model provider: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<Result<Guid, string>> AddSlugAsync(
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

        var currentSubject = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var provider = await db.ModelProviders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == providerId, cancellationToken)
                    .ConfigureAwait(false);

                if (provider is null)
                    return Result<Guid, string>.AsError($"Model provider '{providerId}' not found.");

                var writeGate = GateProviderWrite(provider, currentSubject);
                if (writeGate is not null)
                    return Result<Guid, string>.AsError(writeGate);

                if (!string.IsNullOrWhiteSpace(provider.ManagedSource))
                {
                    return Result<Guid, string>.AsError(
                        $"Provider '{provider.DisplayName}' is managed ({provider.ManagedSource}); slugs are synced via Verify.");
                }

                if (isDefault)
                    await ClearDefaultsAsync(db, cancellationToken).ConfigureAwait(false);

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

                db.ModelSlugs.Add(entity);
                await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
                return Result<Guid, string>.AsValue(entity.Id);
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<Guid, string>.AsError($"Failed to add model slug: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> UpdateSlugAsync(
        DysonModelSlugEntity slug,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slug);
        var currentSubject = _subjectContext.SubjectId;

        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var existing = await db.ModelSlugs
                    .Include(s => s.Provider)
                    .FirstOrDefaultAsync(s => s.Id == slug.Id, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is null)
                    return new VoidResult<string>($"Model slug '{slug.Id}' not found.");

                var writeGate = GateProviderWrite(existing.Provider, currentSubject);
                if (writeGate is not null)
                    return new VoidResult<string>(writeGate);

                if (!string.IsNullOrWhiteSpace(existing.Provider?.ManagedSource))
                {
                    return new VoidResult<string>(
                        $"Slug belongs to managed provider ({existing.Provider.ManagedSource}) and cannot be edited.");
                }

                if (slug.IsDefault && !existing.IsDefault)
                    await ClearDefaultsAsync(db, cancellationToken).ConfigureAwait(false);

                existing.Slug = slug.Slug;
                existing.DisplayAlias = slug.DisplayAlias;
                existing.IsDefault = slug.IsDefault;
                existing.DefaultReasoningEffort = NormalizeReasoningEffort(slug.DefaultReasoningEffort);
                existing.ReasoningModes = StringListJsonValueConverter.Normalize(slug.ReasoningModes);
                existing.UpdatedUtc = DateTime.UtcNow;

                await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return new VoidResult<string>($"Failed to update model slug: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> RemoveSlugAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var currentSubject = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var existing = await db.ModelSlugs
                    .Include(s => s.Provider)
                    .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is null)
                    return new VoidResult<string>($"Model slug '{id}' not found.");

                var writeGate = GateProviderWrite(existing.Provider, currentSubject);
                if (writeGate is not null)
                    return new VoidResult<string>(writeGate);

                if (!string.IsNullOrWhiteSpace(existing.Provider?.ManagedSource))
                {
                    return new VoidResult<string>(
                        $"Slug belongs to managed provider ({existing.Provider.ManagedSource}) and cannot be removed.");
                }

                db.ModelSlugs.Remove(existing);
                await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return new VoidResult<string>($"Failed to remove model slug: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<Result<DysonModelSlugEntity?, string>> GetDefaultSlugAsync(
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var entity = await VisibleSlugs(db, subjectId)
                    .AsNoTracking()
                    .Include(s => s.Provider)
                    .FirstOrDefaultAsync(s => s.IsDefault && s.IsEnabled, cancellationToken)
                    .ConfigureAwait(false);

                if (entity is null)
                {
                    entity = await VisibleSlugs(db, subjectId)
                        .AsNoTracking()
                        .Include(s => s.Provider)
                        .OrderBy(s => s.DisplayAlias)
                        .FirstOrDefaultAsync(s => s.IsEnabled, cancellationToken)
                        .ConfigureAwait(false);
                }

                return Result<DysonModelSlugEntity?, string>.AsValue(entity);
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<DysonModelSlugEntity?, string>.AsError(
                    $"Failed to get default model slug: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> SetDefaultSlugAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var existing = await VisibleSlugs(db, subjectId)
                    .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is null)
                    return new VoidResult<string>($"Model slug '{id}' not found.");

                if (!existing.IsEnabled)
                {
                    return new VoidResult<string>(
                        "Enable the model slug before setting it as default.");
                }

                await ClearDefaultsAsync(db, cancellationToken).ConfigureAwait(false);
                existing.IsDefault = true;
                existing.UpdatedUtc = DateTime.UtcNow;

                await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return new VoidResult<string>($"Failed to set default model slug: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> SetSlugEnabledAsync(
        Guid id,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var currentSubject = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var existing = await db.ModelSlugs
                    .Include(s => s.Provider)
                    .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is null)
                    return new VoidResult<string>($"Model slug '{id}' not found.");

                var writeGate = GateProviderWrite(existing.Provider, currentSubject);
                if (writeGate is not null)
                    return new VoidResult<string>(writeGate);

                if (string.IsNullOrWhiteSpace(existing.Provider?.ManagedSource))
                {
                    return new VoidResult<string>(
                        "Enable/disable is only available for managed provider slugs.");
                }

                existing.IsEnabled = enabled;
                existing.UpdatedUtc = DateTime.UtcNow;

                await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return new VoidResult<string>($"Failed to set model slug enabled: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> SetSlugDefaultReasoningEffortAsync(
        Guid id,
        string? effort,
        CancellationToken cancellationToken = default)
    {
        var currentSubject = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var existing = await db.ModelSlugs
                    .Include(s => s.Provider)
                    .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is null)
                    return new VoidResult<string>($"Model slug '{id}' not found.");

                var writeGate = GateProviderWrite(existing.Provider, currentSubject);
                if (writeGate is not null)
                    return new VoidResult<string>(writeGate);

                if (string.IsNullOrWhiteSpace(existing.Provider?.ManagedSource))
                {
                    return new VoidResult<string>(
                        "Default reasoning effort can only be set for managed provider slugs.");
                }

                existing.DefaultReasoningEffort = NormalizeReasoningEffort(effort);
                existing.UpdatedUtc = DateTime.UtcNow;

                await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return new VoidResult<string>($"Failed to set model slug default reasoning effort: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<Result<DysonModelSlugEntity, string>> GetSlugAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var entity = await VisibleSlugs(db, subjectId)
                    .AsNoTracking()
                    .Include(s => s.Provider)
                    .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                    .ConfigureAwait(false);

                if (entity is null)
                    return Result<DysonModelSlugEntity, string>.AsError($"Model slug '{id}' not found.");

                return Result<DysonModelSlugEntity, string>.AsValue(entity);
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<DysonModelSlugEntity, string>.AsError(
                    $"Failed to get model slug: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<Result<DysonModelSlugEntity, string>> FindSlugByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var subjectId = _subjectContext.SubjectId;

        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var needle = name.Trim();
                var slugs = await VisibleSlugs(db, subjectId)
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
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<DysonModelSlugEntity, string>.AsError(
                    $"Failed to find model slug: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<Guid>, string>> ListFavoriteSlugIdsAsync(
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var ids = await db.ModelFavorites
                    .AsNoTracking()
                    .Where(f => f.SubjectId == subjectId)
                    .OrderBy(f => f.CreatedUtc)
                    .Select(f => f.ModelSlugId)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                return Result<IReadOnlyList<Guid>, string>.AsValue(ids);
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<IReadOnlyList<Guid>, string>.AsError(
                    $"Failed to list favorite model slugs: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<Result<bool, string>> IsFavoriteAsync(
        Guid modelSlugId,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var isFavorite = await db.ModelFavorites
                    .AsNoTracking()
                    .AnyAsync(
                        f => f.SubjectId == subjectId && f.ModelSlugId == modelSlugId,
                        cancellationToken)
                    .ConfigureAwait(false);

                return Result<bool, string>.AsValue(isFavorite);
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<bool, string>.AsError(
                    $"Failed to check favorite model slug: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> AddFavoriteAsync(
        Guid modelSlugId,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var slugExists = await VisibleSlugs(db, subjectId)
                    .AnyAsync(s => s.Id == modelSlugId, cancellationToken)
                    .ConfigureAwait(false);

                if (!slugExists)
                    return new VoidResult<string>($"Model slug '{modelSlugId}' not found.");

                var already = await db.ModelFavorites
                    .AnyAsync(
                        f => f.SubjectId == subjectId && f.ModelSlugId == modelSlugId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (already)
                    return VoidResult<string>.Success;

                db.ModelFavorites.Add(new DysonModelFavoriteEntity
                {
                    Id = Guid.NewGuid(),
                    SubjectId = subjectId,
                    ModelSlugId = modelSlugId,
                    CreatedUtc = DateTime.UtcNow,
                });

                await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return new VoidResult<string>($"Failed to add favorite model slug: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> RemoveFavoriteAsync(
        Guid modelSlugId,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var existing = await db.ModelFavorites
                    .FirstOrDefaultAsync(
                        f => f.SubjectId == subjectId && f.ModelSlugId == modelSlugId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (existing is null)
                    return VoidResult<string>.Success;

                db.ModelFavorites.Remove(existing);
                await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return new VoidResult<string>($"Failed to remove favorite model slug: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<Result<int, string>> RepairMisTaggedProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, cancellationToken) =>
        {
            try
            {
                var candidates = await db.ModelProviders
                    .Where(p => p.SubjectId == subjectId && p.ProviderKind == KindDemo)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var now = DateTime.UtcNow;
                var updated = 0;

                foreach (var provider in candidates)
                {
                    if (!HasCredentials(provider.BaseUrl, provider.ApiKey))
                        continue;

                    provider.ProviderKind = KindOpenAICompatible;
                    provider.OpenAiApiMode = DysonOpenAiApiModes.Normalize(provider.OpenAiApiMode);
                    provider.UpdatedUtc = now;
                    updated++;
                }

                if (updated > 0)
                    await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);

                return Result<int, string>.AsValue(updated);
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<int, string>.AsError(
                    $"Failed to repair mis-tagged providers: {ex.Message}");
            }
        }, cancellationToken);
    }

    private string? GateProviderWrite(DysonModelProviderEntity? provider, string currentSubject)
    {
        if (provider is null)
            return "Model provider not found.";

        if (string.Equals(provider.SubjectId, DysonSubjects.Shared, StringComparison.Ordinal))
        {
            if (!_access.Can(DysonPermission.ManageSharedProviders))
                return "Permission denied: ManageSharedProviders.";
            return null;
        }

        if (!string.Equals(provider.SubjectId, currentSubject, StringComparison.Ordinal))
            return $"Model provider '{provider.Id}' not found.";

        return null;
    }

    private static IQueryable<DysonModelProviderEntity> VisibleProviders(
        DysonDbContext db,
        string subjectId) =>
        db.ModelProviders.Where(p =>
            p.SubjectId == subjectId || p.SubjectId == DysonSubjects.Shared);

    private static IQueryable<DysonModelSlugEntity> VisibleSlugs(
        DysonDbContext db,
        string subjectId) =>
        db.ModelSlugs.Where(s =>
            s.Provider!.SubjectId == subjectId || s.Provider.SubjectId == DysonSubjects.Shared);

    private static async Task ClearDefaultsAsync(DysonDbContext db, CancellationToken cancellationToken)
    {
        var defaults = await db.ModelSlugs
            .Where(s => s.IsDefault)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in defaults)
            item.IsDefault = false;
    }

    private static bool HasCredentials(string? baseUrl, string? apiKey) =>
        !string.IsNullOrWhiteSpace(baseUrl) || !string.IsNullOrWhiteSpace(apiKey);

    private static string? NormalizeReasoningEffort(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
