using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: managed slug IsEnabled / DefaultReasoningEffort merge / SetSlug* / default fallback / FindSlug filter.
/// </summary>
public class DysonModelSlugEnabledTests
{
    [Fact]
    public void Run()
    {
        AssertUpsertMergePreservesIdAndIsEnabled();
        AssertUpsertMergePreservesDefaultReasoningEffort();
        AssertUpsertSyncSlugsFalseLeavesCatalogUnchanged();
        AssertUpsertManagedSlugInsertUpdateAndDisableKeepsRow();
        AssertUpsertManagedSlugRejectsManual();
        AssertSetSlugEnabledRejectsManual();
        AssertSetSlugDefaultReasoningEffortManagedAndRejectsManual();
        AssertSetSlugDefaultMaxTargetContextTokensManagedAndRejectsManual();
        AssertDisabledDefaultFallsBack();
        AssertFindSlugSkipsDisabled();
        AssertSetDefaultRejectsDisabled();
        AssertFormatAvailableModelsSkipsDisabled();
        AssertRemoveSlugAllowsDirectManagedAndRejectsCliProxy();
    }

    private static void AssertUpsertMergePreservesIdAndIsEnabled()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);

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

    private static void AssertUpsertMergePreservesDefaultReasoningEffort()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);

        var first = store.UpsertManagedProviderAsync(
            "cliproxy-effort-merge",
            "Effort Merge",
            "http://127.0.0.1:1/v1",
            "key",
            DysonOpenAiApiModes.Responses,
            [new ManagedSlugSpec("keep", "Keep", "high", ["high", "low"])]).GetAwaiter().GetResult();
        if (first.IsError)
            throw new InvalidOperationException(first.Error);

        var listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        var keepId = listed.Value.Single().Slugs.Single(s => s.Slug == "keep").Id;
        var set = store.SetSlugDefaultReasoningEffortAsync(keepId, "medium").GetAwaiter().GetResult();
        if (set.IsError)
            throw new InvalidOperationException(set.Error);

        var second = store.UpsertManagedProviderAsync(
            "cliproxy-effort-merge",
            "Effort Merge",
            "http://127.0.0.1:1/v1",
            "key",
            DysonOpenAiApiModes.Responses,
            [
                new ManagedSlugSpec("keep", "Keep Renamed", "high", ["none", "high"]),
                new ManagedSlugSpec("fresh", "Fresh", "high", ["high"]),
            ]).GetAwaiter().GetResult();
        if (second.IsError)
            throw new InvalidOperationException(second.Error);

        listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        var provider = listed.Value.Single();
        var keep = provider.Slugs.Single(s => s.Slug == "keep");
        if (!string.Equals(keep.DefaultReasoningEffort, "medium", StringComparison.Ordinal))
            throw new InvalidOperationException("Upsert must preserve user DefaultReasoningEffort across Verify merge.");
        if (keep.ReasoningModes is not ["none", "high"])
            throw new InvalidOperationException("Upsert must still refresh ReasoningModes from catalog.");
        if (!string.Equals(keep.DisplayAlias, "Keep Renamed", StringComparison.Ordinal))
            throw new InvalidOperationException("Upsert must still refresh DisplayAlias from catalog.");

        var fresh = provider.Slugs.Single(s => s.Slug == "fresh");
        if (!string.Equals(fresh.DefaultReasoningEffort, "high", StringComparison.Ordinal))
            throw new InvalidOperationException("New API slugs must get catalog DefaultReasoningEffort.");
    }

    private static void AssertUpsertSyncSlugsFalseLeavesCatalogUnchanged()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);

        var first = store.UpsertManagedProviderAsync(
            "cliproxy-nosync",
            "No Sync",
            "http://127.0.0.1:1/v1",
            "key",
            DysonOpenAiApiModes.Responses,
            [
                new ManagedSlugSpec("keep-a", "Keep A", "high", ["high"]),
                new ManagedSlugSpec("keep-b", "Keep B", null, []),
            ]).GetAwaiter().GetResult();
        if (first.IsError)
            throw new InvalidOperationException(first.Error);

        var listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        var provider = listed.Value.Single(p => p.ManagedSource == "cliproxy-nosync");
        var idA = provider.Slugs.Single(s => s.Slug == "keep-a").Id;
        var idB = provider.Slugs.Single(s => s.Slug == "keep-b").Id;

        var noSync = store.UpsertManagedProviderAsync(
            "cliproxy-nosync",
            "No Sync Renamed",
            "http://127.0.0.1:9999/v1",
            "new-key",
            DysonOpenAiApiModes.Responses,
            [new ManagedSlugSpec("should-not-add", "Nope", null, [])],
            shared: false,
            syncSlugs: false).GetAwaiter().GetResult();
        if (noSync.IsError)
            throw new InvalidOperationException(noSync.Error);

        listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        provider = listed.Value.Single(p => p.ManagedSource == "cliproxy-nosync");
        if (!string.Equals(provider.DisplayName, "No Sync Renamed", StringComparison.Ordinal))
            throw new InvalidOperationException("syncSlugs:false must still update DisplayName.");
        if (!string.Equals(provider.BaseUrl, "http://127.0.0.1:9999/v1", StringComparison.Ordinal))
            throw new InvalidOperationException("syncSlugs:false must still update BaseUrl.");
        if (!string.Equals(provider.ApiKey, "new-key", StringComparison.Ordinal))
            throw new InvalidOperationException("syncSlugs:false must still update ApiKey.");
        if (provider.Slugs.Count != 2)
            throw new InvalidOperationException($"syncSlugs:false must not add/remove slugs, got {provider.Slugs.Count}.");
        if (provider.Slugs.Any(s => s.Slug == "should-not-add"))
            throw new InvalidOperationException("syncSlugs:false must ignore the slugs argument.");
        if (provider.Slugs.Single(s => s.Slug == "keep-a").Id != idA
            || provider.Slugs.Single(s => s.Slug == "keep-b").Id != idB)
        {
            throw new InvalidOperationException("syncSlugs:false must preserve existing slug Ids.");
        }
    }

    private static void AssertUpsertManagedSlugInsertUpdateAndDisableKeepsRow()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);

        var upsert = store.UpsertManagedProviderAsync(
            "cliproxy-oneslug",
            "One Slug",
            "http://127.0.0.1:1/v1",
            "key",
            DysonOpenAiApiModes.Responses,
            slugs: []).GetAwaiter().GetResult();
        if (upsert.IsError)
            throw new InvalidOperationException(upsert.Error);

        var insert = store.UpsertManagedSlugAsync(
            upsert.Value,
            new ManagedSlugSpec("or-model", "OR Model", "high", ["high"]),
            enabled: true).GetAwaiter().GetResult();
        if (insert.IsError)
            throw new InvalidOperationException(insert.Error);

        var listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        var row = listed.Value.Single().Slugs.Single(s => s.Slug == "or-model");
        if (row.Id != insert.Value)
            throw new InvalidOperationException("UpsertManagedSlugAsync insert must return the new slug Id.");
        if (!row.IsEnabled)
            throw new InvalidOperationException("UpsertManagedSlugAsync insert must honor enabled:true.");
        if (row.IsDefault)
            throw new InvalidOperationException("UpsertManagedSlugAsync insert must set IsDefault=false.");

        var setEffort = store.SetSlugDefaultReasoningEffortAsync(row.Id, "medium").GetAwaiter().GetResult();
        if (setEffort.IsError)
            throw new InvalidOperationException(setEffort.Error);

        var second = store.UpsertManagedSlugAsync(
            upsert.Value,
            new ManagedSlugSpec("or-model", "OR Model Renamed", "low", ["none", "low"]),
            enabled: true).GetAwaiter().GetResult();
        if (second.IsError)
            throw new InvalidOperationException(second.Error);
        if (second.Value != insert.Value)
            throw new InvalidOperationException("UpsertManagedSlugAsync must preserve slug Id on update.");

        listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        row = listed.Value.Single().Slugs.Single(s => s.Slug == "or-model");
        if (!string.Equals(row.DisplayAlias, "OR Model Renamed", StringComparison.Ordinal))
            throw new InvalidOperationException("UpsertManagedSlugAsync must refresh DisplayAlias.");
        if (row.ReasoningModes is not ["none", "low"])
            throw new InvalidOperationException("UpsertManagedSlugAsync must refresh ReasoningModes.");
        if (!string.Equals(row.DefaultReasoningEffort, "medium", StringComparison.Ordinal))
            throw new InvalidOperationException("UpsertManagedSlugAsync must preserve user DefaultReasoningEffort.");
        if (!row.IsEnabled)
            throw new InvalidOperationException("UpsertManagedSlugAsync update must set IsEnabled.");

        var disable = store.SetSlugEnabledAsync(row.Id, enabled: false).GetAwaiter().GetResult();
        if (disable.IsError)
            throw new InvalidOperationException(disable.Error);

        listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        row = listed.Value.Single().Slugs.Single(s => s.Slug == "or-model");
        if (row.IsEnabled)
            throw new InvalidOperationException("SetSlugEnabledAsync(false) must disable the slug.");
        if (listed.Value.Single().Slugs.Count != 1)
            throw new InvalidOperationException("SetSlugEnabledAsync(false) must keep the slug row.");
    }

    private static void AssertUpsertManagedSlugRejectsManual()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);

        var create = store.CreateProviderAsync(new DysonModelProviderEntity
        {
            DisplayName = "Manual Upsert",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "k",
        }).GetAwaiter().GetResult();
        if (create.IsError)
            throw new InvalidOperationException(create.Error);

        var reject = store.UpsertManagedSlugAsync(
            create.Value,
            new ManagedSlugSpec("gpt-4o", "GPT-4o", null, []),
            enabled: true).GetAwaiter().GetResult();
        if (!reject.IsError)
            throw new InvalidOperationException("UpsertManagedSlugAsync must reject manual providers.");
    }

    private static void AssertSetSlugEnabledRejectsManual()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);

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

    private static void AssertSetSlugDefaultReasoningEffortManagedAndRejectsManual()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);

        var upsert = store.UpsertManagedProviderAsync(
            "cliproxy-effort-set",
            "Effort Set",
            "http://127.0.0.1:1/v1",
            "key",
            DysonOpenAiApiModes.Responses,
            [new ManagedSlugSpec("m", "M", "high", ["high", "low"])]).GetAwaiter().GetResult();
        if (upsert.IsError)
            throw new InvalidOperationException(upsert.Error);

        var listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        var managedId = listed.Value.Single().Slugs.Single().Id;
        var setLow = store.SetSlugDefaultReasoningEffortAsync(managedId, "  low  ").GetAwaiter().GetResult();
        if (setLow.IsError)
            throw new InvalidOperationException(setLow.Error);

        listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);
        if (!string.Equals(
                listed.Value.Single().Slugs.Single().DefaultReasoningEffort,
                "low",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SetSlugDefaultReasoningEffortAsync must normalize and persist for managed slugs.");
        }

        var clear = store.SetSlugDefaultReasoningEffortAsync(managedId, "   ").GetAwaiter().GetResult();
        if (clear.IsError)
            throw new InvalidOperationException(clear.Error);

        listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);
        if (listed.Value.Single().Slugs.Single().DefaultReasoningEffort is not null)
            throw new InvalidOperationException("Blank effort must clear DefaultReasoningEffort to null.");

        var create = store.CreateProviderAsync(new DysonModelProviderEntity
        {
            DisplayName = "Manual Effort",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "k",
        }).GetAwaiter().GetResult();
        if (create.IsError)
            throw new InvalidOperationException(create.Error);

        var add = store.AddSlugAsync(create.Value, "gpt-4o", "GPT-4o").GetAwaiter().GetResult();
        if (add.IsError)
            throw new InvalidOperationException(add.Error);

        var reject = store.SetSlugDefaultReasoningEffortAsync(add.Value, "high").GetAwaiter().GetResult();
        if (!reject.IsError)
            throw new InvalidOperationException("SetSlugDefaultReasoningEffortAsync must reject manual provider slugs.");
    }

    private static void AssertSetSlugDefaultMaxTargetContextTokensManagedAndRejectsManual()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);

        var upsert = store.UpsertManagedProviderAsync(
            "cliproxy-context-set",
            "Context Set",
            "http://127.0.0.1:1/v1",
            "key",
            DysonOpenAiApiModes.Responses,
            [new ManagedSlugSpec("m", "M", "high", ["high"])]).GetAwaiter().GetResult();
        if (upsert.IsError)
            throw new InvalidOperationException(upsert.Error);

        var listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        var managedId = listed.Value.Single().Slugs.Single().Id;
        var set = store.SetSlugDefaultMaxTargetContextTokensAsync(managedId, 200_000)
            .GetAwaiter().GetResult();
        if (set.IsError)
            throw new InvalidOperationException(set.Error);

        listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);
        if (listed.Value.Single().Slugs.Single().DefaultMaxTargetContextTokens != 200_000)
            throw new InvalidOperationException(
                "SetSlugDefaultMaxTargetContextTokensAsync must persist for managed slugs.");

        var clear = store.SetSlugDefaultMaxTargetContextTokensAsync(managedId, null)
            .GetAwaiter().GetResult();
        if (clear.IsError)
            throw new InvalidOperationException(clear.Error);

        listed = store.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);
        if (listed.Value.Single().Slugs.Single().DefaultMaxTargetContextTokens is not null)
            throw new InvalidOperationException("Null must clear DefaultMaxTargetContextTokens.");

        var create = store.CreateProviderAsync(new DysonModelProviderEntity
        {
            DisplayName = "Manual Context",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "k",
        }).GetAwaiter().GetResult();
        if (create.IsError)
            throw new InvalidOperationException(create.Error);

        var add = store.AddSlugAsync(create.Value, "gpt-4o", "GPT-4o").GetAwaiter().GetResult();
        if (add.IsError)
            throw new InvalidOperationException(add.Error);

        var reject = store.SetSlugDefaultMaxTargetContextTokensAsync(add.Value, 100_000)
            .GetAwaiter().GetResult();
        if (!reject.IsError)
            throw new InvalidOperationException(
                "SetSlugDefaultMaxTargetContextTokensAsync must reject manual provider slugs.");
    }

    private static void AssertDisabledDefaultFallsBack()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);

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
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);

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
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);

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

    private static void AssertRemoveSlugAllowsDirectManagedAndRejectsCliProxy()
    {
        static void AssertRemoved(string source, string display, string baseUrl)
        {
            var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
            using var _keepAlive = conn;
            var store = DysonTempDb.Models(accessor);

            var upsert = store.UpsertManagedProviderAsync(
                source,
                display,
                baseUrl,
                "key",
                DysonOpenAiApiModes.Completions,
                [new ManagedSlugSpec("vendor/model", "vendor/model", null, [])]).GetAwaiter().GetResult();
            if (upsert.IsError)
                throw new InvalidOperationException(upsert.Error);

            var listed = store.ListProvidersAsync().GetAwaiter().GetResult();
            if (listed.IsError)
                throw new InvalidOperationException(listed.Error);

            var slugId = listed.Value.Single(p => p.ManagedSource == source).Slugs.Single().Id;
            var remove = store.RemoveSlugAsync(slugId).GetAwaiter().GetResult();
            if (remove.IsError)
                throw new InvalidOperationException($"{source} RemoveSlugAsync should succeed: {remove.Error}");

            listed = store.ListProvidersAsync().GetAwaiter().GetResult();
            if (listed.IsError)
                throw new InvalidOperationException(listed.Error);

            if (listed.Value.Single(p => p.ManagedSource == source).Slugs.Count != 0)
                throw new InvalidOperationException($"{source} RemoveSlugAsync must delete the slug row.");
        }

        AssertRemoved(
            DysonManagedSources.OpenRouter,
            "OpenRouter",
            OpenRouterManagedInferenceProvider.ApiBaseUrl);
        AssertRemoved(
            DysonManagedSources.OrcaRouter,
            "OrcaRouter",
            OrcaRouterManagedInferenceProvider.ApiBaseUrl);

        var cliproxyAccessor = DysonTempDb.OpenMemoryAccessor(out var cliproxyConn);
        using var _keepCli = cliproxyConn;
        var cliproxyStore = DysonTempDb.Models(cliproxyAccessor);
        var cliproxy = cliproxyStore.UpsertManagedProviderAsync(
            DysonManagedSources.CliProxyCodex,
            "Codex",
            "http://127.0.0.1:8317/v1",
            "key",
            DysonOpenAiApiModes.Responses,
            [new ManagedSlugSpec("gpt-5", "GPT-5", "high", ["high"])]).GetAwaiter().GetResult();
        if (cliproxy.IsError)
            throw new InvalidOperationException(cliproxy.Error);

        var cliproxyListed = cliproxyStore.ListProvidersAsync().GetAwaiter().GetResult();
        if (cliproxyListed.IsError)
            throw new InvalidOperationException(cliproxyListed.Error);

        var cliproxySlugId = cliproxyListed.Value.Single().Slugs.Single().Id;
        var cliproxyRemove = cliproxyStore.RemoveSlugAsync(cliproxySlugId).GetAwaiter().GetResult();
        if (!cliproxyRemove.IsError)
            throw new InvalidOperationException("RemoveSlugAsync must reject CLIProxy slugs.");
        if (cliproxyRemove.Error is null
            || !cliproxyRemove.Error.Contains("cannot be removed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"CLIProxy RemoveSlugAsync error should mention cannot be removed, got '{cliproxyRemove.Error}'.");
        }
    }
}
