using System.Runtime.CompilerServices;
using System.Text;

namespace DysonHarness;

/// <summary>
/// Settings → Voice tryout: speak text through the active TTS model, or record the microphone and append
/// Phonon finals. Phonon has no streaming API; each capture window is one <see cref="IDysonSpeechToText.TranscribeAsync"/>.
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
                "Phonon 2 is not installed. Open Settings → Voice and install it.");
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

        var text = new StringBuilder();
        var windows = _capture.CaptureWindowsAsync(deviceId, cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
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
