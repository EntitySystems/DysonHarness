using System.Text;

namespace DysonHarness;

/// <summary>
/// Optional spoken accept cue. The sentence is an app setting; the clip is one WAV plus a sidecar
/// under the speech install root, written when the sentence is saved. Accept playback never synthesizes.
/// </summary>
public static class DysonAcceptSentence
{
    public const int MaxLength = 40;

    /// <summary>Built-in sentences. Saving one writes that text; there is no second store.</summary>
    public static readonly IReadOnlyList<string> Presets = ["Copy", "Over"];

    private const string WavName = "accept-sentence.wav";
    private const string TextName = "accept-sentence.txt";
    private const string TooLong = "Acceptance sentence was too long to cache.";
    private const string NoAudio = "Acceptance sentence produced no audio.";

    /// <summary>Empty or whitespace is the chime. A line break or more than <see cref="MaxLength"/> characters is an error.</summary>
    public static VoidResult<string> Validate(string? text)
    {
        if (text is not null && (text.Contains('\r') || text.Contains('\n')))
            return new VoidResult<string>("Acceptance sentence must be one line.");
        if (string.IsNullOrWhiteSpace(text))
            return VoidResult<string>.Success;
        if (text.Trim().Length > MaxLength)
            return new VoidResult<string>("Acceptance sentence is over 40 characters.");
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Blank text clears the setting, then deletes the clip. Otherwise synthesizes once
    /// (<c>voiceId</c> null), writes the WAV and sidecar, then stores the sentence.
    /// A failed synth or write leaves the previous key and files.
    /// </summary>
    public static async Task<VoidResult<string>> SaveAsync(
        IDysonTextToSpeech tts,
        IDysonSubjectSettingsRepository settings,
        string installRoot,
        string? text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tts);
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(installRoot))
            return new VoidResult<string>("Speech install folder is missing.");

        var valid = Validate(text);
        if (valid.IsError)
            return valid;
        if (string.IsNullOrWhiteSpace(text))
            return await ClearAsync(settings, installRoot, cancellationToken).ConfigureAwait(false);

        var sentence = text.Trim();
        await Task.Run(() => 0, CancellationToken.None).ConfigureAwait(false);

        byte[]? pcm = null;
        var rate = 0;
        await foreach (var chunk in tts.SynthesizeAsync(sentence, voiceId: null, cancellationToken).ConfigureAwait(false))
        {
            if (chunk.IsError)
                return new VoidResult<string>(chunk.Error);
            if (chunk.Value.Pcm is not { Length: > 0 } part)
                continue;
            if (rate == 0)
                rate = chunk.Value.SampleRate;
            else if (chunk.Value.SampleRate != rate)
                return new VoidResult<string>(NoAudio);
            pcm = pcm is null ? part : Join(pcm, part);
        }

        if (pcm is null || rate <= 0 || pcm.Length < 2)
            return new VoidResult<string>(NoAudio);
        if ((pcm.Length & 1) == 1)
            pcm = pcm[..^1];
        if (pcm.Length / 2 > (long)rate * 5)
            return new VoidResult<string>(TooLong);

        var wavPath = Path.Combine(installRoot, WavName);
        var textPath = Path.Combine(installRoot, TextName);
        try
        {
            var body = pcm;
            var sampleRate = rate;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(installRoot);
                File.WriteAllBytes(wavPath, EncodeWav(body, sampleRate));
                File.WriteAllText(textPath, sentence, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return new VoidResult<string>(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new VoidResult<string>(ex.Message);
        }

        return await settings
            .SetSettingAsync(DysonAppSettingKeys.SpeechAcceptSentence, sentence, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Cached clip, or null when the key is missing, the sidecar disagrees, or the WAV is unreadable.
    /// Never throws. File bytes are read on the thread pool.
    /// </summary>
    public static async Task<DysonSpeechAudioChunk?> TryLoadAsync(
        IDysonSubjectSettingsRepository settings,
        string installRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() => 0, CancellationToken.None).ConfigureAwait(false);
            if (settings is null || string.IsNullOrWhiteSpace(installRoot))
                return null;

            var setting = await settings
                .GetSettingAsync(DysonAppSettingKeys.SpeechAcceptSentence, cancellationToken)
                .ConfigureAwait(false);
            if (setting.IsError || string.IsNullOrWhiteSpace(setting.Value))
                return null;

            var sentence = setting.Value.Trim();
            return await Task.Run(() => Read(installRoot, sentence), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<VoidResult<string>> ClearAsync(
        IDysonSubjectSettingsRepository settings,
        string installRoot,
        CancellationToken cancellationToken)
    {
        var cleared = await settings
            .SetSettingAsync(DysonAppSettingKeys.SpeechAcceptSentence, "", cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                TryDelete(Path.Combine(installRoot, WavName));
                TryDelete(Path.Combine(installRoot, TextName));
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return cleared;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static DysonSpeechAudioChunk? Read(string installRoot, string sentence)
    {
        var textPath = Path.Combine(installRoot, TextName);
        var wavPath = Path.Combine(installRoot, WavName);
        if (!File.Exists(textPath) || !File.Exists(wavPath))
            return null;
        var stored = File.ReadAllText(textPath).Trim();
        if (!string.Equals(stored, sentence, StringComparison.Ordinal))
            return null;
        return ReadWav(File.ReadAllBytes(wavPath));
    }

    private static DysonSpeechAudioChunk? ReadWav(byte[] bytes)
    {
        if (bytes.Length < 44 || bytes[0] != (byte)'R' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F' || bytes[3] != (byte)'F')
            return null;
        if (bytes[8] != (byte)'W' || bytes[9] != (byte)'A' || bytes[10] != (byte)'V' || bytes[11] != (byte)'E')
            return null;

        var rate = 0;
        byte[]? pcm = null;
        var i = 12;
        while (i + 8 <= bytes.Length)
        {
            var size = BitConverter.ToInt32(bytes, i + 4);
            if (size < 0 || i + 8 + size > bytes.Length)
                return null;
            var id = Encoding.ASCII.GetString(bytes, i, 4);
            if (id == "fmt ")
            {
                if (size < 16)
                    return null;
                var format = BitConverter.ToInt16(bytes, i + 8);
                var channels = BitConverter.ToInt16(bytes, i + 10);
                rate = BitConverter.ToInt32(bytes, i + 12);
                var bits = BitConverter.ToInt16(bytes, i + 22);
                if (format != 1 || channels != 1 || bits != 16 || rate <= 0)
                    return null;
            }
            else if (id == "data")
            {
                pcm = new byte[size];
                Buffer.BlockCopy(bytes, i + 8, pcm, 0, size);
            }

            i += 8 + size + (size & 1);
        }

        if (pcm is null || rate <= 0 || pcm.Length < 2 || (pcm.Length & 1) == 1)
            return null;
        return new DysonSpeechAudioChunk(pcm, rate, true);
    }

    private static byte[] EncodeWav(byte[] pcm, int sampleRate)
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
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(pcm.Length);
        }

        stream.Write(pcm);
        return stream.ToArray();
    }

    private static byte[] Join(byte[] left, byte[] right)
    {
        var all = new byte[left.Length + right.Length];
        Buffer.BlockCopy(left, 0, all, 0, left.Length);
        Buffer.BlockCopy(right, 0, all, left.Length, right.Length);
        return all;
    }
}
