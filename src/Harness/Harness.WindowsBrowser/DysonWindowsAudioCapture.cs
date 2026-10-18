using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using NAudio;
using NAudio.Wave;

namespace DysonHarness;

/// <summary>
/// Windows <see cref="IDysonAudioCapture"/>: NAudio <see cref="WaveIn"/> (event capture) at 16 kHz mono s16le.
/// Phonon only accepts a finished WAV, so each yield is one ~2 s clip.
/// </summary>
public sealed class DysonWindowsAudioCapture : IDysonAudioCapture
{
    /// <summary>16 kHz mono, 16-bit, 2 seconds.</summary>
    internal const int SampleRate = 16_000;

    internal const int WindowBytes = SampleRate * 2 * 2;

    /// <summary>100 ms. Shorter tails are dropped when capture stops.</summary>
    internal const int MinFlushBytes = SampleRate * 2 / 10;

    /// <summary>Windows waiting to be transcribed. One more and capture fails instead of growing.</summary>
    internal const int MaxQueuedWindows = 2;

    private readonly Func<Result<IReadOnlyList<DysonAudioInputDevice>, string>> _listDevices;
    private readonly Func<int, IWaveIn> _openDevice;
    private readonly Lock _gate = new();
    private Session? _session;

    public DysonWindowsAudioCapture() : this(ListDevicesWinMm, OpenDevice) { }

    /// <summary>Test seam: no WinMM. <paramref name="openDevice"/> is passed the device number.</summary>
    internal DysonWindowsAudioCapture(
        Func<Result<IReadOnlyList<DysonAudioInputDevice>, string>> listDevices,
        Func<int, IWaveIn> openDevice)
    {
        _listDevices = listDevices ?? throw new ArgumentNullException(nameof(listDevices));
        _openDevice = openDevice ?? throw new ArgumentNullException(nameof(openDevice));
    }

    public Result<IReadOnlyList<DysonAudioInputDevice>, string> ListDevices() => _listDevices();

    public async IAsyncEnumerable<Result<Stream, string>> CaptureWindowsAsync(
        string? deviceId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // WinMM stays off the Blazor circuit even if the caller did not hop.
        await Task.Run(() => 0, CancellationToken.None).ConfigureAwait(false);

        var listed = _listDevices();
        if (listed.IsError)
        {
            yield return Result<Stream, string>.AsError(listed.Error);
            yield break;
        }

        var selected = DysonSpeechInputDevice.Resolve(listed.Value, deviceId);
        if (selected.IsError)
        {
            yield return Result<Stream, string>.AsError(selected.Error);
            yield break;
        }

        if (!int.TryParse(selected.Value.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var deviceNumber)
            || deviceNumber < 0)
        {
            yield return Result<Stream, string>.AsError($"Microphone '{selected.Value.Id}' is not connected.");
            yield break;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Session? session;
        lock (_gate)
        {
            if (_session is null)
            {
                session = new Session(linked);
                _session = session;
            }
            else
                session = null;
        }

        if (session is null)
        {
            linked.Dispose();
            yield return Result<Stream, string>.AsError("Already recording.");
            yield break;
        }

        try
        {
            var opened = Open(session, deviceNumber);
            if (opened.IsError)
            {
                yield return Result<Stream, string>.AsError(opened.Error, opened.Exception);
                yield break;
            }

            while (true)
            {
                while (session.Windows.Reader.TryRead(out var queued))
                    yield return queued;

                if (session.Cts.IsCancellationRequested)
                    break;

                bool more;
                try
                {
                    more = await session.Windows.Reader.WaitToReadAsync(session.Cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    more = false;
                }

                if (!more)
                    break;
            }

            string? fault;
            byte[]? tail = null;
            lock (session.PendingGate)
            {
                fault = session.Fault;
                if (fault is null && session.Pending.Count >= MinFlushBytes)
                    tail = session.DetachPending();
            }

            if (fault is not null)
                yield return Result<Stream, string>.AsError(fault);
            else if (tail is not null)
                yield return Result<Stream, string>.AsValue(ToWav(tail));
        }
        finally
        {
            Close(session);
            lock (_gate)
            {
                if (ReferenceEquals(_session, session))
                    _session = null;
            }

            session.Cts.Dispose();
        }
    }

    public VoidResult<string> Stop()
    {
        Session? session;
        lock (_gate)
            session = _session;
        if (session is null)
            return VoidResult<string>.AsError("Nothing is recording.");
        session.RequestStop();
        return VoidResult<string>.Success;
    }

    /// <summary>PCM s16le mono at <see cref="SampleRate"/> wrapped in a WAV the caller owns.</summary>
    internal static MemoryStream ToWav(ReadOnlySpan<byte> pcm)
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
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(pcm.Length);
        }

        stream.Write(pcm);
        stream.Position = 0;
        return stream;
    }

