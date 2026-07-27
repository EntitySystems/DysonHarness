using DysonHarness;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harness.Tests;

/// <summary>
/// ponytail: managed slug IsEnabled merge / SetSlugEnabled / default fallback / FindSlug filter.
/// </summary>
public class DysonModelSlugEnabledTests
{
    [Fact]
    public void Run()
    {
        AssertUpsertMergePreservesIdAndIsEnabled();
        AssertSetSlugEnabledRejectsManual();
        AssertDisabledDefaultFallsBack();
        AssertFindSlugSkipsDisabled();
        AssertSetDefaultRejectsDisabled();
        AssertFormatAvailableModelsSkipsDisabled();
    }

    private static void AssertUpsertMergePreservesIdAndIsEnabled()
    {
        using var db = OpenTempDb();
        var store = new DysonModelStore(db);

        var first = store.UpsertManagedProviderAsync(
            "cliproxy-test",
            "Test Managed",
            "http://127.0.0.1:8317/v1",
            "key",
            DysonOpenAiApiModes.Responses,
            [
                new ManagedSlugSpec("model-a", "Model A", "high", ["high"]),
                new ManagedSlugSpec("model-b", "Model B", null, []),
            ]).GetAwaiter().GetResult();
        if (first.IsError)
            throw new InvalidOperationException(first.Error);

        var listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        var provider = listed.Value.Single(p => p.ManagedSource == "cliproxy-test");
        var slugA = provider.Slugs.Single(s => s.Slug == "model-a");
        var idA = slugA.Id;

        var disable = store.SetSlugEnabledAsync(idA, enabled: false).GetAwaiter().GetResult();
        if (disable.IsError)
            throw new InvalidOperationException(disable.Error);

        var second = store.UpsertManagedProviderAsync(
            "cliproxy-test",
            "Test Managed",
            "http://127.0.0.1:8317/v1",
            "key",
            DysonOpenAiApiModes.Responses,
            [
                new ManagedSlugSpec("model-a", "Model A Renamed", "low", ["low"]),
                new ManagedSlugSpec("model-c", "Model C", null, []),
            ]).GetAwaiter().GetResult();
        if (second.IsError)
            throw new InvalidOperationException(second.Error);

        listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        provider = listed.Value.Single(p => p.ManagedSource == "cliproxy-test");
        if (provider.Slugs.Count != 2)
            throw new InvalidOperationException($"Expected 2 slugs after merge, got {provider.Slugs.Count}.");

        slugA = provider.Slugs.Single(s => s.Slug == "model-a");
        if (slugA.Id != idA)
            throw new InvalidOperationException("Upsert must preserve slug Id across Verify merge.");
        if (slugA.IsEnabled)
            throw new InvalidOperationException("Upsert must preserve IsEnabled=false across Verify merge.");
        if (!string.Equals(slugA.DisplayAlias, "Model A Renamed", StringComparison.Ordinal))
            throw new InvalidOperationException("Upsert must refresh DisplayAlias from catalog.");
        if (provider.Slugs.Any(s => s.Slug == "model-b"))
            throw new InvalidOperationException("Obsolete API slugs must be removed.");
        if (!provider.Slugs.Any(s => s.Slug == "model-c" && s.IsEnabled))
            throw new InvalidOperationException("New API slugs must insert enabled.");
    }

    private static void AssertSetSlugEnabledRejectsManual()
    {
        using var db = OpenTempDb();
        var store = new DysonModelStore(db);

        var create = store.CreateProviderAsync(new DysonModelProviderEntity
        {
            DisplayName = "Manual",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "k",
        }).GetAwaiter().GetResult();
        if (create.IsError)
            throw new InvalidOperationException(create.Error);

        var add = store.AddSlugAsync(create.Value, "gpt-4o", "GPT-4o").GetAwaiter().GetResult();
        if (add.IsError)
            throw new InvalidOperationException(add.Error);

        var set = store.SetSlugEnabledAsync(add.Value, enabled: false).GetAwaiter().GetResult();
        if (!set.IsError)
            throw new InvalidOperationException("SetSlugEnabledAsync must reject manual provider slugs.");
    }

