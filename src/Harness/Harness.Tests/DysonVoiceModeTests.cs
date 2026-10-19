using System.Runtime.CompilerServices;
using DysonHarness;

namespace Harness.Tests;

/// <summary>Voice-mode rules: wake match, pause, barge-in, echo, requirements, and the accept chime (fake playback).</summary>
public class DysonVoiceModeTests
{
    [Fact]
    public void AfterWake_MatchesAnyStoredVariant_ReturnsRest()
    {
        var names = Eight();
        Assert.Equal(
            "Open the plan list",
            DysonVoiceMode.AfterWake("Hey, Dyson. Open the plan list", names));
    }

    [Fact]
    public void AfterWake_NoVariantInTranscript_ReturnsNull()
    {
        Assert.Null(DysonVoiceMode.AfterWake("hey bison open", Eight()));
    }

    [Fact]
    public void Listener_SubmitsOnlyAfterMoreThanThreeSecondsOfNoNewWords()
    {
        var paused = new DysonVoiceMode.Listener(Eight());
        Assert.Equal(DysonVoiceMode.ListenAction.None, paused.OnText("hey dyson", 0, null));
        Assert.Equal(DysonVoiceMode.ListenAction.None, paused.OnText("hey dyson open the plan", 1000, null));
        Assert.Null(paused.OnTick(3999));
        Assert.Equal("[voice input] open the plan", paused.OnTick(4001));

        var pushed = new DysonVoiceMode.Listener(Eight());
        pushed.OnText("hey dyson", 0, null);
        pushed.OnText("hey dyson open the plan", 1000, null);
        pushed.OnText("hey dyson open the plan please", 3500, null);
        Assert.Null(pushed.OnTick(6500));
        Assert.Equal("[voice input] open the plan please", pushed.OnTick(6501));
    }

    [Fact]
    public void Listener_WakeWithNoMessage_SubmitsNothing()
    {
        var listener = new DysonVoiceMode.Listener(Eight());
        Assert.Equal(DysonVoiceMode.ListenAction.None, listener.OnText("hey dyson", 0, null));
        Assert.True(listener.Capturing);
        Assert.Null(listener.OnTick(3001));
        Assert.False(listener.Capturing);
        Assert.Null(listener.OnTick(9000));
    }

    [Fact]
    public void Listener_OrdinarySpeechDuringPlayback_DoesNotCancel()
    {
        var listener = new DysonVoiceMode.Listener(Eight());
        Assert.Equal(
            DysonVoiceMode.ListenAction.None,
            listener.OnText("stop that now", 100, "Processing request."));
        Assert.False(listener.Capturing);
        Assert.Null(listener.OnTick(100 + DysonVoiceMode.PauseMs + 1));
    }

    [Fact]
    public void Listener_WakeDuringPlayback_CancelsAndSubmitsWordsAfterTheName()
    {
        var listener = new DysonVoiceMode.Listener(Eight());
        Assert.Equal(DysonVoiceMode.ListenAction.None, listener.OnText("not the name", 0, "Processing request."));
        Assert.Equal(
            DysonVoiceMode.ListenAction.BargeIn,
            listener.OnText("not the name hey dyson stop", 100, "Processing request."));
        Assert.True(listener.Capturing);
        Assert.Equal(
            DysonVoiceMode.ListenAction.None,
            listener.OnText("not the name hey dyson stop please", 500, "Processing request."));
        Assert.Null(listener.OnTick(500 + DysonVoiceMode.PauseMs));
        Assert.Equal("[voice input] stop please", listener.OnTick(500 + DysonVoiceMode.PauseMs + 1));
    }

    [Fact]
    public void Listener_EchoOfSpokenText_IsIgnored()
    {
        var listener = new DysonVoiceMode.Listener(Eight());
        Assert.Equal(
            DysonVoiceMode.ListenAction.None,
            listener.OnText("processing request", 50, "Processing request."));
        Assert.False(listener.Capturing);
        Assert.Null(listener.OnTick(50 + DysonVoiceMode.PauseMs + 1));

        var named = new DysonVoiceMode.Listener(Eight());
        Assert.Equal(
            DysonVoiceMode.ListenAction.None,
            named.OnText("hey dyson", 50, "Hey Dyson, I am on it."));
        Assert.False(named.Capturing);
    }

    [Fact]
    public void Listener_SubmittedTranscript_IsNotHeardAgainWhileSpeaking()
    {
        var listener = new DysonVoiceMode.Listener(Eight());
        listener.OnText("hey dyson open the plan", 0, null);
        Assert.Equal("[voice input] open the plan", listener.OnTick(DysonVoiceMode.PauseMs + 1));

        Assert.Equal(
            DysonVoiceMode.ListenAction.None,
            listener.OnText("hey dyson open the plan", DysonVoiceMode.PauseMs + 2, "On it."));
        Assert.False(listener.Capturing);
        Assert.Null(listener.OnTick(DysonVoiceMode.PauseMs + 2 + DysonVoiceMode.PauseMs + 1));
    }

