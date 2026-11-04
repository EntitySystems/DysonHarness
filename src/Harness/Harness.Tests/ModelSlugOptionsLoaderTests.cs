using DysonHarness;
using Harness.UI.Components.Models;

namespace Harness.Tests;

/// <summary>
/// ponytail: option loading builds a fresh list and publishes it by reference swap,
/// so overlapping refreshes cannot duplicate rows and a stale one cannot overwrite.
/// </summary>
public class ModelSlugOptionsLoaderTests
{
    [Fact]
    public async Task ConcurrentRefresh_DoesNotDuplicateOptions()
    {
        var (loader, providersGate, favoritesGate) = Gated(OneProvider("a", "b"));

        var first = loader.RefreshAsync(selectedId: null, isEligible: null);
        var second = loader.RefreshAsync(selectedId: null, isEligible: null);
        providersGate.SetResult();
        favoritesGate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, loader.Options.Count);
        Assert.Equal(loader.Options.Select(o => o.Id).Distinct().Count(), loader.Options.Count);
        Assert.True(first.Result.IsSuccess);
        Assert.True(second.Result.IsSuccess);
    }

    [Fact]
    public async Task StaleRefresh_DoesNotOverwriteNewer()
    {
        var newerId = Guid.NewGuid();
        var staleProviders = new TaskCompletionSource();
        var staleFavorites = new TaskCompletionSource();
        var newerProviders = new TaskCompletionSource();
        var newerFavorites = new TaskCompletionSource();
        var calls = 0;

        var loader = new ModelSlugOptionsLoader(
            () =>
            {
                var call = Interlocked.Increment(ref calls);
                return call == 1
                    ? After(staleProviders, Providers(OneProvider("stale")))
                    : After(newerProviders, Providers(OneProvider(newerId, "newer")));
            },
            () =>
            {
                var call = Volatile.Read(ref calls);
                return call == 1
                    ? After(staleFavorites, Favorites())
                    : After(newerFavorites, Favorites());
            });

        var stale = loader.RefreshAsync(selectedId: null, isEligible: null);
        var newer = loader.RefreshAsync(selectedId: null, isEligible: null);

        newerProviders.SetResult();
        newerFavorites.SetResult();
        await newer;

        staleProviders.SetResult();
        staleFavorites.SetResult();
        await stale;

        Assert.Single(loader.Options);
        Assert.Equal(newerId, loader.Options[0].Id);
    }

    [Fact]
    public async Task SelectedDisabledSlug_StaysListed()
    {
        var disabledId = Guid.NewGuid();
        var provider = Provider(
            Slug(disabledId, "off", enabled: false),
            Slug(Guid.NewGuid(), "on", enabled: true));
        var loader = Immediate(provider);

        await loader.RefreshAsync(selectedId: disabledId, isEligible: null);

        Assert.Equal(2, loader.Options.Count);
        Assert.Contains(loader.Options, o => o.Id == disabledId && !o.IsEligible);

        await loader.RefreshAsync(selectedId: null, isEligible: null);

        Assert.DoesNotContain(loader.Options, o => o.Id == disabledId);
    }

    [Fact]
    public async Task RefreshError_KeepsPreviousOptions()
    {
        var failing = false;
        var loader = new ModelSlugOptionsLoader(
            () => Task.FromResult(failing
                ? Result<IReadOnlyList<DysonModelProviderEntity>, string>.AsError("boom")
                : Providers(OneProvider("kept"))),
            () => Task.FromResult(Favorites()));

        Assert.True((await loader.RefreshAsync(null, null)).IsSuccess);
        var kept = loader.Options[0].Id;

        failing = true;
        var error = await loader.RefreshAsync(null, null);

        Assert.True(error.IsError);
        Assert.Equal("boom", error.Error);
        Assert.Equal(kept, loader.Options[0].Id);
    }

    [Fact]
    public async Task FavoriteFlag_ComesFromFavoriteSet()
    {
        var id = Guid.NewGuid();
        var loader = new ModelSlugOptionsLoader(
            () => Task.FromResult(Providers(OneProvider(id, "fav"))),
            () => Task.FromResult(Favorites(id)));

        await loader.RefreshAsync(null, null);

        Assert.True(loader.Options[0].IsFavorite);
        Assert.Contains(id, loader.FavoriteIds);

        loader.Replace(id, current => current with { IsFavorite = false });

        Assert.False(loader.Options[0].IsFavorite);
        Assert.Equal(id, loader.Options[0].Id);
    }

    private static ModelSlugOptionsLoader Immediate(DysonModelProviderEntity provider) => new(
        () => Task.FromResult(Providers([provider])),
        () => Task.FromResult(Favorites()));

    private static (ModelSlugOptionsLoader Loader, TaskCompletionSource Providers, TaskCompletionSource Favorites) Gated(
        IReadOnlyList<DysonModelProviderEntity> providers)
    {
        var providersGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var favoritesGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new ModelSlugOptionsLoader(
            () => After(providersGate, Providers(providers)),
            () => After(favoritesGate, Favorites()));
        return (loader, providersGate, favoritesGate);
    }

    private static Result<IReadOnlyList<DysonModelProviderEntity>, string> Providers(
        IReadOnlyList<DysonModelProviderEntity> providers) =>
        Result<IReadOnlyList<DysonModelProviderEntity>, string>.AsValue(providers);

    private static Result<IReadOnlyList<Guid>, string> Favorites(params Guid[] ids) =>
        Result<IReadOnlyList<Guid>, string>.AsValue(ids);

    private static async Task<T> After<T>(TaskCompletionSource gate, T value)
    {
        await gate.Task;
        return value;
    }

    private static IReadOnlyList<DysonModelProviderEntity> OneProvider(params string[] aliases) =>
        OneProvider(aliases.Select(alias => (Guid.NewGuid(), alias)).ToArray());

    private static IReadOnlyList<DysonModelProviderEntity> OneProvider(Guid id, string alias) =>
        OneProvider((id, alias));

    private static IReadOnlyList<DysonModelProviderEntity> OneProvider(params (Guid Id, string Alias)[] slugs) =>
        [Provider(slugs.Select(s => Slug(s.Id, s.Alias, enabled: true)).ToArray())];

    private static DysonModelProviderEntity Provider(params DysonModelSlugEntity[] slugs) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Provider",
        Slugs = [.. slugs],
    };

    private static DysonModelSlugEntity Slug(Guid id, string alias, bool enabled) => new()
    {
        Id = id,
        DisplayAlias = alias,
        Slug = alias,
        IsEnabled = enabled,
    };
}
