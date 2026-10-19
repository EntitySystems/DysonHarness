using System.Threading.Channels;

namespace DysonHarness;

/// <summary>
/// Root Meta Agent voice mode: one listen loop over the growing transcript, a 3 s pause to submit,
/// barge-in while speech is playing, and a queue the SpeakToUser tool fills.
/// Windows only, same shape as <see cref="DysonSpeechTryout"/>. No interface: one implementation.
/// </summary>
public sealed class DysonVoiceMode(
    DysonSpeechTryout tryout,
    DysonActiveSpeechToText speechToText,
    DysonActiveTextToSpeech textToSpeech,
    IDysonSpeechModelRepository models,
    IDysonSubjectSettingsRepository settings) : IAsyncDisposable
{
    public const string InputPrefix = "[voice input] ";
    public const int PauseMs = 3000;
    public static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(150);
    public const int MaxSpeakChars = 600;

    /// <summary>Idle transcript longer than this restarts the live stream.</summary>
    private const int RestartChars = 400;

    /// <summary>Wake search keeps this many trailing words so a phrase split across two updates still matches.</summary>
    private const int IdleTailWords = 12;

    private readonly DysonSpeechTryout _tryout = tryout ?? throw new ArgumentNullException(nameof(tryout));
    private readonly DysonActiveSpeechToText _speechToText = speechToText ?? throw new ArgumentNullException(nameof(speechToText));
    private readonly DysonActiveTextToSpeech _textToSpeech = textToSpeech ?? throw new ArgumentNullException(nameof(textToSpeech));
    private readonly IDysonSpeechModelRepository _models = models ?? throw new ArgumentNullException(nameof(models));
    private readonly IDysonSubjectSettingsRepository _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly Lock _state = new();
    private readonly Lock _listenerGate = new();

    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _segmentCts;
    private Channel<string>? _speak;
    private Task? _loops;
    private string? _speakingText;
    private int _speakEpoch;

    public bool IsOn { get; private set; }
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

        var names = await _models.GetWakeNamesAsync(cancellationToken).ConfigureAwait(false);
        if (names.IsError)
            return new VoidResult<string>(names.Error);

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
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var listener = new Listener(names.Value);
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

    /// <summary>Cancels the loops, stops the microphone, and fades playback out. Safe to call more than once.</summary>
    public async Task StopAsync()
    {
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

    /// <summary>Queues speech. Blank and over-long text are refused. Calls play in order.</summary>
    public VoidResult<string> Speak(string text)
    {
        if (!IsOn)
            return new VoidResult<string>("Voice mode is off.");

        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
            return new VoidResult<string>("Text is empty.");
        if (trimmed.Length > MaxSpeakChars)
            return new VoidResult<string>("Text is over 600 characters. Split it into several SpeakToUser calls.");

        Channel<string>? channel;
        lock (_state)
            channel = _speak;
        if (channel is null || !channel.Writer.TryWrite(trimmed))
            return new VoidResult<string>("Voice mode is off.");

        return VoidResult<string>.Success;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

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
    /// ponytail: text heuristic, not acoustic echo cancellation. Loud speakers can still self-interrupt; a headset avoids it.
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
                        Failed?.Invoke(item.Error);
                        failed = true;
                        break;
                    }

                    string? speaking;
                    lock (_state)
                        speaking = _speakingText;

                    ListenAction action;
                    bool capturing;
                    lock (_listenerGate)
                    {
                        action = listener.OnText(item.Value, Environment.TickCount64, speaking);
                        capturing = listener.Capturing;
                    }

                    SetCapturing(capturing);
                    if (action == ListenAction.BargeIn)
                        BargeIn();
                    else if (action == ListenAction.Restart)
                        break;
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

                var ready = MessageReady;
                if (ready is not null)
                {
                    try
                    {
                        await ready(message).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Failed?.Invoke(ex.Message);
                    }
                }

                CancelSegment();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SpeakLoop(CancellationToken runToken)
    {
        var reader = _speak?.Reader;
        if (reader is null)
            return;

        try
        {
            while (await reader.WaitToReadAsync(runToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var text))
                {
                    if (runToken.IsCancellationRequested)
                        return;

                    var epoch = Volatile.Read(ref _speakEpoch);
                    lock (_state)
                    {
                        if (epoch != _speakEpoch)
                            continue;
                        _speakingText = text;
                        IsSpeaking = true;
                    }

                    RaiseState();
                    VoidResult<string> played;
                    try
                    {
                        // None, not the run token: a fade must not cancel synthesis. Playback ends the call.
                        played = await _tryout.PlayAsync(text, outputInstalled: true, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
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

                    if (played.IsError)
                        Failed?.Invoke(played.Error);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void BargeIn()
    {
        // ponytail: a line the speak loop already dequeued can still start; upgrade is a generation check inside PlayAsync.
        Interlocked.Increment(ref _speakEpoch);
        var reader = _speak?.Reader;
        if (reader is not null)
        {
            while (reader.TryRead(out _))
            {
            }
        }

        _tryout.StopPlayback(FadeOut);
    }

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

    /// <summary>Pure listen rules. Time is milliseconds supplied by the caller.</summary>
    internal sealed class Listener(IReadOnlyList<string> variants)
    {
        private readonly IReadOnlyList<string> _variants = variants ?? [];
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

        internal ListenAction OnText(string transcript, long nowMs, string? speaking)
        {
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
                var end = MatchEnd(transcript[mark..], _variants);
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
            if (Tokenize(heard).Count >= 2 && !IsEcho(heard, speaking))
            {
                _messageStart = slice;
                _lastChangeMs = nowMs;
                Capturing = true;
                return ListenAction.BargeIn;
            }

            return ListenAction.None;
        }

        internal string? OnTick(long nowMs)
        {
            if (!Capturing || nowMs - _lastChangeMs <= PauseMs)
                return null;

            var msg = _messageStart >= 0 && _messageStart <= _text.Length
                ? _text[_messageStart..].Trim()
                : "";
            Reset();
            return msg.Length == 0 ? null : InputPrefix + msg;
        }
    }

    private readonly struct Token(string norm, int start, int end)
    {
        public string Norm { get; } = norm;
        public int Start { get; } = start;
        public int End { get; } = end;
    }
}