    [Fact]
    public void Requirements_SttError_FailsWithThatText()
    {
        var result = DysonVoiceMode.Requirements(
            Result<string, string>.AsError("Choose the active speech-to-text model in Settings → Voice."),
            Result<string, string>.AsValue(DysonPocketSpeech.Id),
            Eight(),
            0);
        Assert.True(result.IsError);
        Assert.Equal("Choose the active speech-to-text model in Settings → Voice.", result.Error);
    }

    [Fact]
    public void Requirements_TtsError_Fails()
    {
        var result = DysonVoiceMode.Requirements(
            Result<string, string>.AsValue(DysonPhononSpeech.Id),
            Result<string, string>.AsError("No text-to-speech model is installed. Open Settings → Voice and install one."),
            Eight(),
            0);
        Assert.True(result.IsError);
        Assert.Equal("No text-to-speech model is installed. Open Settings → Voice and install one.", result.Error);
    }

    [Fact]
    public void Requirements_SevenNamesOrBlank_FailsMentioningSettingsVoice()
    {
        const string expected = "Train the agent name: save 8 name variants in Settings → Voice.";
        var seven = Eight().Take(7).ToArray();
        var blank = Eight().ToArray();
        blank[2] = " ";

        var shortList = DysonVoiceMode.Requirements(Ok(DysonPhononSpeech.Id), Ok(DysonPocketSpeech.Id), seven, 0);
        var emptyName = DysonVoiceMode.Requirements(Ok(DysonPhononSpeech.Id), Ok(DysonPocketSpeech.Id), blank, 0);
        Assert.Equal(expected, shortList.Error);
        Assert.Equal(expected, emptyName.Error);
    }

