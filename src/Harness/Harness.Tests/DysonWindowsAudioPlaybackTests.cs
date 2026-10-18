#if WINDOWS
using System.Threading.Channels;
using DysonHarness;
using NAudio.Wave;

namespace Harness.Tests;

/// <summary><see cref="DysonWindowsAudioPlayback"/> over a fake device: no WinMM, no sound card.</summary>
public class DysonWindowsAudioPlaybackTests
{
    [Fact]
    public void Speech_chunk_maps_to_s16le_mono()
    {
        var format = DysonWindowsAudioPlayback.ToWaveFormat(24000);
        Assert.True(format.IsSuccess);
        Assert.Equal(
            (WaveFormatEncoding.Pcm, 24000, 16, 1),
            (format.Value.Encoding, format.Value.SampleRate, format.Value.BitsPerSample, format.Value.Channels));
        Assert.True(DysonWindowsAudioPlayback.ToWaveFormat(0).IsError);
    }

    [Fact]
    public void Stop_with_nothing_playing_is_an_error() =>
        Assert.True(new DysonWindowsAudioPlayback(() => new FakeDevice()).Stop().IsError);

    [Fact]
    public async Task Chunks_play_as_they_arrive_and_Stop_closes_the_device()
    {
        var device = new FakeDevice();
        var playback = new DysonWindowsAudioPlayback(() => device);
        var chunks = Channel.CreateUnbounded<Result<DysonSpeechAudioChunk, string>>();
        var play = playback.PlayAsync(chunks.Reader.ReadAllAsync(), CancellationToken.None);

        chunks.Writer.TryWrite(Chunk(480));
        await WaitUntil(() => device.Buffered == 480);
        Assert.Equal(PlaybackState.Playing, device.PlaybackState);
        Assert.False(play.IsCompleted);
        Assert.True((await playback.PlayAsync(Stream(), CancellationToken.None)).IsError);

        Assert.True(playback.Stop().IsSuccess);
        Assert.True((await play).IsSuccess);
        Assert.True(device.Disposed);
        Assert.True(playback.Stop().IsError);
    }

    [Fact]
    public async Task Cancel_closes_the_device_and_a_finished_clip_waits_for_the_device()
    {
        var device = new FakeDevice();
        using var cts = new CancellationTokenSource();
        var chunks = Channel.CreateUnbounded<Result<DysonSpeechAudioChunk, string>>();
        var play = new DysonWindowsAudioPlayback(() => device).PlayAsync(chunks.Reader.ReadAllAsync(), cts.Token);
        chunks.Writer.TryWrite(Chunk(480));
        await WaitUntil(() => device.Buffered == 480);
        cts.Cancel();
        Assert.True((await play).IsSuccess);
        Assert.True(device.Disposed);

        var drained = new FakeDevice();
        var whole = new DysonWindowsAudioPlayback(() => drained)
            .PlayAsync(Stream(Chunk(480), Chunk(0, isLast: true)), CancellationToken.None);
        await WaitUntil(() => drained.Buffered == 480);
        Assert.False(whole.IsCompleted);
        drained.Drain();
        Assert.True((await whole).IsSuccess);
        Assert.True(drained.Disposed);
    }

    [Fact]
    public async Task Errors_return_results_and_close_the_device()
    {
        var failed = new FakeDevice();
        var error = await new DysonWindowsAudioPlayback(() => failed)
            .PlayAsync(Stream(Chunk(480), Result<DysonSpeechAudioChunk, string>.AsError("bridge died")), CancellationToken.None);
        Assert.Equal("bridge died", error.Error);
        Assert.True(failed.Disposed);

        Assert.True((await new DysonWindowsAudioPlayback(() => new FakeDevice())
            .PlayAsync(Stream(Chunk(481)), CancellationToken.None)).IsError);

        var missing = new FakeDevice { FailInit = true };
        Assert.True((await new DysonWindowsAudioPlayback(() => missing)
            .PlayAsync(Stream(Chunk(480)), CancellationToken.None)).IsError);
        Assert.True(missing.Disposed);
    }

    [Fact]
    public async Task Odd_pcm_chunk_is_carried_into_the_next_chunk()
    {
        byte[] whole = [1, 2, 3, 4, 5, 6, 7, 8];
        Assert.Equal(whole, await PlayedBytes(Pcm(whole)));
        Assert.Equal(whole, await PlayedBytes(Pcm(whole[..3]), Pcm(whole[3..])));
        Assert.Equal(whole, await PlayedBytes(Pcm(whole[..1]), Pcm(whole[1..])));
    }

    private static Result<DysonSpeechAudioChunk, string> Chunk(int bytes, bool isLast = false) =>
        Result<DysonSpeechAudioChunk, string>.AsValue(new DysonSpeechAudioChunk(new byte[bytes], 24000, isLast));

    private static DysonSpeechAudioChunk Pcm(byte[] pcm) => new(pcm, 24000, false);

    private static async Task<byte[]> PlayedBytes(params DysonSpeechAudioChunk[] parts)
    {
        var device = new FakeDevice();
        var play = new DysonWindowsAudioPlayback(() => device).PlayAsync(
            Stream(parts.Select(part => Result<DysonSpeechAudioChunk, string>.AsValue(part)).ToArray()),
            CancellationToken.None);
        var even = 0;
        foreach (var part in parts)
            even += part.Pcm.Length;
        even &= ~1;
        await WaitUntil(() => device.Buffered == even);
        var bytes = device.TakeBuffered();
        Assert.True((await play).IsSuccess);
        return bytes;
    }

    private static async IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> Stream(
        params Result<DysonSpeechAudioChunk, string>[] items)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class FakeDevice : IWavePlayer
    {
        private BufferedWaveProvider? _provider;
        public bool FailInit { get; init; }
        public bool Disposed { get; private set; }
        public int Buffered => _provider?.BufferedBytes ?? 0;
        public void Drain() => _provider!.Read(new byte[_provider.BufferedBytes]);

        public byte[] TakeBuffered()
        {
            var bytes = new byte[_provider!.BufferedBytes];
            var read = _provider.Read(bytes);
            return read == bytes.Length ? bytes : bytes[..read];
        }

        public void Init(IWaveProvider waveProvider)
        {
            if (FailInit)
                throw new NAudio.MmException(NAudio.MmResult.BadDeviceId, "waveOutOpen");
            _provider = (BufferedWaveProvider)waveProvider;
        }

        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Dispose() => Disposed = true;
        public float Volume { get; set; } = 1f;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat => _provider!.WaveFormat;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
    }
}
#endif
