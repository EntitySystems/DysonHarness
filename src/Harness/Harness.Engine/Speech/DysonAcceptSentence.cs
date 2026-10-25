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
    private const string Label = "Acceptance sentence";

    /// <summary>Empty or whitespace is the chime. A line break or more than <see cref="MaxLength"/> characters is an error.</summary>
    public static VoidResult<string> Validate(string? text) => SentenceCue.Validate(text, Label);

    /// <summary>
    /// Blank text clears the setting, then deletes the clip. Otherwise synthesizes once
    /// (<c>voiceId</c> null), writes the WAV and sidecar, then stores the sentence.
    /// A failed synth or write leaves the previous key and files.
    /// </summary>
    public static Task<VoidResult<string>> SaveAsync(
        IDysonTextToSpeech tts,
        IDysonSubjectSettingsRepository settings,
        string installRoot,
        string? text,
        CancellationToken cancellationToken) =>
        SentenceCue.SaveAsync(
            tts,
            settings,
            installRoot,
            text,
            DysonAppSettingKeys.SpeechAcceptSentence,
            WavName,
            TextName,
            Label,
            cancellationToken);

    /// <summary>
    /// Cached clip, or null when the key is missing, the sidecar disagrees, or the WAV is unreadable.
    /// Never throws. File bytes are read on the thread pool.
    /// </summary>
    public static Task<DysonSpeechAudioChunk?> TryLoadAsync(
        IDysonSubjectSettingsRepository settings,
        string installRoot,
        CancellationToken cancellationToken) =>
        SentenceCue.TryLoadAsync(
            settings,
            installRoot,
            DysonAppSettingKeys.SpeechAcceptSentence,
            WavName,
            TextName,
            cancellationToken);
}

/// <summary>
/// Optional spoken listen cue when a stored wake name matches. Same cache as
/// <see cref="DysonAcceptSentence"/>, different key and files. Playback never synthesizes.
/// </summary>
public static class DysonListenSentence
{
    public const int MaxLength = DysonAcceptSentence.MaxLength;

    private const string WavName = "listen-sentence.wav";
    private const string TextName = "listen-sentence.txt";
    private const string Label = "Listen sentence";

    /// <summary>Empty or whitespace is the chime. A line break or more than <see cref="MaxLength"/> characters is an error.</summary>
    public static VoidResult<string> Validate(string? text) => SentenceCue.Validate(text, Label);

    /// <summary>
    /// Blank text clears the setting, then deletes the clip. Otherwise synthesizes once
    /// (<c>voiceId</c> null), writes the WAV and sidecar, then stores the sentence.
    /// A failed synth or write leaves the previous key and files.
    /// </summary>
    public static Task<VoidResult<string>> SaveAsync(
        IDysonTextToSpeech tts,
        IDysonSubjectSettingsRepository settings,
        string installRoot,
        string? text,
        CancellationToken cancellationToken) =>
        SentenceCue.SaveAsync(
            tts,
            settings,
            installRoot,
            text,
            DysonAppSettingKeys.SpeechListenSentence,
            WavName,
            TextName,
            Label,
            cancellationToken);

    /// <summary>
    /// Cached clip, or null when the key is missing, the sidecar disagrees, or the WAV is unreadable.
    /// Never throws. File bytes are read on the thread pool.
    /// </summary>
    public static Task<DysonSpeechAudioChunk?> TryLoadAsync(
        IDysonSubjectSettingsRepository settings,
        string installRoot,
        CancellationToken cancellationToken) =>
        SentenceCue.TryLoadAsync(
            settings,
            installRoot,
            DysonAppSettingKeys.SpeechListenSentence,
            WavName,
            TextName,
            cancellationToken);
}

/// <summary>
/// Fixed sentence played when voice mode turns on. No setting and no UI. The sidecar keys the clip on the active
/// TTS runtime id, the resolved voice id (may be empty), and <see cref="Text"/>. A miss synthesizes once.
/// Never throws; every failure is null.
/// </summary>
public static class DysonActivationSentence
{
    public const string Text = "Voice mode activated. Please wait up to five seconds for all systems to initialize.";

    private const string WavName = "activation-sentence.wav";
    private const string TextName = "activation-sentence.txt";

