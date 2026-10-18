using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using DysonHarness;

namespace Harness.Tests;

/// <summary>Active STT/TTS routing and GGUF enrollment guards. No network, no native DLL, no process.</summary>
public class DysonActiveSpeechTests
{
    [Fact]
    public async Task Active_stt_preference_never_falls_back_and_live_pcm_is_nemotron_only()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        await using var _ = conn;
        var repo = DysonTempDb.Speech(accessor);
        var settings = DysonTempDb.Settings(accessor);
        var stt = new DysonActiveSpeechToText(repo, settings, Path.GetTempPath());
        Assert.True((await repo.EnsureBuiltInAsync()).IsSuccess);
        Assert.True((await repo.MarkInstalledAsync(DysonPhononSpeech.Id, "dev")).IsSuccess);
        await EnsureRowAsync(accessor, repo, DysonGgufSpeechModels.NemotronId, DysonGgufSpeechModels.NemotronDisplayName, DysonSpeechCapability.Input);
        Assert.True((await repo.MarkUninstalledAsync(DysonGgufSpeechModels.NemotronId)).IsSuccess);

        var nemotronName = (await repo.ListAsync()).Value
            .Single(r => r.RuntimeId == DysonGgufSpeechModels.NemotronId).DisplayName;
        Assert.True((await settings.SetSettingAsync(DysonAppSettingKeys.SpeechInputModelId, DysonGgufSpeechModels.NemotronId)).IsSuccess);
        var blocked = await stt.ResolveActiveAsync();
        Assert.True(blocked.IsError);
        Assert.Contains(nemotronName, blocked.Error);
        Assert.Contains("not installed", blocked.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True((await stt.SetActiveAsync(DysonGgufSpeechModels.NemotronId)).IsError);

        Assert.True((await stt.SetActiveAsync(DysonPhononSpeech.Id)).IsSuccess);
        Assert.Equal(DysonPhononSpeech.Id, (await stt.ResolveActiveAsync()).Value);

        Assert.True((await settings.SetSettingAsync(DysonAppSettingKeys.SpeechInputModelId, null)).IsSuccess);
        Assert.Equal(DysonPhononSpeech.Id, (await stt.ResolveActiveAsync()).Value);

        Assert.True((await repo.MarkInstalledAsync(DysonGgufSpeechModels.NemotronId, "dev")).IsSuccess);
        var several = await stt.ResolveActiveAsync();
        Assert.True(several.IsError);
        Assert.Equal("Choose the active speech-to-text model in Settings → Voice.", several.Error);

        Assert.True((await stt.SetActiveAsync(DysonGgufSpeechModels.NemotronId)).IsSuccess);
        Assert.True((await stt.UsesLivePcmAsync(CancellationToken.None)).Value);
        Assert.True((await stt.SetActiveAsync(DysonPhononSpeech.Id)).IsSuccess);
        Assert.False((await stt.UsesLivePcmAsync(CancellationToken.None)).Value);
    }

    [Fact]
    public async Task Active_tts_gguf_preference_not_installed_names_the_model()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        await using var _ = conn;
        var repo = DysonTempDb.Speech(accessor);
        var settings = DysonTempDb.Settings(accessor);
        var root = Path.Combine(Path.GetTempPath(), "dyson-speech-active-gguf-" + Guid.NewGuid().ToString("N"));
        var tts = new DysonActiveTextToSpeech(repo, settings, root);
        Assert.True((await repo.EnsureBuiltInAsync()).IsSuccess);
        Assert.True((await repo.MarkInstalledAsync(DysonPocketSpeech.Id, "dev")).IsSuccess);
        await EnsureRowAsync(accessor, repo, DysonGgufSpeechModels.PocketGgufId, DysonGgufSpeechModels.PocketGgufDisplayName, DysonSpeechCapability.Output);
        Assert.True((await repo.MarkUninstalledAsync(DysonGgufSpeechModels.PocketGgufId)).IsSuccess);

        var name = (await repo.ListAsync()).Value
            .Single(r => r.RuntimeId == DysonGgufSpeechModels.PocketGgufId).DisplayName;
        Assert.True((await settings.SetSettingAsync(DysonAppSettingKeys.SpeechOutputModelId, DysonGgufSpeechModels.PocketGgufId)).IsSuccess);
        var blocked = await tts.ResolveActiveAsync();
        Assert.True(blocked.IsError);
        Assert.Contains(name, blocked.Error);
        Assert.Contains("not installed", blocked.Error, StringComparison.OrdinalIgnoreCase);

