namespace DysonHarness;

/// <summary>PCM s16le mono audio. The last chunk of an utterance has <see cref="IsLast"/> and may be empty.</summary>
public sealed record DysonSpeechAudioChunk(byte[] Pcm, int SampleRate, bool IsLast);

/// <summary>
/// Text to speech, 24 kHz PCM chunks. The app registers <see cref="DysonActiveTextToSpeech"/>, which routes to the
/// active model: Pocket TTS (<see cref="DysonPocketSpeech"/>), Qwen3-TTS (<see cref="DysonQwenSpeech"/>),
/// Chatterbox Turbo, English only (<see cref="DysonChatterboxSpeech"/>), or those three as CrispASR GGUF
/// (<see cref="DysonCrispSpeech"/>).
/// Not installed, empty text, or an unknown voice is an error on the first yield.
/// </summary>
public interface IDysonTextToSpeech
{
    string RuntimeId { get; }

    /// <param name="voiceId">Voice id of the engine; null ⇒ the subject's default output voice for that engine.</param>
    IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> SynthesizeAsync(
        string text,
        string? voiceId,
        CancellationToken cancellationToken);
}
