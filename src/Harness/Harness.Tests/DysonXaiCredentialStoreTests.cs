using DysonHarness;
using Microsoft.EntityFrameworkCore;

namespace Harness.Tests;

public class DysonXaiCredentialStoreTests
{
    [Fact]
    public async Task Save_then_get_round_trips_json_verbatim_in_plaintext_app_settings_row()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _ = conn;
        var store = new DysonXaiCredentialStore(accessor);
        var id = Guid.NewGuid();
        const string json = "{\"access_token\":\" padded \",\"refresh_token\":\"r\"}  ";

        Assert.True((await store.SaveAsync(id, DysonSubjects.Local, json)).IsSuccess);
        var got = await store.GetAsync(id);

        Assert.True(got.IsSuccess);
        Assert.Equal(json, got.Value);

        var raw = await accessor.RunAsync(async (db, ct) =>
            await db.AppSettings.AsNoTracking().SingleAsync(s => s.Key == $"xai_grok_credential:{id:D}", ct));
        Assert.Equal(json, raw.Value);
        Assert.Equal(DysonSubjects.Local, raw.SubjectId);
    }

    [Fact]
    public async Task Overwrite_updates_in_place_and_keeps_original_subject()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _ = conn;
        var store = new DysonXaiCredentialStore(accessor);
        var id = Guid.NewGuid();

        await store.SaveAsync(id, "subject-a", "{\"v\":1}");
        await store.SaveAsync(id, "subject-b", "{\"v\":2}");

        var rows = await accessor.RunAsync(async (db, ct) =>
            await db.AppSettings.AsNoTracking().Where(s => s.Key.StartsWith("xai_grok_credential:")).ToListAsync(ct));
        var row = Assert.Single(rows);
        Assert.Equal("{\"v\":2}", row.Value);
        Assert.Equal("subject-a", row.SubjectId);
    }

    [Fact]
    public async Task Get_unknown_id_returns_null_value_and_delete_missing_is_success()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _ = conn;
        var store = new DysonXaiCredentialStore(accessor);

        var got = await store.GetAsync(Guid.NewGuid());
        Assert.True(got.IsSuccess);
        Assert.Null(got.Value);
        Assert.True((await store.DeleteAsync(Guid.NewGuid())).IsSuccess);
    }

    [Fact]
    public async Task Delete_removes_only_that_row_and_lookup_ignores_caller_subject()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _ = conn;
        var store = new DysonXaiCredentialStore(accessor);
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        await store.SaveAsync(keep, "subject-a", "{\"k\":1}");
        await store.SaveAsync(drop, "subject-b", "{\"d\":1}");

        Assert.Equal("{\"d\":1}", (await store.GetAsync(drop)).Value);
        Assert.True((await store.DeleteAsync(drop)).IsSuccess);

        Assert.Null((await store.GetAsync(drop)).Value);
        Assert.Equal("{\"k\":1}", (await store.GetAsync(keep)).Value);
    }

    [Fact]
    public async Task Does_not_disturb_other_app_settings_keys()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _ = conn;
        var store = new DysonXaiCredentialStore(accessor);
        var settings = DysonTempDb.Settings(accessor);
        await settings.SetSettingAsync(DysonAppSettingKeys.UiTheme, "dark");

        await store.SaveAsync(Guid.NewGuid(), DysonSubjects.Local, "{\"a\":1}");

        Assert.Equal("dark", (await settings.GetSettingAsync(DysonAppSettingKeys.UiTheme)).Value);
        Assert.StartsWith("xai_grok_credential:", DysonXaiCredentialStore.KeyFor(Guid.NewGuid()));
    }

    [Fact]
    public async Task Save_rejects_blank_subject_or_json()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _ = conn;
        var store = new DysonXaiCredentialStore(accessor);

        Assert.True((await store.SaveAsync(Guid.NewGuid(), " ", "{}")).IsError);
        Assert.True((await store.SaveAsync(Guid.NewGuid(), "s", "")).IsError);
    }
}
