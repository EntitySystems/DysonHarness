using System.Runtime.CompilerServices;
using DysonHarness;

namespace Harness.Tests;

/// <summary>Voice tryout: persisted microphone id, install gates, and stop. No driver, no Phonon.</summary>
public class DysonSpeechTryoutTests
{
    [Fact]
    public async Task Saved_microphone_id_is_kept_and_a_missing_one_is_not_swapped()
    {
        var settings = new MemorySettings();
        var devices = new List<DysonAudioInputDevice> { new("1", "USB Mic"), new("0", "Default") };

        Assert.True((await DysonSpeechInputDevice.SaveAsync(settings, devices, "1")).IsSuccess);
        Assert.Equal("1", (await settings.GetSettingAsync(DysonAppSettingKeys.SpeechInputDeviceId)).Value);
        Assert.True((await DysonSpeechInputDevice.SaveAsync(settings, devices, "missing")).IsError);
        Assert.Equal("1", (await settings.GetSettingAsync(DysonAppSettingKeys.SpeechInputDeviceId)).Value);

        Assert.Equal("1", DysonSpeechInputDevice.Resolve(devices, "1").Value.Id);
        Assert.Equal("1", DysonSpeechInputDevice.Resolve(devices, null).Value.Id);
        Assert.True(DysonSpeechInputDevice.Resolve(devices, "gone").IsError);
        Assert.True(DysonSpeechInputDevice.Resolve([], null).IsError);
    }

    [Fact]
    public async Task Play_and_record_do_nothing_until_the_model_is_installed()
    {
        var capture = new FakeCapture();
        var stt = new FakeStt();
        var tts = new FakeTts();
        var playback = new FakePlayback();
        var tryout = new DysonSpeechTryout(capture, stt, tts, playback);

        var play = await tryout.PlayAsync("hello", outputInstalled: false, CancellationToken.None);
        Assert.True(play.IsError);
        Assert.Empty(tts.Texts);
        Assert.Equal(0, playback.Plays);

        Assert.True((await tryout.PlayAsync("  ", outputInstalled: true, CancellationToken.None)).IsError);
        Assert.Equal(0, playback.Plays);

        var record = await Collect(tryout.RecordAsync("0", inputInstalled: false, CancellationToken.None));
        Assert.True(record[0].IsError);
        Assert.Equal(
            "No speech-to-text model is installed. Open Settings → Voice and install one.",
            record[0].Error);
        Assert.Equal(0, capture.Captures);
        Assert.Equal(0, stt.Calls);
    }

    [Fact]
    public async Task Play_surfaces_speech_and_playback_errors_and_a_second_play_stops_the_first()
    {
        var tts = new FakeTts { Error = "bridge died" };
        var playback = new FakePlayback();
        var tryout = new DysonSpeechTryout(new FakeCapture(), new FakeStt(), tts, playback);
        Assert.Equal("bridge died", (await tryout.PlayAsync("hi", true, CancellationToken.None)).Error);

        tts.Error = null;
        playback.Error = "no output device";
        Assert.Equal("no output device", (await tryout.PlayAsync("hi", true, CancellationToken.None)).Error);

        tts.Texts.Clear();
        playback.Error = null;
        playback.Plays = 0;
        playback.Stops = 0;
        var first = tryout.PlayAsync("one", true, CancellationToken.None);
        await WaitUntil(() => playback.Waiting == 1);
        var second = tryout.PlayAsync("two", true, CancellationToken.None);
        Assert.True((await first).IsSuccess);
        Assert.True((await second).IsSuccess);
        Assert.Equal(2, playback.Plays);
        Assert.True(playback.Stops >= 1);
        Assert.Equal(["one", "two"], tts.Texts);
    }

