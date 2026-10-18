namespace DysonHarness;

/// <summary>
/// Microphone capture. Windows registers <c>DysonWindowsAudioCapture</c> (Harness.WindowsBrowser, NAudio);
/// other hosts register nothing. One capture at a time.
/// Phonon takes a finished WAV, so <see cref="CaptureWindowsAsync"/> yields short clips, not a live stream.
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

    /// <summary>Stops the current capture. Nothing capturing is an error. Does not block on the device.</summary>
    VoidResult<string> Stop();
}
