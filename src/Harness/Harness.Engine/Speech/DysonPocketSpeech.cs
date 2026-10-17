using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// Pocket TTS (<c>pocket-tts</c>) through <c>pocket/bridge.py</c>: one JSON line in
/// (<c>text</c>, <c>voicePath</c>), length-prefixed PCM s16le 24 kHz frames out. The bridge is lazy, one
/// request at a time, restarted if it dies, and stopped on app exit. The voice is always a local catalog
/// <c>.safetensors</c> file, so Pocket's voice-cloning check never runs. <c>HF_HUB_OFFLINE=1</c>.
/// </summary>
public sealed partial class DysonPocketSpeech(
    IDysonSpeechModelRepository models,
    IDysonSubjectSettingsRepository settings,
    string installRoot) : IDysonTextToSpeech
{
    public const string Id = "pocket-tts";
    public const int SampleRate = 24_000;
    public const string DefaultVoiceId = DysonSpeechInstaller.CatalogVoiceId;

    // ponytail: one process-wide bridge (single install root per app process); upgrade path is a keyed host.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object ProcessLock = new();
    private static Process? _bridge;
    private static readonly Queue<string> StderrTail = new();

    private readonly IDysonSpeechModelRepository _models = models ?? throw new ArgumentNullException(nameof(models));
    private readonly IDysonSubjectSettingsRepository _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly string _installRoot = installRoot ?? throw new ArgumentNullException(nameof(installRoot));

    public string RuntimeId => Id;

    public async IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> SynthesizeAsync(
        string text,
        string? voiceId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield return Result<DysonSpeechAudioChunk, string>.AsError("Text to speak is empty.");
            yield break;
        }

        var voice = await ResolveVoicePathAsync(_models, _settings, _installRoot, voiceId, cancellationToken)
            .ConfigureAwait(false);
        if (voice.IsError)
        {
            yield return Result<DysonSpeechAudioChunk, string>.AsError(voice.Error);
            yield break;
        }

        var begin = await Task.Run(() => BeginAsync(_installRoot, text, voice.Value, cancellationToken), CancellationToken.None)
            .ConfigureAwait(false);
        if (begin.IsError)
        {
            yield return Result<DysonSpeechAudioChunk, string>.AsError(begin.Error);
            yield break;
        }

        var stdout = begin.Value;
        var finished = false;
        try
        {
            while (true)
            {
                var frame = await ReadFrameAsync(stdout, cancellationToken).ConfigureAwait(false);
                if (frame.IsError)
                {
                    yield return Result<DysonSpeechAudioChunk, string>.AsError(frame.Error);
                    yield break;
                }

                var (kind, payload) = frame.Value;
                if (kind == FrameKind.End)
                {
                    finished = true;
                    yield return Result<DysonSpeechAudioChunk, string>.AsValue(new DysonSpeechAudioChunk([], SampleRate, IsLast: true));
                    yield break;
                }

                if (kind == FrameKind.Error)
                {
                    finished = true;
                    yield return Result<DysonSpeechAudioChunk, string>.AsError("Pocket TTS: " + Encoding.UTF8.GetString(payload));
                    yield break;
                }

                yield return Result<DysonSpeechAudioChunk, string>.AsValue(new DysonSpeechAudioChunk(payload, SampleRate, IsLast: false));
            }
        }
        finally
        {
            // An abandoned or broken stream leaves frames in the pipe: drop the bridge so the next call starts clean.
            if (!finished)
                StopBridge();
            Gate.Release();
        }
    }

    /// <summary>
    /// Null / blank id ⇒ <see cref="DysonAppSettingKeys.SpeechOutputVoiceId"/> ⇒ <see cref="DefaultVoiceId"/>.
    /// Only catalog rows resolve; the path is derived from the install root, never stored.
    /// </summary>
    internal static async Task<Result<string, string>> ResolveVoicePathAsync(
        IDysonSpeechModelRepository models,
        IDysonSubjectSettingsRepository settings,
        string installRoot,
        string? voiceId,
        CancellationToken cancellationToken)
    {
        var id = voiceId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            var setting = await settings.GetSettingAsync(DysonAppSettingKeys.SpeechOutputVoiceId, cancellationToken)
                .ConfigureAwait(false);
            id = setting.IsSuccess && !string.IsNullOrWhiteSpace(setting.Value) ? setting.Value.Trim() : DefaultVoiceId;
        }

        var voices = await models.ListVoicesAsync(Id, cancellationToken).ConfigureAwait(false);
        if (voices.IsError)
            return Result<string, string>.AsError(voices.Error);

        var row = voices.Value.FirstOrDefault(v => string.Equals(v.VoiceId, id, StringComparison.OrdinalIgnoreCase));
        if (row is null || !VoiceIdPattern().IsMatch(row.VoiceId))
        {
            return Result<string, string>.AsError(
                $"Unknown Pocket TTS voice '{id}'. Run Install dependencies in Settings → Voice to add alba.");
        }

        return Result<string, string>.AsValue(
            Path.Combine(DysonSpeechPaths.GetPocketVoicesDir(installRoot), row.VoiceId + ".safetensors"));
    }

    /// <summary>Kills the bridge if this process started one. Safe to call from app shutdown and uninstall.</summary>
    public static void StopBridge()
    {
        Process? bridge;
        lock (ProcessLock)
        {
            bridge = _bridge;
            _bridge = null;
        }

        if (bridge is null)
            return;
        try
        {
            if (!bridge.HasExited)
                bridge.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            bridge.Dispose();
        }
    }

    /// <summary>Takes <see cref="Gate"/>, checks files, starts the bridge if needed, and sends one request.</summary>
    private static async Task<Result<Stream, string>> BeginAsync(
        string installRoot,
        string text,
        string voicePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result<Stream, string>.AsError("Speech synthesis was cancelled.");
        }

        try
        {
            var status = DysonSpeechInstaller.Probe(installRoot);
            if (!status.PocketInstalled)
                return Fail("Pocket TTS is not installed. Open Settings → Voice and run Install dependencies.");
            if (!File.Exists(voicePath))
                return Fail($"Voice file {Path.GetFileName(voicePath)} is missing. Run Install dependencies to repair.");

            var bridge = EnsureBridge(installRoot);
            if (bridge.IsError)
                return Fail(bridge.Error);

            var line = JsonSerializer.Serialize(new { text, voicePath }) + "\n";
            var stdin = bridge.Value.StandardInput.BaseStream;
            await stdin.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            return Result<Stream, string>.AsValue(bridge.Value.StandardOutput.BaseStream);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException
                                       or System.ComponentModel.Win32Exception)
        {
            StopBridge();
            return Fail($"Pocket TTS bridge failed: {ex.Message}");
        }

        Result<Stream, string> Fail(string message)
        {
            Gate.Release();
            return Result<Stream, string>.AsError(message);
        }
    }

    private static Result<Process, string> EnsureBridge(string installRoot)
    {
        lock (ProcessLock)
        {
            if (_bridge is { HasExited: false })
                return Result<Process, string>.AsValue(_bridge);

            _bridge?.Dispose();
            _bridge = null;
            lock (StderrTail)
                StderrTail.Clear();

            var psi = DysonSpeechInstaller.PythonStartInfo(
                installRoot,
                [DysonSpeechPaths.GetPocketSite(installRoot), DysonSpeechPaths.GetTorchDir(installRoot)],
                [DysonSpeechPaths.GetPocketBridge(installRoot), "--config", DysonSpeechPaths.GetPocketConfig(installRoot)]);
            psi.RedirectStandardInput = true;
            var process = Process.Start(psi);
            if (process is null)
                return Result<Process, string>.AsError("Could not start the Pocket TTS bridge.");

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;
                lock (StderrTail)
                {
                    StderrTail.Enqueue(e.Data);
                    while (StderrTail.Count > 20)
                        StderrTail.Dequeue();
                }
            };
            process.BeginErrorReadLine();
            _bridge = process;
            return Result<Process, string>.AsValue(process);
        }
    }

    private enum FrameKind
    {
        Pcm,
        End,
        Error,
    }

    private static async Task<Result<(FrameKind Kind, byte[] Payload), string>> ReadFrameAsync(
        Stream stdout,
        CancellationToken cancellationToken)
    {
        try
        {
            var header = new byte[4];
            await stdout.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length == 0)
                return Result<(FrameKind, byte[]), string>.AsValue((FrameKind.End, []));

            var size = Math.Abs(length);
            if (size > 64 * 1024 * 1024)
                return Result<(FrameKind, byte[]), string>.AsError($"Pocket TTS bridge sent a {size:N0}-byte frame.");

            var payload = new byte[size];
            await stdout.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            return Result<(FrameKind, byte[]), string>.AsValue((length > 0 ? FrameKind.Pcm : FrameKind.Error, payload));
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException
                                       or OperationCanceledException)
        {
            string tail;
            lock (StderrTail)
                tail = string.Join("\n", StderrTail);
            return Result<(FrameKind, byte[]), string>.AsError(
                $"Pocket TTS bridge stopped: {ex.Message}" + (tail.Length > 0 ? "\n" + tail : ""));
        }
    }

    [GeneratedRegex("^[a-z0-9_-]{1,64}$", RegexOptions.IgnoreCase)]
    private static partial Regex VoiceIdPattern();
}
