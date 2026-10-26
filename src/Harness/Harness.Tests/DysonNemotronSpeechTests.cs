using DysonHarness;

namespace Harness.Tests;

/// <summary>Nemotron chunking and PCM conversion. No DLL and no network.</summary>
public class DysonNemotronSpeechTests
{
    [Fact]
    public void NextChunk_emits_a_changed_partial_skips_an_unchanged_one_and_always_emits_a_final()
    {
        var first = DysonNemotronSpeech.NextChunk(null, "hi", false, 5);
        Assert.NotNull(first);
        Assert.Equal("hi", first!.Text);
        Assert.Equal(0, first.StartMs);
        Assert.Equal(5, first.EndMs);
        Assert.False(first.IsFinal);

        Assert.Null(DysonNemotronSpeech.NextChunk("hi", "hi", false, 6));

        var changed = DysonNemotronSpeech.NextChunk("hi", "hello", false, 20);
        Assert.NotNull(changed);
        Assert.False(changed!.IsFinal);
        Assert.Equal("hello", changed.Text);
        Assert.Equal(20, changed.EndMs);

        var final = DysonNemotronSpeech.NextChunk("hello", "hello", true, 30);
        Assert.NotNull(final);
        Assert.True(final!.IsFinal);
        Assert.Equal("hello", final.Text);
        Assert.Equal(30, final.EndMs);
    }

    [Fact]
    public void PcmS16ToFloat_scales_little_endian_samples_and_drops_a_trailing_byte()
    {
        byte[] pcm = [0x00, 0x00, 0xFF, 0x7F, 0x00, 0x80, 0x01];
        var floats = DysonNemotronSpeech.PcmS16ToFloat(pcm);
        Assert.Equal(3, floats.Length);
        Assert.Equal(0f, floats[0]);
        Assert.Equal(32767f / 32768f, floats[1], 5);
        Assert.Equal(-1f, floats[2]);
    }

    [Fact]
    public void ParseSpeechNemotronDevice_defaults_blank_to_vulkan_and_rejects_other_values()
    {
        Assert.Equal("speech_nemotron_device", DysonAppSettingKeys.SpeechNemotronDevice);
        Assert.Equal("vulkan", DysonAppSettingKeys.ParseSpeechNemotronDevice(null).Value);
        Assert.Equal("vulkan", DysonAppSettingKeys.ParseSpeechNemotronDevice("  ").Value);
        Assert.Equal("cpu", DysonAppSettingKeys.ParseSpeechNemotronDevice(" cpu ").Value);
        Assert.Equal("vulkan", DysonAppSettingKeys.ParseSpeechNemotronDevice("vulkan").Value);
        Assert.True(DysonAppSettingKeys.ParseSpeechNemotronDevice("gpu").IsError);
        Assert.True(DysonAppSettingKeys.ParseSpeechNemotronDevice("npu").IsError);
    }

    [Fact]
    public void NeedsReload_only_when_unloaded_or_the_root_or_device_changed()
    {
        Assert.True(DysonNemotronSpeech.NeedsReload(false, null, false, "r", false));
        Assert.False(DysonNemotronSpeech.NeedsReload(true, "R", true, "r", true));
        Assert.True(DysonNemotronSpeech.NeedsReload(true, "r", true, "r", false));
        Assert.True(DysonNemotronSpeech.NeedsReload(true, "r", false, "r", true));
        Assert.True(DysonNemotronSpeech.NeedsReload(true, "old", true, "r", true));
    }

    [Fact]
    public async Task SetNemotronDevice_writes_the_setting_and_rejects_gpu_without_loading_the_dll()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        await using var _ = conn;
        var settings = DysonTempDb.Settings(accessor);
        var stt = new DysonActiveSpeechToText(DysonTempDb.Speech(accessor), settings, Path.GetTempPath());

        Assert.True((await stt.SetNemotronDeviceAsync("gpu")).IsError);
        Assert.True((await stt.SetNemotronDeviceAsync("cpu")).IsSuccess);
        Assert.Equal("cpu", (await settings.GetSettingAsync(DysonAppSettingKeys.SpeechNemotronDevice)).Value);
    }

    /// <summary>Opt-in: set <c>DYSON_NEMOTRON_SMOKE=1</c> to load the installed transcribe.dll. Skipped otherwise.</summary>
    [Fact]
    public async Task CheckRuntime_loads_the_installed_dll_when_smoke_is_opted_in()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DYSON_NEMOTRON_SMOKE"), "1", StringComparison.Ordinal))
            return;

        var root = DysonSpeechPaths.GetInstallRoot(DysonAppMode.Dev, stampedChannel: null);
        var check = await DysonNemotronSpeech.CheckRuntimeAsync(root, CancellationToken.None);
        Assert.True(check.IsSuccess, check.IsError ? check.Error : null);
    }
}
