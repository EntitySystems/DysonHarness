namespace DysonHarness;

/// <summary>PCM s16le mono audio. The last chunk of an utterance has <see cref="IsLast"/> and may be empty.</summary>
public sealed record DysonSpeechAudioChunk(byte[] Pcm, int SampleRate, bool IsLast);

/// <summary>
/// Text to speech. Pocket TTS (<see cref="DysonPocketSpeech"/>) streams 24 kHz PCM chunks.
/// Not installed, empty text, or an unknown voice is an error on the first yield.
/// </summary>
public interface IDysonTextToSpeech
{
    string RuntimeId { get; }

    /// <param name="voiceId">Catalog voice id; null ⇒ the subject's default output voice (<c>alba</c> after install).</param>
    IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> SynthesizeAsync(
        string text,
        string? voiceId,
        CancellationToken cancellationToken);
}
