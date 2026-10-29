using DysonHarness;

namespace Harness.UI.Components.Models;

/// <summary>
/// Loads <see cref="ModelSlugPicker"/> options into a fresh list and publishes it by
/// reference swap. Overlapping <c>RefreshAsync</c> calls can never append into the same
/// list, and a stale refresh that finishes last does not overwrite a newer publish.
/// </summary>
internal sealed class ModelSlugOptionsLoader
{
    private readonly Func<Task<Result<IReadOnlyList<DysonModelProviderEntity>, string>>> _listProviders;
    private readonly Func<Task<Result<IReadOnlyList<Guid>, string>>> _listFavoriteIds;
    private int _version;

    public ModelSlugOptionsLoader(
        Func<Task<Result<IReadOnlyList<DysonModelProviderEntity>, string>>> listProviders,
        Func<Task<Result<IReadOnlyList<Guid>, string>>> listFavoriteIds)
    {
        _listProviders = listProviders;
        _listFavoriteIds = listFavoriteIds;
    }

    /// <summary>Published options. Replaced as a whole; never mutated in place.</summary>
    public IReadOnlyList<SlugOption> Options { get; private set; } = [];

    /// <summary>Favorite slug ids from the last successful refresh.</summary>
    public IReadOnlySet<Guid> FavoriteIds { get; private set; } = new HashSet<Guid>();

    /// <summary>
    /// Builds a new option list and publishes it only when no newer refresh started.
    /// On error the previously published options stay as they are.
    /// </summary>
    public async Task<VoidResult<string>> RefreshAsync(
        Guid? selectedId,
        Func<DysonModelSlugEntity, DysonModelProviderEntity, bool>? isEligible)
    {
        var version = Interlocked.Increment(ref _version);

        var providers = await _listProviders();
        if (providers.IsError)
            return VoidResult<string>.AsError(providers.Error);

        var favorites = await _listFavoriteIds();
        if (favorites.IsError)
            return VoidResult<string>.AsError(favorites.Error);

        if (version != Volatile.Read(ref _version))
            return VoidResult<string>.Success;

        var favoriteIds = favorites.Value.ToHashSet();
        var options = new List<SlugOption>();
        foreach (var provider in providers.Value)
        {
            foreach (var slug in provider.Slugs)
            {
                // Keep the current selection visible even if disabled or ineligible so it can be cleared.
                var eligible = slug.IsEnabled && (isEligible?.Invoke(slug, provider) ?? true);
                if (!eligible && selectedId != slug.Id)
                    continue;

                options.Add(new SlugOption(
                    slug.Id,
                    slug.DisplayAlias,
                    provider.DisplayName,
                    slug.Slug,
                    $"{slug.DisplayAlias} · {provider.DisplayName} / {slug.Slug}",
                    slug.IsDefault,
                    favoriteIds.Contains(slug.Id),
                    eligible));
            }
        }

        if (version != Volatile.Read(ref _version))
            return VoidResult<string>.Success;

        FavoriteIds = favoriteIds;
        Options = options;
        return VoidResult<string>.Success;
    }

    /// <summary>Replaces the published option with the given id (copy-on-write).</summary>
    public void Replace(Guid id, Func<SlugOption, SlugOption> update)
    {
        var current = Options;
        var index = -1;
        for (var i = 0; i < current.Count; i++)
        {
            if (current[i].Id == id)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return;

        var copy = current.ToList();
        copy[index] = update(copy[index]);
        Options = copy;
    }
}

internal sealed record SlugOption(
    Guid Id,
    string DisplayAlias,
    string ProviderName,
    string Slug,
    string Label,
    bool IsDefault,
    bool IsFavorite,
    bool IsEligible);
