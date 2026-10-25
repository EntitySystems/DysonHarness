namespace DysonHarness;

/// <summary>One transcript piece. Times are milliseconds from the start of the clip.</summary>
public sealed record DysonSpeechChunk(string Text, int StartMs, int EndMs, bool IsFinal);

/// <summary>
/// Speech to text. Phonon 2 (<see cref="DysonPhononSpeech"/>) yields each segment, then one
/// <see cref="DysonSpeechChunk.IsFinal"/> chunk with the full text. Not installed is an error on the first yield.
/// </summary>
public interface IDysonSpeechToText
{
    string RuntimeId { get; }

    /// <param name="audio">A WAV clip (16 kHz mono from the composer mic). Capped at 32 MB.</param>
    IAsyncEnumerable<Result<DysonSpeechChunk, string>> TranscribeAsync(
        Stream audio,
        CancellationToken cancellationToken);
}

/// <summary>
/// Live PCM on top of <see cref="IDysonSpeechToText"/>. The active model implements this;
/// <see cref="DysonNemotronSpeech"/> is the Windows streaming engine it calls.
/// </summary>
public interface IDysonLiveSpeechToText
{
    /// <summary>
    /// True when the active model streams live 16 kHz mono s16le
    /// (<see cref="TranscribeLiveAsync"/>). False when the caller should keep sending WAV windows to
    /// <see cref="IDysonSpeechToText.TranscribeAsync"/>. An error means the active model is unusable
    /// (for example it is not installed).
    /// </summary>
    Task<Result<bool, string>> UsesLivePcmAsync(CancellationToken cancellationToken);

    /// <summary>
    /// <paramref name="pcm16kMono"/> is raw 16 kHz mono s16le (no WAV header). Yields partials, then one
    /// <see cref="DysonSpeechChunk.IsFinal"/> chunk.
    /// </summary>
    IAsyncEnumerable<Result<DysonSpeechChunk, string>> TranscribeLiveAsync(
        IAsyncEnumerable<Result<byte[], string>> pcm16kMono,
        CancellationToken cancellationToken);
}
