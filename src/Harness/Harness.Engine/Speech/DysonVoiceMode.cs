using System.Threading.Channels;

namespace DysonHarness;

/// <summary>Why <see cref="DysonVoiceMode.StopAsync"/> was called. Only user-off and app-exit stop the loops.</summary>
public enum DysonVoiceStopReason
{
    UserOff,
    AppExit,
    PageDispose,
    SessionSwitch,
    WorkDirectorySwitch,
    Settings,
}

/// <summary>
/// Root Meta Agent voice mode: one listen loop over the growing transcript, a 3 s pause to submit,
/// barge-in on a stored wake name while speech is playing, and a queue the SpeakToUser tool fills.
/// Windows only, same shape as <see cref="DysonSpeechTryout"/>. No interface: one implementation.
/// </summary>
public sealed class DysonVoiceMode(
    DysonSpeechTryout tryout,
    DysonActiveSpeechToText speechToText,
    DysonActiveTextToSpeech textToSpeech,
    IDysonSpeechModelRepository models,
    IDysonSubjectSettingsRepository settings,
    DysonWorkDirectoryFocus focus) : IAsyncDisposable
{
    public const string InputPrefix = "[voice input] ";
    public const int PauseMs = 3000;
    public static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(150);
    public const int MaxSpeakChars = 600;

    /// <summary>One queued utterance. <see cref="SpokenLine.WorkDirectoryName"/> is the enqueue snapshot.</summary>
    internal readonly record struct SpokenLine(string Text, Guid WorkDirectoryId, string WorkDirectoryName);

    /// <summary>Idle transcript longer than this restarts the live stream.</summary>
    private const int RestartChars = 400;

    /// <summary>Wake search keeps this many trailing words so a phrase split across two updates still matches.</summary>
    private const int IdleTailWords = 12;

    private readonly DysonSpeechTryout _tryout = tryout ?? throw new ArgumentNullException(nameof(tryout));
    private readonly DysonActiveSpeechToText _speechToText = speechToText ?? throw new ArgumentNullException(nameof(speechToText));
    private readonly DysonActiveTextToSpeech _textToSpeech = textToSpeech ?? throw new ArgumentNullException(nameof(textToSpeech));
    private readonly IDysonSpeechModelRepository _models = models ?? throw new ArgumentNullException(nameof(models));
    private readonly IDysonSubjectSettingsRepository _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly DysonWorkDirectoryFocus _focus = focus ?? throw new ArgumentNullException(nameof(focus));
    private readonly Lock _state = new();
    private readonly Lock _listenerGate = new();

    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _segmentCts;
    private Channel<SpokenLine>? _speak;
    private Task? _loops;
    private string? _speakingText;
    private int _speakEpoch;
    private CancellationTokenSource? _currentSynth;
    private CancellationTokenSource? _prefetchSynth;

    public bool IsOn { get; private set; }

    /// <summary>Work directory on screen. Playback reads <see cref="DysonWorkDirectoryFocus.ActiveId"/> from here.</summary>
    public DysonWorkDirectoryFocus Focus => _focus;
    public bool IsCapturing { get; private set; }
    public bool IsSpeaking { get; private set; }

    public event Action? StateChanged;
    public event Func<string, Task>? MessageReady;
    public event Action<string>? Failed;

    /// <summary>
    /// First failure wins. Each message points at Settings → Voice.
    /// <paramref name="activeTtsVoiceCount"/> is <c>ListVoicesAsync</c> for the active TTS model.
    /// </summary>
    internal static VoidResult<string> Requirements(
        Result<string, string> stt,
        Result<string, string> tts,
        IReadOnlyList<string> wakeNames,
        int activeTtsVoiceCount)
    {
        if (stt.IsError)
            return new VoidResult<string>(stt.Error);
        if (tts.IsError)
            return new VoidResult<string>(tts.Error);

        wakeNames ??= [];
        if (wakeNames.Count != 8 || wakeNames.Any(string.IsNullOrWhiteSpace))
            return new VoidResult<string>("Train the agent name: save 8 name variants in Settings → Voice.");

        if ((tts.Value == DysonQwenSpeech.Id || DysonGgufSpeechModels.IsQwenGguf(tts.Value))
            && activeTtsVoiceCount <= 0)
        {
            return new VoidResult<string>(
                $"{ModelName(tts.Value)} needs a trained voice to speak. Train one in Settings → Voice.");
        }

        if (stt.Value is DysonGgufSpeechModels.PhononGgufId or DysonGgufSpeechModels.PhononGgufQ8Id
            && DysonCrispSpeech.IsTextToSpeech(tts.Value))
        {
            return new VoidResult<string>(
                $"Phonon 2 GGUF and {ModelName(tts.Value)} share the one GGUF runtime, so they cannot stay loaded together. " +
                "In Settings → Voice pick Nemotron 3.5 or Phonon 2 (Python) for speech-to-text, or a Python text-to-speech model.");
        }

        return VoidResult<string>.Success;
    }

    /// <summary>Hops once, then reads the active models, the 8 wake names, and the active TTS voice count.</summary>
    public Task<VoidResult<string>> CheckRequirementsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadRequirementsAsync(cancellationToken), CancellationToken.None);

    /// <summary>Checks requirements, then runs the listen, tick, and speak loops off the caller.</summary>
    public async Task<VoidResult<string>> StartAsync(CancellationToken cancellationToken = default)
    {
        var ready = await CheckRequirementsAsync(cancellationToken).ConfigureAwait(false);
        if (ready.IsError)
            return ready;

        var device = await _settings.GetSettingAsync(DysonAppSettingKeys.SpeechInputDeviceId, cancellationToken)
            .ConfigureAwait(false);
        if (device.IsError)
            return new VoidResult<string>(device.Error);

        Task? prior;
        lock (_state)
        {
            if (IsOn)
                return VoidResult<string>.Success;
            prior = _loops;
        }

        if (prior is not null)
        {
            try
            {
                await prior.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The previous loops already logged their own failure.
            }
        }

        var run = new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<SpokenLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var listener = new Listener();
        lock (_state)
        {
            if (IsOn)
            {
                run.Dispose();
                return VoidResult<string>.Success;
            }

            _runCts = run;
            _speak = channel;
            _speakEpoch = 0;
            _loops = Task.Run(() => RunAsync(listener, device.Value, run.Token), CancellationToken.None);
            IsOn = true;
        }

        RaiseState();
        return VoidResult<string>.Success;
    }

    /// <summary>True only for <see cref="DysonVoiceStopReason.UserOff"/> and <see cref="DysonVoiceStopReason.AppExit"/>.</summary>
    internal static bool Stops(DysonVoiceStopReason reason) =>
        reason is DysonVoiceStopReason.UserOff or DysonVoiceStopReason.AppExit;

    /// <summary>
    /// Cancels the loops, stops the microphone, and fades playback out when <see cref="Stops"/> is true.
    /// Every other reason returns without changing <see cref="IsOn"/>. Safe to call more than once.
    /// </summary>
    public async Task StopAsync(DysonVoiceStopReason reason)
    {
        if (!Stops(reason))
            return;

        Task? loops;
        CancellationTokenSource? run;
        lock (_state)
        {
            IsOn = false;
            IsCapturing = false;
            IsSpeaking = false;
            _speakingText = null;
            run = _runCts;
            loops = _loops;
            _speak?.Writer.TryComplete();
        }

        try
        {
            run?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        CancelSynths();
        CancelSegment();
        // Driver stop can block. Hop so a Blazor circuit is not the caller that waits on it.
        await Task.Run(() =>
        {
            _tryout.StopRecording();
            _tryout.StopPlayback(FadeOut);
        }, CancellationToken.None).ConfigureAwait(false);

        if (loops is not null && loops.Id != Task.CurrentId)
        {
            try
            {
                await loops.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        RaiseState();
    }

    /// <summary>
    /// Queues speech for <paramref name="workDirectoryId"/>. Blank and over-long text are refused.
    /// The 600-character check is on <paramref name="text"/> before any work-directory prefix.
    /// Calls play in order. There is no <c>Speak(string)</c>.
    /// </summary>
    public VoidResult<string> Speak(string text, Guid workDirectoryId, string workDirectoryName)
    {
        if (!IsOn)
            return new VoidResult<string>("Voice mode is off.");

        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
            return new VoidResult<string>("Text is empty.");
        if (trimmed.Length > MaxSpeakChars)
            return new VoidResult<string>("Text is over 600 characters. Split it into several SpeakToUser calls.");

        Channel<SpokenLine>? channel;
        lock (_state)
            channel = _speak;
        if (channel is null || !channel.Writer.TryWrite(new SpokenLine(trimmed, workDirectoryId, workDirectoryName ?? "")))
            return new VoidResult<string>("Voice mode is off.");

        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Text actually spoken. The active work directory is not prefixed. Any other line is
    /// <c>Message from workdir {name}: {text}</c>. <paramref name="liveName"/> wins when non-blank,
    /// else <paramref name="snapshotName"/>, else <c>untitled</c>.
    /// </summary>
    internal static string PlaybackText(
        Guid lineWorkDirectoryId,
        string text,
        string snapshotName,
        Guid? activeId,
        string? liveName)
    {
        if (activeId is { } active && active == lineWorkDirectoryId)
            return text;

        var name = !string.IsNullOrWhiteSpace(liveName)
            ? liveName
            : !string.IsNullOrWhiteSpace(snapshotName)
                ? snapshotName
                : "untitled";
        return $"Message from workdir {name}: {text}";
    }

    public async ValueTask DisposeAsync() => await StopAsync(DysonVoiceStopReason.AppExit).ConfigureAwait(false);

    /// <summary>
    /// The original substring after the last stored wake phrase, with leading punctuation and whitespace removed.
    /// Null when no variant matches. Matching is the 8 stored texts only.
    /// </summary>
    internal static string? AfterWake(string transcript, IReadOnlyList<string> variants)
    {
        if (string.IsNullOrEmpty(transcript))
            return null;

        var end = MatchEnd(transcript, variants);
        if (end < 0)
            return null;

        return transcript[(end + SkipLead(transcript.AsSpan(end)))..];
    }

    /// <summary>
    /// Heard text is echo when at least 60% of its words are in the sentence being spoken.
    /// Echo never cancels playback. ponytail: text heuristic, not acoustic echo cancellation.
    /// Upgrade: Windows voice-capture DSP (AEC) in DysonWindowsAudioCapture.
    /// </summary>
    internal static bool IsEcho(string heard, string speaking)
    {
        var heardWords = NormalizeWords(heard);
        if (heardWords.Count == 0)
            return false;

        var spoken = NormalizeWords(speaking).ToHashSet(StringComparer.Ordinal);
        var hits = 0;
        foreach (var word in heardWords)
        {
            if (spoken.Contains(word))
                hits++;
        }

        return hits * 10 >= heardWords.Count * 6;
    }

    private async Task<VoidResult<string>> LoadRequirementsAsync(CancellationToken cancellationToken)
    {
        var stt = await _speechToText.ResolveActiveAsync(cancellationToken).ConfigureAwait(false);
        var tts = await _textToSpeech.ResolveActiveAsync(cancellationToken).ConfigureAwait(false);
        var names = await _models.GetWakeNamesAsync(cancellationToken).ConfigureAwait(false);
        if (names.IsError)
            return new VoidResult<string>(names.Error);

        var voiceCount = 0;
        if (tts.IsSuccess)
        {
            var voices = await _models.ListVoicesAsync(tts.Value, cancellationToken).ConfigureAwait(false);
            if (voices.IsError)
                return new VoidResult<string>(voices.Error);
            voiceCount = voices.Value.Count;
        }

        return Requirements(stt, tts, names.Value, voiceCount);
    }

    private async Task RunAsync(Listener listener, string? deviceId, CancellationToken runToken)
    {
        var run = _runCts;
        try
        {
            var speak = SpeakLoop(runToken);
            var tick = TickLoop(listener, runToken);
            try
            {
                await TranscriptLoop(listener, deviceId, runToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex.Message);
            }

            _speak?.Writer.TryComplete();
            try
            {
                await Task.WhenAll(speak, tick).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            lock (_state)
            {
                if (ReferenceEquals(_runCts, run))
                {
                    _runCts = null;
                    _loops = null;
                    IsOn = false;
                    IsCapturing = false;
                    IsSpeaking = false;
                    _speakingText = null;
                }
            }

            run?.Dispose();
            RaiseState();
        }
    }

    private async Task TranscriptLoop(Listener listener, string? deviceId, CancellationToken runToken)
    {
        while (!runToken.IsCancellationRequested)
        {
            lock (_listenerGate)
                listener.Reset();

            var segment = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            lock (_state)
                _segmentCts = segment;

            var failed = false;
            try
            {
                await foreach (var item in _tryout.RecordAsync(deviceId, inputInstalled: true, segment.Token)
                                   .ConfigureAwait(false))
                {
                    if (item.IsError)
                    {
                        // Barge-in cancels this segment. A CrispASR cancel or a fell-behind fault on
                        // that path is the fade, not a failure the user should see.
                        if (segment.IsCancellationRequested && IsIntentionalSpeechCancel(item.Error))
                            break;

                        Failed?.Invoke(item.Error);
                        failed = true;
                        break;
                    }

                    string? speaking;
                    lock (_state)
                        speaking = _speakingText;

                    var names = await _models.GetWakeNamesAsync(runToken).ConfigureAwait(false);
                    var variants = names.IsError || names.Value is null ? [] : names.Value;

                    ListenAction action;
                    bool wasCapturing;
                    bool capturing;
                    lock (_listenerGate)
                    {
                        wasCapturing = listener.Capturing;
                        action = listener.OnText(item.Value, Environment.TickCount64, speaking, variants);
                        capturing = listener.Capturing;
                    }

                    SetCapturing(capturing);
                    if (action == ListenAction.BargeIn)
                        BargeIn();
                    else if (action == ListenAction.Restart)
                        break;
                    else if (!wasCapturing && capturing)
                        _tryout.PlayListenChime();
                }
            }
            finally
            {
                lock (_state)
                {
                    if (ReferenceEquals(_segmentCts, segment))
                        _segmentCts = null;
                }

                segment.Dispose();
            }

            if (failed)
            {
                // Do not await StopAsync here: this method is the loop task StopAsync waits on.
                CancelRun();
                _ = Task.Run(() =>
                {
                    _tryout.StopRecording();
                    _tryout.StopPlayback(FadeOut);
                }, CancellationToken.None);
                return;
            }

            if (runToken.IsCancellationRequested)
                return;
        }
    }

    private async Task TickLoop(Listener listener, CancellationToken runToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(runToken).ConfigureAwait(false))
            {
                string? message;
                bool capturing;
                lock (_listenerGate)
                {
                    message = listener.OnTick(Environment.TickCount64);
                    capturing = listener.Capturing;
                }

                SetCapturing(capturing);
                if (message is null)
                    continue;

                // Before the turn speaks. Otherwise this segment keeps the old transcript and the
                // next OnText, with the speak mark reset, hears it again from offset 0 and barges in.
                CancelSegment();
                bool speaking;
                lock (_state)
                    speaking = IsSpeaking;
                // Load only when the cue will play. Speech already playing skips the callback.
                DysonSpeechAudioChunk? clip = null;
                if (!speaking)
                {
                    clip = await DysonAcceptSentence
                        .TryLoadAsync(_settings, _textToSpeech.InstallRoot, runToken)
                        .ConfigureAwait(false);
                }

                // Cue is not awaited. Submit still runs if playback throws or is already speaking.
                await AcceptTurnAsync(
                    message, speaking, () => _tryout.PlayAcceptCue(clip), MessageReady, Failed)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Forwards an accepted utterance. The chime is not awaited. Playback throwing, or speech already
    /// playing, does not skip the submit.
    /// </summary>
    internal static async Task AcceptTurnAsync(
        string message,
        bool speaking,
        Action chime,
        Func<string, Task>? ready,
        Action<string>? failed)
    {
        if (!speaking)
        {
            try
            {
                chime();
            }
            catch (Exception)
            {
            }
        }

        if (ready is null)
            return;

        try
        {
            await ready(message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failed?.Invoke(ex.Message);
        }
    }

    private async Task SpeakLoop(CancellationToken runToken)
    {
        var reader = _speak?.Reader;
        if (reader is null)
            return;

        Pumped? ahead = null;
        try
        {
            while (!runToken.IsCancellationRequested)
            {
                Pumped? current = null;
                if (ahead is not null)
                {
                    var queued = ahead;
                    ahead = null;
                    if (Volatile.Read(ref _speakEpoch) != queued.Epoch || runToken.IsCancellationRequested)
                        EndSynth(queued);
                    else
                    {
                        Promote(queued);
                        current = queued;
                    }
                }

                if (current is null)
                {
                    if (!await reader.WaitToReadAsync(runToken).ConfigureAwait(false))
                        return;
                    if (!reader.TryRead(out var line))
                        continue;
                    current = await BeginLineAsync(line, prefetch: false, runToken).ConfigureAwait(false);
                    if (current is null || Volatile.Read(ref _speakEpoch) != current.Epoch)
                    {
                        EndSynth(current);
                        continue;
                    }
                }

                var epoch = current.Epoch;
                var dropped = false;
                lock (_state)
                {
                    if (epoch != _speakEpoch)
                        dropped = true;
                    else
                    {
                        _speakingText = current.Playback;
                        IsSpeaking = true;
                    }
                }

                if (dropped)
                {
                    EndSynth(current);
                    continue;
                }

                RaiseState();
                var playing = current;
                // None, not the run token: a fade must not cancel the device. The pump token is separate.
                var play = _tryout.PlayPreparedAsync(playing.Channel.Reader.ReadAllAsync(), CancellationToken.None);
                if (reader.TryRead(out var nextLine)
                    && Volatile.Read(ref _speakEpoch) == epoch
                    && !runToken.IsCancellationRequested)
                {
                    ahead = await BeginLineAsync(nextLine, prefetch: true, runToken).ConfigureAwait(false);
                }

                VoidResult<string> played;
                try
                {
                    played = await play.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    EndSynth(playing);
                    EndSynth(ahead);
                    return;
                }
                finally
                {
                    var changed = false;
                    lock (_state)
                    {
                        if (epoch == _speakEpoch)
                            _speakingText = null;
                        if (IsSpeaking)
                        {
                            IsSpeaking = false;
                            changed = true;
                        }
                    }

                    if (changed)
                        RaiseState();
                }

                EndSynth(playing);
                if (Volatile.Read(ref _speakEpoch) != epoch || runToken.IsCancellationRequested)
                {
                    EndSynth(ahead);
                    ahead = null;
                }

                if (played.IsError && !IsIntentionalSpeechCancel(played.Error))
                    Failed?.Invoke(played.Error);
            }
        }
        catch (OperationCanceledException)
        {
            EndSynth(ahead);
        }
    }

    /// <summary>
    /// Fixes <see cref="PlaybackText"/> now and starts synthesis into an unbounded channel.
    /// The channel is not passed through <c>PlayAsync</c>, so the engine gate can drop while the device still plays.
    /// </summary>
    private async Task<Pumped?> BeginLineAsync(SpokenLine line, bool prefetch, CancellationToken runToken)
    {
        var epoch = Volatile.Read(ref _speakEpoch);
        if (runToken.IsCancellationRequested)
            return null;

        var activeId = _focus.ActiveId;
        string? liveName = null;
        try
        {
            liveName = await _focus.TryGetLiveNameAsync(line.WorkDirectoryId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A failed or thrown Get still plays. The enqueue snapshot covers a missing row.
            liveName = null;
        }

        if (runToken.IsCancellationRequested || Volatile.Read(ref _speakEpoch) != epoch)
            return null;

        var playback = PlaybackText(line.WorkDirectoryId, line.Text, line.WorkDirectoryName, activeId, liveName);
        var synth = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        // ponytail: unbounded for one Speak line (600 characters before the work-directory prefix).
        // A bounded channel would push back onto the enumerator and hold the engine gate.
        var channel = Channel.CreateUnbounded<Result<DysonSpeechAudioChunk, string>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        lock (_state)
        {
            if (prefetch)
                _prefetchSynth = synth;
            else
                _currentSynth = synth;
        }

        _ = PumpAsync(playback, synth, channel);
        return new Pumped(playback, epoch, synth, channel);
    }

    private async Task PumpAsync(
        string playback,
        CancellationTokenSource synth,
        Channel<Result<DysonSpeechAudioChunk, string>> channel)
    {
        try
        {
            await foreach (var chunk in _tryout.Synthesize(playback, synth.Token).ConfigureAwait(false))
            {
                if (synth.IsCancellationRequested || !channel.Writer.TryWrite(chunk))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            channel.Writer.TryWrite(Result<DysonSpeechAudioChunk, string>.AsError(ex.Message));
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private void Promote(Pumped pumped)
    {
        lock (_state)
        {
            if (ReferenceEquals(_prefetchSynth, pumped.Synth))
                _prefetchSynth = null;
            _currentSynth = pumped.Synth;
        }
    }

    private void CancelSynths()
    {
        CancellationTokenSource? current;
        CancellationTokenSource? prefetch;
        lock (_state)
        {
            current = _currentSynth;
            prefetch = _prefetchSynth;
            _currentSynth = null;
            _prefetchSynth = null;
        }

        EndSource(current);
        EndSource(prefetch);
    }

    private void EndSynth(Pumped? pumped)
    {
        if (pumped is null)
            return;
        lock (_state)
        {
            if (ReferenceEquals(_currentSynth, pumped.Synth))
                _currentSynth = null;
            if (ReferenceEquals(_prefetchSynth, pumped.Synth))
                _prefetchSynth = null;
        }

        EndSource(pumped.Synth);
    }

    private static void EndSource(CancellationTokenSource? synth)
    {
        if (synth is null)
            return;
        try
        {
            synth.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            synth.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void BargeIn()
    {
        // ponytail: a line the speak loop already dequeued can still start; upgrade is a generation check inside PlayAsync.
        Interlocked.Increment(ref _speakEpoch);
        CancelSynths();
        var reader = _speak?.Reader;
        if (reader is not null)
        {
            while (reader.TryRead(out _))
            {
            }
        }

        _tryout.StopPlayback(FadeOut);
        _tryout.PlayListenChime();
    }

    /// <summary>Fade, CrispASR cancel, or a mic fell-behind fault is not a failure the user should see.</summary>
    private static bool IsIntentionalSpeechCancel(string error) =>
        error.Contains("canceled", StringComparison.OrdinalIgnoreCase)
        || error.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
        || error.Contains("fell behind", StringComparison.OrdinalIgnoreCase);

    private void CancelRun()
    {
        CancellationTokenSource? run;
        lock (_state)
        {
            IsOn = false;
            run = _runCts;
            _speak?.Writer.TryComplete();
        }

        try
        {
            run?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        CancelSynths();
        CancelSegment();
    }

    private void CancelSegment()
    {
        CancellationTokenSource? segment;
        lock (_state)
            segment = _segmentCts;
        try
        {
            segment?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SetCapturing(bool capturing)
    {
        var changed = false;
        lock (_state)
        {
            if (IsCapturing != capturing)
            {
                IsCapturing = capturing;
                changed = true;
            }
        }

        if (changed)
            RaiseState();
    }

    private void RaiseState() => StateChanged?.Invoke();

    private static string ModelName(string runtimeId) => runtimeId switch
    {
        DysonQwenSpeech.Id => DysonQwenSpeech.DisplayName,
        DysonChatterboxSpeech.Id => DysonChatterboxSpeech.DisplayName,
        DysonPocketSpeech.Id => "Pocket TTS",
        DysonGgufSpeechModels.QwenGgufId => DysonGgufSpeechModels.QwenGgufDisplayName,
        DysonGgufSpeechModels.QwenGgufQ4Id => DysonGgufSpeechModels.QwenGgufQ4DisplayName,
        DysonGgufSpeechModels.PocketGgufId => DysonGgufSpeechModels.PocketGgufDisplayName,
        DysonGgufSpeechModels.PocketGgufQ8Id => DysonGgufSpeechModels.PocketGgufQ8DisplayName,
        DysonGgufSpeechModels.ChatterboxGgufId => DysonGgufSpeechModels.ChatterboxGgufDisplayName,
        DysonGgufSpeechModels.ChatterboxGgufQ8Id => DysonGgufSpeechModels.ChatterboxGgufQ8DisplayName,
        _ => runtimeId,
    };

    private static int MatchEnd(string text, IReadOnlyList<string>? variants)
    {
        if (string.IsNullOrEmpty(text) || variants is null || variants.Count == 0)
            return -1;

        var tokens = Tokenize(text);
        if (tokens.Count == 0)
            return -1;

        var best = -1;
        foreach (var variant in variants)
        {
            var expected = NormalizeWords(variant);
            if (expected.Count == 0)
                continue;

            for (var i = 0; i <= tokens.Count - expected.Count; i++)
            {
                var matches = true;
                for (var k = 0; k < expected.Count; k++)
                {
                    if (!string.Equals(tokens[i + k].Norm, expected[k], StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                    continue;

                var end = tokens[i + expected.Count - 1].End;
                if (end >= best)
                    best = end;
            }
        }

        return best;
    }

    private static List<string> NormalizeWords(string? text)
    {
        var tokens = Tokenize(text ?? "");
        var words = new List<string>(tokens.Count);
        foreach (var token in tokens)
            words.Add(token.Norm);
        return words;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            if (i >= text.Length)
                break;

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
                i++;

            var norm = NormalizeToken(text.AsSpan(start, i - start));
            if (norm.Length > 0)
                tokens.Add(new Token(norm, start, i));
        }

        return tokens;
    }

    private static string NormalizeToken(ReadOnlySpan<char> raw)
    {
        var buffer = new char[raw.Length];
        var n = 0;
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c))
                buffer[n++] = char.ToLowerInvariant(c);
        }

        return new string(buffer, 0, n);
    }

    private static int SkipLead(ReadOnlySpan<char> text)
    {
        var i = 0;
        while (i < text.Length && (char.IsWhiteSpace(text[i]) || char.IsPunctuation(text[i])))
            i++;
        return i;
    }

    private static int TwelfthFromLast(string text)
    {
        var tokens = Tokenize(text);
        if (tokens.Count <= IdleTailWords)
            return 0;
        return tokens[tokens.Count - IdleTailWords].Start;
    }

    internal enum ListenAction
    {
        None,
        BargeIn,
        Restart,
    }

    /// <summary>Pure listen rules. Time is milliseconds supplied by the caller. Names are per <c>OnText</c> call.</summary>
    internal sealed class Listener
    {
        private string _text = "";
        private int _messageStart;
        private long _lastChangeMs;
        private int _idleMark;
        private int _speakMark;
        private int _previousLength;
        private bool _speakingArmed;

        internal bool Capturing { get; private set; }

        internal void Reset()
        {
            Capturing = false;
            _text = "";
            _messageStart = 0;
            _lastChangeMs = 0;
            _idleMark = 0;
            _speakMark = 0;
            _previousLength = 0;
            _speakingArmed = false;
        }

        internal ListenAction OnText(string transcript, long nowMs, string? speaking, IReadOnlyList<string> variants)
        {
            variants ??= [];
            transcript ??= "";
            if (Capturing)
            {
                if (!string.Equals(_text, transcript, StringComparison.Ordinal))
                    _lastChangeMs = nowMs;
                _text = transcript;
                _previousLength = transcript.Length;
                if (speaking is null)
                    _speakingArmed = false;
                return ListenAction.None;
            }

            if (speaking is null)
            {
                _speakingArmed = false;
                _speakMark = 0;
                var mark = _idleMark > transcript.Length ? 0 : _idleMark;
                var end = MatchEnd(transcript[mark..], variants);
                if (end >= 0)
                {
                    var abs = mark + end;
                    _messageStart = abs + SkipLead(transcript.AsSpan(abs));
                    _lastChangeMs = nowMs;
                    _text = transcript;
                    _previousLength = transcript.Length;
                    Capturing = true;
                    return ListenAction.None;
                }

                var next = TwelfthFromLast(transcript);
                if (next > _idleMark)
                    _idleMark = next;
                _text = transcript;
                _previousLength = transcript.Length;
                if (transcript.Length > RestartChars)
                {
                    // ponytail: a wake spoken during the restart gap is lost.
                    // Upgrade: overlap the next RecordAsync before this segment is dropped.
                    Reset();
                    return ListenAction.Restart;
                }

                return ListenAction.None;
            }

            if (!_speakingArmed)
            {
                _speakMark = _previousLength > transcript.Length ? 0 : _previousLength;
                _speakingArmed = true;
            }

            var slice = _speakMark > transcript.Length ? transcript.Length : _speakMark;
            var heard = transcript[slice..];
            _text = transcript;
            _previousLength = transcript.Length;
            // Ordinary speech and echo do not stop playback. Only a stored wake name does.
            if (IsEcho(heard, speaking))
                return ListenAction.None;

            var wakeEnd = MatchEnd(heard, variants);
            if (wakeEnd < 0)
                return ListenAction.None;

            var wakeAt = slice + wakeEnd;
            _messageStart = wakeAt + SkipLead(transcript.AsSpan(wakeAt));
            _lastChangeMs = nowMs;
            Capturing = true;
            return ListenAction.BargeIn;
        }

        internal string? OnTick(long nowMs)
        {
            if (!Capturing || nowMs - _lastChangeMs <= PauseMs)
                return null;

            var msg = _messageStart >= 0 && _messageStart <= _text.Length
                ? _text[_messageStart..].Trim()
                : "";
            var consumed = _text.Length;
            Reset();
            // Same segment can still deliver this transcript until it restarts. Do not match it from 0.
            _previousLength = consumed;
            _idleMark = consumed;
            return msg.Length == 0 ? null : InputPrefix + msg;
        }
    }

    private sealed class Pumped(
        string playback,
        int epoch,
        CancellationTokenSource synth,
        Channel<Result<DysonSpeechAudioChunk, string>> channel)
    {
        public string Playback { get; } = playback;
        public int Epoch { get; } = epoch;
        public CancellationTokenSource Synth { get; } = synth;
        public Channel<Result<DysonSpeechAudioChunk, string>> Channel { get; } = channel;
    }

    private readonly struct Token(string norm, int start, int end)
    {
        public string Norm { get; } = norm;
        public int Start { get; } = start;
        public int End { get; } = end;
    }
}
