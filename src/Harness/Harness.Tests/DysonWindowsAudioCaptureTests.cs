#if WINDOWS
using DysonHarness;
using NAudio;
using NAudio.Wave;

namespace Harness.Tests;

/// <summary><see cref="DysonWindowsAudioCapture"/> over a fake input: no WinMM, no microphone.</summary>
public class DysonWindowsAudioCaptureTests
{
    [Fact]
    public void Stop_with_nothing_recording_is_an_error() =>
        Assert.True(new DysonWindowsAudioCapture(NoDevices, _ => new FakeWaveIn()).Stop().IsError);

    [Fact]
    public async Task A_full_window_is_a_wav_and_stop_closes_the_device()
    {
        var wave = new FakeWaveIn { PushOnStart = Tone(DysonWindowsAudioCapture.WindowBytes, 9) };
        var capture = new DysonWindowsAudioCapture(OneMic, _ => wave);
        var results = new List<Result<Stream, string>>();
        var read = Task.Run(async () =>
        {
            await foreach (var item in capture.CaptureWindowsAsync("0", CancellationToken.None))
                results.Add(item);
        });

        await wave.Started;
        await WaitUntil(() => results.Count == 1);
        Assert.True(capture.Stop().IsSuccess);
        await read;

        Assert.Single(results);
        Assert.True(results[0].IsSuccess);
        var bytes = ((MemoryStream)results[0].Value).ToArray();
        results[0].Value.Dispose();
        Assert.Equal(44 + DysonWindowsAudioCapture.WindowBytes, bytes.Length);
        Assert.Equal((byte)'R', bytes[0]);
        Assert.Equal((byte)'I', bytes[1]);
        Assert.Equal(9, bytes[44]);
        Assert.True(wave.Stopped);
        Assert.True(wave.Disposed);
        Assert.True(capture.Stop().IsError);
    }

    [Fact]
    public async Task Pcm_blocks_have_no_wav_header_and_stop_ends_the_sequence()
    {
        var wave = new FakeWaveIn { PushOnStart = Tone(DysonWindowsAudioCapture.PcmBlockBytes, 7) };
        var capture = new DysonWindowsAudioCapture(OneMic, _ => wave);
        var results = new List<Result<byte[], string>>();
        var read = Task.Run(async () =>
        {
            await foreach (var item in capture.CapturePcmAsync("0", CancellationToken.None))
                results.Add(item);
        });

        await wave.Started;
        await WaitUntil(() => results.Count == 1);
        Assert.True(capture.Stop().IsSuccess);
        await read;

        Assert.Single(results);
        Assert.True(results[0].IsSuccess);
        Assert.Equal(DysonWindowsAudioCapture.PcmBlockBytes, results[0].Value.Length);
        Assert.Equal(7, results[0].Value[0]);
        Assert.False(results[0].Value.AsSpan(0, 4).SequenceEqual("RIFF"u8));
        Assert.True(wave.Stopped);
        Assert.True(capture.Stop().IsError);
    }

    [Fact]
    public async Task Stop_flushes_a_short_tail_and_drops_a_tiny_one()
    {
        var flushed = await StopAfter(Tone(DysonWindowsAudioCapture.MinFlushBytes, 4));
        Assert.Single(flushed);
        Assert.Equal(44 + DysonWindowsAudioCapture.MinFlushBytes, ((MemoryStream)flushed[0].Value).ToArray().Length);
        flushed[0].Value.Dispose();

        var dropped = await StopAfter(Tone(DysonWindowsAudioCapture.MinFlushBytes - 2, 4));
        Assert.Empty(dropped);
    }

    [Fact]
    public async Task Missing_device_list_errors_and_a_busy_capture_are_results()
    {
        var capture = new DysonWindowsAudioCapture(
            () => Result<IReadOnlyList<DysonAudioInputDevice>, string>.AsError("No microphone (BadDeviceId)."),
            _ => throw new InvalidOperationException("opened"));
        var listed = await First(capture.CaptureWindowsAsync(null, CancellationToken.None));
        Assert.Equal("No microphone (BadDeviceId).", listed.Error);

        var wave = new FakeWaveIn();
        capture = new DysonWindowsAudioCapture(OneMic, _ => wave);
        var firstMove = capture.CaptureWindowsAsync("0", CancellationToken.None).GetAsyncEnumerator();
        var move = firstMove.MoveNextAsync();
        await wave.Started;
        var second = await First(capture.CaptureWindowsAsync("0", CancellationToken.None));
        Assert.Equal("Already recording.", second.Error);

        Assert.True(capture.Stop().IsSuccess);
        Assert.False(await move);
        await firstMove.DisposeAsync();
        Assert.True(wave.Stopped);
    }

