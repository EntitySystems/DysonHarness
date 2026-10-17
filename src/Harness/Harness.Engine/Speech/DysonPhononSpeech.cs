using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// Phonon 2 (<c>phonon-2</c>) speech to text through a loopback <c>fermion serve</c> sidecar
/// (OpenAI-style <c>POST /v1/audio/transcriptions</c>, <c>verbose_json</c>). The clip is copied to a temp
/// WAV (32 MB cap). <c>PYTHONPATH</c> is <c>phonon/site</c> plus <c>torch/</c>; <c>HF_HUB_OFFLINE=1</c>.
/// </summary>
public sealed class DysonPhononSpeech(string installRoot) : IDysonSpeechToText
{
    public const string Id = "phonon-2";
    public const long MaxAudioBytes = 32L * 1024 * 1024;

    private const string ServeScript =
        "import sys\nfrom fermion.cli import main\nsys.argv = ['fermion'] + sys.argv[1:]\nmain()\n";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    // ponytail: one process-wide sidecar (single install root per app process); upgrade path is a keyed host.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _sidecar;
    private static int _port;
    private static readonly Queue<string> OutputTail = new();

    private readonly string _installRoot = installRoot ?? throw new ArgumentNullException(nameof(installRoot));

    public string RuntimeId => Id;

    public async IAsyncEnumerable<Result<DysonSpeechChunk, string>> TranscribeAsync(
        Stream audio,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var result = await Task.Run(() => TranscribeCoreAsync(_installRoot, audio, cancellationToken), CancellationToken.None)
            .ConfigureAwait(false);
        if (result.IsError)
        {
            yield return Result<DysonSpeechChunk, string>.AsError(result.Error);
            yield break;
        }

        foreach (var chunk in result.Value)
            yield return Result<DysonSpeechChunk, string>.AsValue(chunk);
    }

    /// <summary>Starts the sidecar if needed and checks <c>GET /health</c> (installer smoke).</summary>
    internal static async Task<VoidResult<string>> CheckHealthAsync(string installRoot, CancellationToken cancellationToken)
    {
        var port = await EnsureSidecarAsync(installRoot, cancellationToken).ConfigureAwait(false);
        return port.IsError ? new VoidResult<string>(port.Error) : VoidResult<string>.Success;
    }

    /// <summary>Kills the sidecar if this process started one. Safe to call from app shutdown and uninstall.</summary>
    public static void StopSidecar()
    {
        Process? sidecar;
        lock (OutputTail)
        {
            sidecar = _sidecar;
            _sidecar = null;
        }

        if (sidecar is null)
            return;
        try
        {
            if (!sidecar.HasExited)
                sidecar.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            sidecar.Dispose();
        }
    }

    /// <summary>OpenAI <c>verbose_json</c> → each segment, then one final chunk with the full text.</summary>
    internal static Result<IReadOnlyList<DysonSpeechChunk>, string> ParseVerboseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var chunks = new List<DysonSpeechChunk>();
            var endMs = 0;
            if (root.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
            {
                foreach (var segment in segments.EnumerateArray())
                {
                    var text = segment.TryGetProperty("text", out var t) ? (t.GetString() ?? "").Trim() : "";
                    var start = Ms(segment, "start");
                    var end = Ms(segment, "end");
                    endMs = Math.Max(endMs, end);
                    if (text.Length > 0)
                        chunks.Add(new DysonSpeechChunk(text, start, end, IsFinal: false));
                }
            }

            var full = root.TryGetProperty("text", out var all) ? (all.GetString() ?? "").Trim() : "";
            if (full.Length == 0)
                full = string.Join(" ", chunks.Select(c => c.Text));
            chunks.Add(new DysonSpeechChunk(full, 0, endMs, IsFinal: true));
            return Result<IReadOnlyList<DysonSpeechChunk>, string>.AsValue(chunks);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Result<IReadOnlyList<DysonSpeechChunk>, string>.AsError($"Phonon 2 returned unreadable JSON: {ex.Message}");
        }

