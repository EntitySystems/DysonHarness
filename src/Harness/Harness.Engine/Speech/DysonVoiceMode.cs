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

/// <summary>How spoken words become a user message. Callsign is today's wake-name mode.</summary>
public enum DysonVoiceInputMode
{
    CallsignActivated,
    Immediate,
}

/// <summary>One accepted utterance. <see cref="Text"/> still carries <see cref="DysonVoiceMode.InputPrefix"/>.</summary>
public readonly record struct DysonVoiceMessage(string Text, DysonVoiceInputMode Mode);

/// <summary>Where a posted voice message goes. The host switches on <see cref="DysonVoiceMode.RouteFor"/>.</summary>
public enum DysonVoiceRoute
{
    Prompt,
    Inject,
    Queue,
    Interrupt,
}

/// <summary>
/// Root Meta Agent voice mode: one listen loop over the growing transcript, a pause to submit,
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
    public const int ImmediatePauseMs = 2000;
    public const int MinPauseMs = 1000;
    public const int MaxPauseMs = 10000;
    public const int PostWakeIgnoreMs = 1000;
    public const int PostSpeechIgnoreMs = 1000;
    public static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(150);
    public const int MaxSpeakChars = 600;

    /// <summary>Error when Immediate is asked for without Nemotron live speech-to-text.</summary>
    public const string ImmediateNeedsNemotron =
        "Immediate mode needs Nemotron 3.5 live speech-to-text. Pick it in Settings → Voice.";

    /// <summary>Error when a pause setter is given a value outside 1..10 seconds.</summary>
    public const string PauseRangeError = "Pause must be between 1 and 10 seconds.";

    /// <summary>Built-in chime (null clip) has no PCM. Mute covers the beep plus the tail.</summary>
    private const int ChimeMuteMs = 400;

    /// <summary>Immediate echo of the last spoken line is ignored for this long after speech ends.</summary>
    private const int EchoWindowMs = 3000;

    /// <summary>One queued utterance. <see cref="SpokenLine.WorkDirectoryName"/> is the enqueue snapshot.</summary>
    internal readonly record struct SpokenLine(string Text, Guid WorkDirectoryId, string WorkDirectoryName, int Epoch);

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
    private Listener? _listener;
    private bool _modeLoaded;
    private int _callsignPauseMs = PauseMs;
    private int _immediatePauseMs = ImmediatePauseMs;
    private string _lastDraft = "";

    public bool IsOn { get; private set; }

    /// <summary>In-memory mode. Loaded from settings by <see cref="StartAsync"/> and the first <see cref="GetModeAsync"/>.</summary>
    public DysonVoiceInputMode Mode { get; private set; } = DysonVoiceInputMode.CallsignActivated;

    /// <summary>Work directory on screen. Playback reads <see cref="DysonWorkDirectoryFocus.ActiveId"/> from here.</summary>
    public DysonWorkDirectoryFocus Focus => _focus;
    public bool IsCapturing { get; private set; }
    public bool IsSpeaking { get; private set; }

    public event Action? StateChanged;
    public event Func<DysonVoiceMessage, Task>? MessageReady;

    /// <summary>Current Immediate utterance, without the prefix. Empty when it posts, drops, or the mode leaves Immediate.</summary>
    public event Action<string>? DraftChanged;

    public event Action<string>? Failed;

    /// <summary>Cached pause for <paramref name="mode"/>, in milliseconds. Used by UI titles.</summary>
    public int PauseFor(DysonVoiceInputMode mode) =>
        mode == DysonVoiceInputMode.Immediate ? _immediatePauseMs : _callsignPauseMs;

    /// <summary>Hops once, then reads the mode and both pauses and caches them.</summary>
    public Task<DysonVoiceInputMode> GetModeAsync(CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            await LoadModeAsync(cancellationToken).ConfigureAwait(false);
            return Mode;
        }, CancellationToken.None);

    /// <summary>
    /// Switches mode live. Immediate while voice is on re-checks requirements; a failure leaves the mode unchanged.
    /// A persist failure keeps the live switch and returns the error.
    /// </summary>
    public Task<VoidResult<string>> SetModeAsync(DysonVoiceInputMode mode, CancellationToken cancellationToken = default) =>
        Task.Run(() => SetModeCoreAsync(mode, cancellationToken), CancellationToken.None);

    /// <summary>Validates, persists the matching pause key, and updates the live listener when that mode is active.</summary>
    public Task<VoidResult<string>> SetPauseAsync(
        DysonVoiceInputMode mode, int pauseMs, CancellationToken cancellationToken = default) =>
        Task.Run(() => SetPauseCoreAsync(mode, pauseMs, cancellationToken), CancellationToken.None);

    /// <summary>
    /// First failure wins. Each message points at Settings → Voice.
    /// <paramref name="activeTtsVoiceCount"/> is <c>ListVoicesAsync</c> for the active TTS model.
    /// </summary>
    internal static VoidResult<string> Requirements(
        Result<string, string> stt,
        Result<string, string> tts,
        IReadOnlyList<string> wakeNames,
        int activeTtsVoiceCount,
        string? whisperRuntime = null,
        DysonVoiceInputMode mode = DysonVoiceInputMode.CallsignActivated)
    {
        if (stt.IsError)
            return new VoidResult<string>(stt.Error);
        if (tts.IsError)
            return new VoidResult<string>(tts.Error);

        if (mode == DysonVoiceInputMode.Immediate)
        {
            if (stt.Value != DysonGgufSpeechModels.NemotronId)
                return new VoidResult<string>(ImmediateNeedsNemotron);
        }
        else
        {
            wakeNames ??= [];
            if (wakeNames.Count != 8 || wakeNames.Any(string.IsNullOrWhiteSpace))
                return new VoidResult<string>("Train the agent name: save 8 name variants in Settings → Voice.");
        }

        if ((tts.Value == DysonQwenSpeech.Id || DysonGgufSpeechModels.IsQwenGguf(tts.Value))
            && activeTtsVoiceCount <= 0)
        {
            return new VoidResult<string>(
                $"{ModelName(tts.Value)} needs a trained voice to speak. Train one in Settings → Voice.");
        }

        if (DysonGgufSpeechModels.IsWhisper(stt.Value))
        {
            var runtime = DysonAppSettingKeys.ParseSpeechWhisperRuntime(whisperRuntime);
            if (runtime.IsError)
                return new VoidResult<string>(runtime.Error);
            if (runtime.Value != DysonAppSettingKeys.SpeechWhisperRuntimeNpu
                && DysonCrispSpeech.IsTextToSpeech(tts.Value))
            {
                return new VoidResult<string>(SharedRuntime(ModelName(stt.Value), ModelName(tts.Value)));
            }
        }

        if (stt.Value is DysonGgufSpeechModels.PhononGgufId or DysonGgufSpeechModels.PhononGgufQ8Id
            && DysonCrispSpeech.IsTextToSpeech(tts.Value))
        {
            return new VoidResult<string>(SharedRuntime(ModelName(stt.Value), ModelName(tts.Value)));
        }

        return VoidResult<string>.Success;
    }

    private static string SharedRuntime(string sttName, string ttsName) =>
        $"{sttName} and {ttsName} share the one GGUF runtime, so they cannot stay loaded together. " +
        "In Settings → Voice pick a speech-to-text model that is not on that runtime (Nemotron, Phonon 2 Python, or Whisper on Intel NPU), or a Python text-to-speech model.";

    /// <summary>Hops once, then reads the active models, the 8 wake names, and the active TTS voice count.</summary>
    public Task<VoidResult<string>> CheckRequirementsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadRequirementsAsync(cancellationToken), CancellationToken.None);

    /// <summary>
    /// Where a posted voice message goes. Immediate interrupts whenever the session is not idle.
    /// Callsign keeps today's inject / queue behaviour.
    /// </summary>
    internal static DysonVoiceRoute RouteFor(
        DysonVoiceInputMode mode, bool inFlight, bool busy, bool summarizing)
    {
        if (mode == DysonVoiceInputMode.Immediate)
            return inFlight || busy || summarizing ? DysonVoiceRoute.Interrupt : DysonVoiceRoute.Prompt;

        if (inFlight)
            return DysonVoiceRoute.Inject;
        if (busy || summarizing)
            return DysonVoiceRoute.Queue;
        return DysonVoiceRoute.Prompt;
    }

    /// <summary>One prefix, then the first text, one space, and the second text with its prefix stripped.</summary>
    internal static string MergeUtterances(string first, string second)
    {
        var a = StripPrefix(first);
        var b = StripPrefix(second);
        if (a.Length == 0)
            return InputPrefix + b;
        if (b.Length == 0)
            return InputPrefix + a;
        return InputPrefix + a + " " + b;
    }

    /// <summary>
    /// Normal turn for a posted voice message. Immediate sets
    /// <see cref="DysonAgentSystemPrompts.MetaAgentImmediateVoiceTurnInstruction"/>; Callsign does not.
    /// </summary>
    public static DysonAgentTurn CreateTurn(DysonVoiceMessage message)
    {
        var turn = DysonAgentSession.CreateNormalTurn(message.Text);
        if (message.Mode == DysonVoiceInputMode.Immediate)
            turn.HiddenInstruction = DysonAgentSystemPrompts.MetaAgentImmediateVoiceTurnInstruction;
        return turn;
    }

    /// <summary>
    /// Flushes speech the interrupted turn had queued. No listen cue.
    /// ponytail: the flush is global, so queued lines from another work directory's Meta Agent are dropped too.
    /// Upgrade: a per-work-directory epoch.
    /// </summary>
    public void DropPendingSpeech()
    {
        Interlocked.Increment(ref _speakEpoch);
        CancelSynths();
        _tryout.StopPlayback(FadeOut);
        // The line currently playing was already read out of the channel, so the epoch
        // bump cannot reach it. Drop it here too.
        var changed = false;
        lock (_state)
        {
            _speakingText = null;
            changed = IsSpeaking;
            IsSpeaking = false;
        }

        if (changed)
            RaiseState();
        // Lines still in the channel carry the old SpokenLine.Epoch; SpeakLoop (the single reader)
        // drops them on read and never synthesizes them. A line already prefetched was cancelled
        // above and is dropped by the post-play epoch check, so it never plays.
    }

    /// <summary>Checks requirements, then runs the listen, tick, and speak loops off the caller.</summary>
    public async Task<VoidResult<string>> StartAsync(CancellationToken cancellationToken = default)
    {
        await LoadModeAsync(cancellationToken).ConfigureAwait(false);
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
        lock (_listenerGate)
            listener.SetMode(Mode, PauseFor(Mode), Environment.TickCount64);
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
            _listener = listener;
            _lastDraft = "";
            _loops = Task.Run(() => RunAsync(listener, device.Value, run.Token), CancellationToken.None);
            IsOn = true;
        }

        RaiseState();
        PlayActivation(run.Token);
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
        var epoch = Volatile.Read(ref _speakEpoch);
        lock (_state)
            channel = _speak;
        if (channel is null
            || !channel.Writer.TryWrite(new SpokenLine(trimmed, workDirectoryId, workDirectoryName ?? "", epoch)))
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

    private async Task LoadModeAsync(CancellationToken cancellationToken)
    {
        var modeRow = await _settings.GetSettingAsync(DysonAppSettingKeys.SpeechVoiceMode, cancellationToken)
            .ConfigureAwait(false);
        var callsignRow = await _settings.GetSettingAsync(DysonAppSettingKeys.SpeechCallsignPauseMs, cancellationToken)
            .ConfigureAwait(false);
        var immediateRow = await _settings.GetSettingAsync(DysonAppSettingKeys.SpeechImmediatePauseMs, cancellationToken)
            .ConfigureAwait(false);

        var modeText = modeRow.IsSuccess
            ? DysonAppSettingKeys.ParseSpeechVoiceMode(modeRow.Value).Value
            : DysonAppSettingKeys.SpeechVoiceModeCallsign;
        var callsign = callsignRow.IsSuccess
            ? DysonAppSettingKeys.ParseSpeechPauseMs(callsignRow.Value, PauseMs).Value
            : PauseMs;
        var immediate = immediateRow.IsSuccess
            ? DysonAppSettingKeys.ParseSpeechPauseMs(immediateRow.Value, ImmediatePauseMs).Value
            : ImmediatePauseMs;

        var mode = modeText == DysonAppSettingKeys.SpeechVoiceModeImmediate
            ? DysonVoiceInputMode.Immediate
            : DysonVoiceInputMode.CallsignActivated;
        lock (_state)
        {
            Mode = mode;
            _callsignPauseMs = callsign;
            _immediatePauseMs = immediate;
            _modeLoaded = true;
        }
    }

    private async Task<VoidResult<string>> SetModeCoreAsync(DysonVoiceInputMode mode, CancellationToken cancellationToken)
    {
        if (!_modeLoaded)
            await LoadModeAsync(cancellationToken).ConfigureAwait(false);

        if (IsOn && mode == DysonVoiceInputMode.Immediate && Mode != DysonVoiceInputMode.Immediate)
        {
            var ready = await LoadRequirementsForAsync(mode, cancellationToken).ConfigureAwait(false);
            if (ready.IsError)
                return ready;
        }

        var now = Environment.TickCount64;
        var pause = PauseFor(mode);
        var leftImmediate = false;
        lock (_state)
            leftImmediate = Mode == DysonVoiceInputMode.Immediate && mode != DysonVoiceInputMode.Immediate;

        lock (_listenerGate)
            _listener?.SetMode(mode, pause, now);

        lock (_state)
            Mode = mode;

        if (leftImmediate)
            RaiseDraft("");

        var stored = await _settings.SetSettingAsync(
            DysonAppSettingKeys.SpeechVoiceMode,
            mode == DysonVoiceInputMode.Immediate
                ? DysonAppSettingKeys.SpeechVoiceModeImmediate
                : DysonAppSettingKeys.SpeechVoiceModeCallsign,
            cancellationToken).ConfigureAwait(false);
        RaiseState();
        return stored.IsError ? stored : VoidResult<string>.Success;
    }

    private async Task<VoidResult<string>> SetPauseCoreAsync(
        DysonVoiceInputMode mode, int pauseMs, CancellationToken cancellationToken)
    {
        if (pauseMs < MinPauseMs || pauseMs > MaxPauseMs)
            return new VoidResult<string>(PauseRangeError);

        if (!_modeLoaded)
            await LoadModeAsync(cancellationToken).ConfigureAwait(false);

        lock (_state)
        {
            if (mode == DysonVoiceInputMode.Immediate)
                _immediatePauseMs = pauseMs;
            else
                _callsignPauseMs = pauseMs;
        }

        if (Mode == mode)
        {
            lock (_listenerGate)
                _listener?.SetMode(mode, pauseMs, Environment.TickCount64);
        }

        var key = mode == DysonVoiceInputMode.Immediate
            ? DysonAppSettingKeys.SpeechImmediatePauseMs
            : DysonAppSettingKeys.SpeechCallsignPauseMs;
        return await _settings.SetSettingAsync(key, pauseMs.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<VoidResult<string>> LoadRequirementsAsync(CancellationToken cancellationToken)
    {
        if (!_modeLoaded)
            await LoadModeAsync(cancellationToken).ConfigureAwait(false);
        return await LoadRequirementsForAsync(Mode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VoidResult<string>> LoadRequirementsForAsync(
        DysonVoiceInputMode mode, CancellationToken cancellationToken)
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

        string? whisperRuntime = null;
        if (stt.IsSuccess && DysonGgufSpeechModels.IsWhisper(stt.Value))
        {
            var whisper = await _settings.GetSettingAsync(DysonAppSettingKeys.SpeechWhisperRuntime, cancellationToken)
                .ConfigureAwait(false);
            if (whisper.IsError)
                return new VoidResult<string>(whisper.Error);
            whisperRuntime = whisper.Value;
        }

        return Requirements(stt, tts, names.Value, voiceCount, whisperRuntime, mode);
    }

    private async Task RunAsync(Listener listener, string? deviceId, CancellationToken runToken)
    {
        var run = _runCts;
        try
        {
            var speak = SpeakLoop(listener, runToken);
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
                    _listener = null;
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
                    DysonVoiceInputMode mode;
                    string draft;
                    lock (_listenerGate)
                    {
                        wasCapturing = listener.Capturing;
                        action = listener.OnText(item.Value, Environment.TickCount64, speaking, variants);
                        capturing = listener.Capturing;
                        mode = listener.Mode;
                        draft = listener.Draft;
                    }

                    SetCapturing(capturing);
                    if (mode == DysonVoiceInputMode.Immediate)
                        RaiseDraft(draft);
                    if (action == ListenAction.BargeIn)
                        BargeIn();
                    else if (action == ListenAction.Restart)
                        break;
                    else if (!wasCapturing && capturing && mode == DysonVoiceInputMode.CallsignActivated)
                        PlayListen();
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
                DysonVoiceInputMode mode;
                lock (_listenerGate)
                {
                    message = listener.OnTick(Environment.TickCount64);
                    capturing = listener.Capturing;
                    mode = listener.Mode;
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

                if (mode == DysonVoiceInputMode.Immediate)
                    RaiseDraft("");
                MuteFor(clip);

                // Cue is not awaited. Submit still runs if playback throws or is already speaking.
                await AcceptTurnAsync(
                    new DysonVoiceMessage(message, mode),
                    speaking,
                    () => _tryout.PlayAcceptCue(clip),
                    MessageReady,
                    Failed).ConfigureAwait(false);
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
        DysonVoiceMessage message,
        bool speaking,
        Action chime,
        Func<DysonVoiceMessage, Task>? ready,
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

    private async Task SpeakLoop(Listener listener, CancellationToken runToken)
    {
        var reader = _speak?.Reader;
        if (reader is null)
            return;

        Pumped? ahead = null;
        SpokenLine? pending = null;
        try
        {
            while (!runToken.IsCancellationRequested)
            {
                // ponytail: 50 ms poll. Ceiling is one extra beat after the pause before the first resumed line.
                // Upgrade: pulse from SetCapturing when it goes false.
                while (CaptureHold())
                    await Task.Delay(50, runToken).ConfigureAwait(false);

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
                    SpokenLine line;
                    if (pending is not null)
                    {
                        line = pending.Value;
                        pending = null;
                    }
                    else
                    {
                        if (!await reader.WaitToReadAsync(runToken).ConfigureAwait(false))
                            return;
                        // Speak wrote during the hold. Leave the line in the channel.
                        if (CaptureHold())
                            continue;
                        if (!reader.TryRead(out line))
                            continue;
                    }

                    if (line.Epoch != Volatile.Read(ref _speakEpoch))
                        continue;
                    if (CaptureHold())
                    {
                        pending = line;
                        continue;
                    }

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
                if (!CaptureHold()
                    && reader.TryPeek(out var nextLine)
                    && nextLine.Epoch == epoch
                    && Volatile.Read(ref _speakEpoch) == epoch
                    && !runToken.IsCancellationRequested)
                {
                    reader.TryRead(out _);
                    var next = await BeginLineAsync(nextLine, prefetch: true, runToken).ConfigureAwait(false);
                    if (next is not null && next.Epoch == epoch && Volatile.Read(ref _speakEpoch) == epoch)
                        ahead = next;
                    else
                        EndSynth(next);
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
                    // The prefetched line was read out of the channel, so the read-time epoch check
                    // cannot reach it. Lines still queued carry the old epoch and are dropped on read;
                    // do not drain here, or a line spoken right after the flush is lost too.
                    EndSynth(ahead);
                    ahead = null;
                    pending = null;
                }
                else
                {
                    lock (_listenerGate)
                        listener.NoteSpoken(playing.Playback, Environment.TickCount64 + PostSpeechIgnoreMs);
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
        if (line.Epoch != epoch || runToken.IsCancellationRequested)
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

        if (Volatile.Read(ref _speakEpoch) != epoch || runToken.IsCancellationRequested)
        {
            EndSource(synth);
            return null;
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
        _tryout.StopPlayback(FadeOut);
        PlayListen();
    }

    /// <summary>
    /// Not awaited and not enqueued on <see cref="Speak"/>. A missing cache plays the listen chime.
    /// </summary>
    private void PlayListen() => _ = PlayListenAsync();

    private async Task PlayListenAsync()
    {
        var clip = await DysonListenSentence
            .TryLoadAsync(_settings, _textToSpeech.InstallRoot, CancellationToken.None)
            .ConfigureAwait(false);
        MuteFor(clip);
        _tryout.PlayListenCue(clip);
    }

    /// <summary>
    /// Not awaited by <see cref="StartAsync"/>. Resolve the speaking voice, ensure the cached activation sentence
    /// (one synth on a miss), then play it as a listen cue with a non-null clip. Any failure is silent and leaves
    /// voice mode on. <paramref name="run"/> canceled (turned off, or off then on again) skips playback.
    /// </summary>
    private void PlayActivation(CancellationToken run) => _ = PlayActivationAsync(run);

    private async Task PlayActivationAsync(CancellationToken run)
    {
        try
        {
            await Task.Run(() => 0, CancellationToken.None).ConfigureAwait(false);
            var voice = await _textToSpeech.ResolveSpeakVoiceAsync(CancellationToken.None).ConfigureAwait(false);
            if (voice.IsError)
                return;
            var clip = await DysonActivationSentence
                .EnsureAsync(_textToSpeech, _textToSpeech.InstallRoot, voice.Value.RuntimeId, voice.Value.VoiceId, CancellationToken.None)
                .ConfigureAwait(false);
            if (clip is null || !IsOn || run.IsCancellationRequested)
                return;
            MuteFor(clip);
            _tryout.PlayListenCue(clip);
        }
        catch (Exception)
        {
            // Fail soft: the cue is optional and voice mode stays on.
        }
    }

    /// <summary>Mute for the clip length (s16le mono) plus <see cref="PostSpeechIgnoreMs"/>. A null clip is the built-in chime (~400 ms).</summary>
    private void MuteFor(DysonSpeechAudioChunk? clip)
    {
        var ms = clip is null || clip.SampleRate <= 0
            ? ChimeMuteMs
            : clip.Pcm.Length / 2 * 1000 / clip.SampleRate;
        lock (_listenerGate)
            _listener?.MuteUntil(Environment.TickCount64 + ms + PostSpeechIgnoreMs);
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

    private bool CaptureHold()
    {
        lock (_state)
            return IsCapturing;
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

    private void RaiseDraft(string draft)
    {
        if (string.Equals(_lastDraft, draft, StringComparison.Ordinal))
            return;
        _lastDraft = draft;
        DraftChanged?.Invoke(draft);
    }

    private static string StripPrefix(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        return trimmed.StartsWith(InputPrefix, StringComparison.Ordinal)
            ? trimmed[InputPrefix.Length..].Trim()
            : trimmed;
    }

    private static string ModelName(string runtimeId) => runtimeId switch
    {
        DysonQwenSpeech.Id => DysonQwenSpeech.DisplayName,
        DysonChatterboxSpeech.Id => DysonChatterboxSpeech.DisplayName,
        DysonPocketSpeech.Id => "Pocket TTS",
        DysonGgufSpeechModels.QwenGgufId => DysonGgufSpeechModels.QwenGgufDisplayName,
        DysonGgufSpeechModels.QwenGgufQ4Id => DysonGgufSpeechModels.QwenGgufQ4DisplayName,
        DysonGgufSpeechModels.QwenGgufF16Id => DysonGgufSpeechModels.QwenGgufF16DisplayName,
        DysonGgufSpeechModels.PocketGgufId => DysonGgufSpeechModels.PocketGgufDisplayName,
        DysonGgufSpeechModels.PocketGgufQ8Id => DysonGgufSpeechModels.PocketGgufQ8DisplayName,
        DysonGgufSpeechModels.ChatterboxGgufId => DysonGgufSpeechModels.ChatterboxGgufDisplayName,
        DysonGgufSpeechModels.ChatterboxGgufQ8Id => DysonGgufSpeechModels.ChatterboxGgufQ8DisplayName,
        DysonGgufSpeechModels.WhisperTinyId => DysonGgufSpeechModels.WhisperTinyDisplayName,
        DysonGgufSpeechModels.WhisperBaseId => DysonGgufSpeechModels.WhisperBaseDisplayName,
        DysonGgufSpeechModels.PhononGgufId => DysonGgufSpeechModels.PhononGgufDisplayName,
        DysonGgufSpeechModels.PhononGgufQ8Id => DysonGgufSpeechModels.PhononGgufQ8DisplayName,
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
        private long _ignoreUntilMs;
        private int _idleMark;
        private int _speakMark;
        private int _previousLength;
        private bool _speakingArmed;
        private int _pauseMs = PauseMs;
        private long _muteUntilMs;
        private string _lastSpoken = "";
        private long _echoUntilMs;

        internal DysonVoiceInputMode Mode { get; private set; } = DysonVoiceInputMode.CallsignActivated;

        internal bool Capturing { get; private set; }

        /// <summary>Current utterance without the prefix. Empty when not capturing.</summary>
        internal string Draft => Capturing && _messageStart >= 0 && _messageStart <= _text.Length
            ? _text[_messageStart..].Trim()
            : "";

        /// <summary>Does not clear <see cref="Mode"/>, the pause, or the mute. Those survive a segment restart.</summary>
        internal void Reset()
        {
            Capturing = false;
            _text = "";
            _messageStart = 0;
            _lastChangeMs = 0;
            _ignoreUntilMs = 0;
            _idleMark = 0;
            _speakMark = 0;
            _previousLength = 0;
            _speakingArmed = false;
        }

        /// <summary>
        /// Live mode switch. A capture in progress keeps its text; the new pause applies from the last change.
        /// Idle chatter heard before the switch never starts or joins an utterance.
        /// </summary>
        internal void SetMode(DysonVoiceInputMode mode, int pauseMs, long nowMs)
        {
            _ = nowMs;
            var was = Mode;
            Mode = mode;
            _pauseMs = pauseMs;
            if (Capturing)
            {
                if (was == DysonVoiceInputMode.CallsignActivated && mode == DysonVoiceInputMode.Immediate)
                    _ignoreUntilMs = 0;
                return;
            }

            _idleMark = _text.Length;
        }

        /// <summary>Idle text before <paramref name="untilMs"/> never starts a capture. Survives <see cref="Reset"/>.</summary>
        internal void MuteUntil(long untilMs)
        {
            if (untilMs > _muteUntilMs)
                _muteUntilMs = untilMs;
        }

        /// <summary>Records the line just spoken and mutes until <paramref name="muteUntilMs"/>.</summary>
        internal void NoteSpoken(string spoken, long muteUntilMs)
        {
            _lastSpoken = spoken ?? "";
            _echoUntilMs = muteUntilMs - PostSpeechIgnoreMs + EchoWindowMs;
            MuteUntil(muteUntilMs);
        }

        internal ListenAction OnText(string transcript, long nowMs, string? speaking, IReadOnlyList<string> variants)
        {
            variants ??= [];
            transcript ??= "";
            if (Capturing)
            {
                // ponytail: an update is entirely inside the window or entirely eligible, judged only by nowMs.
                // A syllable at t=0.9 s transcribed at t=1.0 s is kept. Upgrade: word timestamps on the STT partial.
                if (_ignoreUntilMs != 0 && nowMs < _ignoreUntilMs)
                {
                    _text = transcript;
                    _messageStart = transcript.Length;
                    _previousLength = transcript.Length;
                    if (speaking is null)
                        _speakingArmed = false;
                    return ListenAction.None;
                }

                if (_ignoreUntilMs != 0)
                    _ignoreUntilMs = 0;
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
                if (_muteUntilMs != 0 && nowMs < _muteUntilMs)
                {
                    _text = transcript;
                    _previousLength = transcript.Length;
                    _idleMark = transcript.Length;
                    return ListenAction.None;
                }

                if (Mode == DysonVoiceInputMode.Immediate)
                {
                    // A replacement transcript (STT rewrote the line) is not a suffix of the
                    // last one. Judging only the tail would slice mid-word and miss the echo.
                    var continues = _text.Length > 0 && transcript.StartsWith(_text, StringComparison.Ordinal);
                    var idleFrom = continues && _idleMark <= transcript.Length ? _idleMark : 0;
                    var start = FirstWordStart(transcript, idleFrom);
                    if (start >= 0)
                    {
                        // A growing transcript still holds the sentence just spoken. Judge the
                        // words that arrived since the last update, not that whole line.
                        var sliceFrom = continues
                            ? Math.Max(Math.Max(start, idleFrom), _previousLength)
                            : start;
                        var heard = sliceFrom < transcript.Length ? transcript[sliceFrom..] : "";
                        // ponytail: text heuristic, not acoustic echo cancellation.
                        // Upgrade: Windows voice-capture DSP (AEC) in DysonWindowsAudioCapture.
                        if (_lastSpoken.Length > 0 && nowMs < _echoUntilMs && IsEcho(heard, _lastSpoken))
                        {
                            _text = transcript;
                            _previousLength = transcript.Length;
                            _idleMark = transcript.Length;
                            return ListenAction.None;
                        }

                        BeginCapture(transcript, nowMs, start, ignore: false);
                        return ListenAction.None;
                    }

                    _text = transcript;
                    _previousLength = transcript.Length;
                    if (transcript.Length > RestartChars)
                    {
                        // ponytail: a word spoken during the restart gap is lost.
                        // Upgrade: overlap the next RecordAsync before this segment is dropped.
                        var mode = Mode;
                        var pause = _pauseMs;
                        Reset();
                        Mode = mode;
                        _pauseMs = pause;
                        return ListenAction.Restart;
                    }

                    return ListenAction.None;
                }

                var mark = _idleMark > transcript.Length ? 0 : _idleMark;
                var end = MatchEnd(transcript[mark..], variants);
                if (end >= 0)
                {
                    BeginCapture(transcript, nowMs, transcript.Length, ignore: true);
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
                    var mode = Mode;
                    var pause = _pauseMs;
                    Reset();
                    Mode = mode;
                    _pauseMs = pause;
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
            var bargeHeard = transcript[slice..];
            _text = transcript;
            _previousLength = transcript.Length;
            // Ordinary speech and echo do not stop playback. Only a stored wake name does.
            if (IsEcho(bargeHeard, speaking))
                return ListenAction.None;

            var wakeEnd = MatchEnd(bargeHeard, variants);
            if (wakeEnd < 0)
                return ListenAction.None;

            // Immediate has no post-wake window. Callsign keeps its 1 s ignore.
            BeginCapture(transcript, nowMs, transcript.Length, ignore: Mode != DysonVoiceInputMode.Immediate);
            return ListenAction.BargeIn;
        }

        private void BeginCapture(string transcript, long nowMs, int messageStart, bool ignore)
        {
            _ignoreUntilMs = ignore ? nowMs + PostWakeIgnoreMs : 0;
            _messageStart = messageStart;
            _lastChangeMs = nowMs;
            _text = transcript;
            _previousLength = transcript.Length;
            Capturing = true;
        }

        internal string? OnTick(long nowMs)
        {
            if (!Capturing)
                return null;
            if (_ignoreUntilMs != 0 && nowMs < _ignoreUntilMs)
                return null;
            if (_ignoreUntilMs != 0)
            {
                _messageStart = _text.Length;
                _ignoreUntilMs = 0;
            }

            if (nowMs - _lastChangeMs <= _pauseMs)
                return null;

            var msg = _messageStart >= 0 && _messageStart <= _text.Length
                ? _text[_messageStart..].Trim()
                : "";
            var consumed = _text.Length;
            var mode = Mode;
            var pause = _pauseMs;
            Reset();
            Mode = mode;
            _pauseMs = pause;
            // Same segment can still deliver this transcript until it restarts. Do not match it from 0.
            _previousLength = consumed;
            _idleMark = consumed;
            return msg.Length == 0 ? null : InputPrefix + msg;
        }

        /// <summary>
        /// First token after <paramref name="mark"/> that still has a letter or digit once
        /// <c>&lt;...&gt;</c> and <c>[...]</c> tags are stripped. -1 when there is none.
        /// </summary>
        private static int FirstWordStart(string transcript, int mark)
        {
            if (mark < 0)
                mark = 0;
            if (mark > transcript.Length)
                return -1;

            var slice = StripTags(transcript[mark..]);
            var i = 0;
            while (i < slice.Length)
            {
                while (i < slice.Length && char.IsWhiteSpace(slice[i]))
                    i++;
                if (i >= slice.Length)
                    break;

                var start = i;
                while (i < slice.Length && !char.IsWhiteSpace(slice[i]))
                    i++;

                for (var k = start; k < i; k++)
                {
                    if (char.IsLetterOrDigit(slice[k]))
                        return mark + start;
                }
            }

            return -1;
        }

        /// <summary>Drops <c>&lt;...&gt;</c> and <c>[...]</c> runs (Nemotron noise tags such as <c>&lt;unk&gt;</c>).</summary>
        private static string StripTags(string text)
        {
            if (text.IndexOf('<') < 0 && text.IndexOf('[') < 0)
                return text;

            var buffer = new char[text.Length];
            var n = 0;
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c is '<' or '[')
                {
                    var close = c == '<' ? '>' : ']';
                    var end = text.IndexOf(close, i + 1);
                    if (end >= 0)
                    {
                        if (n > 0 && !char.IsWhiteSpace(buffer[n - 1]))
                            buffer[n++] = ' ';
                        i = end + 1;
                        continue;
                    }
                }

                buffer[n++] = c;
                i++;
            }

            return new string(buffer, 0, n);
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