        var spoken = await FirstAsync(tts.SynthesizeAsync("Hello.", null, CancellationToken.None));
        Assert.True(spoken.IsError);
        Assert.Contains(name, spoken.Error);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Gguf_enroll_rejects_mp3_bad_wav_and_qwen_without_transcript()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        await using var _ = conn;
        var repo = DysonTempDb.Speech(accessor);
        var settings = DysonTempDb.Settings(accessor);
        var root = Path.Combine(Path.GetTempPath(), "dyson-crisp-enroll-" + Guid.NewGuid().ToString("N"));
        var pocket = new DysonCrispSpeech(repo, settings, root, DysonGgufSpeechModels.PocketGgufId);
        var qwen = new DysonCrispSpeech(repo, settings, root, DysonGgufSpeechModels.QwenGgufId);

        var mp3 = await pocket.EnrollVoiceAsync(new MemoryStream([0xFF, 0xFB]), "me.mp3", null, CancellationToken.None);
        Assert.Equal("GGUF voices take a .wav clip (16-bit PCM).", mp3.Error);

        var junk = await qwen.EnrollVoiceAsync(new MemoryStream(new byte[64]), "clip.wav", "hello", CancellationToken.None);
        Assert.Equal("GGUF voices take a .wav clip (16-bit PCM).", junk.Error);

        var noText = await qwen.EnrollVoiceAsync(new MemoryStream(Pcm16Wav()), "me.wav", null, CancellationToken.None);
        Assert.Equal("Qwen3-TTS GGUF needs the exact transcript of the clip.", noText.Error);
        var blank = await qwen.EnrollVoiceAsync(new MemoryStream(Pcm16Wav()), "me.wav", "  ", CancellationToken.None);
        Assert.Equal("Qwen3-TTS GGUF needs the exact transcript of the clip.", blank.Error);