    private static void AssertDisabledDefaultFallsBack()
    {
        using var db = OpenTempDb();
        var store = new DysonModelStore(db);

        var upsert = store.UpsertManagedProviderAsync(
            "cliproxy-fallback",
            "Fallback",
            "http://127.0.0.1:1/v1",
            "key",
            DysonOpenAiApiModes.Responses,
            [
                new ManagedSlugSpec("alpha", "Alpha", null, []),
                new ManagedSlugSpec("beta", "Beta", null, []),
            ]).GetAwaiter().GetResult();
        if (upsert.IsError)
            throw new InvalidOperationException(upsert.Error);

        var listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        var alpha = listed.Value.Single().Slugs.Single(s => s.Slug == "alpha");
        var beta = listed.Value.Single().Slugs.Single(s => s.Slug == "beta");

        var setDefault = store.SetDefaultSlugAsync(alpha.Id).GetAwaiter().GetResult();
        if (setDefault.IsError)
            throw new InvalidOperationException(setDefault.Error);

        var disable = store.SetSlugEnabledAsync(alpha.Id, enabled: false).GetAwaiter().GetResult();
        if (disable.IsError)
            throw new InvalidOperationException(disable.Error);

        var def = store.GetDefaultSlugAsync().GetAwaiter().GetResult();
        if (def.IsError)
            throw new InvalidOperationException(def.Error);
        if (def.Value is null || def.Value.Id != beta.Id)
            throw new InvalidOperationException("GetDefaultSlugAsync must fall back to first enabled when default is disabled.");
    }

    private static void AssertFindSlugSkipsDisabled()
    {
        using var db = OpenTempDb();
        var store = new DysonModelStore(db);

        var upsert = store.UpsertManagedProviderAsync(
            "cliproxy-find",
            "Find",
            "http://127.0.0.1:1/v1",
            "key",
            DysonOpenAiApiModes.Responses,
            [new ManagedSlugSpec("hidden", "Hidden Alias", null, [])]).GetAwaiter().GetResult();
        if (upsert.IsError)
            throw new InvalidOperationException(upsert.Error);

        var listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        var slug = listed.Value.Single().Slugs.Single();
        var disable = store.SetSlugEnabledAsync(slug.Id, enabled: false).GetAwaiter().GetResult();
        if (disable.IsError)
            throw new InvalidOperationException(disable.Error);

        var byName = store.FindSlugByNameAsync("hidden").GetAwaiter().GetResult();
        if (!byName.IsError)
            throw new InvalidOperationException("FindSlugByNameAsync must skip disabled slugs.");

        var byId = store.GetSlugAsync(slug.Id).GetAwaiter().GetResult();
        if (byId.IsError)
            throw new InvalidOperationException("GetSlugAsync by id must still resolve disabled slugs.");
    }

    private static void AssertSetDefaultRejectsDisabled()
    {
        using var db = OpenTempDb();
        var store = new DysonModelStore(db);

        var upsert = store.UpsertManagedProviderAsync(
            "cliproxy-default",
            "Default",
            "http://127.0.0.1:1/v1",
            "key",
            DysonOpenAiApiModes.Responses,
            [new ManagedSlugSpec("off", "Off", null, [])]).GetAwaiter().GetResult();
        if (upsert.IsError)
            throw new InvalidOperationException(upsert.Error);

        var listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        var slug = listed.Value.Single().Slugs.Single();
        var disable = store.SetSlugEnabledAsync(slug.Id, enabled: false).GetAwaiter().GetResult();
        if (disable.IsError)
            throw new InvalidOperationException(disable.Error);

        var setDefault = store.SetDefaultSlugAsync(slug.Id).GetAwaiter().GetResult();
        if (!setDefault.IsError)
            throw new InvalidOperationException("SetDefaultSlugAsync must reject disabled slugs.");
    }

    private static void AssertFormatAvailableModelsSkipsDisabled()
    {
        var providers = new List<DysonModelProviderEntity>
        {
            new()
            {
                ProviderKind = DysonProviderKinds.OpenAICompatible,
                DisplayName = "OAI",
                Slugs =
                [
                    new DysonModelSlugEntity
                    {
                        Slug = "on",
                        DisplayAlias = "On",
                        IsEnabled = true,
                        DefaultReasoningEffort = "high",
                        ReasoningModes = ["high"],
                    },
                    new DysonModelSlugEntity
                    {
                        Slug = "off",
                        DisplayAlias = "Off",
                        IsEnabled = false,
                        DefaultReasoningEffort = "low",
                        ReasoningModes = ["low"],
                    },
                ],
            },
        };

        var block = DysonAgentSystemPrompts.FormatAvailableModelsBlock(
            providers, DysonProviderKinds.OpenAICompatible);
        if (block is null)
            throw new InvalidOperationException("Expected models block with enabled slug.");
        if (!block.Contains("`on`", StringComparison.Ordinal)
            || block.Contains("`off`", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Catalog must omit disabled slugs:\n{block}");
        }
    }

    private static DysonDbContext OpenTempDb()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<DysonDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new DysonDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