    [Fact]
    public async Task An_unknown_id_or_a_driver_failure_does_not_look_like_a_clip()
    {
        var opened = 0;
        var capture = new DysonWindowsAudioCapture(
            OneMic,
            _ =>
            {
                opened++;
                return new FakeWaveIn();
            });
        var missing = await First(capture.CaptureWindowsAsync("5", CancellationToken.None));
        Assert.True(missing.IsError);
        Assert.Equal(0, opened);

        capture = new DysonWindowsAudioCapture(OneMic, _ => new FakeWaveIn { FailStart = true });
        var failed = await First(capture.CaptureWindowsAsync("0", CancellationToken.None));
        Assert.Contains("No microphone", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_overrun_drops_the_oldest_block_and_does_not_fault()
    {
        var block = DysonWindowsAudioCapture.PcmBlockBytes;
        var pcm = new byte[block * (DysonWindowsAudioCapture.MaxQueuedWindows + 1)];
        pcm[0] = 1;
        pcm[block] = 2;
        pcm[block * 2] = 3;
        var wave = new FakeWaveIn { PushOnStart = pcm };
        var capture = new DysonWindowsAudioCapture(OneMic, _ => wave);
        var results = new List<Result<byte[], string>>();
        var read = Task.Run(async () =>
        {
            await foreach (var item in capture.CapturePcmAsync("0", CancellationToken.None, dropOldest: true))
                results.Add(item);
        });

        await wave.Started;
        await WaitUntil(() => results.Count == DysonWindowsAudioCapture.MaxQueuedWindows);
        Assert.True(capture.Stop().IsSuccess);
        await read;

        Assert.Equal(DysonWindowsAudioCapture.MaxQueuedWindows, results.Count);
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(2, results[0].Value[0]);
        Assert.Equal(3, results[1].Value[0]);
        Assert.DoesNotContain(results, r => r.IsError);
        Assert.True(wave.Stopped);
    }

    [Fact]
    public async Task Window_and_enroll_overrun_is_still_an_error()
    {
        var windows = new FakeWaveIn
        {
            PushOnStart = new byte[DysonWindowsAudioCapture.WindowBytes * (DysonWindowsAudioCapture.MaxQueuedWindows + 1)],
        };
        var windowCapture = new DysonWindowsAudioCapture(OneMic, _ => windows);
        var windowResults = new List<Result<Stream, string>>();
        await foreach (var item in windowCapture.CaptureWindowsAsync("0", CancellationToken.None))
        {
            windowResults.Add(item);
            if (item.IsSuccess)
                await item.Value.DisposeAsync();
        }

        Assert.Contains(windowResults, r => r.IsError && r.Error.Contains("fell behind", StringComparison.Ordinal));
        Assert.True(windows.Stopped);

        var enroll = new FakeWaveIn
        {
            PushOnStart = new byte[DysonWindowsAudioCapture.PcmBlockBytes * (DysonWindowsAudioCapture.MaxQueuedWindows + 1)],
        };
        var enrollCapture = new DysonWindowsAudioCapture(OneMic, _ => enroll);
        var enrollResults = new List<Result<byte[], string>>();
        await foreach (var item in enrollCapture.CapturePcmAsync("0", CancellationToken.None))
            enrollResults.Add(item);

        Assert.Contains(enrollResults, r => r.IsError && r.Error.Contains("fell behind", StringComparison.Ordinal));
        Assert.True(enroll.Stopped);
    }

    private static async Task<List<Result<Stream, string>>> StopAfter(byte[] pcm)
    {
        var wave = new FakeWaveIn { PushOnStart = pcm };
        var capture = new DysonWindowsAudioCapture(OneMic, _ => wave);
        var results = new List<Result<Stream, string>>();
        var read = Task.Run(async () =>
        {
            await foreach (var item in capture.CaptureWindowsAsync("0", CancellationToken.None))
                results.Add(item);
        });
        await wave.Started;
        Assert.True(capture.Stop().IsSuccess);
        await read;
        Assert.True(wave.Stopped);
        return results;
    }

    private static async Task<Result<Stream, string>> First(
        IAsyncEnumerable<Result<Stream, string>> source)
    {
        await foreach (var item in source)
            return item;
        throw new InvalidOperationException("empty capture");
    }

    private static byte[] Tone(int bytes, byte value)
    {
        var pcm = new byte[bytes];
        pcm[0] = value;
        return pcm;
    }

    private static Result<IReadOnlyList<DysonAudioInputDevice>, string> OneMic() =>
        Result<IReadOnlyList<DysonAudioInputDevice>, string>.AsValue([new DysonAudioInputDevice("0", "Mic")]);

    private static Result<IReadOnlyList<DysonAudioInputDevice>, string> NoDevices() =>
        Result<IReadOnlyList<DysonAudioInputDevice>, string>.AsValue([]);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class FakeWaveIn : IWaveIn, IDisposable
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailStart { get; init; }
        public byte[]? PushOnStart { get; init; }
        public WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped { add { } remove { } }

        public void StartRecording()
        {
            if (FailStart)
                throw new MmException(MmResult.BadDeviceId, "waveInOpen");
            if (PushOnStart is { Length: > 0 })
                DataAvailable?.Invoke(this, new WaveInEventArgs(PushOnStart, PushOnStart.Length));
            _started.TrySetResult();
        }

        public void StopRecording() => Stopped = true;
        public void Dispose() => Disposed = true;
    }
}
#endif
