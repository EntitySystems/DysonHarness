namespace DysonHarness;

/// <summary>
/// Microphone capture. Windows registers <c>DysonWindowsAudioCapture</c> (Harness.WindowsBrowser, NAudio);
/// other hosts register nothing. One capture at a time.
/// Phonon takes a finished WAV, so <see cref="CaptureWindowsAsync"/> yields short clips.
/// A live model uses <see cref="CapturePcmAsync"/> (raw PCM, no header).
/// </summary>
public interface IDysonAudioCapture
{
    /// <summary>Input devices. Empty is success (nothing plugged in). A driver failure is an error.</summary>
    Result<IReadOnlyList<DysonAudioInputDevice>, string> ListDevices();

    /// <summary>
    /// Yields WAV clips (16 kHz mono PCM s16le) until <see cref="Stop"/> or <paramref name="cancellationToken"/>.
    /// The caller disposes each success stream. Stop or cancel ends the sequence with no error item.
    /// Already capturing, a missing device, or no microphone is an error item.
    /// </summary>
    IAsyncEnumerable<Result<Stream, string>> CaptureWindowsAsync(
        string? deviceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Yields raw 16 kHz mono s16le blocks of about 560 ms (17920 bytes), no WAV header, until
    /// <see cref="Stop"/> or <paramref name="cancellationToken"/>. Stop or cancel ends the sequence with no
    /// error item. Already capturing, a missing device, or no microphone is an error item, same as
    /// <see cref="CaptureWindowsAsync"/>.
    /// </summary>
    IAsyncEnumerable<Result<byte[], string>> CapturePcmAsync(
        string? deviceId,
        CancellationToken cancellationToken);

    /// <summary>Stops the current capture. Nothing capturing is an error. Does not block on the device.</summary>
    VoidResult<string> Stop();
}
