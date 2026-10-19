using System.Runtime.CompilerServices;
using System.Text;

namespace DysonHarness;

/// <summary>
/// Settings → Voice tryout: speak text through the active TTS model, or record the microphone.
/// A live model (<see cref="IDysonLiveSpeechToText"/>) streams PCM. Otherwise each capture window is one
/// <see cref="IDysonSpeechToText.TranscribeAsync"/>.
/// <see cref="EnrollRecordedVoiceAsync"/> records one 10 s clip and enrolls it through the active TTS model.
/// </summary>
public sealed class DysonSpeechTryout(
    IDysonAudioCapture capture,
    IDysonSpeechToText speechToText,
    IDysonTextToSpeech textToSpeech,
    IDysonAudioPlayback playback)
{
    private readonly IDysonAudioCapture _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    private readonly IDysonSpeechToText _speechToText = speechToText ?? throw new ArgumentNullException(nameof(speechToText));
    private readonly IDysonTextToSpeech _textToSpeech = textToSpeech ?? throw new ArgumentNullException(nameof(textToSpeech));
    private readonly IDysonAudioPlayback _playback = playback ?? throw new ArgumentNullException(nameof(playback));
    /// <summary>
    /// Sentence the train box starts with. The user can edit it; the trimmed text is the enroll transcript.
    /// </summary>
    public const string ReadAloudSentence =
        "The birch canoe slid on the smooth planks, then five thick slabs of blue cheese sat beside a jar of fresh jam.";

    /// <summary>
    /// Train card shows consent and Record 10 seconds.
    /// Python Qwen plus CrispASR GGUF voice models (<see cref="DysonCrispSpeech.IsTextToSpeech"/>),
    /// including the Q8 Pocket and Chatterbox siblings. Python Pocket and Chatterbox stay file-only.
    /// Phonon GGUF does not enroll.
    /// </summary>
    public static bool ShowsRecordedTrain(string? runtimeId) =>
        runtimeId == DysonQwenSpeech.Id
        || (runtimeId is not null && DysonCrispSpeech.IsTextToSpeech(runtimeId));

    /// <summary>
    /// Train card shows the editable transcript box, prefilled with <see cref="ReadAloudSentence"/>.
    /// Every model that enrolls a voice. Python Pocket does not.
    /// </summary>
    public static bool ShowsTrainTranscript(string? runtimeId) =>
        ShowsRecordedTrain(runtimeId) || runtimeId == DysonChatterboxSpeech.Id;

    /// <summary>
    /// Qwen3-TTS GGUF enroll rejects a blank transcript. The train card does not gate the file picker on it.
    /// </summary>
    public static bool RequiresTrainTranscript(string? runtimeId) =>
        DysonGgufSpeechModels.IsQwenGguf(runtimeId);

    /// <summary>16 kHz mono s16le for 10 seconds. Matches <c>DysonWindowsAudioCapture</c>.</summary>
    private const int RecordBytes = 10 * 16_000 * 2;

    private const string EmptyTranscript =
        "Speech-to-text returned an empty transcript, so the voice was not saved.";

    private readonly SemaphoreSlim _playGate = new(1, 1);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _playCts;
    private CancellationTokenSource? _recordCts;
    private bool _recording;

    /// <summary>Device list. Hops off the caller so a Blazor circuit does not touch the driver.</summary>
    public Task<Result<IReadOnlyList<DysonAudioInputDevice>, string>> ListMicrophonesAsync() =>
        Task.Run(_capture.ListDevices);

    /// <summary>
    /// Speaks <paramref name="text"/> with the active model. A second call stops the clip already playing.
    /// <paramref name="outputInstalled"/> false does not start synthesis or playback.
    /// </summary>
    public async Task<VoidResult<string>> PlayAsync(
        string? text,
        bool outputInstalled,
        CancellationToken cancellationToken)
    {
        await Task.Run(() => 0, CancellationToken.None).ConfigureAwait(false);
        if (!outputInstalled)
            return new VoidResult<string>(
                "No text-to-speech model is installed. Open Settings → Voice and install one.");
        if (string.IsNullOrWhiteSpace(text))
            return new VoidResult<string>("Enter text to speak.");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _playCts;
            _playCts = cts;
        }

        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _playback.Stop();
        await _playGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (cts.IsCancellationRequested)
                return VoidResult<string>.Success;
            return await _playback.PlayAsync(
                _textToSpeech.SynthesizeAsync(text.Trim(), voiceId: null, cts.Token),
                cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _playGate.Release();
            lock (_gate)
            {
                if (ReferenceEquals(_playCts, cts))
                    _playCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>Stops the current clip. Nothing playing is an error from <see cref="IDysonAudioPlayback.Stop"/>.</summary>
    public VoidResult<string> StopPlayback()
    {
        CancellationTokenSource? cts;
        lock (_gate)
            cts = _playCts;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return _playback.Stop();
    }

    /// <summary>
    /// Captures until <see cref="StopRecording"/> or <paramref name="cancellationToken"/> and yields the growing transcript.
    /// <paramref name="inputInstalled"/> false does not open the microphone.
    /// </summary>
    public async IAsyncEnumerable<Result<string, string>> RecordAsync(
        string? deviceId,
        bool inputInstalled,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Run(() => 0, CancellationToken.None).ConfigureAwait(false);
        if (!inputInstalled)
        {
            yield return Result<string, string>.AsError(
                "No speech-to-text model is installed. Open Settings → Voice and install one.");
            yield break;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = false;
        lock (_gate)
        {
            if (!_recording)
            {
                _recording = true;
                _recordCts = cts;
                started = true;
            }
        }

        if (!started)
        {
            cts.Dispose();
            yield return Result<string, string>.AsError("Already recording.");
            yield break;
        }

        IAsyncEnumerator<Result<Stream, string>>? windows = null;
        try
        {
            if (_speechToText is IDysonLiveSpeechToText live)
            {
                Result<bool, string> uses;
                try
                {
                    uses = await live.UsesLivePcmAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                if (uses.IsError)
                {
                    yield return Result<string, string>.AsError(uses.Error);
                    yield break;
                }

                if (uses.Value)
                {
                    await foreach (var spoken in RecordLiveAsync(live, deviceId, cts).ConfigureAwait(false))
                        yield return spoken;
                    yield break;
                }
            }

            var text = new StringBuilder();
            windows = _capture.CaptureWindowsAsync(deviceId, cts.Token).GetAsyncEnumerator(cts.Token);
            while (true)
            {
                bool more;
                var cancelled = false;
                try
                {
                    more = await windows.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    more = false;
                }

                if (cancelled || !more)
                    yield break;

                var window = windows.Current;
                if (window.IsError)
                {
                    yield return Result<string, string>.AsError(window.Error);
                    yield break;
                }

                var audio = window.Value;
                var stop = false;
                var chunks = _speechToText.TranscribeAsync(audio, cts.Token).GetAsyncEnumerator(cts.Token);
                try
                {
                    while (true)
                    {
                        bool chunkMore;
                        var chunkCancelled = false;
                        try
                        {
                            chunkMore = await chunks.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            chunkCancelled = true;
                            chunkMore = false;
                        }

                        if (chunkCancelled)
                        {
                            stop = true;
                            break;
                        }

                        if (!chunkMore)
                            break;

                        var chunk = chunks.Current;
                        if (chunk.IsError)
                        {
                            yield return Result<string, string>.AsError(chunk.Error);
                            yield break;
                        }

                        if (!chunk.Value.IsFinal)
                            continue;
                        var piece = chunk.Value.Text.Trim();
                        if (piece.Length == 0)
                            continue;
                        if (text.Length > 0)
                            text.Append(' ');
                        text.Append(piece);
                        yield return Result<string, string>.AsValue(text.ToString());
                    }
                }
                finally
                {
                    await chunks.DisposeAsync().ConfigureAwait(false);
                    await audio.DisposeAsync().ConfigureAwait(false);
                }

                if (stop)
                    yield break;
            }
        }
        finally
        {
            if (windows is not null)
                await windows.DisposeAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _recording = false;
                if (ReferenceEquals(_recordCts, cts))
                    _recordCts = null;
            }

            cts.Dispose();
        }
    }

    private async IAsyncEnumerable<Result<string, string>> RecordLiveAsync(
        IDysonLiveSpeechToText live,
        string? deviceId,
        CancellationTokenSource cts)
    {
        var committed = new StringBuilder();
        var chunks = live.TranscribeLiveAsync(_capture.CapturePcmAsync(deviceId, cts.Token), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        try
        {
            while (true)
            {
                bool more;
                var cancelled = false;
                try
                {
                    more = await chunks.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    more = false;
                }

                if (cancelled || !more)
                    yield break;

                var chunk = chunks.Current;
                if (chunk.IsError)
                {
                    yield return Result<string, string>.AsError(chunk.Error);
                    yield break;
                }

                if (!chunk.Value.IsFinal)
                {
                    var shown = Shown(committed, chunk.Value.Text.Trim());
                    if (shown.Length > 0)
                        yield return Result<string, string>.AsValue(shown);
                    continue;
                }

                var piece = chunk.Value.Text.Trim();
                if (piece.Length == 0)
                    continue;
                if (committed.Length > 0)
                    committed.Append(' ');
                committed.Append(piece);
                yield return Result<string, string>.AsValue(committed.ToString());
            }
        }
        finally
        {
            await chunks.DisposeAsync().ConfigureAwait(false);
        }

        static string Shown(StringBuilder committed, string partial)
        {
            if (committed.Length == 0)
                return partial;
            if (partial.Length == 0)
                return committed.ToString();
            return string.Concat(committed.ToString(), " ", partial);
        }
    }

    /// <summary>
    /// Records about 10 seconds on <paramref name="deviceId"/> and enrolls it through
    /// <see cref="DysonActiveTextToSpeech.PrepareVoiceAsync"/>. A non-blank <paramref name="transcript"/>
    /// is the clip text and speech-to-text is not called. When it is blank, Python Qwen still skips
    /// speech-to-text; every other model transcribes once, refuses an empty transcript, and does not open
    /// the microphone when no speech-to-text model is active. Empty capture writes nothing.
    /// Shares the recording gate with <see cref="RecordAsync"/>.
    /// </summary>
    public async Task<Result<DysonSpeechVoiceEntity, string>> EnrollRecordedVoiceAsync(
        DysonActiveSpeechToText speechToText,
        DysonActiveTextToSpeech textToSpeech,
        string? deviceId,
        CancellationToken cancellationToken,
        string? transcript = null)
    {
        ArgumentNullException.ThrowIfNull(speechToText);
        ArgumentNullException.ThrowIfNull(textToSpeech);
        await Task.Run(() => 0, CancellationToken.None).ConfigureAwait(false);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = false;
        lock (_gate)
        {
            if (!_recording)
            {
                _recording = true;
                _recordCts = cts;
                started = true;
            }
        }

        if (!started)
        {
            cts.Dispose();
            return Result<DysonSpeechVoiceEntity, string>.AsError("Already recording.");
        }

        try
        {
            return await EnrollCapturedAsync(speechToText, textToSpeech, deviceId, cts, transcript)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _recording = false;
                if (ReferenceEquals(_recordCts, cts))
                    _recordCts = null;
            }

            cts.Dispose();
        }
    }

    private async Task<Result<DysonSpeechVoiceEntity, string>> EnrollCapturedAsync(
        DysonActiveSpeechToText speechToText,
        DysonActiveTextToSpeech textToSpeech,
        string? deviceId,
        CancellationTokenSource cts,
        string? transcript)
    {
        // A filled train box is the transcript. Speech-to-text is only the fallback when that box is empty,
        // and Python Qwen still skips it so a missing speech-to-text model does not block the microphone.
        var provided = string.IsNullOrWhiteSpace(transcript) ? null : transcript.Trim();
        var model = await textToSpeech.ResolveActiveAsync(cts.Token).ConfigureAwait(false);
        var pythonQwen = model.IsSuccess && model.Value == DysonQwenSpeech.Id;
        if (provided is null && !pythonQwen)
        {
            var active = await speechToText.ResolveActiveAsync(cts.Token).ConfigureAwait(false);
            if (active.IsError)
                return Result<DysonSpeechVoiceEntity, string>.AsError(active.Error);
        }

        if (cts.IsCancellationRequested)
            return CancelledEnroll();

        var pcm = new byte[RecordBytes];
        var filled = 0;
        try
        {
            await foreach (var block in _capture.CapturePcmAsync(deviceId, cts.Token).ConfigureAwait(false))
            {
                if (cts.IsCancellationRequested)
                    return CancelledEnroll();
                if (block.IsError)
                    return Result<DysonSpeechVoiceEntity, string>.AsError(block.Error);
                var take = Math.Min(block.Value.Length, RecordBytes - filled);
                if (take > 0)
                {
                    block.Value.AsSpan(0, take).CopyTo(pcm.AsSpan(filled));
                    filled += take;
                }

                if (filled < RecordBytes)
                    continue;
                _capture.Stop();
                break;
            }
        }
        catch (OperationCanceledException)
        {
            return CancelledEnroll();
        }

        if (cts.IsCancellationRequested)
            return CancelledEnroll();
        if ((filled & 1) == 1)
            filled--;
        if (filled == 0)
            return Result<DysonSpeechVoiceEntity, string>.AsError("The recording was empty, so the voice was not saved.");

        await using var wav = Wav16k(pcm.AsSpan(0, filled));
        var enrollTranscript = provided;
        if (enrollTranscript is null && !pythonQwen)
        {
            try
            {
                await foreach (var chunk in speechToText.TranscribeAsync(wav, cts.Token).ConfigureAwait(false))
                {
                    if (chunk.IsError)
                        return Result<DysonSpeechVoiceEntity, string>.AsError(chunk.Error);
                    if (chunk.Value.IsFinal)
                        enrollTranscript = chunk.Value.Text.Trim();
                }
            }
            catch (OperationCanceledException)
            {
                return CancelledEnroll();
            }

            if (cts.IsCancellationRequested)
                return CancelledEnroll();
            if (string.IsNullOrWhiteSpace(enrollTranscript))
                return Result<DysonSpeechVoiceEntity, string>.AsError(EmptyTranscript);
        }

        wav.Position = 0;
        return await textToSpeech
            .PrepareVoiceAsync(wav, "recorded.wav", cts.Token, enrollTranscript)
            .ConfigureAwait(false);
    }

    private static Result<DysonSpeechVoiceEntity, string> CancelledEnroll() =>
        Result<DysonSpeechVoiceEntity, string>.AsError("Recording was cancelled.");

    /// <summary>16 kHz mono 16-bit PCM WAV. Not resampled.</summary>
    private static MemoryStream Wav16k(ReadOnlySpan<byte> pcm)
    {
        var stream = new MemoryStream(44 + pcm.Length);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + pcm.Length);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(16_000);
            writer.Write(16_000 * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(pcm.Length);
        }

        stream.Write(pcm);
        stream.Position = 0;
        return stream;
    }

    /// <summary>Stops capture and transcription. Nothing capturing is an error.</summary>
    public VoidResult<string> StopRecording()
    {
        CancellationTokenSource? cts;
        lock (_gate)
            cts = _recordCts;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return _capture.Stop();
    }
}
