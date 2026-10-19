using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using DysonHarness;

namespace Harness.Tests;

/// <summary>Voice-mode rules: wake match, pause, barge-in, echo, requirements, prefix, stop reason, and both chimes (fake playback).</summary>
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
        var names = Eight();
        var paused = new DysonVoiceMode.Listener();
        Assert.Equal(DysonVoiceMode.ListenAction.None, paused.OnText("hey dyson", 0, null, names));
        Assert.Equal(DysonVoiceMode.ListenAction.None, paused.OnText("hey dyson open the plan", 1000, null, names));
        Assert.Null(paused.OnTick(3999));
        Assert.Equal("[voice input] open the plan", paused.OnTick(4001));

        var pushed = new DysonVoiceMode.Listener();
        pushed.OnText("hey dyson", 0, null, names);
        pushed.OnText("hey dyson open the plan", 1000, null, names);
        pushed.OnText("hey dyson open the plan please", 3500, null, names);
        Assert.Null(pushed.OnTick(6500));
        Assert.Equal("[voice input] open the plan please", pushed.OnTick(6501));
    }

    [Fact]
    public void Listener_WakeWithNoMessage_SubmitsNothing()
    {
        var listener = new DysonVoiceMode.Listener();
        Assert.Equal(DysonVoiceMode.ListenAction.None, listener.OnText("hey dyson", 0, null, Eight()));
        Assert.True(listener.Capturing);
        Assert.Null(listener.OnTick(3001));
        Assert.False(listener.Capturing);
        Assert.Null(listener.OnTick(9000));
    }

    [Fact]
    public void Listener_OrdinarySpeechDuringPlayback_DoesNotCancel()
    {
        var listener = new DysonVoiceMode.Listener();
        Assert.Equal(
            DysonVoiceMode.ListenAction.None,
            listener.OnText("stop that now", 100, "Processing request.", Eight()));
        Assert.False(listener.Capturing);
        Assert.Null(listener.OnTick(100 + DysonVoiceMode.PauseMs + 1));
    }

    [Fact]
    public void Listener_WakeDuringPlayback_CancelsAndSubmitsWordsAfterTheName()
    {
        var listener = new DysonVoiceMode.Listener();
        Assert.Equal(DysonVoiceMode.ListenAction.None, listener.OnText("not the name", 0, "Processing request.", Eight()));
        Assert.Equal(
            DysonVoiceMode.ListenAction.BargeIn,
            listener.OnText("not the name hey dyson stop", 100, "Processing request.", Eight()));
        Assert.True(listener.Capturing);
        Assert.Equal(
            DysonVoiceMode.ListenAction.None,
            listener.OnText("not the name hey dyson stop please", 500, "Processing request.", Eight()));
        Assert.Null(listener.OnTick(500 + DysonVoiceMode.PauseMs));
        Assert.Equal("[voice input] stop please", listener.OnTick(500 + DysonVoiceMode.PauseMs + 1));
    }

    [Fact]
    public void Listener_EchoOfSpokenText_IsIgnored()
    {
        var listener = new DysonVoiceMode.Listener();
        Assert.Equal(
            DysonVoiceMode.ListenAction.None,
            listener.OnText("processing request", 50, "Processing request.", Eight()));
        Assert.False(listener.Capturing);
        Assert.Null(listener.OnTick(50 + DysonVoiceMode.PauseMs + 1));

        var named = new DysonVoiceMode.Listener();
        Assert.Equal(
            DysonVoiceMode.ListenAction.None,
            named.OnText("hey dyson", 50, "Hey Dyson, I am on it.", Eight()));
        Assert.False(named.Capturing);
    }

    [Fact]
    public void Listener_SubmittedTranscript_IsNotHeardAgainWhileSpeaking()
    {
        var listener = new DysonVoiceMode.Listener();
        listener.OnText("hey dyson open the plan", 0, null, Eight());
        Assert.Equal("[voice input] open the plan", listener.OnTick(DysonVoiceMode.PauseMs + 1));

        Assert.Equal(
            DysonVoiceMode.ListenAction.None,
            listener.OnText("hey dyson open the plan", DysonVoiceMode.PauseMs + 2, "On it.", Eight()));
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

    [Fact]
    public void PlaybackText_active_is_unprefixed()
    {
        var id = Guid.NewGuid();
        Assert.Equal("Done.", DysonVoiceMode.PlaybackText(id, "Done.", "Harness", id, "Harness"));
    }

    [Fact]
    public void PlaybackText_other_workdir_is_prefixed()
    {
        var active = Guid.NewGuid();
        var other = Guid.NewGuid();
        Assert.Equal(
            "Message from workdir Harness: Done.",
            DysonVoiceMode.PlaybackText(other, "Done.", "Harness", active, "Harness"));
    }

    [Fact]
    public void PlaybackText_rename_uses_live_name()
    {
        var active = Guid.NewGuid();
        var line = Guid.NewGuid();
        Assert.Equal(
            "Message from workdir New: Done.",
            DysonVoiceMode.PlaybackText(line, "Done.", "Old", active, "New"));
    }

    [Fact]
    public void PlaybackText_closed_uses_snapshot()
    {
        var active = Guid.NewGuid();
        var line = Guid.NewGuid();
        Assert.Equal(
            "Message from workdir Old: Done.",
            DysonVoiceMode.PlaybackText(line, "Done.", "Old", active, null));
    }

    [Fact]
    public void PlaybackText_became_active_drops_prefix()
    {
        var id = Guid.NewGuid();
        Assert.Equal("Done.", DysonVoiceMode.PlaybackText(id, "Done.", "Old", id, "New"));
    }

    [Fact]
    public async Task SpeakLoop_plays_one_at_a_time_in_order()
    {
        await using var rig = await VoiceRig.StartAsync();
        var active = Guid.NewGuid();
        var other = Guid.NewGuid();
        rig.Work.Add(new DysonWorkDirectoryEntity
        {
            Id = other,
            Name = "Harness",
            AbsolutePath = "E:\\Harness",
        });
        rig.Focus.SetActive(active);

        Assert.True(rig.Voice.Speak("One", active, "Active").IsSuccess);
        Assert.True(rig.Voice.Speak("Done.", other, "Old").IsSuccess);
        await WaitForAsync(() => rig.Tts.Texts.Count >= 2);

        Assert.Equal(["One", "Message from workdir Harness: Done."], rig.Tts.Texts);
        Assert.Equal(1, rig.Playback.MaxInFlight);
    }

    [Fact]
    public async Task SpeakLoop_synthesizes_next_line_while_current_plays()
    {
        await using var rig = await VoiceRig.StartAsync();
        rig.Playback.HoldSpeech();
        var active = Guid.NewGuid();
        var other = Guid.NewGuid();
        rig.Work.Add(new DysonWorkDirectoryEntity
        {
            Id = other,
            Name = "Harness",
            AbsolutePath = "E:\\Harness",
        });
        rig.Focus.SetActive(active);

        Assert.True(rig.Voice.Speak("One", active, "Active").IsSuccess);
        Assert.True(rig.Voice.Speak("Done.", other, "Old").IsSuccess);
        await WaitForAsync(() => rig.Tts.Texts.Count >= 2 && rig.Playback.SpeechPlays >= 1);

        Assert.Equal(["One", "Message from workdir Harness: Done."], rig.Tts.Texts);
        Assert.Equal(1, rig.Playback.MaxInFlight);
        Assert.Equal(1, rig.Playback.SpeechPlays);
        rig.Playback.ReleaseSpeech();
        await WaitForAsync(() => rig.Playback.SpeechPlays >= 2);
        Assert.Equal(2, rig.Playback.SpeechPlays);
        Assert.Equal(1, rig.Playback.MaxInFlight);
    }

    [Fact]
    public async Task BargeIn_drops_prefetched_line()
    {
        await using var rig = await VoiceRig.StartAsync();
        rig.Playback.HoldSpeech();
        var id = Guid.NewGuid();
        rig.Focus.SetActive(id);
        Assert.True(rig.Voice.Speak("One", id, "A").IsSuccess);
        Assert.True(rig.Voice.Speak("Two", id, "A").IsSuccess);
        await WaitForAsync(() => rig.Tts.Texts.Count >= 2 && rig.Playback.SpeechPlays >= 1);

        rig.Capture.PushText("hey dyson stop");
        Assert.True(await rig.Stt.NextAsync());
        await WaitForAsync(() => rig.Playback.ListenCount >= 1 && !rig.Voice.IsSpeaking);
        await Task.Delay(80);

        Assert.Equal(1, rig.Playback.SpeechPlays);
        Assert.Equal(["One", "Two"], rig.Tts.Texts);
    }

    [Fact]
    public async Task Stop_cancels_prefetched_line()
    {
        await using var rig = await VoiceRig.StartAsync();
        rig.Playback.HoldSpeech();
        var id = Guid.NewGuid();
        rig.Focus.SetActive(id);
        Assert.True(rig.Voice.Speak("One", id, "A").IsSuccess);
        Assert.True(rig.Voice.Speak("Two", id, "A").IsSuccess);
        await WaitForAsync(() => rig.Tts.Texts.Count >= 2 && rig.Playback.SpeechPlays >= 1);

        await rig.Voice.StopAsync(DysonVoiceStopReason.UserOff);
        Assert.False(rig.Voice.IsOn);
        Assert.Equal(1, rig.Playback.SpeechPlays);
        Assert.True(rig.Tts.Cancelled >= 1);
    }

    [Fact]
    public async Task WakeNames_saved_while_on_match_the_next_partial()
    {
        await using var rig = await VoiceRig.StartAsync();
        Assert.True((await rig.Models.SaveWakeNamesAsync(Eight("hey nova"))).IsSuccess);

        rig.Capture.PushText("hey dyson");
        Assert.True(await rig.Stt.NextAsync());
        await Task.Delay(150);
        Assert.Equal(0, rig.Playback.ListenCount);

        rig.Capture.PushText("hey nova");
        Assert.True(await rig.Stt.NextAsync());
        await WaitForAsync(() => rig.Playback.ListenCount >= 1);
        Assert.Equal(1, rig.Playback.ListenCount);
    }

    [Fact]
    public async Task AcceptSentence_configured_plays_cached_pcm_not_the_chime()
    {
        var root = PrivateRoot();
        try
        {
            var settings = new FakeSettings();
            var pcm = new byte[320];
            pcm[0] = 40;
            pcm[1] = 1;
            Assert.True((await DysonAcceptSentence.SaveAsync(
                new ChunkTts(pcm, 16_000), settings, root, "Copy", CancellationToken.None)).IsSuccess);
            var loaded = await DysonAcceptSentence.TryLoadAsync(settings, root, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(pcm, loaded!.Pcm);

            var playback = new ChimePlayback();
            var tryout = new DysonSpeechTryout(new IdleCapture(), new IdleStt(), new IdleTts(), playback);
            tryout.PlayAcceptCue(loaded);
            await WaitForAsync(() => playback.Clips.Count >= 1);
            Assert.Equal(0, playback.AcceptCount);
            Assert.Equal(pcm, playback.Clips[0]);
            Assert.False(DysonAcceptChime.Pcm().AsSpan().SequenceEqual(playback.Clips[0]));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AcceptSentence_unset_plays_the_chime()
    {
        var settings = new FakeSettings();
        var root = PrivateRoot();
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(await DysonAcceptSentence.TryLoadAsync(settings, root, CancellationToken.None));
            var playback = new ChimePlayback();
            var tryout = new DysonSpeechTryout(new IdleCapture(), new IdleStt(), new IdleTts(), playback);
            tryout.PlayAcceptCue(null);
            await WaitForAsync(() => playback.AcceptCount >= 1);
            Assert.Equal(1, playback.AcceptCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AcceptSentence_validate_rejects_newline_and_overlong_pcm()
    {
        Assert.True(DysonAcceptSentence.Validate("Copy").IsSuccess);
        Assert.True(DysonAcceptSentence.Validate("").IsSuccess);
        Assert.True(DysonAcceptSentence.Validate(null).IsSuccess);
        var over = DysonAcceptSentence.Validate(new string('a', 41));
        Assert.True(over.IsError);
        Assert.Equal("Acceptance sentence is over 40 characters.", over.Error);
        Assert.True(DysonAcceptSentence.Validate("no\nline").IsError);

        var root = PrivateRoot();
        try
        {
            var settings = new FakeSettings();
            var pcm = new byte[(16_000 * 5 * 2) + 2];
            var saved = await DysonAcceptSentence.SaveAsync(
                new ChunkTts(pcm, 16_000), settings, root, "Copy", CancellationToken.None);
            Assert.True(saved.IsError);
            Assert.Equal("Acceptance sentence was too long to cache.", saved.Error);
            var key = await settings.GetSettingAsync(DysonAppSettingKeys.SpeechAcceptSentence);
            Assert.True(string.IsNullOrWhiteSpace(key.Value));
            Assert.False(File.Exists(Path.Combine(root, "accept-sentence.wav")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Stop_only_for_user_off_or_app_exit()
    {
        Assert.True(DysonVoiceMode.Stops(DysonVoiceStopReason.UserOff));
        Assert.True(DysonVoiceMode.Stops(DysonVoiceStopReason.AppExit));
        Assert.False(DysonVoiceMode.Stops(DysonVoiceStopReason.PageDispose));
        Assert.False(DysonVoiceMode.Stops(DysonVoiceStopReason.SessionSwitch));
        Assert.False(DysonVoiceMode.Stops(DysonVoiceStopReason.WorkDirectorySwitch));
        Assert.False(DysonVoiceMode.Stops(DysonVoiceStopReason.Settings));

        await using var rig = await VoiceRig.StartAsync();
        Assert.True(rig.Voice.IsOn);

        await rig.Voice.StopAsync(DysonVoiceStopReason.WorkDirectorySwitch);
        await rig.Voice.StopAsync(DysonVoiceStopReason.PageDispose);
        await rig.Voice.StopAsync(DysonVoiceStopReason.SessionSwitch);
        await rig.Voice.StopAsync(DysonVoiceStopReason.Settings);
        Assert.True(rig.Voice.IsOn);
        Assert.Equal(0, rig.Capture.Stops);

        await rig.Voice.StopAsync(DysonVoiceStopReason.UserOff);
        Assert.False(rig.Voice.IsOn);
        Assert.True(rig.Capture.Stops >= 1);

        var again = await rig.Voice.StartAsync();
        Assert.True(again.IsSuccess, again.IsError ? again.Error : null);
        Assert.True(rig.Voice.IsOn);
        await rig.Voice.StopAsync(DysonVoiceStopReason.AppExit);
        Assert.False(rig.Voice.IsOn);
    }

    [Fact]
    public async Task ListenChime_once_on_wake_match()
    {
        await using var rig = await VoiceRig.StartAsync();
        rig.Capture.PushText("hey dyson");
        Assert.True(await rig.Stt.NextAsync());
        await WaitForAsync(() => rig.Playback.ListenCount >= 1);
        Assert.Equal(1, rig.Playback.ListenCount);
        Assert.Equal(0, rig.Playback.AcceptCount);

        rig.Capture.PushText(" open the plan");
        Assert.True(await rig.Stt.NextAsync());
        await Task.Delay(100);
        Assert.Equal(1, rig.Playback.ListenCount);
        Assert.Equal(0, rig.Playback.AcceptCount);
        Assert.Equal(0, rig.Playback.FadeStops);
    }

    [Fact]
    public async Task ListenChime_not_on_ordinary_speech_or_echo()
    {
        await using (var ordinary = await VoiceRig.StartAsync())
        {
            await HearWhileSpeakingAsync(ordinary, "Processing request.", "stop that now");
            Assert.Equal(0, ordinary.Playback.ListenCount);
            Assert.Equal(0, ordinary.Playback.FadeStops);
            Assert.Empty(ordinary.Failed);
        }

        await using (var echo = await VoiceRig.StartAsync())
        {
            await HearWhileSpeakingAsync(echo, "Processing request.", "processing request");
            Assert.Equal(0, echo.Playback.ListenCount);
            Assert.Equal(0, echo.Playback.FadeStops);
            Assert.Empty(echo.Failed);
        }
    }

    [Fact]
    public async Task AcceptChime_only_on_submit()
    {
        await using var rig = await VoiceRig.StartAsync();
        rig.Capture.PushText("hey dyson open the plan");
        Assert.True(await rig.Stt.NextAsync());
        await WaitForAsync(() => rig.Playback.ListenCount >= 1);
        Assert.Equal(1, rig.Playback.ListenCount);
        Assert.Equal(0, rig.Playback.AcceptCount);

        await WaitForAsync(() => rig.Playback.AcceptCount >= 1, 5000);
        Assert.Equal(1, rig.Playback.AcceptCount);
        Assert.Equal(1, rig.Playback.ListenCount);
        Assert.Equal(0, rig.Playback.FadeStops);
    }

    [Fact]
    public async Task BargeIn_fades_and_plays_listen_chime_once()
    {
        await using var rig = await VoiceRig.StartAsync();
        rig.Playback.HoldSpeech();
        rig.Playback.ReleaseError =
            "CrispASR canceled the generation. Speech to text fell behind the microphone.";
        var id = Guid.NewGuid();
        rig.Focus.SetActive(id);
        Assert.True(rig.Voice.Speak("Processing request.", id, "A").IsSuccess);
        await rig.Playback.SpeechStarted.WaitAsync(TimeSpan.FromSeconds(5));

        rig.Capture.PushText("hey dyson stop");
        Assert.True(await rig.Stt.NextAsync());
        await WaitForAsync(() => rig.Playback.ListenCount >= 1 && !rig.Voice.IsSpeaking);
        rig.Capture.PushText(" please");
        Assert.True(await rig.Stt.NextAsync());
        await Task.Delay(50);

        Assert.Equal(1, rig.Playback.FadeStops);
        Assert.Equal(DysonVoiceMode.FadeOut, rig.Playback.LastFade);
        Assert.Equal(1, rig.Playback.ListenCount);
        Assert.Equal(0, rig.Playback.AcceptCount);
        Assert.Empty(rig.Failed);
        Assert.True(rig.Voice.IsOn);
    }

    private static async Task HearWhileSpeakingAsync(VoiceRig rig, string spoken, string heard)
    {
        rig.Playback.HoldSpeech();
        var id = Guid.NewGuid();
        rig.Focus.SetActive(id);
        Assert.True(rig.Voice.Speak(spoken, id, "A").IsSuccess);
        await rig.Playback.SpeechStarted.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Capture.PushText(heard);
        Assert.True(await rig.Stt.NextAsync());
    }

    private static async Task WaitForAsync(Func<bool> ready, int ms = 4000)
    {
        var start = Environment.TickCount64;
        while (!ready())
        {
            if (Environment.TickCount64 - start > ms)
                return;
            await Task.Delay(15);
        }
    }

    private static Result<string, string> Ok(string id) => Result<string, string>.AsValue(id);

    /// <summary>Variant 7 is the stored wake phrase. The other seven cannot match the test sentences.</summary>
    private static string PrivateRoot() =>
        Path.Combine(AppContext.BaseDirectory, "accept-sentence-" + Guid.NewGuid().ToString("n"));

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

    private sealed class VoiceRig(
        ScriptCapture capture,
        Utf8Stt stt,
        RecordingTts tts,
        ChimePlayback playback,
        FakeWorkDirectories work,
        DysonWorkDirectoryFocus focus,
        FakeSpeechModels models,
        DysonVoiceMode voice) : IAsyncDisposable
    {
        public ScriptCapture Capture { get; } = capture;
        public Utf8Stt Stt { get; } = stt;
        public RecordingTts Tts { get; } = tts;
        public ChimePlayback Playback { get; } = playback;
        public FakeWorkDirectories Work { get; } = work;
        public DysonWorkDirectoryFocus Focus { get; } = focus;
        public FakeSpeechModels Models { get; } = models;
        public DysonVoiceMode Voice { get; } = voice;
        public List<string> Failed { get; } = [];

        public static async Task<VoiceRig> StartAsync()
        {
            var capture = new ScriptCapture();
            var stt = new Utf8Stt();
            var tts = new RecordingTts();
            var playback = new ChimePlayback();
            var work = new FakeWorkDirectories();
            var settings = new FakeSettings();
            settings.Set(DysonAppSettingKeys.SpeechInputModelId, DysonPhononSpeech.Id);
            settings.Set(DysonAppSettingKeys.SpeechOutputModelId, DysonPocketSpeech.Id);
            var models = new FakeSpeechModels(Eight());
            var focus = new DysonWorkDirectoryFocus(work);
            var root = Path.GetTempPath();
            var tryout = new DysonSpeechTryout(capture, stt, tts, playback);
            var voice = new DysonVoiceMode(
                tryout,
                new DysonActiveSpeechToText(models, settings, root),
                new DysonActiveTextToSpeech(models, settings, root),
                models,
                settings,
                focus);
            voice.Failed += text => { };
            var ready = await voice.StartAsync();
            if (ready.IsError)
                throw new InvalidOperationException(ready.Error);
            var rig = new VoiceRig(capture, stt, tts, playback, work, focus, models, voice);
            voice.Failed += rig.Failed.Add;
            return rig;
        }

        public async ValueTask DisposeAsync() => await Voice.StopAsync(DysonVoiceStopReason.AppExit);
    }

    private sealed class ScriptCapture : IDysonAudioCapture
    {
        private readonly Channel<Result<Stream, string>> _windows = Channel.CreateUnbounded<Result<Stream, string>>();
        public int Stops;

        public void PushText(string text) =>
            _windows.Writer.TryWrite(Result<Stream, string>.AsValue(new MemoryStream(Encoding.UTF8.GetBytes(text))));

        public Result<IReadOnlyList<DysonAudioInputDevice>, string> ListDevices() =>
            Result<IReadOnlyList<DysonAudioInputDevice>, string>.AsValue([]);

        public async IAsyncEnumerable<Result<Stream, string>> CaptureWindowsAsync(
            string? deviceId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Result<Stream, string>? next = null;
                try
                {
                    if (!await _windows.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                        yield break;
                    if (!_windows.Reader.TryRead(out var item))
                        continue;
                    next = item;
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                if (next is not null)
                    yield return next;
            }
        }

        public async IAsyncEnumerable<Result<byte[], string>> CapturePcmAsync(
            string? deviceId,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            bool dropOldest = false)
        {
            yield break;
        }

        public VoidResult<string> Stop()
        {
            Stops++;
            return VoidResult<string>.AsError("Nothing is recording.");
        }
    }

    private sealed class Utf8Stt : IDysonSpeechToText
    {
        private readonly Channel<int> _done = Channel.CreateUnbounded<int>();
        private int _n;
        public string RuntimeId => "utf8";

        public async Task<bool> NextAsync(int ms = 3000)
        {
            using var cts = new CancellationTokenSource(ms);
            try
            {
                await _done.Reader.ReadAsync(cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public async IAsyncEnumerable<Result<DysonSpeechChunk, string>> TranscribeAsync(
            Stream audio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var copy = new MemoryStream();
            await audio.CopyToAsync(copy, cancellationToken);
            var text = Encoding.UTF8.GetString(copy.ToArray());
            yield return Result<DysonSpeechChunk, string>.AsValue(new DysonSpeechChunk(text, 0, 0, true));
            _done.Writer.TryWrite(Interlocked.Increment(ref _n));
        }
    }

    private sealed class RecordingTts : IDysonTextToSpeech
    {
        private readonly List<string> _texts = [];
        public string RuntimeId => "rec";
        public IReadOnlyList<string> Texts
        {
            get
            {
                lock (_texts)
                    return _texts.ToArray();
            }
        }

        private int _cancelled;
        public int Cancelled => Volatile.Read(ref _cancelled);

        public async IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> SynthesizeAsync(
            string text,
            string? voiceId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            lock (_texts)
                _texts.Add(text);
            cancellationToken.Register(() => Interlocked.Increment(ref _cancelled));
            yield return Result<DysonSpeechAudioChunk, string>.AsValue(new DysonSpeechAudioChunk(new byte[320], 16_000, true));
            await Task.CompletedTask;
        }
    }

    private sealed class ChunkTts(byte[] pcm, int rate) : IDysonTextToSpeech
    {
        public string RuntimeId => "chunk";

        public async IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> SynthesizeAsync(
            string text,
            string? voiceId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Result<DysonSpeechAudioChunk, string>.AsValue(new DysonSpeechAudioChunk(pcm, rate, true));
            await Task.CompletedTask;
        }
    }

    private readonly record struct Heard(int ToneMs, int Hertz, int Peak)
    {
        public bool IsListen => ToneMs is >= 50 and <= 70 && Hertz is >= 620 and <= 700;
        public bool IsAccept => ToneMs is >= 100 and <= 140 && Hertz is >= 840 and <= 920;
    }

    private sealed class ChimePlayback : IDysonAudioPlayback
    {
        private readonly object _gate = new();
        private readonly List<Heard> _plays = [];
        private readonly TaskCompletionSource _speechStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private TaskCompletionSource<bool>? _hold;
        public int MaxInFlight;
        public int SpeechPlays;
        public int Stops;
        public int FadeStops;
        public TimeSpan LastFade;
        public string? ReleaseError;
        public List<byte[]> Clips { get; } = [];
        public Task SpeechStarted => _speechStarted.Task;

        public void ReleaseSpeech() => _hold?.TrySetResult(true);
        public int ListenCount
        {
            get
            {
                lock (_gate)
                    return _plays.Count(p => p.IsListen);
            }
        }

        public int AcceptCount
        {
            get
            {
                lock (_gate)
                    return _plays.Count(p => p.IsAccept);
            }
        }

        public void HoldSpeech() =>
            _hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VoidResult<string>> PlayAsync(
            IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> chunks,
            CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _inFlight);
            lock (_gate)
            {
                if (now > MaxInFlight)
                    MaxInFlight = now;
            }

            try
            {
                byte[]? pcm = null;
                var rate = 0;
                await foreach (var chunk in chunks.WithCancellation(cancellationToken))
                {
                    if (!chunk.IsSuccess || chunk.Value.Pcm is null)
                        continue;
                    pcm = chunk.Value.Pcm;
                    rate = chunk.Value.SampleRate;
                }

                var heard = Measure(pcm, rate);
                lock (_gate)
                {
                    _plays.Add(heard);
                    if (pcm is not null)
                        Clips.Add((byte[])pcm.Clone());
                    if (!heard.IsListen && !heard.IsAccept)
                        SpeechPlays++;
                }

                if (heard.IsListen || heard.IsAccept)
                    return VoidResult<string>.Success;

                _speechStarted.TrySetResult();
                if (_hold is not null)
                    await _hold.Task.WaitAsync(cancellationToken);
                if (ReleaseError is not null)
                    return VoidResult<string>.AsError(ReleaseError);
                await Task.Delay(40, cancellationToken);
                return VoidResult<string>.Success;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public VoidResult<string> Stop(TimeSpan fadeOut = default)
        {
            Stops++;
            if (fadeOut > TimeSpan.Zero)
            {
                FadeStops++;
                LastFade = fadeOut;
                _hold?.TrySetResult(true);
            }

            return VoidResult<string>.Success;
        }

        private static Heard Measure(byte[]? pcm, int rate)
        {
            if (pcm is null || pcm.Length < 2 || rate <= 0)
                return default;
            var samples = pcm.Length / 2;
            var prev = 0;
            var crossings = 0;
            var peak = 0;
            for (var i = 0; i + 1 < pcm.Length; i += 2)
            {
                var sample = pcm[i] | (pcm[i + 1] << 8);
                if (sample >= 0x8000)
                    sample -= 0x10000;
                peak = Math.Max(peak, Math.Abs(sample));
                if (prev < 0 && sample >= 0)
                    crossings++;
                prev = sample;
            }

            var hertz = (int)Math.Round(crossings * (double)rate / samples);
            return new Heard(samples * 1000 / rate, hertz, peak);
        }
    }

    private sealed class FakeWorkDirectories : IDysonWorkDirectoryRepository
    {
        public Dictionary<Guid, DysonWorkDirectoryEntity> Items { get; } = [];

        public void Add(DysonWorkDirectoryEntity entity) => Items[entity.Id] = entity;

        public Task<Result<Guid, string>> CreateAsync(
            string absolutePath, string? name = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Guid, string>.AsError("not used"));

        public Task<Result<DysonWorkDirectoryEntity, string>> GetAsync(
            Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Items.TryGetValue(id, out var entity)
                    ? Result<DysonWorkDirectoryEntity, string>.AsValue(entity)
                    : Result<DysonWorkDirectoryEntity, string>.AsError($"Work directory '{id}' not found."));

        public Task<Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>.AsValue(Items.Values.ToList()));

        public Task<VoidResult<string>> TouchOpenedAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.AsError("not used"));

        public Task<VoidResult<string>> UpdateGitMetadataAsync(
            Guid id, string? gitOrigin, string? gitProvider, CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.AsError("not used"));

        public Task<VoidResult<string>> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.AsError("not used"));
    }

    private sealed class FakeSettings : IDysonSubjectSettingsRepository
    {
        private readonly Dictionary<string, string?> _values = [];

        public void Set(string key, string? value) => _values[key] = value;

        public Task<VoidResult<string>> EnsureSubjectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<Result<string?, string>> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.TryGetValue(key, out var value);
            return Task.FromResult(Result<string?, string>.AsValue(value));
        }

        public Task<VoidResult<string>> SetSettingAsync(
            string key, string? value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.FromResult(VoidResult<string>.Success);
        }
    }

    private sealed class FakeSpeechModels(string[] wake) : IDysonSpeechModelRepository
    {
        private string[] _wake = wake;
        private readonly DysonSpeechModelEntity[] _rows =
        [
            Row(DysonPhononSpeech.Id, DysonSpeechCapability.Input),
            Row(DysonPocketSpeech.Id, DysonSpeechCapability.Output),
        ];

        public Task<VoidResult<string>> EnsureBuiltInAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<Result<IReadOnlyList<DysonSpeechModelEntity>, string>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<DysonSpeechModelEntity>, string>.AsValue(_rows));

        public Task<VoidResult<string>> MarkInstalledAsync(
            string runtimeId, string variant, CancellationToken cancellationToken = default) =>
            NotUsed();

        public Task<VoidResult<string>> MarkUninstalledAsync(
            string runtimeId, CancellationToken cancellationToken = default) =>
            NotUsed();

        public Task<VoidResult<string>> EnsureCatalogVoiceAsync(
            string modelRuntimeId,
            string voiceId,
            string? displayName,
            string variant,
            CancellationToken cancellationToken = default,
            string? sourceFileName = null) =>
            NotUsed();

        public Task<Result<IReadOnlyList<DysonSpeechVoiceEntity>, string>> ListVoicesAsync(
            string modelRuntimeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<IReadOnlyList<DysonSpeechVoiceEntity>, string>.AsValue(
                    Array.Empty<DysonSpeechVoiceEntity>()));

        public Task<Result<IReadOnlyList<string>, string>> GetWakeNamesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<string>, string>.AsValue((IReadOnlyList<string>)_wake.ToArray()));

        public Task<VoidResult<string>> SaveWakeNamesAsync(
            IReadOnlyList<string> variants, CancellationToken cancellationToken = default)
        {
            _wake = variants.ToArray();
            return Task.FromResult(VoidResult<string>.Success);
        }

        public Task<VoidResult<string>> RenameVoiceAsync(
            string modelRuntimeId, string voiceId, string displayName, CancellationToken cancellationToken = default) =>
            NotUsed();

        public Task<VoidResult<string>> DeleteVoiceAsync(
            string modelRuntimeId, string voiceId, CancellationToken cancellationToken = default) =>
            NotUsed();

        private static Task<VoidResult<string>> NotUsed() =>
            Task.FromResult(VoidResult<string>.AsError("not used"));

        private static DysonSpeechModelEntity Row(string id, DysonSpeechCapability capability) => new()
        {
            Id = Guid.NewGuid(),
            RuntimeId = id,
            DisplayName = id,
            Capability = capability,
            IsEnabled = true,
            IsInstalled = true,
        };
    }
}
