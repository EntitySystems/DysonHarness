using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace DysonHarness;

public enum DysonSpeechStepState
{
    Running,
    Done,
    Failed,
}

/// <summary>One installer step update. <see cref="Download"/> is set while that step downloads.</summary>
public sealed record DysonSpeechInstallStep(
    string StepId,
    DysonSpeechStepState State,
    string? Detail = null,
    DysonDownloadProgress? Download = null);

/// <summary>File-level install state of the speech tree (DB rows mirror this at commit).</summary>
public sealed record DysonSpeechInstallStatus(bool PhononInstalled, bool PocketInstalled, string? Note);

/// <summary>
/// Shared speech installer: one CPython, one CPU torch, isolated Phonon-2 and Pocket TTS sites under
/// <see cref="InstallRoot"/> (<see cref="DysonSpeechPaths.GetInstallRoot"/>). Every byte comes through
/// <see cref="HttpClient"/> from <c>packaging/speech/manifest.json</c>; no system Python, pip, or git, no HF token.
/// One try per download; a hash mismatch or non-200 fails with the step id and URL.
/// </summary>
public sealed class DysonSpeechInstaller
{
    public const string SupportedRid = "win-x64";

    /// <summary>
    /// Preflight floor = measured win-x64 trial tree (python 64,991,494 + torch 631,984,027 + phonon 533,602,783
    /// + pocket 480,934,807 = 1,711,513,111 bytes) × 2. ponytail: re-measure when the pins change.
    /// </summary>
    public const long RequiredFreeBytes = 3_423_026_222;

    public static readonly IReadOnlyList<string> StepIds =
    [
        "preflight", "python-download", "python-hash", "python-extract",
        "torch-download", "torch-hash", "torch-install",
        "phonon-wheels-download", "phonon-wheels-hash", "phonon-wheels-install",
        "phonon-weights-download", "phonon-weights-hash", "phonon-weights-unpack",
        "pocket-wheels-download", "pocket-wheels-hash", "pocket-wheels-install",
        "pocket-weights-download", "pocket-weights-hash",
        "pocket-voice-download", "pocket-voice-hash",
        "pocket-config", "licenses", "commit", "smoke",
    ];

    internal const string BridgeResourceName = "DysonHarness.Speech.bridge.py";
    internal const string CatalogVoiceId = "alba";

    private static readonly string[] TreeDirs = ["python", "torch", "phonon", "pocket", "licenses"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private const string PhononUnpackScript =
        "import sys, tarfile, zstandard\n"
        + "with open(sys.argv[1], 'rb') as f, zstandard.ZstdDecompressor().stream_reader(f) as r, "
        + "tarfile.open(fileobj=r, mode='r|') as t:\n"
        + "    t.extractall(sys.argv[2], filter='data')\n";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DysonSpeechInstaller(HttpClient http, string installRoot)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        InstallRoot = Path.GetFullPath(installRoot);
    }

    public string InstallRoot { get; }

