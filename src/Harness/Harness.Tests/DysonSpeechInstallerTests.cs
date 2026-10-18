using DysonHarness;

namespace Harness.Tests;

/// <summary>Speech installer, both engine APIs, DB seed, and push-to-talk gate. No network, no audio.</summary>
public class DysonSpeechInstallerTests
{
    private const string Sha = "0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void Manifest_rejects_gated_pocket_weights_url()
    {
        var ok = DysonSpeechManifest.Parse(ManifestJson(
            "https://huggingface.co/kyutai/pocket-tts-without-voice-cloning/resolve/00eac05ed3d16bdc3f6b5d598874019c34a89214/languages/english/model.safetensors"));
        Assert.True(ok.IsSuccess, ok.IsError ? ok.Error : null);

        var gated = DysonSpeechManifest.Parse(ManifestJson(
            "https://huggingface.co/kyutai/pocket-tts/resolve/main/languages/english/model.safetensors"));
        Assert.True(gated.IsError);
        Assert.Contains("gated", gated.Error);

        var multilingual = DysonSpeechManifest.Parse(ManifestJson(
            PocketUrl, chatterboxUrl: "https://huggingface.co/ResembleAI/chatterbox/resolve/main/t3_mtl23ls_v2.safetensors"));
        Assert.True(multilingual.IsError);
        Assert.Contains("chatterbox-turbo", multilingual.Error);

        var gradio = DysonSpeechManifest.Parse(ManifestJson(PocketUrl, qwenWheel: "gradio-6.8.0-py3-none-any.whl"));
        Assert.True(gradio.IsError);
        Assert.True(DysonSpeechManifest.Parse(ManifestJson(PocketUrl, qwenWheel: "torch-2.6.0-cp312-cp312-win_amd64.whl")).IsError);
    }