    [Fact]
    public async Task PlayPrepared_does_not_synthesize_or_stop_and_Play_still_preempts()
    {
        var tts = new FakeTts();
        var playback = new FakePlayback();
        var tryout = new DysonSpeechTryout(new FakeCapture(), new FakeStt(), tts, playback);
        var first = tryout.PlayPreparedAsync(OneChunk(), CancellationToken.None);
        await WaitUntil(() => playback.Waiting == 1);
        Assert.Empty(tts.Texts);
        Assert.Equal(0, playback.Stops);

        var second = tryout.PlayAsync("two", true, CancellationToken.None);
        Assert.True((await first).IsSuccess);
        Assert.True((await second).IsSuccess);
        Assert.Equal(["two"], tts.Texts);
        Assert.True(playback.Stops >= 1);
        Assert.Equal(2, playback.Plays);
    }

    [Fact]
    public async Task Stop_after_a_cancelled_play_releases_the_next_play()
    {
        var playback = new FakePlayback();
        using var cts = new CancellationTokenSource();
        var first = playback.PlayAsync(OneChunk(), cts.Token);
        await WaitUntil(() => playback.Waiting == 1);
        cts.Cancel();
        Assert.True((await first).IsSuccess);
        Assert.Equal(0, playback.Waiting);

        Assert.True(playback.Stop().IsSuccess);
        var second = playback.PlayAsync(OneChunk(), CancellationToken.None);
        var finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(second, finished);
        Assert.True((await second).IsSuccess);
    }

    [Fact]
    public async Task Record_appends_finals_and_stops_on_capture_or_transcription_errors()
    {
        var capture = new FakeCapture();
        capture.Windows.Add(Result<Stream, string>.AsValue(new MemoryStream([1])));
        capture.Windows.Add(Result<Stream, string>.AsValue(new MemoryStream([2])));
        var stt = new FakeStt
        {
            Scripts =
            [
                [
                    new DysonSpeechChunk("hel", 0, 100, false),
                    new DysonSpeechChunk("hello", 0, 200, true),
                ],
                [new DysonSpeechChunk("world", 0, 200, true)],
            ],
        };
        var tryout = new DysonSpeechTryout(capture, stt, new FakeTts(), new FakePlayback());
        var transcript = await Collect(tryout.RecordAsync("0", true, CancellationToken.None));
        Assert.Equal(["hello", "hello world"], transcript.Select(r => r.Value).ToArray());

        capture = new FakeCapture();
        capture.Windows.Add(Result<Stream, string>.AsError("no microphone"));
        capture.Windows.Add(Result<Stream, string>.AsValue(new MemoryStream([1])));
        var calls = stt.Calls;
        tryout = new DysonSpeechTryout(capture, stt, new FakeTts(), new FakePlayback());
        var missing = await Collect(tryout.RecordAsync("0", true, CancellationToken.None));
        Assert.Equal("no microphone", missing[0].Error);
        Assert.Equal(calls, stt.Calls);

        stt = new FakeStt { Error = "sidecar" };
        capture = new FakeCapture();
        capture.Windows.Add(Result<Stream, string>.AsValue(new MemoryStream([1])));
        capture.Windows.Add(Result<Stream, string>.AsValue(new MemoryStream([2])));
        tryout = new DysonSpeechTryout(capture, stt, new FakeTts(), new FakePlayback());
        var failed = await Collect(tryout.RecordAsync("0", true, CancellationToken.None));
        Assert.Equal("sidecar", failed[0].Error);
        Assert.Equal(1, stt.Calls);
    }

    [Fact]
    public async Task Record_with_live_speech_uses_pcm_and_yields_the_growing_transcript()
    {
        var capture = new FakeCapture();
        capture.Pcm.Add(Result<byte[], string>.AsValue([1, 0]));
        capture.Pcm.Add(Result<byte[], string>.AsValue([2, 0]));
        var stt = new FakeLiveStt();
        var tryout = new DysonSpeechTryout(capture, stt, new FakeTts(), new FakePlayback());
        var transcript = await Collect(tryout.RecordAsync("9", true, CancellationToken.None));
        Assert.Equal(["a", "aa", "aa!"], transcript.Select(r => r.Value).ToArray());
        Assert.Equal(0, capture.Captures);
        Assert.Equal(1, capture.PcmCaptures);
        Assert.Equal(["9"], capture.DeviceIds);
        Assert.Equal(0, stt.Calls);
    }