    [Fact]
    public void Requirements_QwenGgufQ4WithNoVoice_Fails()
    {
        var qwen = DysonVoiceMode.Requirements(
            Ok(DysonGgufSpeechModels.NemotronId),
            Ok(DysonGgufSpeechModels.QwenGgufQ4Id),
            Eight(),
            0);
        Assert.True(qwen.IsError);
        Assert.Contains("needs a trained voice to speak", qwen.Error, StringComparison.Ordinal);
        Assert.Contains("Settings → Voice", qwen.Error, StringComparison.Ordinal);

        var python = DysonVoiceMode.Requirements(
            Ok(DysonPhononSpeech.Id),
            Ok(DysonQwenSpeech.Id),
            Eight(),
            0);
        Assert.True(python.IsError);
        Assert.Contains("needs a trained voice to speak", python.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Requirements_ChatterboxWithNoVoice_Succeeds()
    {
        var result = DysonVoiceMode.Requirements(
            Ok(DysonPhononSpeech.Id),
            Ok(DysonChatterboxSpeech.Id),
            Eight(),
            0);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Requirements_PocketTts_Succeeds()
    {
        var result = DysonVoiceMode.Requirements(
            Ok(DysonPhononSpeech.Id),
            Ok(DysonPocketSpeech.Id),
            Eight(),
            0);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Requirements_PhononGgufPlusPocketGguf_Fails()
    {
        var result = DysonVoiceMode.Requirements(
            Ok(DysonGgufSpeechModels.PhononGgufId),
            Ok(DysonGgufSpeechModels.PocketGgufId),
            Eight(),
            1);
        Assert.True(result.IsError);
        Assert.Contains("share the one GGUF runtime", result.Error, StringComparison.Ordinal);
        Assert.Contains("Settings → Voice", result.Error, StringComparison.Ordinal);

        var q8 = DysonVoiceMode.Requirements(
            Ok(DysonGgufSpeechModels.PhononGgufQ8Id),
            Ok(DysonGgufSpeechModels.ChatterboxGgufQ8Id),
            Eight(),
            0);
        Assert.True(q8.IsError);
        Assert.Contains("share the one GGUF runtime", q8.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Requirements_AllMet_Succeeds()
    {
        var phononGgufWithPythonPocket = DysonVoiceMode.Requirements(
            Ok(DysonGgufSpeechModels.PhononGgufId),
            Ok(DysonPocketSpeech.Id),
            Eight(),
            0);
        Assert.True(phononGgufWithPythonPocket.IsSuccess);

        var qwenWithVoice = DysonVoiceMode.Requirements(
            Ok(DysonGgufSpeechModels.NemotronId),
            Ok(DysonGgufSpeechModels.QwenGgufQ4Id),
            Eight(),
            1);
        Assert.True(qwenWithVoice.IsSuccess);
    }

    [Fact]
    public async Task Accepted_turn_chimes_and_still_submits_when_playback_throws_or_is_busy()
    {
        var submitted = new List<string>();
        string? failed = null;
        Task Ready(string text)
        {
            submitted.Add(text);
            return Task.CompletedTask;
        }

        var throwing = new ProbePlayback { Throw = true };
        var tryout = new DysonSpeechTryout(new IdleCapture(), new IdleStt(), new IdleTts(), throwing);
        await DysonVoiceMode.AcceptTurnAsync(
            "[voice input] open the plan", false, tryout.PlayAcceptChime, Ready, error => failed = error);
        await throwing.Finished;
        Assert.Null(failed);
        Assert.Equal(1, throwing.Plays);
        Assert.True(throwing.ToneMs is > 0 and < 300);
        Assert.True(throwing.Peak < 8000);
        Assert.Equal(["[voice input] open the plan"], submitted);

        submitted.Clear();
        var busy = new ProbePlayback { Busy = true };
        tryout = new DysonSpeechTryout(new IdleCapture(), new IdleStt(), new IdleTts(), busy);
        await DysonVoiceMode.AcceptTurnAsync(
            "[voice input] again", false, tryout.PlayAcceptChime, Ready, error => failed = error);
        await busy.Finished;
        Assert.Null(failed);
        Assert.Equal(1, busy.Plays);
        Assert.Equal(0, busy.Stops);
        Assert.Equal(["[voice input] again"], submitted);

        submitted.Clear();
        var speaking = new ProbePlayback();
        tryout = new DysonSpeechTryout(new IdleCapture(), new IdleStt(), new IdleTts(), speaking);
        await DysonVoiceMode.AcceptTurnAsync(
            "[voice input] while speaking", true, tryout.PlayAcceptChime, Ready, error => failed = error);
        Assert.Null(failed);
        Assert.Equal(0, speaking.Plays);
        Assert.Equal(["[voice input] while speaking"], submitted);
    }

    private static Result<string, string> Ok(string id) => Result<string, string>.AsValue(id);

    /// <summary>Variant 7 is the stored wake phrase. The other seven cannot match the test sentences.</summary>
    private static string[] Eight(string seventh = "hey dyson")
    {
        var names = new string[8];
        for (var i = 0; i < 8; i++)
            names[i] = $"name {i + 1} alpha";
        names[6] = seventh;
        return names;
    }

    private sealed class IdleCapture : IDysonAudioCapture
    {
        public Result<IReadOnlyList<DysonAudioInputDevice>, string> ListDevices() =>
            Result<IReadOnlyList<DysonAudioInputDevice>, string>.AsValue([]);

        public async IAsyncEnumerable<Result<Stream, string>> CaptureWindowsAsync(
            string? deviceId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield break;
        }

        public async IAsyncEnumerable<Result<byte[], string>> CapturePcmAsync(
            string? deviceId,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            bool dropOldest = false)
        {
            yield break;
        }

        public VoidResult<string> Stop() => VoidResult<string>.AsError("Nothing is recording.");
    }

    private sealed class IdleStt : IDysonSpeechToText
    {
        public string RuntimeId => "idle";

        public async IAsyncEnumerable<Result<DysonSpeechChunk, string>> TranscribeAsync(
            Stream audio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield break;
        }
    }

    private sealed class IdleTts : IDysonTextToSpeech
    {
        public string RuntimeId => "idle";

        public async IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> SynthesizeAsync(
            string text,
            string? voiceId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield break;
        }
    }

    private sealed class ProbePlayback : IDysonAudioPlayback
    {
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Plays;
        public int Stops;
        public int ToneMs;
        public int Peak;
        public bool Throw;
        public bool Busy;
        public Task Finished => _finished.Task;

        public async Task<VoidResult<string>> PlayAsync(
            IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> chunks,
            CancellationToken cancellationToken)
        {
            try
            {
                Plays++;
                await foreach (var chunk in chunks)
                {
                    if (!chunk.IsSuccess || chunk.Value.SampleRate <= 0)
                        continue;
                    var pcm = chunk.Value.Pcm;
                    ToneMs = Math.Max(ToneMs, pcm.Length / 2 * 1000 / chunk.Value.SampleRate);
                    for (var i = 0; i + 1 < pcm.Length; i += 2)
                    {
                        var sample = pcm[i] | (pcm[i + 1] << 8);
                        if (sample >= 0x8000)
                            sample -= 0x10000;
                        Peak = Math.Max(Peak, Math.Abs(sample));
                    }
                }

                if (Throw)
                    throw new InvalidOperationException("no device");
                return Busy
                    ? VoidResult<string>.AsError("Audio is already playing.")
                    : VoidResult<string>.Success;
            }
            finally
            {
                _finished.TrySetResult();
            }
        }

        public VoidResult<string> Stop(TimeSpan fadeOut = default)
        {
            Stops++;
            return VoidResult<string>.Success;
        }
    }
}