    [Fact]
    public void Embedded_manifest_loads_and_model_sites_have_no_torch()
    {
        var manifest = DysonSpeechManifest.LoadEmbedded();
        Assert.True(manifest.IsSuccess, manifest.IsError ? manifest.Error : null);
        var rid = manifest.Value.Rids[DysonSpeechInstaller.SupportedRid];

        Assert.StartsWith("torch-", rid.Torch[0].FileName);
        foreach (var wheels in new[] { rid.PhononWheels, rid.PocketWheels })
        {
            var requirements = DysonSpeechInstaller.BuildRequirements(wheels, @"C:\w");
            foreach (var line in requirements.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.DoesNotContain(@"C:\w\torch-", line, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("--hash=sha256:", line);
            }
        }

        Assert.Contains(rid.PhononWheels, w => w.FileName.StartsWith("soundfile-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(rid.PocketWheels, w => w.FileName.StartsWith("soundfile-", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(6_244_152, rid.PocketVoice.Size);
        Assert.Equal("4206b7194018c7e345e5cfd99f147caad36285df858ea153d0188c40d9f261e5", rid.PocketVoice.Sha256);
        Assert.Contains("pocket-tts-without-voice-cloning", rid.PocketWeights.Url);

        Assert.Equal("qwen-tts==0.1.1", manifest.Value.QwenPackage);
        Assert.Equal("chatterbox-tts==0.1.7", manifest.Value.ChatterboxPackage);
        Assert.Equal(2_516_100_892, rid.QwenModel.Concat(rid.QwenSpeechTokenizer).Sum(f => f.Size));
        Assert.Equal(2_987_680_596, rid.ChatterboxModel.Sum(f => f.Size));
        Assert.DoesNotContain(rid.ChatterboxModel, f => f.FileName == "s3gen.safetensors");
        Assert.All(rid.ChatterboxModel, f => Assert.Contains("huggingface.co/ResembleAI/chatterbox-turbo/", f.Url));
        foreach (var wheels in new[] { rid.QwenWheels, rid.ChatterboxWheels })
        {
            Assert.DoesNotContain(wheels, w => w.FileName.StartsWith("torch-", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(wheels, w => w.FileName.StartsWith("gradio", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(wheels, w => w.FileName.StartsWith("pykakasi", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(wheels, w => w.FileName.StartsWith("torchaudio-2.11.0+cpu", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Plan_steps_install_only_missing_models_and_never_rebuild_runtime()
    {
        const string qwen = DysonQwenSpeech.Id, pocket = DysonPocketSpeech.Id, phonon = DysonPhononSpeech.Id;
        const string chatterbox = DysonChatterboxSpeech.Id;
        var pocketOnly = new DysonSpeechInstallStatus(true, false, true, false, false, null);

        var addQwen = DysonSpeechInstaller.PlanSteps(pocketOnly, [pocket, qwen]);
        Assert.Contains("qwen-wheels-download", addQwen);
        Assert.Contains("qwen-config", addQwen);
        Assert.All(DysonSpeechInstaller.StepIds.Where(s => s.StartsWith("qwen-", StringComparison.Ordinal)), s => Assert.Contains(s, addQwen));
        Assert.DoesNotContain(addQwen, s => s.StartsWith("python-") || s.StartsWith("torch-") || s.StartsWith("pocket-") || s.StartsWith("phonon-"));
        Assert.Equal(["commit", "smoke"], addQwen.TakeLast(2));

        Assert.Equal(["preflight"], DysonSpeechInstaller.PlanSteps(pocketOnly, [pocket]));

        var fresh = DysonSpeechInstaller.PlanSteps(DysonSpeechInstallStatus.None(null), [phonon]);
        Assert.Contains("python-download", fresh);
        Assert.Contains("torch-install", fresh);
        Assert.Contains("phonon-weights-unpack", fresh);
        Assert.DoesNotContain(fresh, s => s.StartsWith("qwen-") || s.StartsWith("chatterbox-") || s.StartsWith("pocket-"));

        var withQwen = new DysonSpeechInstallStatus(true, false, true, true, false, null);
        var addTurbo = DysonSpeechInstaller.PlanSteps(withQwen, [pocket, qwen, chatterbox]);
        Assert.Equal(
            ["preflight", .. DysonSpeechInstaller.StepIds.Where(s => s.StartsWith("chatterbox-", StringComparison.Ordinal)), "licenses", "commit", "smoke"],
            addTurbo);

        var turbo = new DysonSpeechInstallStatus(true, false, false, false, true, null);
        Assert.Equal(["preflight"], DysonSpeechInstaller.PlanSteps(turbo, [chatterbox]));

        Assert.True(DysonSpeechInstaller.RequiredFreeBytes(false, [qwen]) < DysonSpeechInstaller.RequiredFreeBytes(true, [qwen]));
    }

    [Fact]
    public async Task Active_tts_persists_switches_and_never_falls_back()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        await using var _ = conn;
        var repo = DysonTempDb.Speech(accessor);
        var settings = DysonTempDb.Settings(accessor);
        var root = Path.Combine(Path.GetTempPath(), "dyson-speech-active-" + Guid.NewGuid().ToString("N"));
        var tts = new DysonActiveTextToSpeech(repo, settings, root);
        Assert.True((await repo.EnsureBuiltInAsync()).IsSuccess);
        Assert.True((await repo.MarkInstalledAsync(DysonPocketSpeech.Id, "dev")).IsSuccess);

        // Only Pocket installed and no setting: Pocket.
        Assert.Equal(DysonPocketSpeech.Id, (await tts.ResolveActiveAsync()).Value);

        // Active Qwen while Qwen is not installed: one error naming Qwen, no fallback to Pocket.
        Assert.True((await settings.SetSettingAsync(DysonAppSettingKeys.SpeechOutputModelId, DysonQwenSpeech.Id)).IsSuccess);
        var notInstalled = await FirstAsync(tts.SynthesizeAsync("Hello.", null, CancellationToken.None));
        Assert.True(notInstalled.IsError);
        Assert.Contains("Qwen3-TTS 0.6B Base is not installed", notInstalled.Error);
        Assert.True((await tts.SetActiveAsync(DysonQwenSpeech.Id)).IsError);

        Assert.True((await settings.SetSettingAsync(DysonAppSettingKeys.SpeechOutputModelId, DysonChatterboxSpeech.Id)).IsSuccess);
        var turboMissing = await FirstAsync(tts.SynthesizeAsync("Hello.", null, CancellationToken.None));
        Assert.True(turboMissing.IsError);
        Assert.Contains("Chatterbox Turbo (English only) is not installed", turboMissing.Error);

        Assert.True((await repo.MarkInstalledAsync(DysonQwenSpeech.Id, "dev")).IsSuccess);
        Assert.True((await repo.MarkInstalledAsync(DysonChatterboxSpeech.Id, "dev")).IsSuccess);

        Assert.True((await tts.SetActiveAsync(DysonQwenSpeech.Id)).IsSuccess);
        Assert.Equal(DysonQwenSpeech.Id, (await settings.GetSettingAsync(DysonAppSettingKeys.SpeechOutputModelId)).Value);
        Assert.Equal(DysonQwenSpeech.Id, (await tts.ResolveActiveAsync()).Value);
        Assert.True((await tts.SetActiveAsync(DysonPhononSpeech.Id)).IsError);

        // Routed to Qwen: only the Qwen engine says this, and it starts no process.
        var noVoice = await FirstAsync(tts.SynthesizeAsync("Hello.", null, CancellationToken.None));
        Assert.True(noVoice.IsError);
        Assert.StartsWith("No Qwen3-TTS voice yet", noVoice.Error);

        Assert.True((await tts.SetActiveAsync(DysonChatterboxSpeech.Id)).IsSuccess);
        Assert.Equal(DysonChatterboxSpeech.Id, (await settings.GetSettingAsync(DysonAppSettingKeys.SpeechOutputModelId)).Value);
        var noTurbo = await FirstAsync(tts.SynthesizeAsync("Hello.", null, CancellationToken.None));
        Assert.True(noTurbo.IsError);
        Assert.StartsWith("No Chatterbox Turbo voice", noTurbo.Error);

        // Train with Turbo active on an empty root: not installed / not .mp3, nothing stored.
        var notReady = await tts.PrepareVoiceAsync(new MemoryStream([0xFF, 0xFB]), "me.mp3");
        Assert.True(notReady.IsError);
        Assert.Contains("is not installed", notReady.Error);
        var wav = await tts.PrepareVoiceAsync(new MemoryStream(), "me.wav");
        Assert.True(wav.IsError);
        Assert.Contains(".mp3", wav.Error);
        Assert.Empty((await repo.ListVoicesAsync(DysonChatterboxSpeech.Id)).Value);
        Assert.False(Directory.Exists(Path.Combine(root, "staging")));

        // Pocket train stays unsupported.
        Assert.True((await tts.SetActiveAsync(DysonPocketSpeech.Id)).IsSuccess);
        var pocketTrain = await tts.PrepareVoiceAsync(new MemoryStream([0xFF, 0xFB]), "me.mp3");
        Assert.StartsWith(DysonSpeechVoicePrep.VoiceCloningUnsupported, pocketTrain.Error);
        Assert.Empty((await repo.ListVoicesAsync(DysonPocketSpeech.Id)).Value);
        Assert.False(Directory.Exists(root));

        // Switching uninstalled nothing.
        var rows = (await repo.ListAsync()).Value;
        Assert.All(
            rows.Where(r => r.Capability == DysonSpeechCapability.Output),
            r => Assert.True(r.IsInstalled, r.RuntimeId));
    }

    [Fact]
    public void New_voice_id_is_a_slug_and_never_collides()
    {
        Assert.Equal("my-voice", DysonSpeechBridge.NewVoiceId("My Voice!.mp3", []));
        Assert.Equal("my-voice-3", DysonSpeechBridge.NewVoiceId("my voice.mp3", ["my-voice", "MY-VOICE-2"]));
        Assert.Equal("voice", DysonSpeechBridge.NewVoiceId("éé.mp3", []));
        Assert.Matches("^[a-z0-9_-]{1,64}$", DysonSpeechBridge.NewVoiceId(new string('a', 200) + ".mp3", []));
    }

    [Fact]
    public void Install_root_is_app_data_not_install_dir()
    {
        var stable = DysonSpeechPaths.GetInstallRoot(DysonAppMode.Prod, "stable");
        Assert.Equal(Path.Combine(DysonAppPaths.GetRoot(DysonAppMode.Prod), "speech", "stable"), stable);
        Assert.False(stable.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Path.Combine("Programs", "DysonHarness"), stable, StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.StartsWith(Path.Combine(local, "DysonProd"), stable, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("preview", DysonSpeechPaths.GetVariant(DysonAppMode.Prod, "preview"));
        Assert.Equal("dev", DysonSpeechPaths.GetVariant(DysonAppMode.Prod, null));
        Assert.Equal("dev", DysonSpeechPaths.GetVariant(DysonAppMode.Dev, "stable"));
        Assert.Equal("test", DysonSpeechPaths.GetVariant(DysonAppMode.Test, null));
    }

    [Fact]
    public void Pocket_config_keeps_gated_weights_path_and_points_fallback_at_local_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-speech-cfg");
        var yaml = DysonSpeechInstaller.BuildPocketConfig(root);
        var lines = yaml.Split('\n');

        Assert.Contains(lines, l => l.StartsWith("weights_path: hf://kyutai/pocket-tts/", StringComparison.Ordinal));
        var local = DysonSpeechPaths.GetPocketModelFile(root).Replace('\\', '/');
        Assert.Contains($"weights_path_without_voice_cloning: {local}", yaml);
        Assert.Contains("tokenizer_path: " + Path.Combine(root, "pocket", "model", "tokenizer.json").Replace('\\', '/'), yaml);
        Assert.DoesNotContain("{{", yaml);
    }

    [Fact]
    public async Task EnsureBuiltIn_seeds_four_rows_once_and_voice_prep_stores_nothing()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        await using var _ = conn;
        var repo = DysonTempDb.Speech(accessor);

        Assert.True((await repo.EnsureBuiltInAsync()).IsSuccess);
        Assert.True((await repo.EnsureBuiltInAsync()).IsSuccess);
        var rows = (await repo.ListAsync()).Value;
        Assert.Equal(4, rows.Count);
        var phonon = Assert.Single(rows, r => r.RuntimeId == DysonPhononSpeech.Id);
        var pocket = Assert.Single(rows, r => r.RuntimeId == DysonPocketSpeech.Id);
        var qwen = Assert.Single(rows, r => r.RuntimeId == DysonQwenSpeech.Id);
        var turbo = Assert.Single(rows, r => r.RuntimeId == DysonChatterboxSpeech.Id);
        Assert.Equal("Qwen3-TTS 0.6B Base", qwen.DisplayName);
        Assert.Equal("Chatterbox Turbo (English only)", turbo.DisplayName);
        Assert.Equal(DysonChatterboxSpeech.DisplayName, turbo.DisplayName);
        Assert.All(new[] { qwen, turbo }, r =>
        {
            Assert.Equal(DysonSpeechCapability.Output, r.Capability);
            Assert.True(r.SupportsVoicePrompt);
        });
        Assert.Equal(DysonSpeechCapability.Input, phonon.Capability);
        Assert.Equal(DysonSpeechCapability.Output, pocket.Capability);
        Assert.False(phonon.SupportsVoicePrompt);
        Assert.True(pocket.SupportsVoicePrompt);
        Assert.False(phonon.IsInstalled || pocket.IsInstalled);

        using var mp3 = new MemoryStream([0xFF, 0xFB, 0x90, 0x00]);
        var prep = await DysonSpeechVoicePrep.PrepareAsync(mp3, "me.mp3");
        Assert.True(prep.IsError);
        Assert.StartsWith(DysonSpeechVoicePrep.VoiceCloningUnsupported, prep.Error);
        Assert.Empty((await repo.ListVoicesAsync(DysonPocketSpeech.Id)).Value);

        Assert.True((await DysonSpeechVoicePrep.PrepareAsync(new MemoryStream(), "me.wav")).IsError);
    }

    [Fact]
    public async Task Voice_resolution_defaults_to_alba_and_rejects_unknown()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        await using var _ = conn;
        var repo = DysonTempDb.Speech(accessor);
        var settings = DysonTempDb.Settings(accessor);
        var root = Path.Combine(Path.GetTempPath(), "dyson-speech-voice");

        Assert.True((await repo.EnsureBuiltInAsync()).IsSuccess);
        var missing = await DysonPocketSpeech.ResolveVoicePathAsync(repo, settings, root, null, CancellationToken.None);
        Assert.True(missing.IsError);

        Assert.True((await repo.MarkInstalledAsync(DysonPocketSpeech.Id, "dev")).IsSuccess);
        Assert.True((await repo.EnsureCatalogVoiceAsync(DysonPocketSpeech.Id, "alba", "Alba", "dev")).IsSuccess);
        Assert.True((await repo.EnsureCatalogVoiceAsync(DysonPocketSpeech.Id, "alba", "Alba", "dev")).IsSuccess);

        var alba = await DysonPocketSpeech.ResolveVoicePathAsync(repo, settings, root, null, CancellationToken.None);
        Assert.True(alba.IsSuccess, alba.IsError ? alba.Error : null);
        Assert.Equal(Path.Combine(root, "pocket", "voices", "alba.safetensors"), alba.Value);

        var unknown = await DysonPocketSpeech.ResolveVoicePathAsync(repo, settings, root, "nobody", CancellationToken.None);
        Assert.True(unknown.IsError);

        // A Qwen voice in the shared default-voice setting does not break Pocket.
        Assert.True((await repo.EnsureCatalogVoiceAsync(DysonQwenSpeech.Id, "me", "me", "dev", sourceFileName: "me.mp3")).IsSuccess);
        Assert.True((await settings.SetSettingAsync(DysonAppSettingKeys.SpeechOutputVoiceId, "me")).IsSuccess);
        var stillAlba = await DysonPocketSpeech.ResolveVoicePathAsync(repo, settings, root, null, CancellationToken.None);
        Assert.Equal(Path.Combine(root, "pocket", "voices", "alba.safetensors"), stillAlba.Value);
        Assert.Equal("me.mp3", Assert.Single((await repo.ListVoicesAsync(DysonQwenSpeech.Id)).Value).SourceFileName);

        var voice = Assert.Single((await repo.ListVoicesAsync(DysonPocketSpeech.Id)).Value);
        Assert.Null(voice.SourceFileName);
        Assert.DoesNotContain(typeof(DysonSpeechVoiceEntity).GetProperties(), p => p.Name.Contains("Path"));
        Assert.DoesNotContain(typeof(DysonSpeechModelEntity).GetProperties(), p => p.Name.Contains("Path"));

        Assert.True((await repo.MarkUninstalledAsync(DysonPocketSpeech.Id)).IsSuccess);
        Assert.Empty((await repo.ListVoicesAsync(DysonPocketSpeech.Id)).Value);
    }

    [Fact]
    public void Selection_resolves_input_and_output_separately()
    {
        var rows = new[]
        {
            Row(DysonPhononSpeech.Id, DysonSpeechCapability.Input, installed: true),
            Row(DysonPocketSpeech.Id, DysonSpeechCapability.Output, installed: true),
        };

        Assert.Equal(DysonPhononSpeech.Id, DysonSpeechModelSelection.Select(rows, DysonSpeechCapability.Input, null)?.RuntimeId);
        Assert.Equal(DysonPocketSpeech.Id, DysonSpeechModelSelection.Select(rows, DysonSpeechCapability.Output, null)?.RuntimeId);
        Assert.Equal(DysonPhononSpeech.Id, DysonSpeechModelSelection.Select(rows, DysonSpeechCapability.Input, DysonPocketSpeech.Id)?.RuntimeId);
        Assert.Null(DysonSpeechModelSelection.Select(
            [Row(DysonPhononSpeech.Id, DysonSpeechCapability.Input, installed: false)], DysonSpeechCapability.Input, null));
    }

    [Fact]
    public void Phonon_verbose_json_yields_segments_then_final()
    {
        var chunks = DysonPhononSpeech.ParseVerboseJson(
            """{"text":" hello world ","segments":[{"start":0.0,"end":0.52,"text":" hello"},{"start":0.52,"end":1.25,"text":"world"}]}""");
        Assert.True(chunks.IsSuccess);
        Assert.Collection(
            chunks.Value,
            c => Assert.Equal(("hello", 0, 520, false), (c.Text, c.StartMs, c.EndMs, c.IsFinal)),
            c => Assert.Equal(("world", 520, 1250, false), (c.Text, c.StartMs, c.EndMs, c.IsFinal)),
            c => Assert.Equal(("hello world", 0, 1250, true), (c.Text, c.StartMs, c.EndMs, c.IsFinal)));
        Assert.True(DysonPhononSpeech.ParseVerboseJson("not json").IsError);
    }

    [Fact]
    public void Push_to_talk_gate_and_caret_insert()
    {
        IReadOnlyList<DysonSpeechModelEntity> none = [];
        IReadOnlyList<DysonSpeechModelEntity> installed = [Row(DysonPhononSpeech.Id, DysonSpeechCapability.Input, installed: true)];

        Assert.Equal(DysonPushToTalkState.Hidden, DysonPushToTalkGate.Evaluate(DysonAgentModes.Work, installed, null));
        Assert.Equal(DysonPushToTalkState.NotInstalled, DysonPushToTalkGate.Evaluate(DysonAgentModes.MetaAgent, none, null));
        Assert.Equal(DysonPushToTalkState.Ready, DysonPushToTalkGate.Evaluate(DysonAgentModes.MetaAgent, installed, null));
        Assert.Equal("/settings/voice", DysonPushToTalkGate.VoiceSettingsHref);

        Assert.Equal(("ask the drone status", 13), DysonPushToTalkGate.InsertAtCaret("ask status", 3, " the drone "));
        Assert.Equal(("hi there", 8), DysonPushToTalkGate.InsertAtCaret("hi", 2, "there"));
        Assert.Equal(("there", 5), DysonPushToTalkGate.InsertAtCaret("", 99, "there"));
        Assert.Equal(("keep", 2), DysonPushToTalkGate.InsertAtCaret("keep", 2, "  "));
    }

#if WINDOWS
    [Fact]
    public void Shell_mic_grants_audio_capture_only()
    {
        Assert.Equal(
            CefSharp.MediaAccessPermissionType.AudioCapture,
            DysonShellMicPermission.GrantMedia(CefSharp.MediaAccessPermissionType.AudioCapture));
        Assert.Null(DysonShellMicPermission.GrantMedia(CefSharp.MediaAccessPermissionType.VideoCapture));
        Assert.Null(DysonShellMicPermission.GrantMedia(
            CefSharp.MediaAccessPermissionType.AudioCapture | CefSharp.MediaAccessPermissionType.VideoCapture));
        Assert.Null(DysonShellMicPermission.GrantMedia(CefSharp.MediaAccessPermissionType.DesktopAudioCapture));
        Assert.Equal(
            CefSharp.PermissionRequestResult.Accept,
            DysonShellMicPermission.GrantPrompt(CefSharp.PermissionRequestType.MicStream));
        Assert.Equal(
            CefSharp.PermissionRequestResult.Deny,
            DysonShellMicPermission.GrantPrompt(CefSharp.PermissionRequestType.MicStream | CefSharp.PermissionRequestType.CameraStream));
    }
#endif

    /// <summary>
    /// Opt-in: set <c>DYSON_SPEECH_E2E_ROOT</c> to an installed win-x64 speech tree. Skipped otherwise,
    /// so <c>dotnet test</c> never runs it.
    /// </summary>
    [SpeechBundleFact]
    public async Task Real_bundle_health_and_one_pocket_chunk()
    {
        var root = Environment.GetEnvironmentVariable(SpeechBundleFact.Variable)!;
        var health = await DysonPhononSpeech.CheckHealthAsync(root, CancellationToken.None);
        DysonPhononSpeech.StopSidecar();
        Assert.True(health.IsSuccess, health.IsError ? health.Error : null);

        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        await using var _ = conn;
        var repo = DysonTempDb.Speech(accessor);
        await repo.EnsureBuiltInAsync();
        await repo.MarkInstalledAsync(DysonPocketSpeech.Id, "dev");
        await repo.EnsureCatalogVoiceAsync(DysonPocketSpeech.Id, "alba", "Alba", "dev");
        var tts = new DysonPocketSpeech(repo, DysonTempDb.Settings(accessor), root);
        try
        {
            await foreach (var chunk in tts.SynthesizeAsync("Hello world.", null, CancellationToken.None))
            {
                Assert.True(chunk.IsSuccess, chunk.IsError ? chunk.Error : null);
                Assert.Equal(24_000, chunk.Value.SampleRate);
                if (chunk.Value.Pcm.Length > 0)
                    return;
            }

            Assert.Fail("Pocket TTS produced no PCM.");
        }
        finally
        {
            DysonPocketSpeech.StopBridge();
        }
    }

    /// <summary>
    /// Opt-in: <c>DYSON_SPEECH_E2E_ROOT</c> with Qwen installed and <c>DYSON_SPEECH_E2E_MP3</c> (a clip longer than
    /// 5 s). Enrolls the clip, then synthesizes one 24 kHz chunk with the new voice.
    /// </summary>
    [SpeechBundleFact]
    public async Task Real_bundle_qwen_enroll_and_one_chunk()
    {
        var (root, mp3) = (Environment.GetEnvironmentVariable(SpeechBundleFact.Variable)!, Environment.GetEnvironmentVariable(ClipVariable));
        if (!DysonSpeechInstaller.Probe(root).QwenInstalled || !File.Exists(mp3))
            return;

        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        await using var _ = conn;
        var repo = DysonTempDb.Speech(accessor);
        await repo.EnsureBuiltInAsync();
        await repo.MarkInstalledAsync(DysonQwenSpeech.Id, "dev");
        var tts = new DysonQwenSpeech(repo, DysonTempDb.Settings(accessor), root);
        try
        {
            await using (var clip = File.OpenRead(mp3))
            {
                var voice = await tts.EnrollVoiceAsync(clip, "e2e-qwen.mp3");
                Assert.True(voice.IsSuccess, voice.IsError ? voice.Error : null);
            }

            await AssertOneChunkAsync(tts.SynthesizeAsync("Hello from Qwen.", null, CancellationToken.None));
        }
        finally
        {
            DysonQwenSpeech.StopBridge();
        }
    }

    /// <summary>
    /// Opt-in: <c>DYSON_SPEECH_E2E_ROOT</c> with Chatterbox Turbo installed. One 24 kHz chunk with <c>turbo</c>;
    /// when <c>DYSON_SPEECH_E2E_SHORT_MP3</c> names a clip of 5 s or less, enrolling it is an error with no row.
    /// </summary>
    [SpeechBundleFact]
    public async Task Real_bundle_chatterbox_turbo_one_chunk_and_short_clip_error()
    {
        var root = Environment.GetEnvironmentVariable(SpeechBundleFact.Variable)!;
        if (!DysonSpeechInstaller.Probe(root).ChatterboxInstalled)
            return;

        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        await using var _ = conn;
        var repo = DysonTempDb.Speech(accessor);
        await repo.EnsureBuiltInAsync();
        await repo.MarkInstalledAsync(DysonChatterboxSpeech.Id, "dev");
        await repo.EnsureCatalogVoiceAsync(DysonChatterboxSpeech.Id, DysonChatterboxSpeech.CatalogVoiceId, "Turbo default", "dev");
        var tts = new DysonChatterboxSpeech(repo, DysonTempDb.Settings(accessor), root);
        try
        {
            await AssertOneChunkAsync(tts.SynthesizeAsync("Hello from Chatterbox Turbo.", null, CancellationToken.None));

            var shortClip = Environment.GetEnvironmentVariable(ShortClipVariable);
            if (File.Exists(shortClip))
            {
                await using var clip = File.OpenRead(shortClip);
                var voice = await tts.EnrollVoiceAsync(clip, "e2e-short.mp3");
                Assert.True(voice.IsError);
                Assert.Contains("longer than 5 seconds", voice.Error);
                Assert.Single((await repo.ListVoicesAsync(DysonChatterboxSpeech.Id)).Value);
            }
        }
        finally
        {
            DysonChatterboxSpeech.StopBridge();
        }
    }

    private const string ClipVariable = "DYSON_SPEECH_E2E_MP3";
    private const string ShortClipVariable = "DYSON_SPEECH_E2E_SHORT_MP3";

    private static async Task AssertOneChunkAsync(IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> stream)
    {
        await foreach (var chunk in stream)
        {
            Assert.True(chunk.IsSuccess, chunk.IsError ? chunk.Error : null);
            Assert.Equal(24_000, chunk.Value.SampleRate);
            if (chunk.Value.Pcm.Length > 0)
                return;
        }

        Assert.Fail("No PCM.");
    }

    private sealed class SpeechBundleFact : FactAttribute
    {
        public const string Variable = "DYSON_SPEECH_E2E_ROOT";

        public SpeechBundleFact()
        {
            var root = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(root) || !File.Exists(DysonSpeechPaths.GetPythonExe(root)))
                Skip = $"Set {Variable} to an installed win-x64 speech tree to run.";
        }
    }

    private static DysonSpeechModelEntity Row(string id, DysonSpeechCapability capability, bool installed) => new()
    {
        RuntimeId = id,
        Capability = capability,
        IsEnabled = true,
        IsInstalled = installed,
        SupportsVoicePrompt = capability == DysonSpeechCapability.Output,
    };

    private static async Task<Result<DysonSpeechAudioChunk, string>> FirstAsync(
        IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> stream)
    {
        await foreach (var item in stream)
            return item;
        throw new InvalidOperationException("Synthesis yielded nothing.");
    }

    private const string PocketUrl =
        "https://huggingface.co/kyutai/pocket-tts-without-voice-cloning/resolve/00eac05ed3d16bdc3f6b5d598874019c34a89214/languages/english/model.safetensors";

    private static string ManifestJson(
        string pocketWeightsUrl,
        string chatterboxUrl = "https://huggingface.co/ResembleAI/chatterbox-turbo/resolve/749d1c1a46eb10492095d68fbcf55691ccf137cd/conds.pt",
        string qwenWheel = "qwen_tts-0.1.1-py3-none-any.whl")
    {
        string File(string name, string url = "https://example.test/f") =>
            $$"""{"fileName":"{{name}}","url":"{{url}}","size":1,"sha256":"{{Sha}}"}""";

        return $$"""
            {"schemaVersion":1,"pythonVersion":"3.12.15","torchRequirement":"torch==2.14.1+cpu",
             "phononPackage":"fermion-research==0.2.11","pocketPackage":"pocket-tts==3.3.0",
             "qwenPackage":"qwen-tts==0.1.1","chatterboxPackage":"chatterbox-tts==0.1.7",
             "rids":{"win-x64":{
               "qwenWheels":[{{File(qwenWheel)}}],
               "qwenModel":[{{File("model.safetensors")}}],
               "qwenSpeechTokenizer":[{{File("model.safetensors")}}],
               "chatterboxWheels":[{{File("chatterbox_tts-0.1.7-py3-none-any.whl")}}],
               "chatterboxModel":[{{File("conds.pt", chatterboxUrl)}}],
               "python":{{File("python.tar.gz")}},
               "torch":[{{File("torch-2.14.1+cpu-cp312-cp312-win_amd64.whl")}}],
               "phononWheels":[{{File("soundfile-0.13.1-py2.py3-none-win_amd64.whl")}}],
               "pocketWheels":[{{File("pocket_tts-3.3.0-py3-none-any.whl")}}],
               "phononWeights":{{File("phonon-2.bps.tar.zst")}},
               "pocketWeights":{{File("model.safetensors", pocketWeightsUrl)}},
               "pocketTokenizer":{{File("tokenizer.json")}},
               "pocketVoice":{{File("alba.safetensors")}}
             } } }
            """;
    }
}