    public Task<DysonSpeechInstallStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Probe(InstallRoot), cancellationToken);

    /// <summary>
    /// Runs every step in <see cref="StepIds"/> order on the thread pool. Stops at the first
    /// <see cref="DysonSpeechStepState.Failed"/> step. Repair is the same pipeline.
    /// </summary>
    public async IAsyncEnumerable<DysonSpeechInstallStep> InstallAsync(
        IDysonSpeechModelRepository models,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (!_gate.Wait(0))
        {
            yield return new DysonSpeechInstallStep("preflight", DysonSpeechStepState.Failed, "An install is already running.");
            yield break;
        }

        var channel = Channel.CreateUnbounded<DysonSpeechInstallStep>(new UnboundedChannelOptions { SingleReader = true });
        var run = Task.Run(async () =>
        {
            try
            {
                await new Pipeline(this, models, channel.Writer, cancellationToken).RunAsync().ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
                _gate.Release();
            }
        }, CancellationToken.None);

        await foreach (var step in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            yield return step;

        await run.ConfigureAwait(false);
    }

    /// <summary>Stops sidecars this process started, deletes the variant tree, and clears both rows.</summary>
    public async Task<VoidResult<string>> UninstallAsync(
        IDysonSpeechModelRepository models,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (!_gate.Wait(0))
            return new VoidResult<string>("An install is already running.");

        try
        {
            var deleted = await Task.Run(() =>
            {
                StopSidecars();
                return DeleteTree(InstallRoot);
            }, CancellationToken.None).ConfigureAwait(false);
            if (deleted.IsError)
                return deleted;

            var ensure = await models.EnsureBuiltInAsync(cancellationToken).ConfigureAwait(false);
            if (ensure.IsError)
                return ensure;

            foreach (var id in new[] { DysonPhononSpeech.Id, DysonPocketSpeech.Id })
            {
                var mark = await models.MarkUninstalledAsync(id, cancellationToken).ConfigureAwait(false);
                if (mark.IsError)
                    return mark;
            }

            return VoidResult<string>.Success;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static bool IsSupportedPlatform() =>
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>
    /// Installed only when <c>install.json</c> matches the manifest pins, python and torch exist, and that
    /// model's weights exist. Sync file I/O: callers hop to the thread pool first.
    /// </summary>
    internal static DysonSpeechInstallStatus Probe(string installRoot)
    {
        if (!IsSupportedPlatform())
            return new(false, false, $"Speech install is {SupportedRid} only in this release.");

        var manifest = DysonSpeechManifest.LoadEmbedded();
        if (manifest.IsError)
            return new(false, false, manifest.Error);

        var stamp = ReadStamp(installRoot);
        if (stamp is null)
            return new(false, false, "Not installed.");
        if (!StampMatches(stamp, manifest.Value))
            return new(false, false, "Installed files do not match this version's pins. Run Install dependencies to repair.");

        if (!File.Exists(DysonSpeechPaths.GetPythonExe(installRoot))
            || !Directory.Exists(Path.Combine(DysonSpeechPaths.GetTorchDir(installRoot), "torch")))
        {
            return new(false, false, "Python or torch is missing. Run Install dependencies to repair.");
        }

        var phonon = Directory.Exists(DysonSpeechPaths.GetPhononModelDir(installRoot))
            && Directory.EnumerateFileSystemEntries(DysonSpeechPaths.GetPhononModelDir(installRoot)).Any();
        var pocket = File.Exists(DysonSpeechPaths.GetPocketModelFile(installRoot));
        return new(phonon, pocket, phonon && pocket ? null : "Model files are missing. Run Install dependencies to repair.");
    }

    internal static string BuildRequirements(IReadOnlyList<DysonSpeechFile> wheels, string wheelDir) =>
        string.Concat(wheels.Select(w => $"{Path.Combine(wheelDir, w.FileName)} --hash=sha256:{w.Sha256}\n"));

    /// <summary>
    /// Local Pocket config. <c>weights_path</c> stays the gated <c>hf://</c> string so the package's fallback
    /// (offline, no token) loads the local no-cloning file and sets <c>has_voice_cloning = False</c>.
    /// </summary>
    internal static string BuildPocketConfig(string installRoot)
    {
        string Local(string path) => path.Replace('\\', '/');
        var model = Local(DysonSpeechPaths.GetPocketModelFile(installRoot));
        var tokenizer = Local(Path.Combine(installRoot, "pocket", "model", "tokenizer.json"));
        return PocketConfigTemplate
            .Replace("{{WEIGHTS_WITHOUT_VOICE_CLONING}}", model, StringComparison.Ordinal)
            .Replace("{{TOKENIZER}}", tokenizer, StringComparison.Ordinal);
    }

    /// <summary>
    /// pocket_tts 3.3.0 <c>config/english.yaml</c> verbatim except the two local paths. <c>weights_path</c> is the
    /// package's gated URL on purpose (see <see cref="BuildPocketConfig"/>).
    /// </summary>
    internal const string PocketConfigTemplate = """
        weights_path: hf://kyutai/pocket-tts/languages/english/model.safetensors@983151f13aaeab1b13c1e5e3c2c383d49a9edf3f
        weights_path_without_voice_cloning: {{WEIGHTS_WITHOUT_VOICE_CLONING}}
        default_temperature: 0.3

        flow_lm:
          insert_bos_before_voice: true
          dtype: float32
          flow:
            depth: 6
            dim: 512
          transformer:
            d_model: 1024
            hidden_scale: 4
            max_period: 10000
            num_heads: 16
            num_layers: 6
          lookup_table:
            dim: 1024
            n_bins: 4000
            tokenizer: tokenizers
            tokenizer_path: {{TOKENIZER}}

        mimi:
          dtype: float32
          sample_rate: 24000
          inner_dim: 32
          outer_dim: 512
          channels: 1
          frame_rate: 12.5
          seanet:
            dimension: 512
            channels: 1
            n_filters: 64
            n_residual_layers: 1
            ratios:
            - 6
            - 5
            - 4
            kernel_size: 7
            residual_kernel_size: 3
            last_kernel_size: 3
            dilation_base: 2
            pad_mode: constant
            compress: 2
          transformer:
            d_model: 512
            num_heads: 8
            num_layers: 2
            layer_scale: 0.01
            context: 250
            dim_feedforward: 2048
            input_dimension: 512
            output_dimensions:
            - 512
          quantizer:
            dimension: 32
            output_dimension: 512

        """;

    internal static ProcessStartInfo PythonStartInfo(
        string installRoot,
        IReadOnlyList<string> pythonPath,
        IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(DysonSpeechPaths.GetPythonExe(installRoot))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = installRoot,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        psi.Environment.Remove("PYTHONHOME");
        psi.Environment["PYTHONPATH"] = string.Join(Path.PathSeparator, pythonPath);
        psi.Environment["PYTHONNOUSERSITE"] = "1";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["HF_HUB_OFFLINE"] = "1";
        psi.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        psi.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        psi.Environment["PIP_NO_INPUT"] = "1";
        return psi;
    }

    internal static void StopSidecars()
    {
        DysonPhononSpeech.StopSidecar();
        DysonPocketSpeech.StopBridge();
    }

    internal static VoidResult<string> DeleteTree(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return VoidResult<string>.Success;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new VoidResult<string>($"Could not delete {path}: {ex.Message}");
        }
    }

    private static DysonSpeechInstallStamp? ReadStamp(string installRoot)
    {
        try
        {
            var path = DysonSpeechPaths.GetInstallJson(installRoot);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DysonSpeechInstallStamp>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool StampMatches(DysonSpeechInstallStamp stamp, DysonSpeechManifest manifest) =>
        stamp.SchemaVersion == manifest.SchemaVersion
        && stamp.PythonVersion == manifest.PythonVersion
        && stamp.TorchRequirement == manifest.TorchRequirement
        && stamp.PhononPackage == manifest.PhononPackage
        && stamp.PocketPackage == manifest.PocketPackage
        && stamp.Rid == SupportedRid;

    private static DysonSpeechInstallStamp NewStamp(DysonSpeechManifest manifest) => new(
        manifest.SchemaVersion,
        manifest.PythonVersion,
        manifest.TorchRequirement,
        manifest.PhononPackage,
        manifest.PocketPackage,
        SupportedRid,
        DateTime.UtcNow);

    private sealed record DysonSpeechInstallStamp(
        int SchemaVersion,
        string PythonVersion,
        string TorchRequirement,
        string PhononPackage,
        string PocketPackage,
        string Rid,
        DateTime InstalledUtc);

    /// <summary>One install run. Everything here already runs on the thread pool.</summary>
    private sealed class Pipeline(
        DysonSpeechInstaller owner,
        IDysonSpeechModelRepository models,
        ChannelWriter<DysonSpeechInstallStep> writer,
        CancellationToken ct)
    {
        private readonly string _root = owner.InstallRoot;
        private string Staging => Path.Combine(_root, "staging");
        private string Downloads => Path.Combine(Staging, "downloads");
        private string Tree => Path.Combine(Staging, "tree");
        private string Old => Path.Combine(Staging, "old");
        private DysonSpeechManifest _manifest = null!;
        private DysonSpeechRidManifest _m = null!;

        public async Task RunAsync()
        {
            var phononWheels = Path.Combine(Downloads, "phonon-wheelhouse");
            var pocketWheels = Path.Combine(Downloads, "pocket-wheelhouse");
            var torchWheels = Path.Combine(Downloads, "torch");

            if (!await StepAsync("preflight", _ => Task.FromResult(Preflight()))) return;
            if (!await StepAsync("python-download", p => DownloadAllAsync("python-download", [_m.Python], Downloads, p))) return;
            if (!await StepAsync("python-hash", _ => VerifyAllAsync("python-hash", [_m.Python], Downloads))) return;
            if (!await StepAsync("python-extract", _ => ExtractPythonAsync())) return;
            if (!await StepAsync("torch-download", p => DownloadAllAsync("torch-download", _m.Torch, torchWheels, p))) return;
            if (!await StepAsync("torch-hash", _ => VerifyAllAsync("torch-hash", _m.Torch, torchWheels))) return;
            if (!await StepAsync("torch-install", _ => PipInstallAsync("torch-install", _m.Torch, torchWheels, DysonSpeechPaths.GetTorchDir(Tree), keepWheelhouseIn: null))) return;
            if (!await StepAsync("phonon-wheels-download", p => DownloadAllAsync("phonon-wheels-download", _m.PhononWheels, phononWheels, p))) return;
            if (!await StepAsync("phonon-wheels-hash", _ => VerifyAllAsync("phonon-wheels-hash", _m.PhononWheels, phononWheels))) return;
            if (!await StepAsync("phonon-wheels-install", _ => PipInstallAsync("phonon-wheels-install", _m.PhononWheels, phononWheels, DysonSpeechPaths.GetPhononSite(Tree), Path.Combine(Tree, "phonon", "wheelhouse")))) return;
            if (!await StepAsync("phonon-weights-download", p => DownloadAllAsync("phonon-weights-download", [_m.PhononWeights], Downloads, p))) return;
            if (!await StepAsync("phonon-weights-hash", _ => VerifyAllAsync("phonon-weights-hash", [_m.PhononWeights], Downloads))) return;
            if (!await StepAsync("phonon-weights-unpack", _ => UnpackPhononAsync())) return;
            if (!await StepAsync("pocket-wheels-download", p => DownloadAllAsync("pocket-wheels-download", _m.PocketWheels, pocketWheels, p))) return;
            if (!await StepAsync("pocket-wheels-hash", _ => VerifyAllAsync("pocket-wheels-hash", _m.PocketWheels, pocketWheels))) return;
            if (!await StepAsync("pocket-wheels-install", _ => PipInstallAsync("pocket-wheels-install", _m.PocketWheels, pocketWheels, DysonSpeechPaths.GetPocketSite(Tree), Path.Combine(Tree, "pocket", "wheelhouse")))) return;
            if (!await StepAsync("pocket-weights-download", p => DownloadAllAsync("pocket-weights-download", [_m.PocketWeights, _m.PocketTokenizer], Downloads, p))) return;
            if (!await StepAsync("pocket-weights-hash", _ => VerifyAllAsync("pocket-weights-hash", [_m.PocketWeights, _m.PocketTokenizer], Downloads))) return;
            if (!await StepAsync("pocket-voice-download", p => DownloadAllAsync("pocket-voice-download", [_m.PocketVoice], Downloads, p))) return;
            if (!await StepAsync("pocket-voice-hash", _ => VerifyAllAsync("pocket-voice-hash", [_m.PocketVoice], Downloads))) return;
            if (!await StepAsync("pocket-config", _ => Task.FromResult(WritePocketConfig()))) return;
            if (!await StepAsync("licenses", _ => Task.FromResult(WriteLicenses()))) return;
            if (!await StepAsync("commit", _ => CommitAsync())) return;
            await StepAsync("smoke", _ => SmokeAsync());
        }

        private async Task<bool> StepAsync(
            string stepId,
            Func<IProgress<DysonDownloadProgress>, Task<VoidResult<string>>> work)
        {
            writer.TryWrite(new DysonSpeechInstallStep(stepId, DysonSpeechStepState.Running));
            VoidResult<string> result;
            try
            {
                ct.ThrowIfCancellationRequested();
                var progress = new ChannelProgress(p =>
                    writer.TryWrite(new DysonSpeechInstallStep(stepId, DysonSpeechStepState.Running, Download: p)));
                result = await work(progress).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new VoidResult<string>($"{stepId}: cancelled.");
            }
            catch (Exception ex)
            {
                // Process / disk / network boundary: any failure ends the run as a Failed step, never a throw.
                result = new VoidResult<string>($"{stepId}: {ex.Message}", ex);
            }

            writer.TryWrite(result.IsError
                ? new DysonSpeechInstallStep(stepId, DysonSpeechStepState.Failed, result.Error)
                : new DysonSpeechInstallStep(stepId, DysonSpeechStepState.Done));
            return !result.IsError;
        }

        private VoidResult<string> Preflight()
        {
            if (!IsSupportedPlatform())
                return new VoidResult<string>($"preflight: speech install is {SupportedRid} only in this release (not supported on {RuntimeInformation.RuntimeIdentifier}).");

            var manifest = DysonSpeechManifest.LoadEmbedded();
            if (manifest.IsError)
                return new VoidResult<string>("preflight: " + manifest.Error);
            if (!manifest.Value.Rids.TryGetValue(SupportedRid, out var rid))
                return new VoidResult<string>($"preflight: manifest has no {SupportedRid} entry.");
            _manifest = manifest.Value;
            _m = rid;

            Directory.CreateDirectory(_root);
            var free = new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace;
            if (free < RequiredFreeBytes)
                return new VoidResult<string>($"preflight: needs {RequiredFreeBytes:N0} free bytes on {Path.GetPathRoot(_root)}, has {free:N0}.");

            StopSidecars();
            var clean = DeleteTree(Tree);
            if (clean.IsError)
                return new VoidResult<string>("preflight: " + clean.Error);
            Directory.CreateDirectory(Tree);
            Directory.CreateDirectory(Downloads);
            return VoidResult<string>.Success;
        }

        private async Task<VoidResult<string>> DownloadAllAsync(
            string stepId,
            IReadOnlyList<DysonSpeechFile> files,
            string dir,
            IProgress<DysonDownloadProgress> progress)
        {
            Directory.CreateDirectory(dir);
            var total = files.Sum(f => f.Size);
            long done = 0;
            foreach (var file in files)
            {
                var dest = Path.Combine(dir, file.FileName);
                if (!File.Exists(dest) || new FileInfo(dest).Length != file.Size)
                {
                    var download = await DownloadAsync(stepId, file, dest, done, total, progress).ConfigureAwait(false);
                    if (download.IsError)
                        return download;
                }

                done += file.Size;
                progress.Report(new DysonDownloadProgress(done, total, (double)done / total));
            }

            return VoidResult<string>.Success;
        }

        private async Task<VoidResult<string>> DownloadAsync(
            string stepId,
            DysonSpeechFile file,
            string dest,
            long offset,
            long total,
            IProgress<DysonDownloadProgress> progress)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DysonHarness", "1.0"));
            using var response = await owner._http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new VoidResult<string>($"{stepId}: HTTP {(int)response.StatusCode} for {file.Url}");

            var part = dest + ".part";
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 16];
                long received = 0, reported = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (received - reported >= 1 << 20)
                    {
                        reported = received;
                        var at = offset + received;
                        progress.Report(new DysonDownloadProgress(at, total, (double)at / total));
                    }
                }
            }

            File.Move(part, dest, overwrite: true);
            return VoidResult<string>.Success;
        }

        private async Task<VoidResult<string>> VerifyAllAsync(string stepId, IReadOnlyList<DysonSpeechFile> files, string dir)
        {
            foreach (var file in files)
            {
                var path = Path.Combine(dir, file.FileName);
                if (!File.Exists(path))
                    return new VoidResult<string>($"{stepId}: {file.FileName} is missing ({file.Url}).");

                string hash;
                await using (var stream = File.OpenRead(path))
                    hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));

                var size = new FileInfo(path).Length;
                if (size != file.Size || !string.Equals(hash, file.Sha256, StringComparison.Ordinal))
                {
                    File.Delete(path);
                    return new VoidResult<string>(
                        $"{stepId}: sha256 mismatch for {file.Url} (expected {file.Sha256} / {file.Size:N0} bytes, got {hash} / {size:N0}).");
                }
            }

            return VoidResult<string>.Success;
        }

        private async Task<VoidResult<string>> ExtractPythonAsync()
        {
            await using (var file = File.OpenRead(Path.Combine(Downloads, _m.Python.FileName)))
            await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gzip, Tree, overwriteFiles: true, ct).ConfigureAwait(false);

            if (!File.Exists(DysonSpeechPaths.GetPythonExe(Tree)))
                return new VoidResult<string>($"python-extract: python/python.exe is not in {_m.Python.Url}.");

            return await RunPythonAsync("python-extract", [], ["-m", "pip", "--version"]).ConfigureAwait(false);
        }

        private async Task<VoidResult<string>> PipInstallAsync(
            string stepId,
            IReadOnlyList<DysonSpeechFile> wheels,
            string wheelDir,
            string target,
            string? keepWheelhouseIn)
        {
            var requirements = Path.Combine(Staging, stepId + ".txt");
            await File.WriteAllTextAsync(requirements, BuildRequirements(wheels, wheelDir), ct).ConfigureAwait(false);
            var pip = await RunPythonAsync(stepId, [],
            [
                "-m", "pip", "install", "--isolated", "--no-cache-dir", "--no-index", "--no-deps",
                "--require-hashes", "--no-warn-script-location", "--target", target, "-r", requirements,
            ]).ConfigureAwait(false);
            if (pip.IsError || keepWheelhouseIn is null)
                return pip;

            Directory.CreateDirectory(Path.GetDirectoryName(keepWheelhouseIn)!);
            Directory.Move(wheelDir, keepWheelhouseIn);
            return VoidResult<string>.Success;
        }

        private async Task<VoidResult<string>> UnpackPhononAsync()
        {
            var dest = Path.Combine(Tree, "phonon", "model");
            Directory.CreateDirectory(dest);
            var unpack = await RunPythonAsync(
                "phonon-weights-unpack",
                [DysonSpeechPaths.GetPhononSite(Tree)],
                ["-c", PhononUnpackScript, Path.Combine(Downloads, _m.PhononWeights.FileName), dest]).ConfigureAwait(false);
            if (unpack.IsError)
                return unpack;

            return Directory.Exists(DysonSpeechPaths.GetPhononModelDir(Tree))
                ? VoidResult<string>.Success
                : new VoidResult<string>($"phonon-weights-unpack: {_m.PhononWeights.FileName} has no {Path.GetFileName(DysonSpeechPaths.GetPhononModelDir(Tree))} directory.");
        }

        private VoidResult<string> WritePocketConfig()
        {
            var modelDir = Path.Combine(Tree, "pocket", "model");
            var voicesDir = DysonSpeechPaths.GetPocketVoicesDir(Tree);
            Directory.CreateDirectory(modelDir);
            Directory.CreateDirectory(voicesDir);
            File.Copy(Path.Combine(Downloads, _m.PocketWeights.FileName), Path.Combine(modelDir, "model.safetensors"), overwrite: true);
            File.Copy(Path.Combine(Downloads, _m.PocketTokenizer.FileName), Path.Combine(modelDir, "tokenizer.json"), overwrite: true);
            File.Copy(Path.Combine(Downloads, _m.PocketVoice.FileName), Path.Combine(voicesDir, CatalogVoiceId + ".safetensors"), overwrite: true);

            // Paths point at the final location; the tree is swapped into place at commit.
            File.WriteAllText(DysonSpeechPaths.GetPocketConfig(Tree), BuildPocketConfig(_root));

            using var bridge = typeof(DysonSpeechInstaller).Assembly.GetManifestResourceStream(BridgeResourceName);
            if (bridge is null)
                return new VoidResult<string>($"pocket-config: embedded {BridgeResourceName} is missing.");
            using (var output = File.Create(DysonSpeechPaths.GetPocketBridge(Tree)))
                bridge.CopyTo(output);
            return VoidResult<string>.Success;
        }

        private VoidResult<string> WriteLicenses()
        {
            var dir = Path.Combine(Tree, "licenses");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "NOTICES.md"), Notices);
            return VoidResult<string>.Success;
        }

        private async Task<VoidResult<string>> CommitAsync()
        {
            StopSidecars();
            var swap = Swap();
            if (swap.IsError)
            {
                RestoreOld();
                return swap;
            }

            await File.WriteAllTextAsync(
                DysonSpeechPaths.GetInstallJson(_root),
                JsonSerializer.Serialize(NewStamp(_manifest), JsonOptions),
                ct).ConfigureAwait(false);

            var variant = Path.GetFileName(_root);
            var ensure = await models.EnsureBuiltInAsync(ct).ConfigureAwait(false);
            if (ensure.IsError)
                return new VoidResult<string>("commit: " + ensure.Error);
            foreach (var id in new[] { DysonPhononSpeech.Id, DysonPocketSpeech.Id })
            {
                var mark = await models.MarkInstalledAsync(id, variant, ct).ConfigureAwait(false);
                if (mark.IsError)
                    return new VoidResult<string>("commit: " + mark.Error);
            }

            var voice = await models
                .EnsureCatalogVoiceAsync(DysonPocketSpeech.Id, CatalogVoiceId, "Alba", variant, ct)
                .ConfigureAwait(false);
            return voice.IsError ? new VoidResult<string>("commit: " + voice.Error) : VoidResult<string>.Success;
        }

        private async Task<VoidResult<string>> SmokeAsync()
        {
            var health = await DysonPhononSpeech.CheckHealthAsync(_root, ct).ConfigureAwait(false);
            DysonPhononSpeech.StopSidecar();
            if (health.IsError)
            {
                var hadPrevious = File.Exists(Path.Combine(Old, "install.json"));
                RestoreOld();
                if (!hadPrevious)
                {
                    foreach (var id in new[] { DysonPhononSpeech.Id, DysonPocketSpeech.Id })
                        await models.MarkUninstalledAsync(id, CancellationToken.None).ConfigureAwait(false);
                }

                return new VoidResult<string>("smoke: " + health.Error + " The previous state was restored.");
            }

            DeleteTree(Staging);
            return VoidResult<string>.Success;
        }

        private VoidResult<string> Swap()
        {
            try
            {
                var clean = DeleteTree(Old);
                if (clean.IsError)
                    return new VoidResult<string>("commit: " + clean.Error);
                Directory.CreateDirectory(Old);
                foreach (var name in TreeDirs)
                {
                    if (Directory.Exists(Path.Combine(_root, name)))
                        Directory.Move(Path.Combine(_root, name), Path.Combine(Old, name));
                }

                var json = DysonSpeechPaths.GetInstallJson(_root);
                if (File.Exists(json))
                    File.Move(json, Path.Combine(Old, "install.json"));

                foreach (var name in TreeDirs)
                    Directory.Move(Path.Combine(Tree, name), Path.Combine(_root, name));
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new VoidResult<string>($"commit: could not swap the new tree into {_root}: {ex.Message}");
            }
        }

        private void RestoreOld()
        {
            try
            {
                foreach (var name in TreeDirs)
                {
                    if (!Directory.Exists(Path.Combine(Old, name)))
                        continue;
                    DeleteTree(Path.Combine(_root, name));
                    Directory.Move(Path.Combine(Old, name), Path.Combine(_root, name));
                }

                var json = DysonSpeechPaths.GetInstallJson(_root);
                if (File.Exists(json))
                    File.Delete(json);
                if (File.Exists(Path.Combine(Old, "install.json")))
                    File.Move(Path.Combine(Old, "install.json"), json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // ponytail: a failed rollback leaves a tree whose install.json is missing or stale, so Probe
                // reports not installed and Repair rebuilds it.
            }
        }

        private async Task<VoidResult<string>> RunPythonAsync(string stepId, IReadOnlyList<string> pythonPath, IReadOnlyList<string> args)
        {
            var psi = PythonStartInfo(Tree, pythonPath, args);
            using var process = Process.Start(psi);
            if (process is null)
                return new VoidResult<string>($"{stepId}: could not start {psi.FileName}.");

            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }

            if (process.ExitCode == 0)
                return VoidResult<string>.Success;

            var output = (await stderr.ConfigureAwait(false)).Trim();
            if (output.Length == 0)
                output = (await stdout.ConfigureAwait(false)).Trim();
            if (output.Length > 1200)
                output = "…" + output[^1200..];
            return new VoidResult<string>($"{stepId}: python exited {process.ExitCode}: {output}");
        }
    }

    private sealed class ChannelProgress(Action<DysonDownloadProgress> report) : IProgress<DysonDownloadProgress>
    {
        public void Report(DysonDownloadProgress value) => report(value);
    }

    private const string Notices = """
        # Speech runtime notices

        Downloaded by DysonHarness from the public sources in packaging/speech/manifest.json. Not redistributed in the app zip or MSI.

        - CPython 3.12.15 (python-build-standalone): PSF License.
        - PyTorch 2.14.1+cpu: BSD-3-Clause.
        - fermion-research 0.2.11: Apache-2.0.
        - Phonon-2 weights (Fermion Research, derived from NVIDIA Parakeet TDT 0.6B v3): CC-BY-4.0.
        - pocket-tts 3.3.0 code (Kyutai): MIT.
        - Pocket TTS weights and the alba voice embedding (kyutai/pocket-tts-without-voice-cloning): CC-BY-4.0.
        - libsndfile inside the upstream soundfile wheel: LGPL-2.1.
        - Other wheels in phonon/wheelhouse and pocket/wheelhouse keep the license files inside each wheel's dist-info.
        """;
}