    private VoidResult<string> Open(Session session, int deviceNumber)
    {
        IWaveIn? wave = null;
        void OnData(object? sender, WaveInEventArgs e) => session.AddBytes(e);
        try
        {
            wave = _openDevice(deviceNumber);
            wave.WaveFormat = new WaveFormat(SampleRate, 16, 1);
            wave.DataAvailable += OnData;
            wave.StartRecording();
            session.Wave = wave;
            session.OnDataHandler = OnData;
            return VoidResult<string>.Success;
        }
        catch (MmException e)
        {
            if (wave is not null)
            {
                wave.DataAvailable -= OnData;
                (wave as IDisposable)?.Dispose();
            }

            return VoidResult<string>.AsError($"No microphone ({e.Result}).", e);
        }
    }

    private static void Close(Session session)
    {
        var wave = session.Wave;
        session.Wave = null;
        if (wave is not null)
        {
            if (session.OnDataHandler is not null)
                wave.DataAvailable -= session.OnDataHandler;
            try
            {
                wave.StopRecording();
            }
            catch (Exception ex) when (ex is MmException or ObjectDisposedException)
            {
            }

            (wave as IDisposable)?.Dispose();
        }

        while (session.Windows.Reader.TryRead(out var leftover))
        {
            if (leftover.IsSuccess)
                leftover.Value.Dispose();
        }
    }

    private static Result<IReadOnlyList<DysonAudioInputDevice>, string> ListDevicesWinMm()
    {
        try
        {
            var count = WaveIn.DeviceCount;
            var list = new List<DysonAudioInputDevice>(count);
            for (var i = 0; i < count; i++)
            {
                var raw = WaveIn.GetCapabilities(i).ProductName ?? "";
                var name = raw.TrimEnd('\0').Trim();
                if (name.Length == 0)
                    name = "Microphone " + (i + 1).ToString(CultureInfo.InvariantCulture);
                else if (list.Any(d => string.Equals(d.Name, name, StringComparison.Ordinal)))
                    name += " (" + (i + 1).ToString(CultureInfo.InvariantCulture) + ")";
                list.Add(new DysonAudioInputDevice(i.ToString(CultureInfo.InvariantCulture), name));
            }

            return Result<IReadOnlyList<DysonAudioInputDevice>, string>.AsValue(list);
        }
        catch (MmException e)
        {
            return Result<IReadOnlyList<DysonAudioInputDevice>, string>.AsError($"No microphone ({e.Result}).", e);
        }
    }

    private static IWaveIn OpenDevice(int deviceNumber) =>
        new WaveIn
        {
            DeviceNumber = deviceNumber,
            BufferMilliseconds = 50,
            NumberOfBuffers = 3,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
        };

    private sealed class Session(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public Channel<Result<Stream, string>> Windows { get; } = Channel.CreateBounded<Result<Stream, string>>(
            new BoundedChannelOptions(MaxQueuedWindows)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        public object PendingGate { get; } = new();
        public List<byte> Pending { get; } = new(WindowBytes);
        public IWaveIn? Wave { get; set; }
        public EventHandler<WaveInEventArgs>? OnDataHandler { get; set; }
        public string? Fault { get; private set; }

        public void RequestStop()
        {
            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            Windows.Writer.TryComplete();
        }

        public void AddBytes(WaveInEventArgs e)
        {
            if (Cts.IsCancellationRequested)
                return;
            var copy = e.BufferSpan.ToArray();
            if (copy.Length == 0)
                return;

            lock (PendingGate)
            {
                if (Fault is not null || Cts.IsCancellationRequested)
                    return;
                Pending.AddRange(copy);
                // ponytail: queue cap is MaxQueuedWindows (~4 s). Past that, error instead of a streaming STT API.
                while (Pending.Count >= WindowBytes && Fault is null)
                {
                    var pcm = DetachWindow();
                    if (!Windows.Writer.TryWrite(Result<Stream, string>.AsValue(ToWav(pcm))))
                    {
                        Fault = "Speech to text fell behind the microphone.";
                        Pending.Clear();
                        try
                        {
                            Cts.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }

                        Windows.Writer.TryComplete();
                        return;
                    }
                }
            }
        }

        public byte[] DetachWindow()
        {
            var pcm = new byte[WindowBytes];
            Pending.CopyTo(0, pcm, 0, WindowBytes);
            Pending.RemoveRange(0, WindowBytes);
            return pcm;
        }

        public byte[] DetachPending()
        {
            var pcm = Pending.ToArray();
            Pending.Clear();
            return pcm;
        }
    }
}
