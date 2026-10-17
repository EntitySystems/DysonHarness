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
