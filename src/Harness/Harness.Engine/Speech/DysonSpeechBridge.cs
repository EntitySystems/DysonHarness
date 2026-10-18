using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// One lazy Python bridge process (one per TTS engine): one JSON line in, length-prefixed frames out
/// (&gt;0 PCM s16le, 0 end, &lt;0 UTF-8 error). One request at a time, restarted if it dies, stopped on app exit.
/// Also holds the voice resolution and MP3 enrollment plumbing shared by Pocket, Qwen3-TTS, and Chatterbox Turbo.
/// </summary>
internal sealed partial class DysonSpeechBridge(string name)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _processLock = new();
    private readonly Queue<string> _stderrTail = new();
    private Process? _process;

    /// <summary>Streams one utterance. <paramref name="check"/> runs inside the gate on the thread pool.</summary>
    public async IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> SynthesizeAsync(
        Func<VoidResult<string>> check,
        Func<ProcessStartInfo> startInfo,
        object request,
        int sampleRate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var begin = await Task.Run(() => BeginAsync(check, startInfo, request, cancellationToken), CancellationToken.None)
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
                    yield return Result<DysonSpeechAudioChunk, string>.AsValue(new DysonSpeechAudioChunk([], sampleRate, IsLast: true));
                    yield break;
                }

                if (kind == FrameKind.Error)
                {
                    finished = true;
                    yield return Result<DysonSpeechAudioChunk, string>.AsError($"{name}: " + Encoding.UTF8.GetString(payload));
                    yield break;
                }

                yield return Result<DysonSpeechAudioChunk, string>.AsValue(new DysonSpeechAudioChunk(payload, sampleRate, IsLast: false));
            }
        }
        finally
        {
            // An abandoned or broken stream leaves frames in the pipe: drop the bridge so the next call starts clean.
            if (!finished)
                Stop();
            _gate.Release();
        }
    }

    /// <summary>Sends one request that answers with a single end (success) or error frame.</summary>
    public async Task<VoidResult<string>> RequestAsync(
        Func<VoidResult<string>> check,
        Func<ProcessStartInfo> startInfo,
        object request,
        CancellationToken cancellationToken)
    {
        var begin = await Task.Run(() => BeginAsync(check, startInfo, request, cancellationToken), CancellationToken.None)
            .ConfigureAwait(false);
        if (begin.IsError)
            return new VoidResult<string>(begin.Error);

        var finished = false;
        try
        {
            var frame = await ReadFrameAsync(begin.Value, cancellationToken).ConfigureAwait(false);
            if (frame.IsError)
                return new VoidResult<string>(frame.Error);

            finished = frame.Value.Kind != FrameKind.Pcm;
            return frame.Value.Kind switch
            {
                FrameKind.End => VoidResult<string>.Success,
                FrameKind.Error => new VoidResult<string>(Encoding.UTF8.GetString(frame.Value.Payload)),
                _ => new VoidResult<string>($"{name} bridge sent audio for a non-audio request."),
            };
        }
        finally
        {
            if (!finished)
                Stop();
            _gate.Release();
        }
    }

    /// <summary>Kills the bridge if this process started one. Safe to call from app shutdown and uninstall.</summary>
    public void Stop()
    {
        Process? process;
        lock (_processLock)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Voice id for one engine. Explicit id ⇒ must be that engine's voice. Null ⇒
    /// <see cref="DysonAppSettingKeys.SpeechOutputVoiceId"/> when it names a voice of this engine ⇒
    /// <paramref name="defaultVoiceId"/> (null ⇒ newest voice by <c>CreatedUtc</c>) ⇒ <paramref name="noVoiceMessage"/>.
    /// </summary>
    public static async Task<Result<string, string>> ResolveVoiceIdAsync(
        IDysonSpeechModelRepository models,
        IDysonSubjectSettingsRepository settings,
        string runtimeId,
        string modelName,
        string? voiceId,
        string? defaultVoiceId,
        string noVoiceMessage,
        CancellationToken cancellationToken)
    {
        var voices = await models.ListVoicesAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (voices.IsError)
            return Result<string, string>.AsError(voices.Error);

        DysonSpeechVoiceEntity? Find(string? id) => string.IsNullOrWhiteSpace(id)
            ? null
            : voices.Value.FirstOrDefault(v =>
                string.Equals(v.VoiceId, id.Trim(), StringComparison.OrdinalIgnoreCase) && VoiceIdPattern().IsMatch(v.VoiceId));

        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            var explicitRow = Find(voiceId);
            return explicitRow is null
                ? Result<string, string>.AsError($"Unknown {modelName} voice '{voiceId.Trim()}'.")
                : Result<string, string>.AsValue(explicitRow.VoiceId);
        }

        var setting = await settings.GetSettingAsync(DysonAppSettingKeys.SpeechOutputVoiceId, cancellationToken)
            .ConfigureAwait(false);
        var row = Find(setting.IsSuccess ? setting.Value : null)
            ?? (defaultVoiceId is null
                ? voices.Value.Where(v => VoiceIdPattern().IsMatch(v.VoiceId)).MaxBy(v => v.CreatedUtc)
                : Find(defaultVoiceId));
        return row is null
            ? Result<string, string>.AsError(noVoiceMessage)
            : Result<string, string>.AsValue(row.VoiceId);
    }

    /// <summary>
    /// Train TTS model for engines that make a voice prompt (speaker embedding), never a fine-tune: copies the
    /// clip to <c>staging/enroll-{guid}.mp3</c> (or <c>.wav</c> when <paramref name="acceptWav"/>), sends
    /// <c>op: enroll</c>, always deletes the clip, then inserts the voice row and makes it the default voice.
    /// A failed DB step deletes the <c>.pt</c>.
    /// </summary>
    public async Task<Result<DysonSpeechVoiceEntity, string>> EnrollAsync(
        IDysonSpeechModelRepository models,
        IDysonSubjectSettingsRepository settings,
        string installRoot,
        string runtimeId,
        string voicesDir,
        Func<VoidResult<string>> check,
        Func<ProcessStartInfo> startInfo,
        Stream clip,
        string fileName,
        CancellationToken cancellationToken,
        bool acceptWav = false)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var extension = Path.GetExtension(fileName ?? "");
        var mp3 = extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase);
        var wav = acceptWav && extension.Equals(".wav", StringComparison.OrdinalIgnoreCase);
        if (!mp3 && !wav)
        {
            return Result<DysonSpeechVoiceEntity, string>.AsError(acceptWav
                ? "Train TTS model takes one .mp3 or .wav file."
                : "Train TTS model takes one .mp3 file.");
        }

        var installed = await Task.Run(check, CancellationToken.None).ConfigureAwait(false);
        if (installed.IsError)
            return Result<DysonSpeechVoiceEntity, string>.AsError(installed.Error);

        var voices = await models.ListVoicesAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (voices.IsError)
            return Result<DysonSpeechVoiceEntity, string>.AsError(voices.Error);
        var voiceId = NewVoiceId(fileName!, voices.Value.Select(v => v.VoiceId));
        var outPath = Path.Combine(voicesDir, voiceId + ".pt");
        var staging = Path.Combine(installRoot, "staging");
        var audioPath = Path.Combine(staging, $"enroll-{Guid.NewGuid():N}{(mp3 ? ".mp3" : ".wav")}");

        VoidResult<string> enroll;
        try
        {
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(voicesDir);
            await using (var output = new FileStream(audioPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = await clip.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > DysonPhononSpeech.MaxAudioBytes)
                        return Result<DysonSpeechVoiceEntity, string>.AsError($"Audio clip is over {DysonPhononSpeech.MaxAudioBytes / (1024 * 1024)} MB.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            enroll = await RequestAsync(check, startInfo, new { op = "enroll", audioPath, outPath }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            enroll = new VoidResult<string>($"{name}: could not read the clip: {ex.Message}");
        }
        finally
        {
            TryDelete(audioPath);
        }

        if (enroll.IsError || !File.Exists(outPath))
        {
            TryDelete(outPath);
            return Result<DysonSpeechVoiceEntity, string>.AsError(enroll.IsError ? enroll.Error : $"{name} wrote no voice file.");
        }

        var row = await models.EnsureCatalogVoiceAsync(
                runtimeId, voiceId, Path.GetFileNameWithoutExtension(fileName)!, Path.GetFileName(installRoot), CancellationToken.None,
                sourceFileName: Path.GetFileName(fileName))
            .ConfigureAwait(false);
        var select = row.IsError
            ? row
            : await settings.SetSettingAsync(DysonAppSettingKeys.SpeechOutputVoiceId, voiceId, CancellationToken.None).ConfigureAwait(false);
        var saved = select.IsError
            ? null
            : await models.ListVoicesAsync(runtimeId, CancellationToken.None).ConfigureAwait(false);
        var entity = saved is { IsSuccess: true } ? saved.Value.FirstOrDefault(v => v.VoiceId == voiceId) : null;
        if (entity is not null)
            return Result<DysonSpeechVoiceEntity, string>.AsValue(entity);

        TryDelete(outPath);
        return Result<DysonSpeechVoiceEntity, string>.AsError(
            select.IsError ? select.Error : saved is { IsError: true } ? saved.Error : $"Voice '{voiceId}' was not saved.");
    }

    /// <summary>Slug of the file name (<c>^[a-z0-9_-]{1,64}$</c>); <c>-2</c>, <c>-3</c>, … when taken.</summary>
    internal static string NewVoiceId(string fileName, IEnumerable<string> taken)
    {
        var slug = SlugPattern().Replace(Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 60)
            slug = slug[..60].Trim('-');
        if (slug.Length == 0)
            slug = "voice";

        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = slug;
        for (var n = 2; used.Contains(id); n++)
            id = $"{slug}-{n}";
        return id;
    }

    /// <summary>Takes the gate, runs <paramref name="check"/>, starts the bridge if needed, and sends one request.</summary>
    private async Task<Result<Stream, string>> BeginAsync(
        Func<VoidResult<string>> check,
        Func<ProcessStartInfo> startInfo,
        object request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result<Stream, string>.AsError("Speech synthesis was cancelled.");
        }

        try
        {
            var ok = check();
            if (ok.IsError)
                return Fail(ok.Error);

            var process = EnsureProcess(startInfo);
            if (process.IsError)
                return Fail(process.Error);

            var line = JsonSerializer.Serialize(request) + "\n";
            var stdin = process.Value.StandardInput.BaseStream;
            await stdin.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            return Result<Stream, string>.AsValue(process.Value.StandardOutput.BaseStream);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException
                                       or System.ComponentModel.Win32Exception)
        {
            Stop();
            return Fail($"{name} bridge failed: {ex.Message}");
        }

        Result<Stream, string> Fail(string message)
        {
            _gate.Release();
            return Result<Stream, string>.AsError(message);
        }
    }

    private Result<Process, string> EnsureProcess(Func<ProcessStartInfo> startInfo)
    {
        lock (_processLock)
        {
            if (_process is { HasExited: false })
                return Result<Process, string>.AsValue(_process);

            _process?.Dispose();
            _process = null;
            lock (_stderrTail)
                _stderrTail.Clear();

            var psi = startInfo();
            psi.RedirectStandardInput = true;
            var process = Process.Start(psi);
            if (process is null)
                return Result<Process, string>.AsError($"Could not start the {name} bridge.");

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;
                lock (_stderrTail)
                {
                    _stderrTail.Enqueue(e.Data);
                    while (_stderrTail.Count > 20)
                        _stderrTail.Dequeue();
                }
            };
            process.BeginErrorReadLine();
            _process = process;
            return Result<Process, string>.AsValue(process);
        }
    }

    private enum FrameKind
    {
        Pcm,
        End,
        Error,
    }

    private async Task<Result<(FrameKind Kind, byte[] Payload), string>> ReadFrameAsync(
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
                return Result<(FrameKind, byte[]), string>.AsError($"{name} bridge sent a {size:N0}-byte frame.");

            var payload = new byte[size];
            await stdout.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            return Result<(FrameKind, byte[]), string>.AsValue((length > 0 ? FrameKind.Pcm : FrameKind.Error, payload));
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException
                                       or OperationCanceledException)
        {
            string tail;
            lock (_stderrTail)
                tail = string.Join("\n", _stderrTail);
            return Result<(FrameKind, byte[]), string>.AsError(
                $"{name} bridge stopped: {ex.Message}" + (tail.Length > 0 ? "\n" + tail : ""));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ponytail: a locked leftover clip stays in staging until the next install clears it.
        }
    }

    [GeneratedRegex("^[a-z0-9_-]{1,64}$", RegexOptions.IgnoreCase)]
    private static partial Regex VoiceIdPattern();

    [GeneratedRegex("[^a-z0-9_-]+")]
    private static partial Regex SlugPattern();
}
