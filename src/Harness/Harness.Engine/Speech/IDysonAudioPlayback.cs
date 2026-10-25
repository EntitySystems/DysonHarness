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

    /// <summary>
    /// Stops the current clip. <paramref name="fadeOut"/> of zero cancels at once.
    /// A positive fade ramps device volume to zero, then stops; the call returns immediately.
    /// Nothing playing is an error.
    /// </summary>
    VoidResult<string> Stop(TimeSpan fadeOut = default);
}

/// <summary>One quiet beep when a voice turn is accepted. Not speech, and not a second voice.</summary>
internal static class DysonAcceptChime
{
    internal const int SampleRate = 16_000;
    internal const int DurationMs = 120;

    internal static async IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> Once()
    {
        yield return Result<DysonSpeechAudioChunk, string>.AsValue(
            new DysonSpeechAudioChunk(Pcm(), SampleRate, true));
    }

    internal static byte[] Pcm()
    {
        const double amplitude = 0.1;
        var samples = SampleRate * DurationMs / 1000;
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var edge = Math.Min(i, samples - 1 - i);
            var env = Math.Min(1d, edge / 80d);
            var wave = Math.Sin(2d * Math.PI * 880d * i / SampleRate);
            var value = (short)Math.Round(wave * env * amplitude * short.MaxValue);
            pcm[i * 2] = (byte)value;
            pcm[i * 2 + 1] = (byte)(value >> 8);
        }

        return pcm;
    }
}