    [Fact]
    public async Task Record_yields_a_live_probe_error_without_opening_the_microphone()
    {
        var capture = new FakeCapture();
        capture.Pcm.Add(Result<byte[], string>.AsValue([1]));
        var stt = new FakeLiveStt { UsesError = "runtime missing" };
        var tryout = new DysonSpeechTryout(capture, stt, new FakeTts(), new FakePlayback());
        var failed = await Collect(tryout.RecordAsync("0", true, CancellationToken.None));
        Assert.Equal("runtime missing", failed[0].Error);
        Assert.Equal(0, capture.Captures);
        Assert.Equal(0, capture.PcmCaptures);
        Assert.Equal(0, stt.LiveCalls);
    }

    [Fact]
    public async Task Recorded_enroll_does_not_open_the_mic_when_no_speech_to_text_is_installed()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        await using var _ = conn;
        var repo = DysonTempDb.Speech(accessor);
        var settings = DysonTempDb.Settings(accessor);
        Assert.True((await repo.EnsureBuiltInAsync()).IsSuccess);
        var root = Path.Combine(Path.GetTempPath(), "dyson-enroll-nostt-" + Guid.NewGuid().ToString("N"));
        var stt = new DysonActiveSpeechToText(repo, settings, root);
        var tts = new DysonActiveTextToSpeech(repo, settings, root);
        var capture = new FakeCapture();
        var tryout = new DysonSpeechTryout(capture, new FakeStt(), new FakeTts(), new FakePlayback());

        var result = await tryout.EnrollRecordedVoiceAsync(stt, tts, "0", CancellationToken.None);

        Assert.Equal(
            "No speech-to-text model is installed. Open Settings → Voice and install one.",
            result.Error);
        Assert.Equal(0, capture.PcmCaptures);
        Assert.Equal(0, capture.Captures);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Q8_gguf_siblings_take_the_q4_voice_train_card()
    {
        Assert.True(DysonSpeechTryout.ShowsRecordedTrain(DysonGgufSpeechModels.PocketGgufId));
        Assert.True(DysonSpeechTryout.ShowsRecordedTrain(DysonGgufSpeechModels.PocketGgufQ8Id));
        Assert.True(DysonSpeechTryout.ShowsTrainTranscript(DysonGgufSpeechModels.PocketGgufQ8Id));
        Assert.Equal(
            DysonSpeechTryout.ShowsTrainTranscript(DysonGgufSpeechModels.PocketGgufId),
            DysonSpeechTryout.ShowsTrainTranscript(DysonGgufSpeechModels.PocketGgufQ8Id));
        Assert.False(DysonSpeechTryout.RequiresTrainTranscript(DysonGgufSpeechModels.PocketGgufQ8Id));
        Assert.True(DysonCrispSpeech.IsTextToSpeech(DysonGgufSpeechModels.PocketGgufQ8Id));

        Assert.True(DysonSpeechTryout.ShowsRecordedTrain(DysonGgufSpeechModels.ChatterboxGgufId));
        Assert.True(DysonSpeechTryout.ShowsRecordedTrain(DysonGgufSpeechModels.ChatterboxGgufQ8Id));
        Assert.True(DysonSpeechTryout.ShowsTrainTranscript(DysonGgufSpeechModels.ChatterboxGgufQ8Id));
        Assert.Equal(
            DysonSpeechTryout.ShowsTrainTranscript(DysonGgufSpeechModels.ChatterboxGgufId),
            DysonSpeechTryout.ShowsTrainTranscript(DysonGgufSpeechModels.ChatterboxGgufQ8Id));
        Assert.False(DysonSpeechTryout.RequiresTrainTranscript(DysonGgufSpeechModels.ChatterboxGgufQ8Id));
        Assert.True(DysonCrispSpeech.IsTextToSpeech(DysonGgufSpeechModels.ChatterboxGgufQ8Id));

        Assert.False(DysonSpeechTryout.ShowsRecordedTrain(DysonGgufSpeechModels.PhononGgufQ8Id));
        Assert.False(DysonSpeechTryout.ShowsTrainTranscript(DysonGgufSpeechModels.PhononGgufQ8Id));
        Assert.False(DysonCrispSpeech.IsTextToSpeech(DysonGgufSpeechModels.PhononGgufQ8Id));
    }

