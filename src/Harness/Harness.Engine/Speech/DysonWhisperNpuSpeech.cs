using System.Diagnostics;

namespace DysonHarness;

/// <summary>
/// Whisper tiny/base on Intel NPU via one embedded Python bridge and <c>WhisperPipeline(..., "NPU")</c>.
/// A missing NPU, a missing runtime, or a pipeline error is a <see cref="Result{T,TError}"/>. This type never
/// starts CrispASR and never retries on CPU or on OpenVINO <c>GPU</c>.
/// </summary>
public sealed class DysonWhisperNpuSpeech(string installRoot, string runtimeId) : IDysonSpeechToText
{
    public const string BridgeResourceName = "DysonHarness.Speech.whisper_npu.py";

    private readonly string _installRoot = installRoot ?? throw new ArgumentNullException(nameof(installRoot));
    private readonly string _runtimeId = runtimeId ?? throw new ArgumentNullException(nameof(runtimeId));

    public string RuntimeId => _runtimeId;

    public async IAsyncEnumerable<Result<DysonSpeechChunk, string>> TranscribeAsync(
        Stream audio,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var result = await Task.Run(() => TranscribeCoreAsync(audio, cancellationToken), CancellationToken.None)
            .ConfigureAwait(false);
        yield return result.IsError
            ? Result<DysonSpeechChunk, string>.AsError(result.Error)
            : Result<DysonSpeechChunk, string>.AsValue(new DysonSpeechChunk(result.Value, 0, 0, true));
    }

    private async Task<Result<string, string>> TranscribeCoreAsync(Stream audio, CancellationToken cancellationToken)
    {
        if (!DysonGgufSpeechModels.IsWhisper(_runtimeId))
            return Result<string, string>.AsError($"'{_runtimeId}' is not a Whisper model.");

        var python = DysonSpeechPaths.GetPythonExe(_installRoot);
        if (!File.Exists(python))
        {
            return Result<string, string>.AsError(
                "Intel NPU runtime is not installed (speech Python is missing). Open Settings → Voice and install Whisper. Not CPU and not Vulkan.");
        }

        var site = DysonSpeechPaths.GetOpenVinoSite(_installRoot);
        if (!Directory.Exists(site))
        {
            return Result<string, string>.AsError(
                "Intel NPU runtime is not installed (OpenVINO site is missing). Open Settings → Voice and install Whisper. Not CPU and not Vulkan.");
        }

        var modelDir = DysonGgufSpeechModels.GetOpenVinoModelDir(_installRoot, _runtimeId);
        if (!File.Exists(Path.Combine(modelDir, "openvino_encoder_model.xml")))
        {
            return Result<string, string>.AsError(
                "Intel NPU Whisper model is not installed. Open Settings → Voice and install the Whisper row. Not CPU and not Vulkan.");
        }

        var wav = Path.Combine(Path.GetTempPath(), $"dyson-whisper-npu-{Guid.NewGuid():N}.wav");
        var script = Path.Combine(Path.GetTempPath(), $"dyson-whisper-npu-{Guid.NewGuid():N}.py");
        try
        {
            var written = await WriteWavAsync(audio, wav, cancellationToken).ConfigureAwait(false);
            if (written.IsError)
                return Result<string, string>.AsError(written.Error);
            var bridge = WriteBridge(script);
            if (bridge.IsError)
                return Result<string, string>.AsError(bridge.Error);

            var psi = DysonSpeechInstaller.PythonStartInfo(
                _installRoot,
                [site],
                [script, wav, modelDir]);
            using var process = Process.Start(psi);
            if (process is null)
            {
                return Result<string, string>.AsError(
                    "Intel NPU could not start the speech Python. Not CPU and not Vulkan.");
            }

            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // ponytail: first NPU compile can sit for minutes. Upgrade: a shorter timeout after the cache exists.
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return Result<string, string>.AsError(
                    cancellationToken.IsCancellationRequested
                        ? "Speech was cancelled."
                        : "Intel NPU Whisper timed out after 10 minutes. Not CPU and not Vulkan.");
            }

            var err = (await stderr.ConfigureAwait(false)).Trim();
            var text = (await stdout.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0)
            {
                var detail = err.Length > 0 ? err : text;
                if (detail.Length > 1200)
                    detail = detail[^1200..];
                if (detail.Length == 0)
                    detail = $"bridge exited {process.ExitCode}.";
                if (detail.Contains("Intel NPU", StringComparison.Ordinal))
                    return Result<string, string>.AsError(detail);
                return Result<string, string>.AsError("Intel NPU: " + detail + " Not CPU and not Vulkan.");
            }

            return Result<string, string>.AsValue(text);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Result<string, string>.AsError(
                $"Intel NPU Whisper failed: {ex.Message}. Not CPU and not Vulkan.");
        }
        finally
        {
            TryDelete(wav);
            TryDelete(script);
        }
    }

    private static async Task<VoidResult<string>> WriteWavAsync(
        Stream audio, string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        var buffer = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = await audio.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > DysonPhononSpeech.MaxAudioBytes)
            {
                return new VoidResult<string>(
                    $"Audio clip is over {DysonPhononSpeech.MaxAudioBytes / (1024 * 1024)} MB.");
            }

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return total == 0
            ? new VoidResult<string>("Audio clip is empty.")
            : VoidResult<string>.Success;
    }

    private static VoidResult<string> WriteBridge(string path)
    {
        using var bridge = typeof(DysonWhisperNpuSpeech).Assembly.GetManifestResourceStream(BridgeResourceName);
        if (bridge is null)
            return new VoidResult<string>($"Intel NPU bridge '{BridgeResourceName}' is missing.");
        using var output = File.Create(path);
        bridge.CopyTo(output);
        return VoidResult<string>.Success;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
