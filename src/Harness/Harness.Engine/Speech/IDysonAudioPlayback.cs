namespace DysonHarness;

/// <summary>
/// Live playback of <see cref="DysonSpeechAudioChunk"/> streams (e.g. <see cref="IDysonTextToSpeech.SynthesizeAsync"/>).
/// Windows registers <c>DysonWindowsAudioPlayback</c> (Harness.WindowsBrowser, NAudio); other hosts register nothing.
/// One clip at a time.
/// </summary>
public interface IDysonAudioPlayback
{
    /// <summary>
    /// Writes each chunk to the output device as it arrives and completes once the clip has played.
    /// <see cref="Stop"/> or <paramref name="cancellationToken"/> closes the device and returns success.
    /// A chunk error, a bad format, no output device, or a clip already playing is an error.
    /// </summary>
    Task<VoidResult<string>> PlayAsync(
        IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> chunks,
        CancellationToken cancellationToken);

    /// <summary>Stops the current clip. Nothing playing is an error.</summary>
    VoidResult<string> Stop();
}
