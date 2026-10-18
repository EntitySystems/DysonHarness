using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// Phonon 2, Pocket TTS, Qwen3-TTS 0.6B Base, and Chatterbox Turbo GGUF through one out-of-process
/// CrispASR <c>crispasr.exe --server</c> (its ggml must not share a process with transcribe.dll).
/// TTS is 24 kHz PCM s16le. One sidecar per process: switching model restarts it.
/// </summary>
public sealed class DysonCrispSpeech(
    IDysonSpeechModelRepository models,
    IDysonSubjectSettingsRepository settings,
    string installRoot,
    string runtimeId) : IDysonTextToSpeech, IDysonSpeechToText
{
    public const int SampleRate = 24_000;

    /// <summary>Sent on every voiced TTS request. CrispASR rejects a voice with no attestation.</summary>
    private const string ConsentAttestation =
        "The Dyson Harness user enrolled this clip in Settings → Voice and confirmed it is their own voice or they have the speaker's consent.";

    private const string QwenNoVoice =
        "No Qwen3-TTS GGUF voice yet. Use Train TTS model in Settings → Voice with a .wav clip and its transcript.";

    internal const string CpuRuntimeMissing =
        "CrispASR CPU runtime is not installed. Open Settings → Voice and use Repair CrispASR runtimes.";

    internal const string VulkanRuntimeMissing =
        "CrispASR GPU (Vulkan) runtime is not installed. Open Settings → Voice and use Repair CrispASR runtimes.";

    // Distinct from any repository error so a missing Pocket/Chatterbox voice can omit "voice".
    private const string NoVoiceOmit = "\u0001";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    // ponytail: one GGUF resident at a time (crispasr.exe and transcribe.dll each ship ggml). Upgrade path: a sidecar per runtime id.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _sidecar;
    private static int _port;
    private static string? _servingId;
    private static string? _servingRoot;
    private static bool _servingVulkan;
    private static readonly Queue<string> OutputTail = new();

    private readonly IDysonSpeechModelRepository _models = models ?? throw new ArgumentNullException(nameof(models));
    private readonly IDysonSubjectSettingsRepository _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly string _installRoot = installRoot ?? throw new ArgumentNullException(nameof(installRoot));
    private readonly string _runtimeId = runtimeId ?? throw new ArgumentNullException(nameof(runtimeId));

    public string RuntimeId => _runtimeId;

    public async IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> SynthesizeAsync(
        string text,
        string? voiceId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsTextToSpeech(_runtimeId))
        {
            yield return AudioError($"{DisplayName(_runtimeId)} is speech to text, not text to speech.");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            yield return AudioError("Text to speak is empty.");
            yield break;
        }

        var voice = await ResolveVoiceAsync(voiceId, cancellationToken).ConfigureAwait(false);
        if (voice.IsError)
        {
            yield return AudioError(voice.Error);
            yield break;
        }

        var vulkan = await UseVulkanAsync(cancellationToken).ConfigureAwait(false);
        if (vulkan.IsError)
        {
            yield return AudioError(vulkan.Error);
            yield break;
        }

        var port = await Task.Run(
                () => AcquireSidecarAsync(_installRoot, _runtimeId, vulkan.Value, cancellationToken), CancellationToken.None)
            .ConfigureAwait(false);
        if (port.IsError)
        {
            yield return AudioError(port.Error);
            yield break;
        }

        try
        {
            if (_runtimeId == DysonGgufSpeechModels.QwenGgufId && !string.IsNullOrEmpty(voice.Value))
            {
                var wavPath = Path.Combine(
                    DysonGgufSpeechModels.GetVoicesDir(_installRoot, _runtimeId), voice.Value + ".wav");
                var prepared = await Task.Run(() => EnsureQwenPromptFile(wavPath), CancellationToken.None)
                    .ConfigureAwait(false);
                if (prepared.IsError)
                {
                    yield return AudioError(prepared.Error);
                    yield break;
                }
            }

            await foreach (var chunk in ReadSpeechAsync(port.Value, text, voice.Value, cancellationToken).ConfigureAwait(false))
                yield return chunk;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async IAsyncEnumerable<Result<DysonSpeechChunk, string>> TranscribeAsync(
        Stream audio,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (!IsSpeechToText(_runtimeId))
        {
            yield return Result<DysonSpeechChunk, string>.AsError(
                $"{DisplayName(_runtimeId)} is text to speech, not speech to text.");
            yield break;
        }

        var vulkan = await UseVulkanAsync(cancellationToken).ConfigureAwait(false);
        if (vulkan.IsError)
        {
            yield return Result<DysonSpeechChunk, string>.AsError(vulkan.Error);
            yield break;
        }

        var result = await Task.Run(
                () => TranscribeCoreAsync(_installRoot, _runtimeId, vulkan.Value, audio, cancellationToken), CancellationToken.None)
            .ConfigureAwait(false);
        if (result.IsError)
        {
            yield return Result<DysonSpeechChunk, string>.AsError(result.Error);
            yield break;
        }

        foreach (var chunk in result.Value)
            yield return Result<DysonSpeechChunk, string>.AsValue(chunk);
    }

    /// <summary>
    /// Enrollment, not a fine-tune. One 16-bit PCM <c>.wav</c> (32 MB) is copied to
    /// <c>{voiceId}.wav</c>. Qwen3-TTS GGUF is stored as 24 kHz mono (CrispASR returns empty audio at any other rate)
    /// and requires <paramref name="transcript"/>. A non-blank transcript is stored as <c>{voiceId}.txt</c> for
    /// Pocket, Qwen, and Chatterbox.
    /// </summary>
    public async Task<Result<DysonSpeechVoiceEntity, string>> EnrollVoiceAsync(
        Stream clip,
        string fileName,
        string? transcript,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!IsTextToSpeech(_runtimeId))
            return VoiceError($"{DisplayName(_runtimeId)} is speech to text, not text to speech.");
        if (!string.Equals(Path.GetExtension(fileName ?? ""), ".wav", StringComparison.OrdinalIgnoreCase))
            return VoiceError("GGUF voices take a .wav clip (16-bit PCM).");
        if (_runtimeId == DysonGgufSpeechModels.QwenGgufId && string.IsNullOrWhiteSpace(transcript))
            return VoiceError("Qwen3-TTS GGUF needs the exact transcript of the clip.");

        var read = await Task.Run(() => ReadClipAsync(clip, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        if (read.IsError)
            return VoiceError(read.Error);
        if (!IsPcm16Wav(read.Value))
            return VoiceError("GGUF voices take a .wav clip (16-bit PCM).");

        var stored = read.Value;
        if (_runtimeId == DysonGgufSpeechModels.QwenGgufId)
        {
            var prompt = ToQwenPromptWav(stored);
            if (prompt.IsError)
                return VoiceError(prompt.Error);
            stored = prompt.Value;
        }

        var installed = await Task.Run(() => RequireInstalled(_installRoot, _runtimeId), CancellationToken.None)
            .ConfigureAwait(false);
        if (installed.IsError)
            return VoiceError(installed.Error);

        var voices = await _models.ListVoicesAsync(_runtimeId, cancellationToken).ConfigureAwait(false);
        if (voices.IsError)
            return VoiceError(voices.Error);

        var voiceId = DysonSpeechBridge.NewVoiceId(fileName!, voices.Value.Select(v => v.VoiceId));
        var voicesDir = DysonGgufSpeechModels.GetVoicesDir(_installRoot, _runtimeId);
        var wavPath = Path.Combine(voicesDir, voiceId + ".wav");
        var txtPath = Path.Combine(voicesDir, voiceId + ".txt");
        var writeTranscript = !string.IsNullOrWhiteSpace(transcript);
        var wrote = await Task.Run(
                () => WriteClip(voicesDir, wavPath, writeTranscript ? txtPath : null, writeTranscript ? transcript : null, stored),
                CancellationToken.None)
            .ConfigureAwait(false);
        if (wrote.IsError)
            return VoiceError(wrote.Error);

        var saved = await _models.EnsureCatalogVoiceAsync(
                _runtimeId,
                voiceId,
                Path.GetFileNameWithoutExtension(fileName)!,
                Path.GetFileName(_installRoot),
                CancellationToken.None,
                sourceFileName: Path.GetFileName(fileName))
            .ConfigureAwait(false);
        var selected = saved.IsError
            ? saved
            : await _settings.SetSettingAsync(DysonAppSettingKeys.SpeechOutputVoiceId, voiceId, CancellationToken.None)
                .ConfigureAwait(false);
        if (!selected.IsError)
        {
            var listed = await _models.ListVoicesAsync(_runtimeId, CancellationToken.None).ConfigureAwait(false);
            var entity = listed.IsSuccess ? listed.Value.FirstOrDefault(v => v.VoiceId == voiceId) : null;
            if (entity is not null)
                return Result<DysonSpeechVoiceEntity, string>.AsValue(entity);
            selected = listed.IsError ? new VoidResult<string>(listed.Error) : new VoidResult<string>($"Voice '{voiceId}' was not saved.");
        }

        TryDelete(wavPath);
        TryDelete(txtPath);
        return VoiceError(selected.Error);
    }

    /// <summary>Kills the sidecar if this process started one. Safe from shutdown and uninstall.</summary>
    public static void StopSidecar()
    {
        Process? sidecar;
        lock (OutputTail)
        {
            sidecar = _sidecar;
            _sidecar = null;
            _servingId = null;
            _servingRoot = null;
            _servingVulkan = false;
            _port = 0;
        }

        if (sidecar is null)
            return;
        TryKill(sidecar);
        sidecar.Dispose();
    }

    /// <summary>Stops the sidecar unless it is already serving <paramref name="runtimeId"/>.</summary>
    internal static void StopSidecarUnless(string runtimeId)
    {
        lock (OutputTail)
        {
            if (string.Equals(_servingId, runtimeId, StringComparison.Ordinal) && Alive(_sidecar))
                return;
        }

        StopSidecar();
    }

    /// <summary>
    /// <c>crispasr.exe --list-backends</c> (60 s). Succeeds when <paramref name="backendName"/> is a whitespace token
    /// on stdout. A process that fails to start names a possibly missing VC++ runtime.
    /// </summary>
    internal static Task<VoidResult<string>> CheckRuntimeAsync(
        string installRoot,
        string backendName,
        CancellationToken cancellationToken) =>
        Task.Run(() => ListBackendsAsync(installRoot, backendName, cancellationToken), CancellationToken.None);

    private async Task<Result<string?, string>> ResolveVoiceAsync(string? voiceId, CancellationToken cancellationToken)
    {
        var qwen = _runtimeId == DysonGgufSpeechModels.QwenGgufId;
        var modelName = _runtimeId switch
        {
            DysonGgufSpeechModels.QwenGgufId => "Qwen3-TTS GGUF",
            DysonGgufSpeechModels.ChatterboxGgufId => "Chatterbox Turbo GGUF",
            _ => "Pocket TTS GGUF",
        };
        var resolved = await DysonSpeechBridge.ResolveVoiceIdAsync(
                _models, _settings, _runtimeId, modelName, voiceId, defaultVoiceId: null,
                qwen ? QwenNoVoice : NoVoiceOmit, cancellationToken)
            .ConfigureAwait(false);
        if (!resolved.IsError)
            return Result<string?, string>.AsValue(resolved.Value);
        if (!qwen && resolved.Error == NoVoiceOmit)
            return Result<string?, string>.AsValue(null);
        return Result<string?, string>.AsError(resolved.Error);
    }

    /// <summary><c>speech_crisp_device</c> is <c>gpu</c>. Missing, blank, or any other value is CPU, not an error.</summary>
    private async Task<Result<bool, string>> UseVulkanAsync(CancellationToken cancellationToken)
    {
        var device = await _settings.GetSettingAsync(DysonAppSettingKeys.SpeechCrispDevice, cancellationToken).ConfigureAwait(false);
        return device.IsError
            ? Result<bool, string>.AsError(device.Error)
            : Result<bool, string>.AsValue(string.Equals(device.Value?.Trim(), "gpu", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>On success the caller owns <see cref="Gate"/> and must release it.</summary>
    private static async Task<Result<int, string>> AcquireSidecarAsync(
        string installRoot,
        string runtimeId,
        bool useVulkan,
        CancellationToken cancellationToken)
    {
        var installed = RequireInstalled(installRoot, runtimeId);
        if (installed.IsError)
            return Result<int, string>.AsError(installed.Error);

        try
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result<int, string>.AsError("Speech was cancelled.");
        }

        try
        {
            var port = await EnsureLockedAsync(installRoot, runtimeId, useVulkan, cancellationToken).ConfigureAwait(false);
            if (port.IsError)
                Gate.Release();
            return port;
        }
        catch
        {
            Gate.Release();
            throw;
        }
    }

    private static async Task<Result<int, string>> EnsureLockedAsync(
        string installRoot,
        string runtimeId,
        bool useVulkan,
        CancellationToken cancellationToken)
    {
        if (Serving(installRoot, runtimeId, useVulkan) && await IsHealthyAsync(_port, cancellationToken).ConfigureAwait(false))
            return Result<int, string>.AsValue(_port);

        StopSidecar();
        lock (OutputTail)
            OutputTail.Clear();

        var backend = DysonGgufSpeechModels.CrispBackend(runtimeId);
        if (backend is null)
            return Result<int, string>.AsError($"'{runtimeId}' is not a CrispASR model.");

        var port = FreeLoopbackPort();
        var start = StartInfo(installRoot, runtimeId, backend, port, useVulkan);
        if (start.IsError)
            return Result<int, string>.AsError(start.Error);

        try
        {
            Directory.CreateDirectory(DysonGgufSpeechModels.GetVoicesDir(installRoot, runtimeId));
            var process = Process.Start(start.Value);
            if (process is null)
                return Result<int, string>.AsError(VcStart("Could not start crispasr.exe."));

            DataReceivedEventHandler keep = (_, e) =>
            {
                if (e.Data is null)
                    return;
                lock (OutputTail)
                {
                    OutputTail.Enqueue(e.Data);
                    while (OutputTail.Count > 20)
                        OutputTail.Dequeue();
                }
            };
            lock (OutputTail)
            {
                _sidecar = process;
                _port = port;
                _servingId = runtimeId;
                _servingRoot = Path.GetFullPath(installRoot);
                _servingVulkan = useVulkan;
            }

            process.OutputDataReceived += keep;
            process.ErrorDataReceived += keep;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var deadline = DateTime.UtcNow + StartupTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    var code = process.ExitCode;
                    var tail = Tail();
                    StopSidecar();
                    return Result<int, string>.AsError($"CrispASR sidecar exited ({code}).\n{tail}");
                }
                if (await IsHealthyAsync(port, cancellationToken).ConfigureAwait(false))
                    return Result<int, string>.AsValue(port);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            StopSidecar();
            return Result<int, string>.AsError(
                $"CrispASR sidecar did not answer /health within {StartupTimeout.TotalSeconds:0} s.\n{Tail()}");
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception
                                       or InvalidOperationException or IOException)
        {
            StopSidecar();
            var message = ex is System.ComponentModel.Win32Exception or InvalidOperationException
                ? VcStart(ex.Message)
                : $"CrispASR sidecar failed: {ex.Message}";
            return Result<int, string>.AsError(message);
        }
    }

    /// <summary>
    /// CPU: <c>crispasr/crispasr.exe</c>. GPU: <c>crispasr-vulkan/crispasr.exe</c> plus <c>--gpu-backend vulkan</c> after
    /// <c>--backend</c> (the model architecture). The chosen exe missing is an error naming the repair; the other exe is
    /// never used instead (the Vulkan build does not fall back to CPU, and GPU never silently becomes CPU).
    /// </summary>
    internal static Result<ProcessStartInfo, string> StartInfo(
        string installRoot, string runtimeId, string backend, int port, bool useVulkan)
    {
        var exe = useVulkan ? DysonGgufSpeechModels.GetCrispVulkanExe(installRoot) : DysonGgufSpeechModels.GetCrispExe(installRoot);
        if (!File.Exists(exe))
            return Result<ProcessStartInfo, string>.AsError(useVulkan ? VulkanRuntimeMissing : CpuRuntimeMissing);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--server");
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--backend");
        psi.ArgumentList.Add(backend);
        if (useVulkan)
        {
            psi.ArgumentList.Add("--gpu-backend");
            psi.ArgumentList.Add("vulkan");
        }

        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(DysonGgufSpeechModels.GetMainModelFile(installRoot, runtimeId));
        var companion = DysonGgufSpeechModels.GetCompanionFile(installRoot, runtimeId);
        if (companion is not null)
        {
            psi.ArgumentList.Add("--codec-model");
            psi.ArgumentList.Add(companion);
        }

        psi.ArgumentList.Add("--voice-dir");
        psi.ArgumentList.Add(DysonGgufSpeechModels.GetVoicesDir(installRoot, runtimeId));
        psi.ArgumentList.Add("--no-warmup");
        psi.ArgumentList.Add("--punc-model");
        psi.ArgumentList.Add("none");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(Math.Min(8, Environment.ProcessorCount).ToString(CultureInfo.InvariantCulture));
        return Result<ProcessStartInfo, string>.AsValue(psi);
    }

    private static async IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> ReadSpeechAsync(
        int port,
        string text,
        string? voice,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, string>
        {
            ["input"] = text,
            ["response_format"] = "pcm",
        };
        if (!string.IsNullOrEmpty(voice))
        {
            payload["voice"] = voice;
            payload["consent_attestation"] = ConsentAttestation;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/v1/audio/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage? response = null;
        string? sendError = null;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            sendError = $"CrispASR speech failed: {ex.Message}";
        }

        if (response is null)
        {
            yield return AudioError(sendError ?? "CrispASR speech failed.");
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                yield return AudioError($"CrispASR HTTP {(int)response.StatusCode}: {Clip(body)}");
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[8192];
            while (true)
            {
                int read;
                string? readError = null;
                try
                {
                    read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                {
                    read = -1;
                    readError = $"CrispASR speech failed: {ex.Message}";
                }

                if (readError is not null)
                {
                    yield return AudioError(readError);
                    yield break;
                }

                if (read == 0)
                    break;
                var pcm = new byte[read];
                Buffer.BlockCopy(buffer, 0, pcm, 0, read);
                yield return Result<DysonSpeechAudioChunk, string>.AsValue(new DysonSpeechAudioChunk(pcm, SampleRate, IsLast: false));
            }

            yield return Result<DysonSpeechAudioChunk, string>.AsValue(new DysonSpeechAudioChunk([], SampleRate, IsLast: true));
        }
    }

    private static async Task<Result<IReadOnlyList<DysonSpeechChunk>, string>> TranscribeCoreAsync(
        string installRoot,
        string runtimeId,
        bool useVulkan,
        Stream audio,
        CancellationToken cancellationToken)
    {
        var wav = Path.Combine(Path.GetTempPath(), $"dyson-crisp-{Guid.NewGuid():N}.wav");
        var held = false;
        try
        {
            var installed = RequireInstalled(installRoot, runtimeId);
            if (installed.IsError)
                return Result<IReadOnlyList<DysonSpeechChunk>, string>.AsError(installed.Error);

            await using (var file = new FileStream(wav, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = await audio.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > DysonPhononSpeech.MaxAudioBytes)
                        return Result<IReadOnlyList<DysonSpeechChunk>, string>.AsError(
                            $"Audio clip is over {DysonPhononSpeech.MaxAudioBytes / (1024 * 1024)} MB.");
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (total == 0)
                    return Result<IReadOnlyList<DysonSpeechChunk>, string>.AsError("Audio clip is empty.");
            }

            var port = await AcquireSidecarAsync(installRoot, runtimeId, useVulkan, cancellationToken).ConfigureAwait(false);
            if (port.IsError)
                return Result<IReadOnlyList<DysonSpeechChunk>, string>.AsError(port.Error);
            held = true;

            using var form = new MultipartFormDataContent();
            var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(wav, cancellationToken).ConfigureAwait(false));
            bytes.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(bytes, "file", "clip.wav");
            form.Add(new StringContent("verbose_json"), "response_format");
            using var response = await Http
                .PostAsync(
                    $"http://127.0.0.1:{port.Value.ToString(CultureInfo.InvariantCulture)}/v1/audio/transcriptions",
                    form,
                    cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result<IReadOnlyList<DysonSpeechChunk>, string>.AsError(
                    $"CrispASR HTTP {(int)response.StatusCode}: {Clip(body)}");

            var parsed = DysonPhononSpeech.ParseVerboseJson(body);
            return parsed.IsError
                ? Result<IReadOnlyList<DysonSpeechChunk>, string>.AsError(
                    parsed.Error.Replace("Phonon 2", "CrispASR", StringComparison.Ordinal))
                : parsed;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<DysonSpeechChunk>, string>.AsError($"CrispASR transcription failed: {ex.Message}");
        }
        finally
        {
            if (held)
                Gate.Release();
            TryDelete(wav);
        }
    }

    private static async Task<VoidResult<string>> ListBackendsAsync(
        string installRoot,
        string backendName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(backendName))
            return new VoidResult<string>("Backend name is empty.");

        var exe = DysonGgufSpeechModels.GetCrispExe(installRoot);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = DysonGgufSpeechModels.GetCrispRuntimeDir(installRoot),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--list-backends");

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
                return VcResult(VcStart("Could not start crispasr.exe."));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                return new VoidResult<string>("crispasr.exe --list-backends timed out after 60 s.");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var missingDll = process.ExitCode == unchecked((int)0xC0000135);
                return new VoidResult<string>(
                    $"crispasr.exe --list-backends exited {process.ExitCode}."
                    + (missingDll ? " A VC++ runtime may be missing." : "")
                    + (stderr.Length > 0 ? " " + Clip(stderr) : ""));
            }

            return ListsBackend(stdout, backendName)
                ? VoidResult<string>.Success
                : new VoidResult<string>($"crispasr.exe did not list backend '{backendName}'. {Clip(stdout)}");
        }
        catch (OperationCanceledException)
        {
            if (process is not null)
                TryKill(process);
            return new VoidResult<string>("crispasr.exe --list-backends was cancelled.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return VcResult(VcStart(ex.Message));
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static async Task<Result<byte[], string>> ReadClipAsync(Stream clip, CancellationToken cancellationToken)
    {
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[1 << 16];
            long total = 0;
            int read;
            while ((read = await clip.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > DysonPhononSpeech.MaxAudioBytes)
                    return Result<byte[], string>.AsError(
                        $"Audio clip is over {DysonPhononSpeech.MaxAudioBytes / (1024 * 1024)} MB.");
                buffer.Write(chunk, 0, read);
            }

            return total == 0
                ? Result<byte[], string>.AsError("Audio clip is empty.")
                : Result<byte[], string>.AsValue(buffer.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return Result<byte[], string>.AsError($"Could not read the clip: {ex.Message}");
        }
    }

    private static VoidResult<string> WriteClip(string dir, string wavPath, string? txtPath, string? transcript, byte[] wav)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(wavPath, wav);
            if (txtPath is not null)
                File.WriteAllText(txtPath, transcript);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(wavPath);
            if (txtPath is not null)
                TryDelete(txtPath);
            return new VoidResult<string>($"Could not store the voice clip: {ex.Message}");
        }
    }

    /// <summary>
    /// CrispASR <c>qwen3_tts_set_voice_prompt</c> accepts only 24 kHz PCM (it averages channels) and the
    /// server reports that refusal as empty audio. Qwen prompts are stored as 24 kHz mono 16-bit PCM.
    /// </summary>
    internal static Result<byte[], string> ToQwenPromptWav(ReadOnlySpan<byte> wav)
    {
        if (!TryReadPcm16(wav, out var rate, out var channels, out var pcm))
            return Result<byte[], string>.AsError("Qwen3-TTS GGUF needs a 16-bit PCM clip with audio.");

        var frames = pcm.Length / (channels * 2);
        var mono = new int[frames];
        for (var i = 0; i < frames; i++)
        {
            long sum = 0;
            var at = i * channels * 2;
            for (var c = 0; c < channels; c++)
                sum += BinaryPrimitives.ReadInt16LittleEndian(pcm[(at + (c * 2))..]);
            mono[i] = (int)Math.Round(sum / (double)channels);
        }

        var outCount = (int)Math.Round(frames * (double)SampleRate / rate);
        if (outCount < 1)
            outCount = 1;
        // ponytail: box resample. Upsampling holds a sample; non-integer ratios alias. Upgrade path: windowed sinc.
        var samples = new short[outCount];
        for (var i = 0; i < outCount; i++)
        {
            var start = (int)((long)i * frames / outCount);
            var end = (int)((long)(i + 1) * frames / outCount);
            if (end <= start)
                end = Math.Min(frames, start + 1);
            long sum = 0;
            for (var k = start; k < end; k++)
                sum += mono[k];
            var value = (int)Math.Round(sum / (double)(end - start));
            samples[i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        }

        return Result<byte[], string>.AsValue(WritePcm16Mono(samples));
    }

    /// <summary>
    /// Rewrites <paramref name="wavPath"/> when it is not already 24 kHz PCM so a voice saved before the
    /// resample (48 kHz stereo recordings) speaks without a re-enroll. 24 kHz files are left as stored.
    /// </summary>
    internal static VoidResult<string> EnsureQwenPromptFile(string wavPath)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(wavPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new VoidResult<string>($"Could not read the Qwen3-TTS GGUF voice clip: {ex.Message}");
        }

        if (TryReadPcm16(bytes, out var rate, out _, out _) && rate == SampleRate)
            return VoidResult<string>.Success;

        var prompt = ToQwenPromptWav(bytes);
        if (prompt.IsError)
            return new VoidResult<string>(prompt.Error);

        var tmp = wavPath + ".24k.tmp";
        try
        {
            File.WriteAllBytes(tmp, prompt.Value);
            File.Move(tmp, wavPath, overwrite: true);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            return new VoidResult<string>($"Could not store the Qwen3-TTS GGUF voice clip: {ex.Message}");
        }
    }

    private static bool TryReadPcm16(ReadOnlySpan<byte> wav, out int rate, out int channels, out ReadOnlySpan<byte> pcm)
    {
        rate = 0;
        channels = 0;
        pcm = default;
        if (wav.Length < 44
            || wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F'
            || wav[8] != (byte)'W' || wav[9] != (byte)'A' || wav[10] != (byte)'V' || wav[11] != (byte)'E')
            return false;

        var rateFound = 0;
        var channelsFound = 0;
        var bits = 0;
        var format = 0;
        var data = ReadOnlySpan<byte>.Empty;
        var sawFmt = false;
        var sawData = false;
        var off = 12;
        while (off + 8 <= wav.Length)
        {
            var sz = BinaryPrimitives.ReadInt32LittleEndian(wav[(off + 4)..]);
            if (sz < 0)
                return false;
            var payload = off + 8;
            if (sz > wav.Length - payload)
                return false;

            if (wav[off] == (byte)'f' && wav[off + 1] == (byte)'m' && wav[off + 2] == (byte)'t' && wav[off + 3] == (byte)' ')
            {
                if (sz < 16)
                    return false;
                format = BinaryPrimitives.ReadUInt16LittleEndian(wav[payload..]);
                channelsFound = BinaryPrimitives.ReadUInt16LittleEndian(wav[(payload + 2)..]);
                rateFound = BinaryPrimitives.ReadInt32LittleEndian(wav[(payload + 4)..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav[(payload + 14)..]);
                sawFmt = true;
            }
            else if (wav[off] == (byte)'d' && wav[off + 1] == (byte)'a' && wav[off + 2] == (byte)'t' && wav[off + 3] == (byte)'a')
            {
                data = wav.Slice(payload, sz);
                sawData = true;
            }

            var next = payload + sz;
            if ((sz & 1) == 1)
                next++;
            if (next <= off)
                return false;
            off = next;
            if (sawFmt && sawData)
                break;
        }

        var block = channelsFound * 2;
        if (!sawFmt || !sawData || format != 1 || bits != 16 || channelsFound < 1 || rateFound < 1
            || block < 2 || data.Length < block || data.Length % block != 0)
            return false;

        rate = rateFound;
        channels = channelsFound;
        pcm = data;
        return true;
    }

    private static byte[] WritePcm16Mono(ReadOnlySpan<short> samples)
    {
        var dataBytes = samples.Length * 2;
        var wav = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        "fmt "u8.CopyTo(wav.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), SampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), dataBytes);
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + (i * 2)), samples[i]);
        return wav;
    }

    private static bool IsPcm16Wav(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 44
            || wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F'
            || wav[8] != (byte)'W' || wav[9] != (byte)'A' || wav[10] != (byte)'V' || wav[11] != (byte)'E'
            || wav[12] != (byte)'f' || wav[13] != (byte)'m' || wav[14] != (byte)'t' || wav[15] != (byte)' ')
            return false;

        var fmtSize = BinaryPrimitives.ReadInt32LittleEndian(wav[16..]);
        if (fmtSize < 16)
            return false;
        var format = BinaryPrimitives.ReadUInt16LittleEndian(wav[20..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(wav[34..]);
        return format == 1 && bits == 16;
    }

    private static bool IsSpeechToText(string id) => id == DysonGgufSpeechModels.PhononGgufId;

    private static bool IsTextToSpeech(string id) =>
        id is DysonGgufSpeechModels.PocketGgufId or DysonGgufSpeechModels.QwenGgufId or DysonGgufSpeechModels.ChatterboxGgufId;

    private static VoidResult<string> RequireInstalled(string installRoot, string runtimeId) =>
        DysonSpeechInstaller.Probe(installRoot).IsInstalled(runtimeId)
            ? VoidResult<string>.Success
            : new VoidResult<string>(NotInstalled(runtimeId));

    private static string NotInstalled(string runtimeId) =>
        $"{DisplayName(runtimeId)} is not installed. Open Settings → Voice and install it.";

    private static string DisplayName(string runtimeId) => runtimeId switch
    {
        DysonGgufSpeechModels.PhononGgufId => DysonGgufSpeechModels.PhononGgufDisplayName,
        DysonGgufSpeechModels.PocketGgufId => DysonGgufSpeechModels.PocketGgufDisplayName,
        DysonGgufSpeechModels.QwenGgufId => DysonGgufSpeechModels.QwenGgufDisplayName,
        DysonGgufSpeechModels.ChatterboxGgufId => DysonGgufSpeechModels.ChatterboxGgufDisplayName,
        _ => runtimeId,
    };

    private static bool Serving(string installRoot, string runtimeId, bool useVulkan) =>
        Alive(_sidecar)
        && string.Equals(_servingId, runtimeId, StringComparison.Ordinal)
        && _servingVulkan == useVulkan
        && string.Equals(_servingRoot, Path.GetFullPath(installRoot), StringComparison.OrdinalIgnoreCase);

    private static bool Alive(Process? process)
    {
        try
        {
            return process is { HasExited: false };
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<bool> IsHealthyAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await Http.GetAsync($"http://127.0.0.1:{port}/health", timeout.Token).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool ListsBackend(string stdout, string backendName)
    {
        foreach (var token in stdout.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, backendName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static async Task DrainAsync(Task stdout, Task stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException
                                       or ObjectDisposedException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
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
        }
    }

    private static string Tail()
    {
        lock (OutputTail)
            return string.Join("\n", OutputTail);
    }

    private static string Clip(string body) => body.Length > 400 ? body[..400] + "…" : body;

    private static string VcStart(string message) =>
        $"Could not start crispasr.exe ({message}). A VC++ runtime may be missing.";

    private static VoidResult<string> VcResult(string message) => new(message);

    private static Result<DysonSpeechAudioChunk, string> AudioError(string message) =>
        Result<DysonSpeechAudioChunk, string>.AsError(message);

    private static Result<DysonSpeechVoiceEntity, string> VoiceError(string message) =>
        Result<DysonSpeechVoiceEntity, string>.AsError(message);
}
