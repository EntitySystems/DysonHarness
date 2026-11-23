using DysonHarness;

namespace Harness.Tests;

/// <summary>AudioPlayer url/path: exactly-one-of rule, accepted props, content types, open_file path rules, card data and the click-to-load URL gate.</summary>
public class DysonA2uiAudioTests
{
    private static string Surface(string props) =>
        DysonA2uiValidatorTests.Create("{\"id\":\"root\",\"component\":\"AudioPlayer\"," + props + "}");

    [Fact]
    public void Validator_requires_exactly_one_of_url_or_path()
    {
        foreach (var props in new[] { "\"url\":\"https://example.com/a.mp3\",\"path\":\"a.mp3\"", "\"title\":\"none\"" })
        {
            var bad = DysonA2uiValidator.Parse(Surface(props));
            Assert.True(bad.IsError, props);
            Assert.Equal("/messages/0/createSurface/components/0", bad.Error.Path);
            Assert.Contains("exactly one of url or path", bad.Error.Message, StringComparison.Ordinal);
        }

        var pathOnly = DysonA2uiValidator.Parse(Surface("\"path\":\"media/a.mp3\",\"title\":\"T\",\"caption\":\"C\""));
        Assert.True(pathOnly.IsSuccess, pathOnly.IsError ? pathOnly.Error.ToWireJson() : null);

        var urlOnly = DysonA2uiValidator.Parse(Surface("\"url\":\"https://example.com/a.mp3\""));
        Assert.True(urlOnly.IsSuccess, urlOnly.IsError ? urlOnly.Error.ToWireJson() : null);
        var link = Assert.Single(DysonA2uiValidator.LinkFuncPointers(urlOnly.Value));
        Assert.Equal("/messages/0/createSurface/components/0/url", link.Pointer);
        Assert.Equal("open_url:https://example.com/a.mp3", link.Func);
    }

    [Fact]
    public void Unknown_prop_lists_accepted_props()
    {
        var bad = DysonA2uiValidator.Parse(Surface("\"path\":\"a.mp3\",\"bogus\":1"));
        Assert.True(bad.IsError);
        Assert.Contains("accepted: url, path, title, caption", bad.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"url\":\"javascript:alert(1)\"")]
    [InlineData("\"url\":\"data:audio/mpeg;base64,AAAA\"")]
    [InlineData("\"url\":\"media/a.mp3\"")]
    [InlineData("\"url\":{\"@path\":\"/x\"}")]
    public void Url_must_be_a_link_target(string props)
    {
        var bad = DysonA2uiValidator.Parse(Surface(props));
        Assert.True(bad.IsError, props);
        Assert.EndsWith("/url", bad.Error.Path);
    }

    [Fact]
    public void PathPointers_lists_AudioPlayer_path_and_skips_url_only_players()
    {
        var both = DysonA2uiValidator.Parse(DysonA2uiValidatorTests.Create(
            "{\"id\":\"root\",\"component\":\"Column\",\"children\":[\"a\",\"b\"]},"
            + "{\"id\":\"a\",\"component\":\"AudioPlayer\",\"url\":\"https://example.com/a.mp3\"},"
            + "{\"id\":\"b\",\"component\":\"AudioPlayer\",\"path\":\"media/b.mp3\"}"));
        Assert.True(both.IsSuccess, both.IsError ? both.Error.ToWireJson() : null);
        var (pointer, path) = Assert.Single(DysonA2uiLocalVideo.PathPointers(both.Value, DysonA2uiAudio.ComponentName));
        Assert.Equal("/messages/0/createSurface/components/2/path", pointer);
        Assert.Equal("media/b.mp3", path);
    }

    [Fact]
    public void ContentTypeFor_maps_the_file_viewer_audio_formats_only()
    {
        Assert.Equal("audio/mpeg", DysonA2uiAudio.ContentTypeFor("a.MP3"));
        Assert.Equal("audio/mp4", DysonA2uiAudio.ContentTypeFor("b.m4a"));
        Assert.Equal("audio/flac", DysonA2uiAudio.ContentTypeFor("c.flac"));
        Assert.Equal("audio/ogg", DysonA2uiAudio.ContentTypeFor("d.opus"));
        foreach (var other in new[] { "x.mp4", "x.txt", "noext" })
            Assert.Null(DysonA2uiAudio.ContentTypeFor(other));
    }

    [Fact]
    public async Task LoadCard_returns_the_card_and_texts_for_escaping_missing_unsupported_oversized_and_rootless()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "media"));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "media", "a.mp3"), [1, 2, 3]);
            await File.WriteAllBytesAsync(Path.Combine(root, "a.txt"), [1]);

            var ok = await DysonA2uiAudio.LoadCardAsync("media/a.mp3", null, root);
            Assert.True(ok.IsSuccess, ok.IsError ? ok.Error : null);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "media", "a.mp3")), ok.Value.AbsolutePath);
            Assert.Equal("audio/mpeg", ok.Value.ContentType);
            Assert.Equal("open_file:media/a.mp3", ok.Value.OpenFunc);
            Assert.Equal("a.mp3", ok.Value.Title);

            var titled = await DysonA2uiAudio.LoadCardAsync("media/a.mp3", "  Stand-up  ", root);
            Assert.Equal("Stand-up", titled.Value.Title);

            Assert.StartsWith("Path escapes work directory", (await DysonA2uiAudio.LoadCardAsync("../x.mp3", null, root)).Error);
            Assert.Equal("File not found: gone.mp3", (await DysonA2uiAudio.LoadCardAsync("gone.mp3", null, root)).Error);
            Assert.StartsWith("Not a supported audio file", (await DysonA2uiAudio.LoadCardAsync("a.txt", null, root)).Error);
            Assert.True((await DysonA2uiAudio.LoadCardAsync("media/a.mp3", null, null)).IsError);

            await using (var big = File.Create(Path.Combine(root, "big.mp3")))
                big.SetLength(DysonA2uiAudio.MaxBytes + 1);
            Assert.Equal("Audio file is larger than 500 MB: big.mp3", (await DysonA2uiAudio.LoadCardAsync("big.mp3", null, root)).Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("open_url:https://example.com/a.mp3", "https://example.com/a.mp3")]
    [InlineData("open_url:http://cdn.example/a.ogg", "http://cdn.example/a.ogg")]
    [InlineData("open_file:a.mp3", null)]
    [InlineData("open_plan:3", null)]
    [InlineData("https://example.com/a.mp3", null)]
    [InlineData(null, null)]
    [InlineData("open_url:http://localhost/a", null)]
    [InlineData("open_url:http://127.0.0.1/a", null)]
    [InlineData("open_url:http://10.0.0.5/a", null)]
    [InlineData("open_url:http://172.20.1.1/a", null)]
    [InlineData("open_url:http://192.168.1.2/a", null)]
    [InlineData("open_url:http://169.254.169.254/a", null)]
    [InlineData("open_url:http://[::1]/a", null)]
    [InlineData("open_url:http://[fd00::1]/a", null)]
    [InlineData("open_url:http://[fe80::1]/a", null)]
    [InlineData("open_url:http://0.0.0.0/a", null)]
    public void PlayableUrl_allows_public_http_hosts_only(string? func, string? expected) =>
        Assert.Equal(expected, DysonA2uiAudio.PlayableUrl(func));
}