    // ponytail: 15 s bounds a runaway synth. Raise only if a real voice reads this sentence past 15 s.
    private const int MaxSeconds = 15;

    /// <summary>One ensure at a time, so a quick off/on does not run two synths; the second waiter loads the file.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Cached clip when all three sidecar lines match and the WAV reads, else null. File IO on the thread pool.</summary>
    public static async Task<DysonSpeechAudioChunk?> TryLoadAsync(
        string installRoot,
        string runtimeId,
        string voiceId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installRoot) || string.IsNullOrWhiteSpace(runtimeId))
                return null;
            return await Task.Run(() => Read(installRoot, runtimeId, voiceId ?? ""), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Cache hit ⇒ that clip, no synth. Miss ⇒ <see cref="IDysonTextToSpeech.SynthesizeAsync"/> once with
    /// <c>voiceId</c> null, then writes the WAV and sidecar. Synth error, IO error, or audio over
    /// 15 seconds ⇒ null and nothing written.
    /// </summary>
    public static async Task<DysonSpeechAudioChunk?> EnsureAsync(
        IDysonTextToSpeech tts,
        string installRoot,
        string runtimeId,
        string voiceId,
        CancellationToken cancellationToken)
    {
        if (tts is null || string.IsNullOrWhiteSpace(installRoot) || string.IsNullOrWhiteSpace(runtimeId))
            return null;
        voiceId ??= "";
        try
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            var hit = await TryLoadAsync(installRoot, runtimeId, voiceId, cancellationToken).ConfigureAwait(false);
            if (hit is not null)
                return hit;

            await Task.Run(() => 0, CancellationToken.None).ConfigureAwait(false);
            byte[]? pcm = null;
            var rate = 0;
            await foreach (var chunk in tts.SynthesizeAsync(Text, voiceId: null, cancellationToken).ConfigureAwait(false))
            {
                if (chunk.IsError)
                    return null;
                if (chunk.Value.Pcm is not { Length: > 0 } part)
                    continue;
                if (rate == 0)
                    rate = chunk.Value.SampleRate;
                else if (chunk.Value.SampleRate != rate)
                    return null;
                pcm = pcm is null ? part : SentenceCue.Join(pcm, part);
                if (rate <= 0 || pcm.Length / 2 > (long)rate * MaxSeconds)
                    return null;
            }

            if (pcm is null || rate <= 0 || pcm.Length < 2)
                return null;
            if ((pcm.Length & 1) == 1)
                pcm = pcm[..^1];

            var body = pcm;
            var sampleRate = rate;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(installRoot);
                File.WriteAllBytes(Path.Combine(installRoot, WavName), SentenceCue.EncodeWav(body, sampleRate));
                File.WriteAllText(
                    Path.Combine(installRoot, TextName),
                    string.Join('\n', runtimeId, voiceId, Text),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }, CancellationToken.None).ConfigureAwait(false);
            return new DysonSpeechAudioChunk(body, sampleRate, true);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static DysonSpeechAudioChunk? Read(string installRoot, string runtimeId, string voiceId)
    {
        var textPath = Path.Combine(installRoot, TextName);
        var wavPath = Path.Combine(installRoot, WavName);
        if (!File.Exists(textPath) || !File.Exists(wavPath))
            return null;
        var lines = File.ReadAllLines(textPath);
        if (lines.Length != 3
            || !string.Equals(lines[0], runtimeId, StringComparison.Ordinal)
            || !string.Equals(lines[1], voiceId, StringComparison.Ordinal)
            || !string.Equals(lines[2], Text, StringComparison.Ordinal))
            return null;
        return SentenceCue.ReadWav(File.ReadAllBytes(wavPath));
    }
}

