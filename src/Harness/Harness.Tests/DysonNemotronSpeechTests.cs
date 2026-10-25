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