        var notInstalled = await qwen.EnrollVoiceAsync(new MemoryStream(Pcm16Wav()), "me.wav", "hello there", CancellationToken.None);
        Assert.True(notInstalled.IsError);
        Assert.Contains(DysonGgufSpeechModels.QwenGgufDisplayName, notInstalled.Error);
        Assert.Contains("not installed", notInstalled.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Qwen_enroll_with_transcript_stores_24k_wav_and_txt()
    {
        if (!DysonSpeechInstaller.IsSupportedPlatform())
            return;

        var manifest = DysonSpeechManifest.LoadEmbedded();
        Assert.True(manifest.IsSuccess, manifest.IsError ? manifest.Error : null);
        var rid = manifest.Value.Rids[DysonSpeechInstaller.SupportedRid];
        var root = Path.Combine(Path.GetTempPath(), "dyson-qwen-enroll-" + Guid.NewGuid().ToString("N"));
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        await using var _ = conn;
        try
        {
            var id = DysonGgufSpeechModels.QwenGgufId;
            Directory.CreateDirectory(DysonGgufSpeechModels.GetModelDir(root, id));
            File.WriteAllText(DysonGgufSpeechModels.GetMainModelFile(root, id), "");
            File.WriteAllText(DysonGgufSpeechModels.GetCompanionFile(root, id)!, "");
            Directory.CreateDirectory(DysonGgufSpeechModels.GetCrispRuntimeDir(root));
            File.WriteAllText(DysonGgufSpeechModels.GetCrispExe(root), "");
            var pin = rid.QwenGguf!.First(f => f.FileName == DysonGgufSpeechModels.QwenGgufFile).Sha256;
            File.WriteAllText(
                Path.Combine(root, "install-gguf.json"),
                JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["crispasr"] = rid.CrispasrRuntime!.Sha256,
                    [id] = pin,
                }));

            var repo = DysonTempDb.Speech(accessor);
            var settings = DysonTempDb.Settings(accessor);
            Assert.True((await repo.EnsureBuiltInAsync()).IsSuccess);
            var qwen = new DysonCrispSpeech(repo, settings, root, id);
            const string transcript = "the birch canoe slid on the smooth planks";
            var enrolled = await qwen.EnrollVoiceAsync(
                new MemoryStream(Pcm16Wav()), "recorded.wav", transcript, CancellationToken.None);
            Assert.True(enrolled.IsSuccess, enrolled.IsError ? enrolled.Error : null);

            var voiceId = enrolled.Value.VoiceId;
            var dir = DysonGgufSpeechModels.GetVoicesDir(root, id);
            var wav = File.ReadAllBytes(Path.Combine(dir, voiceId + ".wav"));
            Assert.Equal(24_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
            Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)));
            Assert.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)));
            Assert.Equal(transcript, await File.ReadAllTextAsync(Path.Combine(dir, voiceId + ".txt")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Qwen_prompt_wav_is_24k_mono_so_a_48k_clip_is_not_sent_through()
    {
        // 48 kHz stereo with a LIST chunk (the shape that made CrispASR return empty audio).
        var source = InsertBeforeData(Pcm16Wav(48_000, [0, 0, 4000, 4000], [0, 0, 4000, 4000]), "LIST"u8, [1, 2, 3, 4]);
        var prompt = DysonCrispSpeech.ToQwenPromptWav(source);
        Assert.True(prompt.IsSuccess);
        AssertPcm16(prompt.Value, 24_000, 1, [0, 4000]);

        var dir = Path.Combine(Path.GetTempPath(), "dyson-qwen-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var readyPath = Path.Combine(dir, "ready.wav");
            var ready = InsertBeforeData(Pcm16Wav(24_000, [100, -100], null), "LIST"u8, [9, 9, 9, 9]);
            File.WriteAllBytes(readyPath, ready);
            Assert.True(DysonCrispSpeech.EnsureQwenPromptFile(readyPath).IsSuccess);
            Assert.Equal(ready, File.ReadAllBytes(readyPath));

            var rawPath = Path.Combine(dir, "raw.wav");
            File.WriteAllBytes(rawPath, source);
            Assert.True(DysonCrispSpeech.EnsureQwenPromptFile(rawPath).IsSuccess);
            AssertPcm16(File.ReadAllBytes(rawPath), 24_000, 1, [0, 4000]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task EnsureRowAsync(
        DysonDbAccessor accessor,
        DysonSpeechModelRepository repo,
        string runtimeId,
        string displayName,
        DysonSpeechCapability capability)
    {
        if ((await repo.ListAsync()).Value.Any(r => r.RuntimeId == runtimeId))
            return;

        var now = DateTime.UtcNow;
        await accessor.RunAsync(async (db, ct) =>
        {
            db.SpeechModels.Add(new DysonSpeechModelEntity
            {
                Id = Guid.NewGuid(),
                SubjectId = DysonSubjects.Local,
                RuntimeId = runtimeId,
                DisplayName = displayName,
                Capability = capability,
                RuntimeKind = "gguf",
                SupportsVoicePrompt = capability.HasFlag(DysonSpeechCapability.Output),
                IsEnabled = true,
                IsInstalled = false,
                CreatedUtc = now,
                UpdatedUtc = now,
            });
            await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
        });
    }

    private static byte[] Pcm16Wav()
    {
        var wav = new byte[48];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 16_000);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 32_000);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), 4);
        return wav;
    }

    private static byte[] Pcm16Wav(int rate, short[] left, short[]? right)
    {
        var channels = right is null ? 1 : 2;
        var frames = left.Length;
        var data = new byte[frames * channels * 2];
        for (var i = 0; i < frames; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * channels * 2), left[i]);
            if (right is not null)
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan((i * channels * 2) + 2), right[i]);
        }

        var wav = new byte[44 + data.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), rate * channels * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), data.Length);
        data.CopyTo(wav, 44);
        return wav;
    }

    private static byte[] InsertBeforeData(byte[] wav, ReadOnlySpan<byte> id, byte[] payload)
    {
        var dataAt = wav.AsSpan().IndexOf("data"u8);
        var chunk = new byte[8 + payload.Length];
        id.CopyTo(chunk);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), payload.Length);
        payload.CopyTo(chunk, 8);
        var merged = new byte[wav.Length + chunk.Length];
        wav.AsSpan(0, dataAt).CopyTo(merged);
        chunk.CopyTo(merged, dataAt);
        wav.AsSpan(dataAt).CopyTo(merged.AsSpan(dataAt + chunk.Length));
        BinaryPrimitives.WriteInt32LittleEndian(merged.AsSpan(4), merged.Length - 8);
        return merged;
    }

    private static void AssertPcm16(byte[] wav, int rate, int channels, short[] samples)
    {
        Assert.Equal((byte)'f', wav[12]);
        Assert.Equal(rate, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal((ushort)channels, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(samples.Length * channels * 2, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        for (var i = 0; i < samples.Length; i++)
            Assert.Equal(samples[i], BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + (i * 2))));
    }

    private static async Task<Result<DysonSpeechAudioChunk, string>> FirstAsync(
        IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> stream)
    {
        await foreach (var item in stream)
            return item;
        throw new InvalidOperationException("Synthesis yielded nothing.");
    }
}