/// <summary>One sentence setting: synth once, then a WAV and a sidecar. Accept and listen share this.</summary>
file static class SentenceCue
{
    public static VoidResult<string> Validate(string? text, string label)
    {
        if (text is not null && (text.Contains('\r') || text.Contains('\n')))
            return new VoidResult<string>($"{label} must be one line.");
        if (string.IsNullOrWhiteSpace(text))
            return VoidResult<string>.Success;
        if (text.Trim().Length > DysonAcceptSentence.MaxLength)
            return new VoidResult<string>($"{label} is over 40 characters.");
        return VoidResult<string>.Success;
    }

    public static async Task<VoidResult<string>> SaveAsync(
        IDysonTextToSpeech tts,
        IDysonSubjectSettingsRepository settings,
        string installRoot,
        string? text,
        string settingKey,
        string wavName,
        string textName,
        string label,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tts);
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(installRoot))
            return new VoidResult<string>("Speech install folder is missing.");

        var valid = Validate(text, label);
        if (valid.IsError)
            return valid;
        if (string.IsNullOrWhiteSpace(text))
            return await ClearAsync(settings, installRoot, settingKey, wavName, textName, cancellationToken).ConfigureAwait(false);

        var sentence = text.Trim();
        await Task.Run(() => 0, CancellationToken.None).ConfigureAwait(false);

        byte[]? pcm = null;
        var rate = 0;
        var tooLong = $"{label} was too long to cache.";
        var noAudio = $"{label} produced no audio.";
        await foreach (var chunk in tts.SynthesizeAsync(sentence, voiceId: null, cancellationToken).ConfigureAwait(false))
        {
            if (chunk.IsError)
                return new VoidResult<string>(chunk.Error);
            if (chunk.Value.Pcm is not { Length: > 0 } part)
                continue;
            if (rate == 0)
                rate = chunk.Value.SampleRate;
            else if (chunk.Value.SampleRate != rate)
                return new VoidResult<string>(noAudio);
            pcm = pcm is null ? part : Join(pcm, part);
        }

        if (pcm is null || rate <= 0 || pcm.Length < 2)
            return new VoidResult<string>(noAudio);
        if ((pcm.Length & 1) == 1)
            pcm = pcm[..^1];
        if (pcm.Length / 2 > (long)rate * 5)
            return new VoidResult<string>(tooLong);

        var wavPath = Path.Combine(installRoot, wavName);
        var textPath = Path.Combine(installRoot, textName);
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
            .SetSettingAsync(settingKey, sentence, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<DysonSpeechAudioChunk?> TryLoadAsync(
        IDysonSubjectSettingsRepository settings,
        string installRoot,
        string settingKey,
        string wavName,
        string textName,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() => 0, CancellationToken.None).ConfigureAwait(false);
            if (settings is null || string.IsNullOrWhiteSpace(installRoot))
                return null;

            var setting = await settings
                .GetSettingAsync(settingKey, cancellationToken)
                .ConfigureAwait(false);
            if (setting.IsError || string.IsNullOrWhiteSpace(setting.Value))
                return null;

            var sentence = setting.Value.Trim();
            return await Task.Run(
                () => Read(installRoot, sentence, wavName, textName),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<VoidResult<string>> ClearAsync(
        IDysonSubjectSettingsRepository settings,
        string installRoot,
        string settingKey,
        string wavName,
        string textName,
        CancellationToken cancellationToken)
    {
        var cleared = await settings
            .SetSettingAsync(settingKey, "", cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                TryDelete(Path.Combine(installRoot, wavName));
                TryDelete(Path.Combine(installRoot, textName));
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

    private static DysonSpeechAudioChunk? Read(string installRoot, string sentence, string wavName, string textName)
    {
        var textPath = Path.Combine(installRoot, textName);
        var wavPath = Path.Combine(installRoot, wavName);
        if (!File.Exists(textPath) || !File.Exists(wavPath))
            return null;
        var stored = File.ReadAllText(textPath).Trim();
        if (!string.Equals(stored, sentence, StringComparison.Ordinal))
            return null;
        return ReadWav(File.ReadAllBytes(wavPath));
    }

    public static DysonSpeechAudioChunk? ReadWav(byte[] bytes)
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

    public static byte[] EncodeWav(byte[] pcm, int sampleRate)
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

    public static byte[] Join(byte[] left, byte[] right)
    {
        var all = new byte[left.Length + right.Length];
        Buffer.BlockCopy(left, 0, all, 0, left.Length);
        Buffer.BlockCopy(right, 0, all, left.Length, right.Length);
        return all;
    }
}
