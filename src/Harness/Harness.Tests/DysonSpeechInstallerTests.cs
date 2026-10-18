using System.Security.Cryptography;
using System.Text.Json;
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
        Assert.All(
            DysonSpeechInstaller.StepIds.Where(s => s.StartsWith("qwen-", StringComparison.Ordinal) && !s.StartsWith("qwen-gguf-", StringComparison.Ordinal)),
            s => Assert.Contains(s, addQwen));
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
            [
                "preflight",
                .. DysonSpeechInstaller.StepIds.Where(s =>
                    s.StartsWith("chatterbox-", StringComparison.Ordinal) && !s.StartsWith("chatterbox-gguf-", StringComparison.Ordinal)),
                "licenses", "commit", "smoke",
            ],
            addTurbo);

        var turbo = new DysonSpeechInstallStatus(true, false, false, false, true, null);
        Assert.Equal(["preflight"], DysonSpeechInstaller.PlanSteps(turbo, [chatterbox]));

        Assert.True(DysonSpeechInstaller.RequiredFreeBytes(false, [qwen]) < DysonSpeechInstaller.RequiredFreeBytes(true, [qwen]));
    }

    [Fact]
    public void Plan_steps_for_gguf_skip_python_and_share_crispasr()
    {
        var fresh = DysonSpeechInstallStatus.None(null);
        var nemotron = DysonSpeechInstaller.PlanSteps(fresh, [DysonGgufSpeechModels.NemotronId]);
        Assert.Equal(
            ["preflight", "nemotron-download", "nemotron-hash", "nemotron-unpack", "licenses", "commit", "smoke"],
            nemotron);
        Assert.DoesNotContain(
            nemotron,
            s => s.StartsWith("python-", StringComparison.Ordinal)
                || s.StartsWith("torch-", StringComparison.Ordinal)
                || s.StartsWith("crispasr-", StringComparison.Ordinal)
                || s.StartsWith("phonon", StringComparison.Ordinal)
                || s.StartsWith("pocket", StringComparison.Ordinal)
                || s.StartsWith("qwen", StringComparison.Ordinal)
                || s.StartsWith("chatterbox", StringComparison.Ordinal));

        var pocket = DysonSpeechInstaller.PlanSteps(fresh, [DysonGgufSpeechModels.PocketGgufId]);
        Assert.Equal(
            [
                "preflight", "crispasr-download", "crispasr-hash", "crispasr-extract",
                "pocket-gguf-download", "pocket-gguf-hash", "pocket-gguf-unpack",
                "licenses", "commit", "smoke",
            ],
            pocket);
        Assert.DoesNotContain(pocket, s => s.StartsWith("python-", StringComparison.Ordinal) || s.StartsWith("torch-", StringComparison.Ordinal));

        var crispPresent = new DysonSpeechInstallStatus(
            false, false, false, false, false, null, GgufInstalled: [DysonGgufSpeechModels.PocketGgufId]);
        var qwen = DysonSpeechInstaller.PlanSteps(crispPresent, [DysonGgufSpeechModels.QwenGgufId]);
        Assert.DoesNotContain(qwen, s => s.StartsWith("crispasr-", StringComparison.Ordinal));
        Assert.Contains("qwen-gguf-download", qwen);
        Assert.Contains("qwen-gguf-unpack", qwen);
        Assert.DoesNotContain(qwen, s => s.StartsWith("python-", StringComparison.Ordinal));

        Assert.Equal(["preflight"], DysonSpeechInstaller.PlanSteps(crispPresent, [DysonGgufSpeechModels.PocketGgufId]));
    }

    [Fact]
    public void Embedded_manifest_pins_gguf_downloads()
    {
        var manifest = DysonSpeechManifest.LoadEmbedded();
        Assert.True(manifest.IsSuccess, manifest.IsError ? manifest.Error : null);
        var rid = manifest.Value.Rids[DysonSpeechInstaller.SupportedRid];
        DysonSpeechFile[] singles =
        [
            rid.NemotronNative!, rid.NemotronWeights!, rid.CrispasrRuntime!, rid.PhononGguf!, rid.PocketGguf!,
        ];
        Assert.All(singles, f => Assert.StartsWith("https://", f.Url));
        Assert.Equal(2, rid.QwenGguf!.Count);
        Assert.Equal(2, rid.ChatterboxGguf!.Count);
        Assert.All(rid.QwenGguf.Concat(rid.ChatterboxGguf), f => Assert.StartsWith("https://", f.Url));
        Assert.Equal(18_036_555, rid.NemotronNative.Size);
        Assert.Equal(559_647_200, rid.NemotronWeights.Size);
        Assert.DoesNotContain(rid.ChatterboxGguf, f => f.Url.Contains("ResembleAI", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Probe_gguf_only_tree_reports_installed_without_python()
    {
        if (!DysonSpeechInstaller.IsSupportedPlatform())
            return;

        var manifest = DysonSpeechManifest.LoadEmbedded();
        Assert.True(manifest.IsSuccess, manifest.IsError ? manifest.Error : null);
        var rid = manifest.Value.Rids[DysonSpeechInstaller.SupportedRid];
        var root = Path.Combine(Path.GetTempPath(), "dyson-speech-gguf-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = DysonGgufSpeechModels.PocketGgufId;
            Directory.CreateDirectory(DysonGgufSpeechModels.GetModelDir(root, id));
            File.WriteAllText(DysonGgufSpeechModels.GetMainModelFile(root, id), "");
            Directory.CreateDirectory(DysonGgufSpeechModels.GetCrispRuntimeDir(root));
            File.WriteAllText(DysonGgufSpeechModels.GetCrispExe(root), "");
            var stamp = new Dictionary<string, string>
            {
                ["crispasr"] = rid.CrispasrRuntime!.Sha256,
                [id] = rid.PocketGguf!.Sha256,
            };
            File.WriteAllText(Path.Combine(root, "install-gguf.json"), JsonSerializer.Serialize(stamp));

            var status = DysonSpeechInstaller.Probe(root);
            Assert.False(status.RuntimeInstalled);
            Assert.False(status.PhononInstalled);
            Assert.True(status.IsInstalled(id));
            Assert.False(status.IsInstalled(DysonGgufSpeechModels.QwenGgufId));
            Assert.False(status.IsInstalled(DysonGgufSpeechModels.NemotronId));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
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
        Assert.Contains("Qwen3-TTS 0.6B Base · safetensors (Python) is not installed", notInstalled.Error);
        Assert.True((await tts.SetActiveAsync(DysonQwenSpeech.Id)).IsError);

        Assert.True((await settings.SetSettingAsync(DysonAppSettingKeys.SpeechOutputModelId, DysonChatterboxSpeech.Id)).IsSuccess);
        var turboMissing = await FirstAsync(tts.SynthesizeAsync("Hello.", null, CancellationToken.None));
        Assert.True(turboMissing.IsError);
        Assert.Contains("Chatterbox Turbo (English only) · safetensors (Python) is not installed", turboMissing.Error);

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
            rows.Where(r => r.RuntimeId is DysonPocketSpeech.Id or DysonQwenSpeech.Id or DysonChatterboxSpeech.Id),
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
    public async Task EnsureBuiltIn_seeds_built_in_rows_once_and_voice_prep_stores_nothing()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        await using var _ = conn;
        var repo = DysonTempDb.Speech(accessor);

        Assert.True((await repo.EnsureBuiltInAsync()).IsSuccess);
        Assert.True((await repo.EnsureBuiltInAsync()).IsSuccess);
        var rows = (await repo.ListAsync()).Value;
        Assert.Equal(9, rows.Count);
        var phonon = Assert.Single(rows, r => r.RuntimeId == DysonPhononSpeech.Id);
        var pocket = Assert.Single(rows, r => r.RuntimeId == DysonPocketSpeech.Id);
        var qwen = Assert.Single(rows, r => r.RuntimeId == DysonQwenSpeech.Id);
        var turbo = Assert.Single(rows, r => r.RuntimeId == DysonChatterboxSpeech.Id);
        Assert.Equal("Phonon 2 \u00b7 BPS packed (Python)", phonon.DisplayName);
        Assert.Equal("Pocket TTS \u00b7 safetensors, no voice cloning (Python)", pocket.DisplayName);
        Assert.Equal("Qwen3-TTS 0.6B Base \u00b7 safetensors (Python)", qwen.DisplayName);
        Assert.Equal("Chatterbox Turbo (English only) \u00b7 safetensors (Python)", turbo.DisplayName);
        var nemotron = Assert.Single(rows, r => r.RuntimeId == DysonGgufSpeechModels.NemotronId);
        Assert.Equal(DysonGgufSpeechModels.NemotronDisplayName, nemotron.DisplayName);
        Assert.Equal(DysonSpeechCapability.Input, nemotron.Capability);
        Assert.Equal("gguf", nemotron.RuntimeKind);
        Assert.False(nemotron.SupportsVoicePrompt);
        Assert.Equal(DysonSpeechCapability.Input, rows.Single(r => r.RuntimeId == DysonGgufSpeechModels.PhononGgufId).Capability);
        Assert.All(
            new[] { DysonGgufSpeechModels.PocketGgufId, DysonGgufSpeechModels.QwenGgufId, DysonGgufSpeechModels.ChatterboxGgufId }
                .Select(id => rows.Single(r => r.RuntimeId == id)),
            r =>
            {
                Assert.Equal(DysonSpeechCapability.Output, r.Capability);
                Assert.Equal("gguf", r.RuntimeKind);
                Assert.True(r.SupportsVoicePrompt);
                Assert.False(r.IsInstalled);
            });

        await accessor.RunAsync(async (db, ct) =>
        {
            var row = db.SpeechModels.Single(m => m.RuntimeId == DysonPhononSpeech.Id);
            row.DisplayName = "Phonon 2";
            await db.SaveChangesAsync(ct);
        });
        Assert.True((await repo.EnsureBuiltInAsync()).IsSuccess);
        Assert.Equal(
            "Phonon 2 \u00b7 BPS packed (Python)",
            (await repo.ListAsync()).Value.Single(r => r.RuntimeId == DysonPhononSpeech.Id).DisplayName);
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

    [Fact]
    public void Adopt_present_torch_gguf_and_safetensors_writes_stamp_and_skips_download()
    {
        var weights = new byte[] { 1, 2, 3, 4 };
        var tokenizer = new byte[] { 5, 6, 7 };
        var gguf = new byte[] { 8, 9 };
        var manifest = AdoptManifest(weights, tokenizer, gguf);
        var rid = manifest.Rids[DysonSpeechInstaller.SupportedRid];
        var root = NewSpeechRoot();
        try
        {
            TorchAndPython(root);
            WriteBytes(DysonSpeechPaths.GetPocketModelFile(root), weights);
            WriteBytes(Path.Combine(root, "pocket", "model", "tokenizer.json"), tokenizer);
            Directory.CreateDirectory(Path.Combine(DysonSpeechPaths.GetPocketSite(root), "pocket_tts-3.3.0.dist-info"));
            var ggufPath = DysonGgufSpeechModels.GetMainModelFile(root, DysonGgufSpeechModels.PocketGgufId);
            WriteBytes(ggufPath, gguf);

            var ids = new[] { DysonPocketSpeech.Id, DysonGgufSpeechModels.PocketGgufId };
            var adopt = DysonSpeechInstaller.Adopt(root, manifest, DysonSpeechInstallStatus.None(null), ids);
            var plan = DysonSpeechInstaller.PlanSteps(adopt.Status, ids, adopt);

            Assert.Equal(["preflight"], plan);
            Assert.DoesNotContain(plan, s => s.Contains("download", StringComparison.Ordinal));
            using var stampJson = JsonDocument.Parse(File.ReadAllText(DysonSpeechPaths.GetInstallJson(root)));
            var stamp = stampJson.RootElement;
            Assert.Equal("pocket-tts==3.3.0", stamp.GetProperty("PocketPackage").GetString());
            Assert.Equal("torch==2.14.1+cpu", stamp.GetProperty("TorchRequirement").GetString());
            var ggufStamp = File.ReadAllText(Path.Combine(root, "install-gguf.json"));
            Assert.Contains(rid.PocketGguf!.Sha256, ggufStamp, StringComparison.Ordinal);
            Assert.Contains(rid.CrispasrRuntime!.Sha256, ggufStamp, StringComparison.Ordinal);
            Assert.Equal(weights.Length, new FileInfo(DysonSpeechPaths.GetPocketModelFile(root)).Length);
            Assert.Equal(gguf.Length, new FileInfo(ggufPath).Length);
        }
        finally
        {
            DeleteSpeechRoot(root);
        }
    }

    [Fact]
    public void Adopt_wrong_size_is_not_adopted()
    {
        var weights = new byte[] { 1, 2, 3, 4 };
        var tokenizer = new byte[] { 5, 6, 7 };
        var gguf = new byte[] { 8, 9 };
        var manifest = AdoptManifest(weights, tokenizer, gguf);
        var root = NewSpeechRoot();
        try
        {
            TorchAndPython(root);
            var badWeights = new byte[] { 1, 2, 3 };
            var badGguf = new byte[] { 8 };
            var model = DysonSpeechPaths.GetPocketModelFile(root);
            var ggufPath = DysonGgufSpeechModels.GetMainModelFile(root, DysonGgufSpeechModels.PocketGgufId);
            WriteBytes(model, badWeights);
            WriteBytes(Path.Combine(root, "pocket", "model", "tokenizer.json"), tokenizer);
            Directory.CreateDirectory(Path.Combine(DysonSpeechPaths.GetPocketSite(root), "pocket_tts-3.3.0.dist-info"));
            WriteBytes(ggufPath, badGguf);

            var ids = new[] { DysonPocketSpeech.Id, DysonGgufSpeechModels.PocketGgufId };
            var adopt = DysonSpeechInstaller.Adopt(root, manifest, DysonSpeechInstallStatus.None(null), ids);
            var plan = DysonSpeechInstaller.PlanSteps(adopt.Status, ids, adopt);

            Assert.False(File.Exists(DysonSpeechPaths.GetInstallJson(root)));
            Assert.False(File.Exists(Path.Combine(root, "install-gguf.json")));
            Assert.Contains("pocket-weights-download", plan);
            Assert.Contains("pocket-gguf-download", plan);
            Assert.Equal(badWeights, File.ReadAllBytes(model));
            Assert.Equal(badGguf, File.ReadAllBytes(ggufPath));
        }
        finally
        {
            DeleteSpeechRoot(root);
        }
    }

    [Fact]
    public void Adopt_matching_weights_missing_site_plans_pip_only()
    {
        var weights = new byte[] { 1, 2, 3, 4 };
        var tokenizer = new byte[] { 5, 6, 7 };
        var manifest = AdoptManifest(weights, tokenizer, [8, 9]);
        var root = NewSpeechRoot();
        try
        {
            TorchAndPython(root);
            var model = DysonSpeechPaths.GetPocketModelFile(root);
            WriteBytes(model, weights);
            WriteBytes(Path.Combine(root, "pocket", "model", "tokenizer.json"), tokenizer);

            var ids = new[] { DysonPocketSpeech.Id };
            var adopt = DysonSpeechInstaller.Adopt(root, manifest, DysonSpeechInstallStatus.None(null), ids);
            var plan = DysonSpeechInstaller.PlanSteps(adopt.Status, ids, adopt);

            Assert.Contains("pocket-wheels-download", plan);
            Assert.Contains("pocket-wheels-install", plan);
            Assert.DoesNotContain("pocket-weights-download", plan);
            Assert.DoesNotContain("torch-download", plan);
            Assert.False(File.Exists(DysonSpeechPaths.GetInstallJson(root)));
            Assert.Equal(weights, File.ReadAllBytes(model));
        }
        finally
        {
            DeleteSpeechRoot(root);
        }
    }

    private static string NewSpeechRoot() =>
        Path.Combine(Path.GetTempPath(), "dyson-speech-adopt-" + Guid.NewGuid().ToString("N"));

    private static void DeleteSpeechRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static void TorchAndPython(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "torch", "torch"));
        Directory.CreateDirectory(Path.Combine(root, "torch", "torch-2.14.1+cpu.dist-info"));
        WriteBytes(DysonSpeechPaths.GetPythonExe(root), []);
        WriteBytes(DysonGgufSpeechModels.GetCrispExe(root), []);
    }

    private static DysonSpeechManifest AdoptManifest(byte[] weights, byte[] tokenizer, byte[] gguf)
    {
        static string Entry(string name, byte[] bytes, string url) =>
            $$"""{"fileName":"{{name}}","url":"{{url}}","size":{{bytes.Length}},"sha256":"{{Convert.ToHexStringLower(SHA256.HashData(bytes))}}"}""";

        var one = new byte[] { 1 };
        var json = $$"""
            {"schemaVersion":1,"pythonVersion":"3.12.15","torchRequirement":"torch==2.14.1+cpu",
             "phononPackage":"fermion-research==0.2.11","pocketPackage":"pocket-tts==3.3.0",
             "qwenPackage":"qwen-tts==0.1.1","chatterboxPackage":"chatterbox-tts==0.1.7",
             "rids":{"win-x64":{
               "qwenWheels":[{{Entry("qwen_tts-0.1.1-py3-none-any.whl", one, "https://example.test/qwen.whl")}}],
               "qwenModel":[{{Entry("model.safetensors", one, "https://example.test/qwen-model")}}],
               "qwenSpeechTokenizer":[{{Entry("model.safetensors", one, "https://example.test/qwen-tok")}}],
               "chatterboxWheels":[{{Entry("chatterbox_tts-0.1.7-py3-none-any.whl", one, "https://example.test/cb.whl")}}],
               "chatterboxModel":[{{Entry("conds.pt", one, "https://huggingface.co/ResembleAI/chatterbox-turbo/resolve/main/conds.pt")}}],
               "python":{{Entry("python.tar.gz", one, "https://example.test/python.tar.gz")}},
               "torch":[{{Entry("torch-2.14.1+cpu-cp312-cp312-win_amd64.whl", one, "https://example.test/torch.whl")}}],
               "phononWheels":[{{Entry("soundfile-0.13.1-py2.py3-none-win_amd64.whl", one, "https://example.test/sf.whl")}}],
               "pocketWheels":[{{Entry("pocket_tts-3.3.0-py3-none-any.whl", one, "https://example.test/pocket.whl")}}],
               "phononWeights":{{Entry("phonon-2.bps.tar.zst", one, "https://example.test/phonon.zst")}},
               "pocketWeights":{{Entry("model.safetensors", weights, "https://huggingface.co/kyutai/pocket-tts-without-voice-cloning/resolve/00eac05ed3d16bdc3f6b5d598874019c34a89214/languages/english/model.safetensors")}},
               "pocketTokenizer":{{Entry("tokenizer.json", tokenizer, "https://example.test/tokenizer.json")}},
               "pocketVoice":{{Entry("alba.safetensors", one, "https://example.test/alba.safetensors")}},
               "crispasrRuntime":{{Entry("crispasr.zip", one, "https://example.test/crispasr.zip")}},
               "pocketGguf":{{Entry("pocket-tts-english-q4_k.gguf", gguf, "https://example.test/pocket.gguf")}}
             } } }
            """;
        var parsed = DysonSpeechManifest.Parse(json);
        Assert.True(parsed.IsSuccess, parsed.IsError ? parsed.Error : null);
        return parsed.Value;
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