    [Fact]
    public async Task Python_qwen_recorded_enroll_skips_speech_to_text_and_writes_nothing_without_a_runtime()
    {
        Assert.True(DysonSpeechTryout.ShowsRecordedTrain(DysonQwenSpeech.Id));
        Assert.False(DysonSpeechTryout.RequiresTrainTranscript(DysonQwenSpeech.Id));
        Assert.True(DysonSpeechTryout.ShowsRecordedTrain(DysonGgufSpeechModels.QwenGgufId));
        Assert.True(DysonSpeechTryout.RequiresTrainTranscript(DysonGgufSpeechModels.QwenGgufId));
        Assert.True(DysonSpeechTryout.ShowsRecordedTrain(DysonGgufSpeechModels.QwenGgufQ4Id));
        Assert.True(DysonSpeechTryout.RequiresTrainTranscript(DysonGgufSpeechModels.QwenGgufQ4Id));
        Assert.True(DysonCrispSpeech.IsTextToSpeech(DysonGgufSpeechModels.QwenGgufQ4Id));
        Assert.True(DysonSpeechTryout.ShowsTrainTranscript(DysonGgufSpeechModels.QwenGgufQ4Id));
        Assert.False(DysonSpeechTryout.ShowsRecordedTrain(DysonChatterboxSpeech.Id));
        Assert.False(DysonSpeechTryout.ShowsRecordedTrain(DysonPocketSpeech.Id));
        Assert.True(DysonSpeechTryout.ShowsTrainTranscript(DysonQwenSpeech.Id));
        Assert.True(DysonSpeechTryout.ShowsTrainTranscript(DysonChatterboxSpeech.Id));
        Assert.True(DysonSpeechTryout.ShowsTrainTranscript(DysonGgufSpeechModels.PocketGgufId));
        Assert.True(DysonSpeechTryout.ShowsTrainTranscript(DysonGgufSpeechModels.QwenGgufId));
        Assert.True(DysonSpeechTryout.ShowsTrainTranscript(DysonGgufSpeechModels.ChatterboxGgufId));
        Assert.False(DysonSpeechTryout.ShowsTrainTranscript(DysonPocketSpeech.Id));

        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        await using var _ = conn;
        var repo = DysonTempDb.Speech(accessor);
        var settings = DysonTempDb.Settings(accessor);
        Assert.True((await repo.EnsureBuiltInAsync()).IsSuccess);
        Assert.True((await repo.MarkInstalledAsync(DysonQwenSpeech.Id, "dev")).IsSuccess);
        var root = Path.Combine(Path.GetTempPath(), "dyson-enroll-qwen-" + Guid.NewGuid().ToString("N"));
        var stt = new DysonActiveSpeechToText(repo, settings, root);
        var tts = new DysonActiveTextToSpeech(repo, settings, root);
        Assert.True((await tts.SetActiveAsync(DysonQwenSpeech.Id)).IsSuccess);

        var emptyCapture = new FakeCapture();
        var tryout = new DysonSpeechTryout(emptyCapture, new FakeStt(), new FakeTts(), new FakePlayback());
        var empty = await tryout.EnrollRecordedVoiceAsync(stt, tts, "0", CancellationToken.None);
        Assert.Equal("The recording was empty, so the voice was not saved.", empty.Error);
        Assert.Equal(1, emptyCapture.PcmCaptures);
        Assert.False(Directory.Exists(root));
        Assert.Empty((await repo.ListVoicesAsync(DysonQwenSpeech.Id)).Value);

        var capture = new FakeCapture();
        capture.Pcm.Add(Result<byte[], string>.AsValue([1, 0, 2, 0]));
        tryout = new DysonSpeechTryout(capture, new FakeStt(), new FakeTts(), new FakePlayback());
        var reached = await tryout.EnrollRecordedVoiceAsync(stt, tts, "0", CancellationToken.None);
        Assert.Contains("is not installed", reached.Error);
        Assert.DoesNotContain(".mp3", reached.Error);
        Assert.NotEqual(
            "No speech-to-text model is installed. Open Settings → Voice and install one.",
            reached.Error);
        Assert.Equal(1, capture.PcmCaptures);
        Assert.Empty((await repo.ListVoicesAsync(DysonQwenSpeech.Id)).Value);
        Assert.False(Directory.Exists(Path.Combine(root, "qwen", "voices")));
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);

