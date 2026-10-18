using NAudio;
using NAudio.Wave;

namespace DysonHarness;

/// <summary>
/// Windows <see cref="IDysonAudioPlayback"/>: NAudio <see cref="WaveOut"/> fed by a <see cref="BufferedWaveProvider"/>.
/// The device opens on the first non-empty chunk and every chunk is queued the moment it arrives.
/// </summary>
public sealed class DysonWindowsAudioPlayback : IDysonAudioPlayback
{
    private readonly Func<IWavePlayer> _openDevice;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _playing;

    public DysonWindowsAudioPlayback() : this(() => new WaveOut()) { }

    /// <summary>Test seam: <paramref name="openDevice"/> stands in for the real output device.</summary>
    internal DysonWindowsAudioPlayback(Func<IWavePlayer> openDevice) => _openDevice = openDevice;

    /// <summary><see cref="DysonSpeechAudioChunk"/> is PCM s16le mono at <paramref name="sampleRate"/>.</summary>
    internal static Result<WaveFormat, string> ToWaveFormat(int sampleRate) =>
        sampleRate is >= 8000 and <= 192000
            ? Result<WaveFormat, string>.AsValue(new WaveFormat(sampleRate, 16, 1))
            : Result<WaveFormat, string>.AsError($"Unsupported sample rate {sampleRate} Hz.");

    public async Task<VoidResult<string>> PlayAsync(
        IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> chunks,
        CancellationToken cancellationToken)
    {
        var playing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            if (_playing is not null)
            {
                playing.Dispose();
                return VoidResult<string>.AsError("Audio is already playing.");
            }
            _playing = playing;
        }

        var token = playing.Token;
        IWavePlayer? device = null;
        BufferedWaveProvider? buffer = null;
        long written = 0;
        // A streamed read (Vulkan especially) can end on one byte of an s16le sample. Hold it for the next chunk.
        byte held = 0;
        var holding = false;
        try
        {
            await foreach (var chunk in chunks.WithCancellation(token))
            {
                if (chunk.IsError)
                    return VoidResult<string>.AsError(chunk.Error);

                var pcm = chunk.Value.Pcm;
                if (holding)
                {
                    var merged = new byte[pcm.Length + 1];
                    merged[0] = held;
                    pcm.AsSpan().CopyTo(merged.AsSpan(1));
                    pcm = merged;
                    holding = false;
                }

                if ((pcm.Length & 1) != 0)
                {
                    held = pcm[^1];
                    holding = true;
                    pcm = pcm.Length == 1 ? [] : pcm[..^1];
                }

                if (pcm.Length > 0)
                {
                    if (buffer is null)
                    {
                        var format = ToWaveFormat(chunk.Value.SampleRate);
                        if (format.IsError)
                            return VoidResult<string>.AsError(format.Error);
                        buffer = new BufferedWaveProvider(format.Value);
                        var opened = Open(buffer);
                        if (opened.IsError)
                            return VoidResult<string>.AsError(opened.Error, opened.Exception);
                        device = opened.Value;
                    }
                    else if (chunk.Value.SampleRate != buffer.WaveFormat.SampleRate)
                        return VoidResult<string>.AsError("Sample rate changed mid-clip.");

                    // Synthesis can outrun playback: wait for room instead of overflowing the 5 s buffer.
                    for (var offset = 0; offset < pcm.Length;)
                    {
                        var room = Math.Min(pcm.Length - offset, (buffer.BufferLength - buffer.BufferedBytes) & ~1);
                        if (room == 0)
                        {
                            await Task.Delay(20, token);
                            continue;
                        }
                        buffer.AddSamples(pcm, offset, room);
                        offset += room;
                    }
                    written += pcm.Length;
                }

                if (chunk.Value.IsLast)
                    break;
            }

            if (holding)
                return VoidResult<string>.AsError("PCM chunk is not whole 16-bit samples.");

            // WaveOut reports bytes played, so the tail still in device buffers is heard; a seam falls back to the queue.
            while (device is { PlaybackState: PlaybackState.Playing }
                   && (device is IWavePosition position ? position.GetPosition() < written : buffer!.BufferedBytes > 0))
                await Task.Delay(20, token);
            return VoidResult<string>.Success;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return VoidResult<string>.Success; // Stop() or the caller's token.
        }
        finally
        {
            device?.Stop();
            device?.Dispose();
            lock (_gate)
                _playing = null;
            playing.Dispose();
        }
    }

    public VoidResult<string> Stop()
    {
        lock (_gate)
        {
            if (_playing is null)
                return VoidResult<string>.AsError("Nothing is playing.");
            _playing.Cancel();
            return VoidResult<string>.Success;
        }
    }

    private Result<IWavePlayer, string> Open(BufferedWaveProvider buffer)
    {
        IWavePlayer? device = null;
        try
        {
            device = _openDevice();
            device.Init(buffer);
            device.Play();
            return Result<IWavePlayer, string>.AsValue(device);
        }
        catch (MmException e)
        {
            device?.Dispose();
            return Result<IWavePlayer, string>.AsError($"No audio output device ({e.Result}).", e);
        }
    }
}