        static int Ms(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.TryGetDouble(out var seconds)
                ? (int)Math.Round(seconds * 1000)
                : 0;
    }

    private static async Task<Result<IReadOnlyList<DysonSpeechChunk>, string>> TranscribeCoreAsync(
        string installRoot,
        Stream audio,
        CancellationToken cancellationToken)
    {
        if (!DysonSpeechInstaller.Probe(installRoot).PhononInstalled)
            return Fail("Phonon 2 is not installed. Open Settings → Voice and run Install dependencies.");

        var wav = Path.Combine(Path.GetTempPath(), $"dyson-stt-{Guid.NewGuid():N}.wav");
        try
        {
            await using (var file = new FileStream(wav, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = await audio.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxAudioBytes)
                        return Fail($"Audio clip is over {MaxAudioBytes / (1024 * 1024)} MB.");
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (total == 0)
                    return Fail("Audio clip is empty.");
            }

            var port = await EnsureSidecarAsync(installRoot, cancellationToken).ConfigureAwait(false);
            if (port.IsError)
                return Fail(port.Error);

            using var form = new MultipartFormDataContent();
            var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(wav, cancellationToken).ConfigureAwait(false));
            bytes.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(bytes, "file", "clip.wav");
            form.Add(new StringContent("verbose_json"), "response_format");
            // A local-dir `fermion serve` only answers to its served id, which is the absolute model directory.
            form.Add(new StringContent(DysonSpeechPaths.GetPhononModelDir(installRoot)), "model");

            using var response = await Http
                .PostAsync($"http://127.0.0.1:{port.Value}/v1/audio/transcriptions", form, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Fail($"Phonon 2 HTTP {(int)response.StatusCode}: {Clip(body)}");

            return ParseVerboseJson(body);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException
                                       or UnauthorizedAccessException)
        {
            return Fail($"Phonon 2 transcription failed: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(wav);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        static Result<IReadOnlyList<DysonSpeechChunk>, string> Fail(string message) =>
            Result<IReadOnlyList<DysonSpeechChunk>, string>.AsError(message);
    }

    private static async Task<Result<int, string>> EnsureSidecarAsync(string installRoot, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sidecar is { HasExited: false } && await IsHealthyAsync(_port, cancellationToken).ConfigureAwait(false))
                return Result<int, string>.AsValue(_port);

            StopSidecar();
            lock (OutputTail)
                OutputTail.Clear();

            var port = FreeLoopbackPort();
            var psi = DysonSpeechInstaller.PythonStartInfo(
                installRoot,
                [DysonSpeechPaths.GetPhononSite(installRoot), DysonSpeechPaths.GetTorchDir(installRoot)],
                ["-c", ServeScript, "serve", DysonSpeechPaths.GetPhononModelDir(installRoot),
                 "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            var process = Process.Start(psi);
            if (process is null)
                return Result<int, string>.AsError("Could not start the Phonon 2 sidecar.");

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
            process.OutputDataReceived += keep;
            process.ErrorDataReceived += keep;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            lock (OutputTail)
            {
                _sidecar = process;
                _port = port;
            }

            var deadline = DateTime.UtcNow + StartupTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                    return Result<int, string>.AsError($"Phonon 2 sidecar exited ({process.ExitCode}).\n{Tail()}");
                if (await IsHealthyAsync(port, cancellationToken).ConfigureAwait(false))
                    return Result<int, string>.AsValue(port);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            StopSidecar();
            return Result<int, string>.AsError($"Phonon 2 sidecar did not answer /health within {StartupTimeout.TotalSeconds:0} s.\n{Tail()}");
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception
                                       or InvalidOperationException)
        {
            StopSidecar();
            return Result<int, string>.AsError($"Phonon 2 sidecar failed: {ex.Message}");
        }
        finally
        {
            Gate.Release();
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

    private static string Tail()
    {
        lock (OutputTail)
            return string.Join("\n", OutputTail);
    }

    private static string Clip(string body) => body.Length > 400 ? body[..400] + "…" : body;
}