        Assert.True((await repo.MarkInstalledAsync(DysonGgufSpeechModels.QwenGgufId, "dev")).IsSuccess);
        Assert.True((await tts.SetActiveAsync(DysonGgufSpeechModels.QwenGgufId)).IsSuccess);
        var ggufCapture = new FakeCapture();
        ggufCapture.Pcm.Add(Result<byte[], string>.AsValue([1, 0]));
        tryout = new DysonSpeechTryout(ggufCapture, new FakeStt(), new FakeTts(), new FakePlayback());
        var gguf = await tryout.EnrollRecordedVoiceAsync(stt, tts, "0", CancellationToken.None);
        Assert.Equal(
            "No speech-to-text model is installed. Open Settings → Voice and install one.",
            gguf.Error);
        Assert.Equal(0, ggufCapture.PcmCaptures);
    }

    [Fact]
    public async Task Recorded_enroll_uses_the_train_box_and_skips_speech_to_text()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        await using var _ = conn;
        var repo = DysonTempDb.Speech(accessor);
        var settings = DysonTempDb.Settings(accessor);
        Assert.True((await repo.EnsureBuiltInAsync()).IsSuccess);
        Assert.True((await repo.MarkInstalledAsync(DysonGgufSpeechModels.QwenGgufId, "dev")).IsSuccess);
        var root = Path.Combine(Path.GetTempPath(), "dyson-enroll-box-" + Guid.NewGuid().ToString("N"));
        var stt = new DysonActiveSpeechToText(repo, settings, root);
        var tts = new DysonActiveTextToSpeech(repo, settings, root);
        Assert.True((await tts.SetActiveAsync(DysonGgufSpeechModels.QwenGgufId)).IsSuccess);

        var blankCapture = new FakeCapture();
        blankCapture.Pcm.Add(Result<byte[], string>.AsValue([1, 0]));
        var tryout = new DysonSpeechTryout(blankCapture, new FakeStt(), new FakeTts(), new FakePlayback());
        var blank = await tryout.EnrollRecordedVoiceAsync(stt, tts, "0", CancellationToken.None, "   ");
        Assert.Equal(
            "No speech-to-text model is installed. Open Settings → Voice and install one.",
            blank.Error);
        Assert.Equal(0, blankCapture.PcmCaptures);

        var emptyCapture = new FakeCapture();
        tryout = new DysonSpeechTryout(emptyCapture, new FakeStt(), new FakeTts(), new FakePlayback());
        var empty = await tryout.EnrollRecordedVoiceAsync(
            stt, tts, "0", CancellationToken.None, DysonSpeechTryout.ReadAloudSentence);
        Assert.Equal("The recording was empty, so the voice was not saved.", empty.Error);
        Assert.Equal(1, emptyCapture.PcmCaptures);
        Assert.False(Directory.Exists(root));

        var capture = new FakeCapture();
        capture.Pcm.Add(Result<byte[], string>.AsValue([1, 0]));
        tryout = new DysonSpeechTryout(capture, new FakeStt(), new FakeTts(), new FakePlayback());
        var reached = await tryout.EnrollRecordedVoiceAsync(
            stt, tts, "0", CancellationToken.None, DysonSpeechTryout.ReadAloudSentence);
        Assert.Contains("not installed", reached.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exact transcript", reached.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No speech-to-text", reached.Error);
        Assert.Equal(1, capture.PcmCaptures);
        Assert.False(Directory.Exists(Path.Combine(root, "qwen-gguf", "voices")));
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Stop_recording_ends_capture_and_a_second_record_is_refused()
    {
        var capture = new FakeCapture { Hold = true };
        var tryout = new DysonSpeechTryout(capture, new FakeStt(), new FakeTts(), new FakePlayback());
        var done = Task.Run(async () =>
            await Collect(tryout.RecordAsync("3", true, CancellationToken.None)));
        await capture.Holding.Task;
        Assert.True(tryout.StopRecording().IsSuccess);
        var stopped = await done;
        Assert.Empty(stopped);
        Assert.Equal(1, capture.Stops);
        Assert.Equal(["3"], capture.DeviceIds);

        capture = new FakeCapture { Hold = true };
        tryout = new DysonSpeechTryout(capture, new FakeStt(), new FakeTts(), new FakePlayback());
        var first = Task.Run(async () => await Collect(tryout.RecordAsync("0", true, CancellationToken.None)));
        await capture.Holding.Task;
        var second = await Collect(tryout.RecordAsync("0", true, CancellationToken.None));
        Assert.Equal("Already recording.", second[0].Error);
        Assert.True(tryout.StopRecording().IsSuccess);
        Assert.Empty(await first);
    }

    private static async Task<List<Result<string, string>>> Collect(
        IAsyncEnumerable<Result<string, string>> source)
    {
        var list = new List<Result<string, string>>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private static async IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> OneChunk()
    {
        yield return Result<DysonSpeechAudioChunk, string>.AsValue(new DysonSpeechAudioChunk([0, 0], 24000, true));
        await Task.CompletedTask;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class MemorySettings : IDysonSubjectSettingsRepository
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<VoidResult<string>> EnsureSubjectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<Result<string?, string>> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            Values.TryGetValue(key, out var value);
            return Task.FromResult(Result<string?, string>.AsValue(value));
        }

        public Task<VoidResult<string>> SetSettingAsync(
            string key, string? value, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(value))
                Values.Remove(key);
            else
                Values[key] = value;
            return Task.FromResult(VoidResult<string>.Success);
        }
    }

    private sealed class FakeCapture : IDysonAudioCapture
    {
        public int Captures;
        public int Stops;
        public bool Hold;
        public List<string?> DeviceIds { get; } = [];
        public List<Result<Stream, string>> Windows { get; } = [];
        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _hold;

        public Result<IReadOnlyList<DysonAudioInputDevice>, string> ListDevices() =>
            Result<IReadOnlyList<DysonAudioInputDevice>, string>.AsValue([new DysonAudioInputDevice("0", "Mic")]);

        public int PcmCaptures;
        public List<Result<byte[], string>> Pcm { get; } = [];

        public async IAsyncEnumerable<Result<byte[], string>> CapturePcmAsync(
            string? deviceId,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            bool dropOldest = false)
        {
            PcmCaptures++;
            DeviceIds.Add(deviceId);
            foreach (var block in Pcm)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;
                yield return block;
            }

            if (!Hold)
                yield break;

            _hold = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Holding.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, _hold.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async IAsyncEnumerable<Result<Stream, string>> CaptureWindowsAsync(
            string? deviceId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Captures++;
            DeviceIds.Add(deviceId);
            foreach (var window in Windows)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;
                yield return window;
            }

            if (!Hold)
                yield break;

            _hold = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Holding.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, _hold.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public VoidResult<string> Stop()
        {
            Stops++;
            if (_hold is null)
                return VoidResult<string>.AsError("Nothing is recording.");
            _hold.Cancel();
            return VoidResult<string>.Success;
        }
    }

    private sealed class FakeStt : IDysonSpeechToText
    {
        public int Calls;
        public string? Error;
        public List<IReadOnlyList<DysonSpeechChunk>> Scripts { get; set; } = [];
        public string RuntimeId => DysonPhononSpeech.Id;

        public async IAsyncEnumerable<Result<DysonSpeechChunk, string>> TranscribeAsync(
            Stream audio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var index = Calls++;
            if (Error is not null)
            {
                yield return Result<DysonSpeechChunk, string>.AsError(Error);
                yield break;
            }

            var script = Scripts.Count == 0
                ? [new DysonSpeechChunk("hello", 0, 1, true)]
                : Scripts[Math.Min(index, Scripts.Count - 1)];
            foreach (var chunk in script)
                yield return Result<DysonSpeechChunk, string>.AsValue(chunk);
            await Task.CompletedTask;
        }
    }

    private sealed class FakeLiveStt : IDysonSpeechToText, IDysonLiveSpeechToText
    {
        public int Calls;
        public int LiveCalls;
        public string? UsesError;
        public string RuntimeId => DysonGgufSpeechModels.NemotronId;

        public Task<Result<bool, string>> UsesLivePcmAsync(CancellationToken cancellationToken) =>
            Task.FromResult(UsesError is null
                ? Result<bool, string>.AsValue(true)
                : Result<bool, string>.AsError(UsesError));

        public async IAsyncEnumerable<Result<DysonSpeechChunk, string>> TranscribeLiveAsync(
            IAsyncEnumerable<Result<byte[], string>> pcm16kMono,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LiveCalls++;
            var text = "";
            await foreach (var block in pcm16kMono.WithCancellation(cancellationToken))
            {
                if (block.IsError)
                {
                    yield return Result<DysonSpeechChunk, string>.AsError(block.Error);
                    yield break;
                }

                text += "a";
                yield return Result<DysonSpeechChunk, string>.AsValue(new DysonSpeechChunk(text, 0, 100, false));
            }

            yield return Result<DysonSpeechChunk, string>.AsValue(new DysonSpeechChunk(text + "!", 0, 200, true));
        }

        public async IAsyncEnumerable<Result<DysonSpeechChunk, string>> TranscribeAsync(
            Stream audio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            yield return Result<DysonSpeechChunk, string>.AsError("wav");
            await Task.CompletedTask;
        }
    }

    private sealed class FakeTts : IDysonTextToSpeech
    {
        public List<string> Texts { get; } = [];
        public string? Error;
        public string RuntimeId => DysonActiveTextToSpeech.ActiveRuntimeId;

        public async IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> SynthesizeAsync(
            string text,
            string? voiceId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Texts.Add(text);
            if (Error is not null)
                yield return Result<DysonSpeechAudioChunk, string>.AsError(Error);
            else
                yield return Result<DysonSpeechAudioChunk, string>.AsValue(new DysonSpeechAudioChunk([0, 0], 24000, true));
            await Task.CompletedTask;
        }
    }

    private sealed class FakePlayback : IDysonAudioPlayback
    {
        public int Plays;
        public int Stops;
        public int Waiting;
        public string? Error;
        private readonly TaskCompletionSource _blocker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public async Task<VoidResult<string>> PlayAsync(
            IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> chunks,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Plays);
            await foreach (var chunk in chunks.WithCancellation(cancellationToken))
            {
                if (chunk.IsError)
                    return new VoidResult<string>(chunk.Error);
            }

            if (Error is not null)
                return new VoidResult<string>(Error);

            Interlocked.Increment(ref _armed);
            Interlocked.Increment(ref Waiting);
            try
            {
                await _blocker.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return VoidResult<string>.Success;
            }
            finally
            {
                Interlocked.Decrement(ref Waiting);
            }

            return VoidResult<string>.Success;
        }

        public VoidResult<string> Stop(TimeSpan fadeOut = default)
        {
            Stops++;
            // A replaced play cancels the waiter before Stop runs. Waiting is already 0,
            // but the next PlayAsync still has to observe this Stop or the suite deadlocks.
            if (Volatile.Read(ref _armed) > 0)
                _blocker.TrySetResult();
            return VoidResult<string>.Success;
        }
    }
}
