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

/// <summary>
/// File-level install state of the speech tree (DB rows mirror this at commit). <see cref="RuntimeInstalled"/> is the
/// shared CPython + torch; each Python model is installed only on top of it. Ids in <see cref="GgufInstalled"/> are
/// independent: a tree with only <c>install-gguf.json</c> can report a GGUF model installed. <see cref="CrispCpuInstalled"/> /
/// <see cref="CrispVulkanInstalled"/>: that runtime's stamp equals the manifest sha and its <c>crispasr.exe</c> exists.
/// A CrispASR model needs only the CPU runtime to count as installed.
/// </summary>
public sealed record DysonSpeechInstallStatus(
    bool RuntimeInstalled,
    bool PhononInstalled,
    bool PocketInstalled,
    bool QwenInstalled,
    bool ChatterboxInstalled,
    string? Note,
    IReadOnlyCollection<string>? GgufInstalled = null,
    bool CrispCpuInstalled = false,
    bool CrispVulkanInstalled = false,
    bool PythonReady = false,
    bool OpenVinoInstalled = false)
{
    public static DysonSpeechInstallStatus None(string? note) => new(false, false, false, false, false, note);

    public bool IsInstalled(string runtimeId) => runtimeId switch
    {
        DysonPhononSpeech.Id => PhononInstalled,
        DysonPocketSpeech.Id => PocketInstalled,
        DysonQwenSpeech.Id => QwenInstalled,
        DysonChatterboxSpeech.Id => ChatterboxInstalled,
        _ => GgufInstalled?.Contains(runtimeId) == true,
    };
}

/// <summary>
/// Shared speech installer: one CPython, one CPU torch, and per-model isolated sites (Phonon-2, Pocket TTS,
/// Qwen3-TTS 0.6B Base, Chatterbox Turbo) under <see cref="InstallRoot"/> (<see cref="DysonSpeechPaths.GetInstallRoot"/>).
/// Each model installs and uninstalls on its own; the runtime is built once. Every byte comes through
/// <see cref="HttpClient"/> from <c>packaging/speech/manifest.json</c>; no system Python, pip, or git, no HF token.
/// One try per download; a hash mismatch or non-200 fails with the step id and URL.
/// </summary>
public sealed class DysonSpeechInstaller
{
    public const string SupportedRid = "win-x64";

    // Preflight floors: measured win-x64 trees (Qwen / Chatterbox: site + weights + wheelhouse). Preflight needs
    // 2 × the sum of what this run builds. ponytail: re-measure when the pins change.
    internal const long RuntimeBytes = 696_975_521; // python 64,991,494 + torch 631,984,027
    internal const long RocmTorchBytes = 1_573_325_768; // ROCm 10.1.0 torch + gfx1102 wheels + the rocm sdist + the CPU torch pure-Python deps
    internal const long PhononBytes = 533_602_783;
    internal const long PocketBytes = 480_934_807;
    internal const long QwenBytes = 683_576_361 + 2_516_100_892 + QwenWheelhouseBytes;
    internal const long ChatterboxBytes = 665_031_080 + 2_987_680_596 + ChatterboxWheelhouseBytes;
    internal const long NemotronBytes = 650_000_000; // archive 18,036,555 + weights 559,647,200 + extracted native
    internal const long CrispAsrBytes = 60_000_000; // CPU zip 9,088,774 + Vulkan zip 35,991,946 + extracted
    internal const long PhononGgufBytes = 402_226_656;
    internal const long PhononGgufQ8Bytes = 674_342_560;
    internal const long PocketGgufBytes = 72_645_472;
    internal const long PocketGgufQ8Bytes = 123_632_992;
    internal const long QwenGgufBytes = 985_716_544 + 358_453_280;
    internal const long QwenGgufQ4Bytes = 532_862_784 + 358_453_280;
    internal const long QwenGgufF16Bytes = 1_834_817_248L + 358_453_280L;
    internal const long ChatterboxGgufBytes = 478_800_960 + 255_891_744;
    internal const long ChatterboxGgufQ8Bytes = 658_897_472 + 366_820_384;
    internal const long WhisperTinyBytes = 77_691_713;
    internal const long WhisperBaseBytes = 147_951_465;
    internal const long OpenVinoWheelBytes = 104_000_000;
    internal const long WhisperTinyOpenVinoBytes = 78_000_000;
    internal const long WhisperBaseOpenVinoBytes = 148_000_000;
    private const long QwenWheelhouseBytes = 138_343_182;
    private const long ChatterboxWheelhouseBytes = 182_916_172;

    /// <summary>Model ids in install / UI order. Each owns <c>{root}/{dir}</c>. Each GGUF id follows its sibling.</summary>
    public static readonly IReadOnlyList<string> ModelIds =
    [
        DysonPhononSpeech.Id,
        DysonGgufSpeechModels.NemotronId,
        DysonGgufSpeechModels.PhononGgufId,
        DysonGgufSpeechModels.PhononGgufQ8Id,
        DysonGgufSpeechModels.WhisperTinyId,
        DysonGgufSpeechModels.WhisperBaseId,
        DysonPocketSpeech.Id,
        DysonGgufSpeechModels.PocketGgufId,
        DysonGgufSpeechModels.PocketGgufQ8Id,
        DysonQwenSpeech.Id,
        DysonGgufSpeechModels.QwenGgufId,
        DysonGgufSpeechModels.QwenGgufQ4Id,
        DysonGgufSpeechModels.QwenGgufF16Id,
        DysonChatterboxSpeech.Id,
        DysonGgufSpeechModels.ChatterboxGgufId,
        DysonGgufSpeechModels.ChatterboxGgufQ8Id,
    ];

    /// <summary>First installed of these becomes the active TTS when <c>speech_output_model_id</c> is missing.</summary>
    internal static readonly IReadOnlyList<string> ActiveDefaultOrder =
    [
        DysonPocketSpeech.Id,
        DysonChatterboxSpeech.Id,
        DysonQwenSpeech.Id,
        DysonGgufSpeechModels.PocketGgufId,
        DysonGgufSpeechModels.PocketGgufQ8Id,
        DysonGgufSpeechModels.ChatterboxGgufId,
        DysonGgufSpeechModels.ChatterboxGgufQ8Id,
        DysonGgufSpeechModels.QwenGgufId,
        DysonGgufSpeechModels.QwenGgufQ4Id,
        DysonGgufSpeechModels.QwenGgufF16Id,
    ];

    /// <summary>Input models. The installer sets <c>speech_input_model_id</c> only when exactly one of these is installed.</summary>
    private static readonly string[] InputModelIds =
        [DysonPhononSpeech.Id, DysonGgufSpeechModels.PhononGgufId, DysonGgufSpeechModels.PhononGgufQ8Id, DysonGgufSpeechModels.WhisperTinyId, DysonGgufSpeechModels.WhisperBaseId, DysonGgufSpeechModels.NemotronId];

    private static readonly string[] RuntimeSteps =
    [
        "python-download", "python-hash", "python-extract", "torch-download", "torch-hash", "torch-install",
    ];

    private static readonly string[] RocmTorchSteps =
        ["rocm-torch-download", "rocm-torch-hash", "rocm-torch-install"];

    private static readonly string[] CrispSteps = ["crispasr-download", "crispasr-hash", "crispasr-extract"];

    private static readonly string[] CrispVulkanSteps =
        ["crispasr-vulkan-download", "crispasr-vulkan-hash", "crispasr-vulkan-extract"];

    private static readonly string[] PythonOnlySteps = ["python-download", "python-hash", "python-extract"];

    private static readonly string[] OpenVinoSteps = ["openvino-download", "openvino-hash", "openvino-install"];

    private static readonly Dictionary<string, string[]> ModelSteps = new()
    {
        [DysonPhononSpeech.Id] =
        [
            "phonon-wheels-download", "phonon-wheels-hash", "phonon-wheels-install",
            "phonon-weights-download", "phonon-weights-hash", "phonon-weights-unpack",
        ],
        [DysonGgufSpeechModels.NemotronId] = ["nemotron-download", "nemotron-hash", "nemotron-unpack"],
        [DysonGgufSpeechModels.PhononGgufId] = ["phonon-gguf-download", "phonon-gguf-hash", "phonon-gguf-unpack"],
        [DysonGgufSpeechModels.PhononGgufQ8Id] = ["phonon-gguf-q8-download", "phonon-gguf-q8-hash", "phonon-gguf-q8-unpack"],
        [DysonGgufSpeechModels.WhisperTinyId] =
        [
            "whisper-tiny-download", "whisper-tiny-hash", "whisper-tiny-unpack",
            "whisper-tiny-openvino-download", "whisper-tiny-openvino-hash", "whisper-tiny-openvino-unpack",
        ],
        [DysonGgufSpeechModels.WhisperBaseId] =
        [
            "whisper-base-download", "whisper-base-hash", "whisper-base-unpack",
            "whisper-base-openvino-download", "whisper-base-openvino-hash", "whisper-base-openvino-unpack",
        ],
        [DysonPocketSpeech.Id] =
        [
            "pocket-wheels-download", "pocket-wheels-hash", "pocket-wheels-install",
            "pocket-weights-download", "pocket-weights-hash", "pocket-voice-download", "pocket-voice-hash", "pocket-config",
        ],
        [DysonGgufSpeechModels.PocketGgufId] = ["pocket-gguf-download", "pocket-gguf-hash", "pocket-gguf-unpack"],
        [DysonGgufSpeechModels.PocketGgufQ8Id] = ["pocket-gguf-q8-download", "pocket-gguf-q8-hash", "pocket-gguf-q8-unpack"],
        [DysonQwenSpeech.Id] =
        [
            "qwen-wheels-download", "qwen-wheels-hash", "qwen-wheels-install",
            "qwen-weights-download", "qwen-weights-hash", "qwen-config",
        ],
        [DysonGgufSpeechModels.QwenGgufId] = ["qwen-gguf-download", "qwen-gguf-hash", "qwen-gguf-unpack"],
        [DysonGgufSpeechModels.QwenGgufQ4Id] = ["qwen-gguf-q4-download", "qwen-gguf-q4-hash", "qwen-gguf-q4-unpack"],
        [DysonGgufSpeechModels.QwenGgufF16Id] = ["qwen-gguf-f16-download", "qwen-gguf-f16-hash", "qwen-gguf-f16-unpack"],
        [DysonChatterboxSpeech.Id] =
        [
            "chatterbox-wheels-download", "chatterbox-wheels-hash", "chatterbox-wheels-install",
            "chatterbox-weights-download", "chatterbox-weights-hash", "chatterbox-config",
        ],
        [DysonGgufSpeechModels.ChatterboxGgufId] =
            ["chatterbox-gguf-download", "chatterbox-gguf-hash", "chatterbox-gguf-unpack"],
        [DysonGgufSpeechModels.ChatterboxGgufQ8Id] =
            ["chatterbox-gguf-q8-download", "chatterbox-gguf-q8-hash", "chatterbox-gguf-q8-unpack"],
    };

    private static readonly string[] TailSteps = ["licenses", "commit", "smoke"];

    /// <summary>Every step in run order. A run emits only <see cref="PlanSteps"/>; skipped steps are not emitted.</summary>
    public static readonly IReadOnlyList<string> StepIds =
        ["preflight", .. RuntimeSteps, .. RocmTorchSteps, .. CrispSteps, .. CrispVulkanSteps, .. OpenVinoSteps, .. ModelIds.SelectMany(id => ModelSteps[id]), .. TailSteps];

    internal const string BridgeResourceName = "DysonHarness.Speech.bridge.py";
    internal const string CatalogVoiceId = "alba";
    private const string GgufStampFileName = "install-gguf.json";
    private const string CrispStampKey = "crispasr";
    private const string CrispVulkanStampKey = "crispasr-vulkan";
    private const string OpenVinoStampKey = "openvino";
    internal const string CrispVulkanMissingNote = "CrispASR Vulkan runtime is missing. Use Repair CrispASR runtimes.";
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
    /// Installs the requested models (<see cref="ModelIds"/>) that are missing, plus the shared runtime only when it
    /// is missing, on the thread pool (<see cref="PlanSteps"/>). Installed models are never re-downloaded. Stops at
    /// the first <see cref="DysonSpeechStepState.Failed"/> step. Nothing missing ⇒ preflight "Already installed."
    /// and a DB sync only.
    /// </summary>
    public async IAsyncEnumerable<DysonSpeechInstallStep> InstallAsync(
        IDysonSpeechModelRepository models,
        IDysonSubjectSettingsRepository settings,
        IReadOnlyCollection<string> modelIds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(settings);
        var invalid = modelIds is not { Count: > 0 }
            ? "Choose at least one model to install."
            : modelIds.FirstOrDefault(id => !ModelIds.Contains(id)) is { } unknown ? $"Unknown speech model '{unknown}'." : null;
        if (invalid is not null)
        {
            yield return new DysonSpeechInstallStep("preflight", DysonSpeechStepState.Failed, invalid);
            yield break;
        }

        await foreach (var step in RunGatedAsync(models, settings, modelIds.Distinct().ToList(), PipelineKind.Install, cancellationToken)
                           .ConfigureAwait(false))
            yield return step;
    }

    /// <summary>
    /// Re-downloads only a CrispASR runtime (CPU or Vulkan) whose <c>crispasr.exe</c> is missing or whose stamp is not
    /// the current manifest sha (<see cref="PlanRepairSteps"/>), extracts it into its own folder, swaps only that folder,
    /// and writes only its stamp key. No weights, no python, no hashing of installed trees. Same gate and step stream
    /// as <see cref="InstallAsync"/>; both runtimes ok ⇒ preflight "Already installed.".
    /// </summary>
    public async IAsyncEnumerable<DysonSpeechInstallStep> RepairCrispRuntimesAsync(
        IDysonSpeechModelRepository models,
        IDysonSubjectSettingsRepository settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(settings);
        await foreach (var step in RunGatedAsync(models, settings, [], PipelineKind.RepairCrisp, cancellationToken).ConfigureAwait(false))
            yield return step;
    }

    /// <summary>
    /// Installs the saved PyTorch device now, on the same python/torch and <c>rocm-torch-*</c> steps a Python model
    /// install uses (<see cref="PlanTorchRepairSteps"/>). No model weights. CPU repairs the CPU torch (and the private
    /// Python that pip needs, when that runtime is missing). ROCm repairs <c>torch-rocm/</c> and, when the CPU runtime
    /// is missing, the CPU torch beside it. A <c>torch-rocm</c> missing <c>rocm_sdk</c> is not ready: Repair deletes it
    /// and reinstalls the pinned ROCm wheels. Already present ⇒ preflight "Already installed.". CUDA is not a target.
    /// A missing manifest, disk, or pip failure is a Failed step whose detail is the <c>VoidResult</c> error; it does not throw.
    /// </summary>
    public async IAsyncEnumerable<DysonSpeechInstallStep> RepairTorchRuntimesAsync(
        IDysonSpeechModelRepository models,
        IDysonSubjectSettingsRepository settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(settings);
        await foreach (var step in RunGatedAsync(models, settings, [], PipelineKind.RepairTorch, cancellationToken).ConfigureAwait(false))
            yield return step;
    }

    private enum PipelineKind
    {
        Install,
        RepairCrisp,
        RepairTorch,
    }

    private async IAsyncEnumerable<DysonSpeechInstallStep> RunGatedAsync(
        IDysonSpeechModelRepository models,
        IDysonSubjectSettingsRepository settings,
        IReadOnlyList<string> requested,
        PipelineKind kind,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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
                await new Pipeline(this, models, settings, requested, kind, channel.Writer, cancellationToken)
                    .RunAsync().ConfigureAwait(false);
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

    /// <summary>
    /// Uninstall everything: stops sidecars this process started, deletes the whole variant tree (runtime and every
    /// model), and clears every model row and its voices.
    /// </summary>
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

            foreach (var id in ModelIds)
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

    /// <summary>
    /// Uninstalls one model: stops only its process, deletes <c>{root}/{model dir}</c> (and its voices), clears its
    /// pin in <c>install.json</c>, and its row. Python, torch, and other models stay. The active TTS setting is not
    /// changed (synthesis then reports the model as not installed).
    /// </summary>
    public async Task<VoidResult<string>> UninstallModelAsync(
        IDysonSpeechModelRepository models,
        string runtimeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (!ModelIds.Contains(runtimeId))
            return new VoidResult<string>($"Unknown speech model '{runtimeId}'.");
        if (!_gate.Wait(0))
            return new VoidResult<string>("An install is already running.");

        try
        {
            var deleted = await Task.Run(() =>
            {
                StopModel(runtimeId);
                var delete = DeleteTree(Path.Combine(InstallRoot, ModelDir(runtimeId)));
                if (delete.IsError)
                    return delete;

                if (!DysonGgufSpeechModels.IsGguf(runtimeId))
                {
                    var stamp = ReadStamp(InstallRoot);
                    return stamp is null ? VoidResult<string>.Success : WriteStamp(InstallRoot, WithPackage(stamp, runtimeId, null));
                }

                var gguf = ReadGgufStamp(InstallRoot) ?? new Dictionary<string, string>(StringComparer.Ordinal);
                gguf.Remove(runtimeId);
                if (DysonGgufSpeechModels.IsWhisper(runtimeId))
                {
                    gguf.Remove(runtimeId + "-openvino");
                    if (!WhisperOpenVinoRemains(gguf))
                    {
                        var site = DeleteTree(Path.Combine(InstallRoot, "openvino"));
                        if (site.IsError)
                            return site;
                        gguf.Remove(OpenVinoStampKey);
                    }
                }

                if (DysonGgufSpeechModels.IsCrisp(runtimeId) && !CrispModelRemains(InstallRoot, gguf))
                {
                    var crisp = DeleteTree(DysonGgufSpeechModels.GetCrispRuntimeDir(InstallRoot));
                    if (crisp.IsError)
                        return crisp;
                    var vulkan = DeleteTree(DysonGgufSpeechModels.GetCrispVulkanRuntimeDir(InstallRoot));
                    if (vulkan.IsError)
                        return vulkan;
                    gguf.Remove(CrispStampKey);
                    gguf.Remove(CrispVulkanStampKey);
                }

                return WriteGgufStamp(InstallRoot, gguf);
            }, CancellationToken.None).ConfigureAwait(false);
            if (deleted.IsError)
                return deleted;

            return await models.MarkUninstalledAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The steps one run executes: preflight; python/torch only when a non-GGUF model is missing and the runtime is
    /// missing; CrispASR CPU only when a CrispASR model is missing and no CrispASR model is already installed; CrispASR
    /// Vulkan when a CrispASR model is missing and <see cref="DysonSpeechInstallStatus.CrispVulkanInstalled"/> is false; each
    /// requested model that is not installed; then licenses, commit, smoke when anything above runs. Pure.
    /// <paramref name="adopt"/> null keeps today's list. A non-null value drops downloads <see cref="Adopt"/> already
    /// matched. Licenses, commit, and smoke stay when a stamp still has to be written after pip or python extract.
    /// <paramref name="installRocm"/> adds the ROCm torch steps into <c>torch-rocm/</c> and still runs when every
    /// requested model is already installed. The CPU torch steps are unchanged when it is false.
    /// </summary>
    internal static IReadOnlyList<string> PlanSteps(
        DysonSpeechInstallStatus status, IReadOnlyCollection<string> modelIds, DysonSpeechAdopt? adopt = null, bool installRocm = false)
    {
        var missing = ModelIds.Where(id => modelIds.Contains(id) && !status.IsInstalled(id)).ToList();
        if (missing.Count == 0 && !installRocm)
            return ["preflight"];

        var buildPython = missing.Any(id => !DysonGgufSpeechModels.IsGguf(id));
        var whisper = missing.Any(DysonGgufSpeechModels.IsWhisper);
        var pythonSteps = !status.RuntimeInstalled && buildPython
            ? RuntimeSteps
            : whisper && !status.PythonReady && !status.RuntimeInstalled
                ? PythonOnlySteps
                : Array.Empty<string>();
        List<string> steps =
        [
            "preflight",
            .. pythonSteps,
            .. installRocm ? RocmTorchSteps : Array.Empty<string>(),
            .. missing.Any(DysonGgufSpeechModels.IsCrisp) && !CrispasrInstalled(status) ? CrispSteps : Array.Empty<string>(),
            .. missing.Any(DysonGgufSpeechModels.IsCrisp) && !status.CrispVulkanInstalled ? CrispVulkanSteps : Array.Empty<string>(),
            .. whisper && !status.OpenVinoInstalled ? OpenVinoSteps : Array.Empty<string>(),
            .. missing.SelectMany(id => ModelSteps[id]),
            .. TailSteps,
        ];
        if (adopt is null)
            return steps;

        steps.RemoveAll(step => step != "preflight" && AdoptDrops(step, adopt));
        return steps;
    }

    private static bool AdoptDrops(string step, DysonSpeechAdopt adopt)
    {
        if (adopt.SkipPython && step.StartsWith("python-", StringComparison.Ordinal))
            return true;
        if (adopt.SkipTorchWheel && !adopt.PipTorchDeps && step.StartsWith("torch-", StringComparison.Ordinal))
            return true;
        if (adopt.SkipCrisp && CrispSteps.Contains(step))
            return true;
        foreach (var id in adopt.SkipWeightIds)
        {
            if (ModelSteps.TryGetValue(id, out var owned) && owned.Contains(step) && DropOwnedStep(id, step, adopt))
                return true;
        }

        return false;
    }

    private static bool DropOwnedStep(string id, string step, DysonSpeechAdopt adopt)
    {
        if (step.Contains("-wheels-", StringComparison.Ordinal))
            return !adopt.PipSiteIds.Contains(id);
        if (id == DysonPocketSpeech.Id && (step.StartsWith("pocket-voice-", StringComparison.Ordinal) || step == "pocket-config"))
            return adopt.PocketVoiceMatched;
        return true;
    }

    /// <summary>
    /// Repair plan: each CrispASR runtime that is not ok (exe missing, stamp key missing, or not the manifest sha) gets
    /// its download / hash / extract trio, then <c>commit</c>. Both ok ⇒ <c>["preflight"]</c>. Pure.
    /// </summary>
    internal static IReadOnlyList<string> PlanRepairSteps(DysonSpeechInstallStatus status) =>
        status.CrispCpuInstalled && status.CrispVulkanInstalled
            ? ["preflight"]
            :
            [
                "preflight",
                .. status.CrispCpuInstalled ? Array.Empty<string>() : CrispSteps,
                .. status.CrispVulkanInstalled ? Array.Empty<string>() : CrispVulkanSteps,
                "commit",
            ];

    /// <summary>
    /// Repair plan for the selected torch. CPU (<paramref name="installRocm"/> false): shared runtime steps when
    /// <see cref="DysonSpeechInstallStatus.RuntimeInstalled"/> is false. ROCm: those steps when the CPU runtime is
    /// missing, plus the model-install <c>rocm-torch-*</c> steps when <paramref name="rocmReady"/> is false
    /// (<c>torch-rocm</c> missing <c>rocm_sdk</c> is not ready). No model weights. Both already ok ⇒ <c>["preflight"]</c>. Pure.
    /// </summary>
    internal static IReadOnlyList<string> PlanTorchRepairSteps(
        DysonSpeechInstallStatus status, bool installRocm, bool rocmReady)
    {
        var needRuntime = !status.RuntimeInstalled;
        var needRocm = installRocm && !rocmReady;
        if (!needRuntime && !needRocm)
            return ["preflight"];

        return
        [
            "preflight",
            .. needRuntime ? RuntimeSteps : Array.Empty<string>(),
            .. needRocm ? RocmTorchSteps : Array.Empty<string>(),
            "commit",
        ];
    }

    /// <summary>A CrispASR model in <see cref="DysonSpeechInstallStatus.GgufInstalled"/> means the shared runtime is present.</summary>
    private static bool CrispasrInstalled(DysonSpeechInstallStatus status) =>
        status.GgufInstalled?.Any(DysonGgufSpeechModels.IsCrisp) == true;

    internal static long RequiredFreeBytes(bool runtime, IEnumerable<string> modelIds, DysonSpeechAdopt? adopt = null, bool rocm = false)
    {
        long models = 0;
        var crisp = false;
        var whisper = false;
        foreach (var id in modelIds)
        {
            if (adopt?.SkipWeightIds.Contains(id) != true)
                models += ModelBytes(id);
            if (DysonGgufSpeechModels.IsCrisp(id))
                crisp = true;
            if (DysonGgufSpeechModels.IsWhisper(id))
                whisper = true;
        }

        // ponytail: the CrispASR floor covers both zips (CPU + Vulkan) and is still added whenever a CrispASR id is
        // requested, even when one or both runtimes are installed. A pip-only repair (weights skipped) reserves nothing
        // for that model. Upgrade: add wheelhouse sizes.
        if (crisp)
            models += CrispAsrBytes;
        if (whisper)
            models += OpenVinoWheelBytes;
        return 2 * ((runtime ? RuntimeBytes : 0) + (rocm ? RocmTorchBytes : 0) + models);
    }

    private static long ModelBytes(string id) => id switch
    {
        DysonPhononSpeech.Id => PhononBytes,
        DysonPocketSpeech.Id => PocketBytes,
        DysonQwenSpeech.Id => QwenBytes,
        DysonChatterboxSpeech.Id => ChatterboxBytes,
        DysonGgufSpeechModels.NemotronId => NemotronBytes,
        DysonGgufSpeechModels.PhononGgufId => PhononGgufBytes,
        DysonGgufSpeechModels.PhononGgufQ8Id => PhononGgufQ8Bytes,
        DysonGgufSpeechModels.PocketGgufId => PocketGgufBytes,
        DysonGgufSpeechModels.PocketGgufQ8Id => PocketGgufQ8Bytes,
        DysonGgufSpeechModels.QwenGgufId => QwenGgufBytes,
        DysonGgufSpeechModels.QwenGgufQ4Id => QwenGgufQ4Bytes,
        DysonGgufSpeechModels.QwenGgufF16Id => QwenGgufF16Bytes,
        DysonGgufSpeechModels.ChatterboxGgufId => ChatterboxGgufBytes,
        DysonGgufSpeechModels.ChatterboxGgufQ8Id => ChatterboxGgufQ8Bytes,
        DysonGgufSpeechModels.WhisperTinyId => WhisperTinyBytes + WhisperTinyOpenVinoBytes,
        DysonGgufSpeechModels.WhisperBaseId => WhisperBaseBytes + WhisperBaseOpenVinoBytes,
        _ => 0,
    };

    private static string ModelDir(string runtimeId) =>
        DysonGgufSpeechModels.Dir(runtimeId) ?? runtimeId switch
        {
            DysonPhononSpeech.Id => "phonon",
            DysonPocketSpeech.Id => "pocket",
            DysonQwenSpeech.Id => "qwen",
            _ => "chatterbox",
        };

    private static void StopModel(string runtimeId)
    {
        if (runtimeId == DysonGgufSpeechModels.NemotronId)
        {
            DysonNemotronSpeech.Stop();
            return;
        }

        if (DysonGgufSpeechModels.IsCrisp(runtimeId))
        {
            DysonCrispSpeech.StopSidecar();
            return;
        }

        switch (runtimeId)
        {
            case DysonPhononSpeech.Id: DysonPhononSpeech.StopSidecar(); break;
            case DysonPocketSpeech.Id: DysonPocketSpeech.StopBridge(); break;
            case DysonQwenSpeech.Id: DysonQwenSpeech.StopBridge(); break;
            default: DysonChatterboxSpeech.StopBridge(); break;
        }
    }

    public static bool IsSupportedPlatform() =>
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>
    /// Runtime installed when <c>install.json</c> matches the shared pins and RID and python + torch exist. A Python
    /// model is installed on top of that when its stamp pin equals the manifest package and its weights exist. A
    /// plan-97 stamp (no Qwen / Chatterbox pins) still probes as runtime + Phonon + Pocket. GGUF ids are probed from
    /// <c>install-gguf.json</c> even when <c>install.json</c> is missing. Does not hash. Sync file I/O: callers hop
    /// to the thread pool first.
    /// </summary>
    internal static DysonSpeechInstallStatus Probe(string installRoot)
    {
        if (!IsSupportedPlatform())
            return DysonSpeechInstallStatus.None($"Speech install is {SupportedRid} only in this release.");

        var manifest = DysonSpeechManifest.LoadEmbedded();
        if (manifest.IsError)
            return DysonSpeechInstallStatus.None(manifest.Error);

        return ProbeFiles(installRoot, manifest.Value);
    }

    /// <summary>Stamp and key-file probe with no win-x64 gate, so <see cref="Adopt"/> can re-probe on any OS. No hashing.</summary>
    private static DysonSpeechInstallStatus ProbeFiles(string installRoot, DysonSpeechManifest manifest)
    {
        var python = ProbePython(installRoot, manifest);
        var gguf = ProbeGguf(installRoot, manifest);
        var stamp = ReadGgufStamp(installRoot);
        manifest.Rids.TryGetValue(SupportedRid, out var rid);
        var vulkan = CrispRuntimeOk(stamp, CrispVulkanStampKey, rid?.CrispasrVulkan, DysonGgufSpeechModels.GetCrispVulkanExe(installRoot));
        var status = python with
        {
            GgufInstalled = gguf.Count == 0 ? null : gguf,
            CrispCpuInstalled = CrispRuntimeOk(stamp, CrispStampKey, rid?.CrispasrRuntime, DysonGgufSpeechModels.GetCrispExe(installRoot)),
            CrispVulkanInstalled = vulkan,
            PythonReady = File.Exists(DysonSpeechPaths.GetPythonExe(installRoot)),
            OpenVinoInstalled = OpenVinoStampOk(stamp, rid),
        };
        return !vulkan && gguf.Any(DysonGgufSpeechModels.IsCrisp) ? status with { Note = CrispVulkanMissingNote } : status;
    }

    /// <summary>Stamp <paramref name="key"/> equals the manifest sha and <paramref name="exe"/> exists. Does not hash.</summary>
    private static bool CrispRuntimeOk(
        IReadOnlyDictionary<string, string>? stamp, string key, DysonSpeechFile? pin, string exe) =>
        stamp is not null
        && pin is not null
        && stamp.TryGetValue(key, out var got)
        && string.Equals(got, pin.Sha256, StringComparison.Ordinal)
        && File.Exists(exe);

    private static bool OpenVinoStampOk(IReadOnlyDictionary<string, string>? stamp, DysonSpeechRidManifest? rid)
    {
        var sha = OpenVinoWheelSha(rid);
        return sha is not null
            && stamp is not null
            && stamp.TryGetValue(OpenVinoStampKey, out var got)
            && string.Equals(got, sha, StringComparison.Ordinal);
    }

    private static bool WhisperOpenVinoRemains(IReadOnlyDictionary<string, string> stamp) =>
        stamp.ContainsKey(DysonGgufSpeechModels.WhisperTinyId + "-openvino")
        || stamp.ContainsKey(DysonGgufSpeechModels.WhisperBaseId + "-openvino");

    private static string? OpenVinoWheelSha(DysonSpeechRidManifest? manifest) =>
        manifest?.OpenVinoWheels?.FirstOrDefault(f => f.FileName.StartsWith("openvino-", StringComparison.Ordinal))?.Sha256;

    private static string? OpenVinoEncoderSha(DysonSpeechRidManifest manifest, string runtimeId) =>
        OpenVinoFiles(manifest, runtimeId)?.FirstOrDefault(f => f.FileName == "openvino_encoder_model.bin")?.Sha256;

    private static IReadOnlyList<DysonSpeechFile>? OpenVinoFiles(DysonSpeechRidManifest manifest, string runtimeId) =>
        runtimeId switch
        {
            DysonGgufSpeechModels.WhisperTinyId when manifest.WhisperTinyOpenVino is { Count: > 0 } => manifest.WhisperTinyOpenVino,
            DysonGgufSpeechModels.WhisperBaseId when manifest.WhisperBaseOpenVino is { Count: > 0 } => manifest.WhisperBaseOpenVino,
            _ => null,
        };

    private static DysonSpeechInstallStatus ProbePython(string installRoot, DysonSpeechManifest m)
    {
        var stamp = ReadStamp(installRoot);
        if (stamp is null)
            return DysonSpeechInstallStatus.None("Not installed.");
        if (stamp.SchemaVersion != m.SchemaVersion || stamp.PythonVersion != m.PythonVersion
            || stamp.TorchRequirement != m.TorchRequirement || stamp.Rid != SupportedRid)
        {
            return DysonSpeechInstallStatus.None("Python or torch does not match this version's pins. Install a model to rebuild it.");
        }

        if (!File.Exists(DysonSpeechPaths.GetPythonExe(installRoot))
            || !Directory.Exists(Path.Combine(DysonSpeechPaths.GetTorchDir(installRoot), "torch")))
        {
            return DysonSpeechInstallStatus.None("Python or torch is missing. Install a model to rebuild it.");
        }

        var phononDir = DysonSpeechPaths.GetPhononModelDir(installRoot);
        var qwenDir = DysonSpeechPaths.GetQwenModelDir(installRoot);
        var chatterboxDir = DysonSpeechPaths.GetChatterboxModelDir(installRoot);
        return new(
            RuntimeInstalled: true,
            PhononInstalled: stamp.PhononPackage == m.PhononPackage
                && Directory.Exists(phononDir) && Directory.EnumerateFileSystemEntries(phononDir).Any(),
            PocketInstalled: stamp.PocketPackage == m.PocketPackage && File.Exists(DysonSpeechPaths.GetPocketModelFile(installRoot)),
            QwenInstalled: stamp.QwenPackage == m.QwenPackage
                && File.Exists(Path.Combine(qwenDir, "model.safetensors"))
                && File.Exists(Path.Combine(qwenDir, "speech_tokenizer", "model.safetensors")),
            ChatterboxInstalled: stamp.ChatterboxPackage == m.ChatterboxPackage
                && File.Exists(Path.Combine(chatterboxDir, "t3_turbo_v1.safetensors"))
                && File.Exists(Path.Combine(chatterboxDir, "s3gen_meanflow.safetensors")),
            Note: null);
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
        IEnumerable<string> args,
        string? bridgeDevice = null)
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
        if (bridgeDevice is not null)
            psi.Environment["DYSON_SPEECH_TORCH_DEVICE"] = bridgeDevice;
        return psi;
    }

    internal static void StopSidecars()
    {
        foreach (var id in ModelIds)
            StopModel(id);
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

    private static VoidResult<string> WriteStamp(string installRoot, DysonSpeechInstallStamp stamp)
    {
        try
        {
            File.WriteAllText(DysonSpeechPaths.GetInstallJson(installRoot), JsonSerializer.Serialize(stamp, JsonOptions));
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new VoidResult<string>($"Could not write {DysonSpeechPaths.GetInstallJson(installRoot)}: {ex.Message}");
        }
    }

    private static DysonSpeechInstallStamp WithPackage(DysonSpeechInstallStamp stamp, string runtimeId, string? package) =>
        runtimeId switch
        {
            DysonPhononSpeech.Id => stamp with { PhononPackage = package },
            DysonPocketSpeech.Id => stamp with { PocketPackage = package },
            DysonQwenSpeech.Id => stamp with { QwenPackage = package },
            _ => stamp with { ChatterboxPackage = package },
        };

    private static string PackageOf(DysonSpeechManifest manifest, string runtimeId) => runtimeId switch
    {
        DysonPhononSpeech.Id => manifest.PhononPackage,
        DysonPocketSpeech.Id => manifest.PocketPackage,
        DysonQwenSpeech.Id => manifest.QwenPackage,
        _ => manifest.ChatterboxPackage,
    };

    private static string GgufStampPath(string installRoot) => Path.Combine(installRoot, GgufStampFileName);

    /// <summary>
    /// <c>{ "crispasr": sha, "&lt;runtimeId&gt;": pin }</c>. Pin is the main GGUF sha256; Nemotron is
    /// <c>weightsSha+nativeSha</c>.
    /// </summary>
    private static Dictionary<string, string>? ReadGgufStamp(string installRoot)
    {
        try
        {
            var path = GgufStampPath(installRoot);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static VoidResult<string> WriteGgufStamp(string installRoot, IReadOnlyDictionary<string, string> stamp)
    {
        try
        {
            var path = GgufStampPath(installRoot);
            if (stamp.Count == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return VoidResult<string>.Success;
            }

            Directory.CreateDirectory(installRoot);
            File.WriteAllText(path, JsonSerializer.Serialize(stamp, JsonOptions));
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new VoidResult<string>($"Could not write {GgufStampPath(installRoot)}: {ex.Message}");
        }
    }

    private static bool CrispModelRemains(string installRoot, IReadOnlyDictionary<string, string> stamp) =>
        DysonGgufSpeechModels.CrispIds.Any(id =>
            stamp.ContainsKey(id)
            || Directory.Exists(Path.Combine(installRoot, DysonGgufSpeechModels.Dir(id)!)));

    private static IReadOnlyList<string> ProbeGguf(string installRoot, DysonSpeechManifest manifest)
    {
        if (!manifest.Rids.TryGetValue(SupportedRid, out var rid))
            return [];

        var stamp = ReadGgufStamp(installRoot);
        if (stamp is null || stamp.Count == 0)
            return [];

        var crispOk = CrispRuntimeOk(stamp, CrispStampKey, rid.CrispasrRuntime, DysonGgufSpeechModels.GetCrispExe(installRoot));
        var installed = new List<string>();
        foreach (var id in DysonGgufSpeechModels.AllIds)
        {
            var pin = GgufPin(rid, id);
            if (pin is null || !stamp.TryGetValue(id, out var got) || !string.Equals(got, pin, StringComparison.Ordinal))
                continue;
            if (!File.Exists(DysonGgufSpeechModels.GetMainModelFile(installRoot, id)))
                continue;
            var companion = DysonGgufSpeechModels.GetCompanionFile(installRoot, id);
            if (companion is not null && !File.Exists(companion))
                continue;
            if (id == DysonGgufSpeechModels.NemotronId)
            {
                if (!File.Exists(DysonGgufSpeechModels.GetTranscribeDll(installRoot)))
                    continue;
            }
            else if (!crispOk)
            {
                continue;
            }

            installed.Add(id);
        }

        return installed;
    }

    private static bool GgufAvailable(DysonSpeechRidManifest manifest, string runtimeId) =>
        GgufFiles(manifest, runtimeId) is not null
        && (!DysonGgufSpeechModels.IsCrisp(runtimeId) || manifest.CrispasrRuntime is not null);

    private static IReadOnlyList<DysonSpeechFile>? GgufFiles(DysonSpeechRidManifest manifest, string runtimeId) =>
        runtimeId switch
        {
            DysonGgufSpeechModels.NemotronId when manifest.NemotronNative is not null && manifest.NemotronWeights is not null
                => [manifest.NemotronNative, manifest.NemotronWeights],
            DysonGgufSpeechModels.PhononGgufId when manifest.PhononGguf is not null => [manifest.PhononGguf],
            DysonGgufSpeechModels.PhononGgufQ8Id when manifest.PhononGgufQ8 is not null => [manifest.PhononGgufQ8],
            DysonGgufSpeechModels.PocketGgufId when manifest.PocketGguf is not null => [manifest.PocketGguf],
            DysonGgufSpeechModels.PocketGgufQ8Id when manifest.PocketGgufQ8 is not null => [manifest.PocketGgufQ8],
            DysonGgufSpeechModels.QwenGgufId when manifest.QwenGguf is { Count: > 0 } => manifest.QwenGguf,
            DysonGgufSpeechModels.QwenGgufQ4Id when manifest.QwenGgufQ4 is { Count: > 0 } => manifest.QwenGgufQ4,
            DysonGgufSpeechModels.QwenGgufF16Id when manifest.QwenGgufF16 is { Count: > 0 } => manifest.QwenGgufF16,
            DysonGgufSpeechModels.ChatterboxGgufId when manifest.ChatterboxGguf is { Count: > 0 } => manifest.ChatterboxGguf,
            DysonGgufSpeechModels.ChatterboxGgufQ8Id when manifest.ChatterboxGgufQ8 is { Count: > 0 } => manifest.ChatterboxGgufQ8,
            DysonGgufSpeechModels.WhisperTinyId when manifest.WhisperTiny is not null => [manifest.WhisperTiny],
            DysonGgufSpeechModels.WhisperBaseId when manifest.WhisperBase is not null => [manifest.WhisperBase],
            _ => null,
        };

    /// <summary>Main-file sha256. Nemotron is <c>weightsSha+nativeSha</c>.</summary>
    private static string? GgufPin(DysonSpeechRidManifest manifest, string runtimeId) => runtimeId switch
    {
        DysonGgufSpeechModels.NemotronId when manifest.NemotronWeights is not null && manifest.NemotronNative is not null
            => manifest.NemotronWeights.Sha256 + "+" + manifest.NemotronNative.Sha256,
        DysonGgufSpeechModels.PhononGgufId => manifest.PhononGguf?.Sha256,
        DysonGgufSpeechModels.PhononGgufQ8Id => manifest.PhononGgufQ8?.Sha256,
        DysonGgufSpeechModels.PocketGgufId => manifest.PocketGguf?.Sha256,
        DysonGgufSpeechModels.PocketGgufQ8Id => manifest.PocketGgufQ8?.Sha256,
        DysonGgufSpeechModels.QwenGgufId => manifest.QwenGguf?
            .FirstOrDefault(f => f.FileName == DysonGgufSpeechModels.QwenGgufFile)?.Sha256,
        DysonGgufSpeechModels.QwenGgufQ4Id => manifest.QwenGgufQ4?
            .FirstOrDefault(f => f.FileName == DysonGgufSpeechModels.QwenGgufQ4File)?.Sha256,
        DysonGgufSpeechModels.QwenGgufF16Id => manifest.QwenGgufF16?
            .FirstOrDefault(f => f.FileName == DysonGgufSpeechModels.QwenGgufF16File)?.Sha256,
        DysonGgufSpeechModels.ChatterboxGgufId => manifest.ChatterboxGguf?
            .FirstOrDefault(f => f.FileName == DysonGgufSpeechModels.ChatterboxT3File)?.Sha256,
        DysonGgufSpeechModels.ChatterboxGgufQ8Id => manifest.ChatterboxGgufQ8?
            .FirstOrDefault(f => f.FileName == DysonGgufSpeechModels.ChatterboxT3Q8File)?.Sha256,
        DysonGgufSpeechModels.WhisperTinyId => manifest.WhisperTiny?.Sha256,
        DysonGgufSpeechModels.WhisperBaseId => manifest.WhisperBase?.Sha256,
        _ => null,
    };

    /// <summary>
    /// What <see cref="Adopt"/> proved. <see cref="PlanSteps"/> drops downloads for a matched set. <see cref="PocketVoiceMatched"/>
    /// is Pocket weights plus <c>alba</c>; the voice is not required to adopt the weights.
    /// </summary>
    internal sealed record DysonSpeechAdopt(
        DysonSpeechInstallStatus Status,
        bool SkipTorchWheel,
        bool PipTorchDeps,
        IReadOnlySet<string> SkipWeightIds,
        IReadOnlySet<string> PipSiteIds,
        bool SkipPython,
        bool SkipCrisp,
        bool NemotronWeightsOnDisk,
        bool NemotronDllOnDisk,
        bool PocketVoiceMatched)
    {
        public static DysonSpeechAdopt Empty { get; } = new(
            DysonSpeechInstallStatus.None(null),
            false, false,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            false, false, false, false, false);
    }

    /// <summary>
    /// When <paramref name="probed"/> does not already report a requested id, match essential on-disk files and write
    /// the stamp so the next probe takes the existing DB-sync repair. No HTTP. Does not delete or truncate anything.
    /// Hash only after <c>Length == Size</c>. ponytail: torch is the dist-info folder name (not a hash of
    /// <c>torch_cpu.dll</c>); Phonon is a non-empty unpack dir (the archive is not kept); <c>crispasr.exe</c> and
    /// <c>transcribe.dll</c> are existence-only.
    /// </summary>
    internal static DysonSpeechAdopt Adopt(
        string installRoot,
        DysonSpeechManifest manifest,
        DysonSpeechInstallStatus probed,
        IReadOnlyCollection<string> requested)
    {
        if (!manifest.Rids.TryGetValue(SupportedRid, out var rid))
            return DysonSpeechAdopt.Empty with { Status = probed };

        var torchInfo = TorchDistInfoName(manifest.TorchRequirement);
        var torchMatches = TorchTreeMatches(installRoot, torchInfo);
        var pythonExists = File.Exists(DysonSpeechPaths.GetPythonExe(installRoot));
        var skipCrisp = File.Exists(DysonGgufSpeechModels.GetCrispExe(installRoot));
        var pipTorch = torchMatches
            && !OtherTorchDistInfo(installRoot, torchInfo)
            && rid.Torch.Any(f => !DysonSpeechManifest.IsTorchWheel(f));

        var skip = new HashSet<string>(StringComparer.Ordinal);
        var pipSites = new HashSet<string>(StringComparer.Ordinal);
        var stampPython = new HashSet<string>(StringComparer.Ordinal);
        var nemoWeights = false;
        var nemoDll = false;
        foreach (var id in requested)
        {
            if (!ModelIds.Contains(id) || probed.IsInstalled(id))
                continue;
            if (id == DysonGgufSpeechModels.NemotronId)
            {
                nemoWeights = rid.NemotronWeights is not null
                    && FileMatches(DysonGgufSpeechModels.GetMainModelFile(installRoot, id), rid.NemotronWeights);
                nemoDll = File.Exists(DysonGgufSpeechModels.GetTranscribeDll(installRoot));
                if (nemoWeights && nemoDll)
                    skip.Add(id);
                continue;
            }

            if (!EssentialsMatch(installRoot, rid, id))
                continue;
            skip.Add(id);
            if (DysonGgufSpeechModels.IsGguf(id))
                continue;
            var site = PythonSite(installRoot, id);
            if (site is not null && SiteHasDistInfo(site))
                stampPython.Add(id);
            else
                pipSites.Add(id);
        }

        var pocketVoice = skip.Contains(DysonPocketSpeech.Id)
            && FileMatches(Path.Combine(DysonSpeechPaths.GetPocketVoicesDir(installRoot), CatalogVoiceId + ".safetensors"), rid.PocketVoice);

        try
        {
            WriteAdoptStamps(installRoot, manifest, rid, probed, requested, torchMatches, pythonExists, skipCrisp, skip, stampPython);
            if (pocketVoice && !EnsurePocketVoiceFiles(installRoot))
                pocketVoice = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            pocketVoice = false;
        }

        return new DysonSpeechAdopt(
            ProbeFiles(installRoot, manifest), torchMatches, pipTorch, skip, pipSites, pythonExists, skipCrisp,
            nemoWeights, nemoDll, pocketVoice);
    }

    private static void WriteAdoptStamps(
        string installRoot,
        DysonSpeechManifest manifest,
        DysonSpeechRidManifest rid,
        DysonSpeechInstallStatus probed,
        IReadOnlyCollection<string> requested,
        bool torchMatches,
        bool pythonExists,
        bool skipCrisp,
        HashSet<string> skip,
        HashSet<string> stampPython)
    {
        var previous = ReadStamp(installRoot);
        var sharedMismatch = previous is null
            || previous.SchemaVersion != manifest.SchemaVersion
            || previous.PythonVersion != manifest.PythonVersion
            || previous.TorchRequirement != manifest.TorchRequirement
            || previous.Rid != SupportedRid;
        if (torchMatches && pythonExists && (stampPython.Count > 0 || (previous is not null && sharedMismatch)))
        {
            Directory.CreateDirectory(installRoot);
            var stamp = new DysonSpeechInstallStamp(
                manifest.SchemaVersion, manifest.PythonVersion, manifest.TorchRequirement,
                previous?.PhononPackage, previous?.PocketPackage, SupportedRid, DateTime.UtcNow,
                previous?.QwenPackage, previous?.ChatterboxPackage);
            foreach (var id in requested)
            {
                if (DysonGgufSpeechModels.IsGguf(id) || probed.IsInstalled(id))
                    continue;
                stamp = WithPackage(stamp, id, stampPython.Contains(id) ? PackageOf(manifest, id) : null);
            }

            WriteStamp(installRoot, stamp);
        }

        Dictionary<string, string>? gguf = null;
        foreach (var id in requested)
        {
            if (!DysonGgufSpeechModels.IsGguf(id) || probed.IsInstalled(id) || !skip.Contains(id))
                continue;
            if (DysonGgufSpeechModels.IsCrisp(id) && !skipCrisp)
                continue;
            var pin = GgufPin(rid, id);
            if (pin is null)
                continue;
            gguf ??= new Dictionary<string, string>(
                ReadGgufStamp(installRoot) ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            gguf[id] = pin;
            if (DysonGgufSpeechModels.IsCrisp(id) && rid.CrispasrRuntime is not null)
                gguf[CrispStampKey] = rid.CrispasrRuntime.Sha256;
        }

        if (gguf is not null)
            WriteGgufStamp(installRoot, gguf);
    }

    /// <summary><c>english.yaml</c> and <c>bridge.py</c> when alba and the weights are already on disk. False if either write failed.</summary>
    private static bool EnsurePocketVoiceFiles(string installRoot)
    {
        var config = DysonSpeechPaths.GetPocketConfig(installRoot);
        if (!File.Exists(config))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, BuildPocketConfig(installRoot));
        }

        var bridge = DysonSpeechPaths.GetPocketBridge(installRoot);
        if (!File.Exists(bridge))
        {
            var write = WriteBridge("pocket-config", BridgeResourceName, bridge);
            if (write.IsError)
                return false;
        }

        return File.Exists(config) && File.Exists(bridge);
    }

    private static VoidResult<string> WriteBridge(string stepId, string resourceName, string path)
    {
        using var bridge = typeof(DysonSpeechInstaller).Assembly.GetManifestResourceStream(resourceName);
        if (bridge is null)
            return new VoidResult<string>($"{stepId}: embedded {resourceName} is missing.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var output = File.Create(path))
            bridge.CopyTo(output);
        return VoidResult<string>.Success;
    }

    internal const string SentencesResourceName = "DysonHarness.Speech.speech_sentences.py";

    /// <summary>
    /// Copies the embedded <c>bridge.py</c> and its sibling <c>speech_sentences.py</c> over the installed files when
    /// missing or different, so an installed tree picks up a new script on app update. True when either was written
    /// (the running bridge still has the old code).
    /// </summary>
    internal static Result<bool, string> EnsureBridgeScripts(string bridgeResourceName, string bridgePath)
    {
        var sentences = EnsureEmbeddedScript(
            SentencesResourceName, Path.Combine(Path.GetDirectoryName(bridgePath)!, "speech_sentences.py"));
        if (sentences.IsError)
            return sentences;
        var bridge = EnsureEmbeddedScript(bridgeResourceName, bridgePath);
        return bridge.IsError ? bridge : Result<bool, string>.AsValue(sentences.Value || bridge.Value);
    }

    private static Result<bool, string> EnsureEmbeddedScript(string resourceName, string path)
    {
        using var stream = typeof(DysonSpeechInstaller).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return Result<bool, string>.AsError($"Embedded {resourceName} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                return Result<bool, string>.AsValue(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return Result<bool, string>.AsValue(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<bool, string>.AsError($"Could not write {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static bool EssentialsMatch(string installRoot, DysonSpeechRidManifest rid, string id)
    {
        if (id == DysonPhononSpeech.Id)
        {
            var dir = DysonSpeechPaths.GetPhononModelDir(installRoot);
            return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
        }

        if (id == DysonPocketSpeech.Id)
        {
            return FileMatches(DysonSpeechPaths.GetPocketModelFile(installRoot), rid.PocketWeights)
                && FileMatches(Path.Combine(installRoot, "pocket", "model", rid.PocketTokenizer.FileName), rid.PocketTokenizer);
        }

        if (id == DysonQwenSpeech.Id)
        {
            var model = DysonSpeechPaths.GetQwenModelDir(installRoot);
            return AllMatch(model, rid.QwenModel)
                && AllMatch(Path.Combine(model, "speech_tokenizer"), rid.QwenSpeechTokenizer);
        }

        if (id == DysonChatterboxSpeech.Id)
            return AllMatch(DysonSpeechPaths.GetChatterboxModelDir(installRoot), rid.ChatterboxModel);
        if (DysonGgufSpeechModels.IsWhisper(id))
        {
            var ggml = GgufFiles(rid, id);
            var ir = OpenVinoFiles(rid, id);
            return ggml is not null && ir is not null
                && AllMatch(DysonGgufSpeechModels.GetModelDir(installRoot, id), ggml)
                && AllMatch(DysonGgufSpeechModels.GetOpenVinoModelDir(installRoot, id), ir);
        }

        if (!DysonGgufSpeechModels.IsGguf(id))
            return false;
        var files = GgufFiles(rid, id);
        return files is not null && AllMatch(DysonGgufSpeechModels.GetModelDir(installRoot, id), files);
    }

    private static bool AllMatch(string dir, IReadOnlyList<DysonSpeechFile> files)
    {
        if (files.Count == 0)
            return false;
        foreach (var file in files)
        {
            if (!FileMatches(Path.Combine(dir, file.FileName), file))
                return false;
        }

        return true;
    }

    /// <summary>Exists, then size, then sha256. A size miss does not read the file. Never deletes.</summary>
    private static bool FileMatches(string path, DysonSpeechFile pin)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            if (new FileInfo(path).Length != pin.Size)
                return false;
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            return string.Equals(hash, pin.Sha256, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool RocmTorchReady(string installRoot, DysonSpeechManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.RocmTorchRequirement)
            || !manifest.Rids.TryGetValue(SupportedRid, out var rid)
            || rid.RocmTorch is not { Count: > 0 })
            return false;
        var dir = DysonSpeechPaths.GetRocmTorchDir(installRoot);
        var info = TorchDistInfoName(manifest.RocmTorchRequirement);
        // rocm_sdk comes from the rocm sdist; torch/_rocm_init.py imports it first. A bare directory is not the package.
        return Directory.Exists(Path.Combine(dir, "torch"))
            && Directory.Exists(Path.Combine(dir, info))
            && File.Exists(Path.Combine(dir, "rocm_sdk", "__init__.py"));
    }

    /// <summary>
    /// pip <c>--target</c> leaves a foreign torch in place. A <c>torch-rocm</c> that exists but is not the pinned
    /// tree (no <c>rocm_sdk/__init__.py</c>, or otherwise not <see cref="RocmTorchReady"/>) is deleted. A ready tree is left alone.
    /// </summary>
    internal static VoidResult<string> DropUnreadyRocmTorch(string installRoot, DysonSpeechManifest manifest)
    {
        var dir = DysonSpeechPaths.GetRocmTorchDir(installRoot);
        if (!Directory.Exists(dir) || RocmTorchReady(installRoot, manifest))
            return VoidResult<string>.Success;
        var deleted = DeleteTree(dir);
        return deleted.IsError
            ? new VoidResult<string>("rocm-torch-install: " + deleted.Error)
            : VoidResult<string>.Success;
    }

    private static bool TorchTreeMatches(string installRoot, string distInfoName)
    {
        var torch = DysonSpeechPaths.GetTorchDir(installRoot);
        return Directory.Exists(Path.Combine(torch, "torch"))
            && Directory.Exists(Path.Combine(torch, distInfoName));
    }

    private static bool OtherTorchDistInfo(string installRoot, string distInfoName)
    {
        var torch = DysonSpeechPaths.GetTorchDir(installRoot);
        if (!Directory.Exists(torch))
            return false;
        foreach (var path in Directory.EnumerateDirectories(torch, "*.dist-info"))
        {
            if (!string.Equals(Path.GetFileName(path), distInfoName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string TorchDistInfoName(string torchRequirement)
    {
        const string prefix = "torch==";
        var version = torchRequirement.StartsWith(prefix, StringComparison.Ordinal)
            ? torchRequirement[prefix.Length..]
            : torchRequirement;
        return "torch-" + version + ".dist-info";
    }

    private static bool SiteHasDistInfo(string site) =>
        Directory.Exists(site) && Directory.EnumerateDirectories(site, "*.dist-info").Any();

    private static string? PythonSite(string installRoot, string id) => id switch
    {
        DysonPhononSpeech.Id => DysonSpeechPaths.GetPhononSite(installRoot),
        DysonPocketSpeech.Id => DysonSpeechPaths.GetPocketSite(installRoot),
        DysonQwenSpeech.Id => DysonSpeechPaths.GetQwenSite(installRoot),
        DysonChatterboxSpeech.Id => DysonSpeechPaths.GetChatterboxSite(installRoot),
        _ => null,
    };

    /// <summary>Moves the single top-level folder's contents into <paramref name="dest"/> when the archive is wrapped.</summary>
    private static VoidResult<string> FlattenMove(string extracted, string dest, string stepId, string expectedFile)
    {
        try
        {
            Directory.CreateDirectory(dest);
            var topFiles = Directory.GetFiles(extracted);
            var topDirs = Directory.GetDirectories(extracted);
            var source = topFiles.Length == 0 && topDirs.Length == 1 ? topDirs[0] : extracted;
            foreach (var file in Directory.GetFiles(source))
                File.Move(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.GetDirectories(source))
                Directory.Move(dir, Path.Combine(dest, Path.GetFileName(dir)));
            return File.Exists(Path.Combine(dest, expectedFile))
                ? VoidResult<string>.Success
                : new VoidResult<string>($"{stepId}: {expectedFile} is not in the archive.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new VoidResult<string>($"{stepId}: {ex.Message}");
        }
    }

    /// <summary>Shared pins + one nullable package pin per model. Plan-97 stamps (no Qwen / Chatterbox) still read.</summary>
    private sealed record DysonSpeechInstallStamp(
        int SchemaVersion,
        string PythonVersion,
        string TorchRequirement,
        string? PhononPackage,
        string? PocketPackage,
        string Rid,
        DateTime InstalledUtc,
        string? QwenPackage = null,
        string? ChatterboxPackage = null);

    /// <summary>One install run. Everything here already runs on the thread pool.</summary>
    private sealed class Pipeline(
        DysonSpeechInstaller owner,
        IDysonSpeechModelRepository models,
        IDysonSubjectSettingsRepository settings,
        IReadOnlyList<string> requested,
        PipelineKind kind,
        ChannelWriter<DysonSpeechInstallStep> writer,
        CancellationToken ct)
    {
        private readonly string _root = owner.InstallRoot;
        private readonly List<string> _swapped = [];
        private string Staging => Path.Combine(_root, "staging");
        private string Downloads => Path.Combine(Staging, "downloads");
        private string Tree => Path.Combine(Staging, "tree");
        private string Old => Path.Combine(Staging, "old");
        private DysonSpeechManifest _manifest = null!;
        private DysonSpeechRidManifest _m = null!;
        private DysonSpeechInstallStatus _before = DysonSpeechInstallStatus.None(null);
        private IReadOnlyList<string> _plan = [];
        private List<string> _building = [];
        private DysonSpeechAdopt _adopt = DysonSpeechAdopt.Empty;
        private bool _swapPython;
        private bool _swapTorch;
        private bool _swapRocm;
        private bool _useRocmTorch;
        private bool _swapCrisp;
        private bool _swapCrispVulkan;
        private bool _swapOpenVino;
        private bool _committed;
        private string? _doneDetail;

        /// <summary>The staged interpreter while python is extracted this run, else the installed one.</summary>
        private string PythonRoot => _swapPython && !_committed ? Tree : _root;

        public async Task RunAsync()
        {
            var torchWheels = Path.Combine(Downloads, "torch");
            var phononWheels = Path.Combine(Downloads, "phonon-wheelhouse");
            var pocketWheels = Path.Combine(Downloads, "pocket-wheelhouse");
            var qwenWheels = Path.Combine(Downloads, "qwen-wheelhouse");
            var qwenModel = Path.Combine(Downloads, "qwen-model");
            var qwenTokenizer = Path.Combine(Downloads, "qwen-speech-tokenizer");
            var chatterboxWheels = Path.Combine(Downloads, "chatterbox-wheelhouse");
            var chatterboxModel = Path.Combine(Downloads, "chatterbox-model");
            var crispZip = Path.Combine(Downloads, "crispasr");
            var crispVulkanZip = Path.Combine(Downloads, "crispasr-vulkan");
            var nemotronFiles = Path.Combine(Downloads, "nemotron");
            var phononGgufFiles = Path.Combine(Downloads, "phonon-gguf");
            var phononGgufQ8Files = Path.Combine(Downloads, "phonon-gguf-q8");
            var pocketGgufFiles = Path.Combine(Downloads, "pocket-gguf");
            var pocketGgufQ8Files = Path.Combine(Downloads, "pocket-gguf-q8");
            var qwenGgufFiles = Path.Combine(Downloads, "qwen-gguf");
            var qwenGgufQ4Files = Path.Combine(Downloads, "qwen-gguf-q4");
            var qwenGgufF16Files = Path.Combine(Downloads, "qwen-gguf-f16");
            var chatterboxGgufFiles = Path.Combine(Downloads, "chatterbox-gguf");
            var chatterboxGgufQ8Files = Path.Combine(Downloads, "chatterbox-gguf-q8");
            var openVinoWheels = Path.Combine(Downloads, "openvino");
            var whisperTinyFiles = Path.Combine(Downloads, "whisper-tiny");
            var whisperBaseFiles = Path.Combine(Downloads, "whisper-base");
            var whisperTinyIr = Path.Combine(Downloads, "whisper-tiny-openvino");
            var whisperBaseIr = Path.Combine(Downloads, "whisper-base-openvino");

            if (!await StepAsync("preflight", _ => PreflightAsync()) || _plan.Count <= 1)
                return;

            // Same order as StepIds; only the steps PlanSteps chose run.
            (string Id, Func<IProgress<DysonDownloadProgress>, Task<VoidResult<string>>> Work)[] steps =
            [
                ("python-download", p => DownloadAllAsync("python-download", [_m.Python], Downloads, p)),
                ("python-hash", _ => VerifyAllAsync("python-hash", [_m.Python], Downloads)),
                ("python-extract", _ => ExtractPythonAsync()),
                ("torch-download", p => DownloadAllAsync("torch-download", TorchPlanFiles(), torchWheels, p)),
                ("torch-hash", _ => VerifyAllAsync("torch-hash", TorchPlanFiles(), torchWheels)),
                ("torch-install", _ => PipInstallAsync("torch-install", TorchPlanFiles(), torchWheels, TorchInstallDir(), keepWheelhouseIn: null)),
                ("rocm-torch-download", p => DownloadAllAsync("rocm-torch-download", RocmPlanFiles(), Path.Combine(Downloads, "rocm-torch"), p)),
                ("rocm-torch-hash", _ => VerifyAllAsync("rocm-torch-hash", RocmPlanFiles(), Path.Combine(Downloads, "rocm-torch"))),
                ("rocm-torch-install", _ => InstallRocmTorchAsync()),
                ("crispasr-download", p => DownloadOneAsync("crispasr-download", _m.CrispasrRuntime, crispZip, p)),
                ("crispasr-hash", _ => VerifyOneAsync("crispasr-hash", _m.CrispasrRuntime, crispZip)),
                ("crispasr-extract", _ => Task.FromResult(ExtractCrispasr("crispasr-extract", _m.CrispasrRuntime, crispZip, DysonGgufSpeechModels.GetCrispRuntimeDir(Tree)))),
                ("crispasr-vulkan-download", p => DownloadOneAsync("crispasr-vulkan-download", _m.CrispasrVulkan, crispVulkanZip, p)),
                ("crispasr-vulkan-hash", _ => VerifyOneAsync("crispasr-vulkan-hash", _m.CrispasrVulkan, crispVulkanZip)),
                ("crispasr-vulkan-extract", _ => Task.FromResult(ExtractCrispasr("crispasr-vulkan-extract", _m.CrispasrVulkan, crispVulkanZip, DysonGgufSpeechModels.GetCrispVulkanRuntimeDir(Tree)))),
                ("openvino-download", p => DownloadAllAsync("openvino-download", _m.OpenVinoWheels ?? [], openVinoWheels, p)),
                ("openvino-hash", _ => VerifyAllAsync("openvino-hash", _m.OpenVinoWheels ?? [], openVinoWheels)),
                ("openvino-install", _ => InstallOpenVinoAsync(openVinoWheels)),
                ("phonon-wheels-download", p => DownloadAllAsync("phonon-wheels-download", _m.PhononWheels, phononWheels, p)),
                ("phonon-wheels-hash", _ => VerifyAllAsync("phonon-wheels-hash", _m.PhononWheels, phononWheels)),
                ("phonon-wheels-install", _ => PipInstallAsync("phonon-wheels-install", _m.PhononWheels, phononWheels, DysonSpeechPaths.GetPhononSite(InstallTree(DysonPhononSpeech.Id)), Path.Combine(InstallTree(DysonPhononSpeech.Id), "phonon", "wheelhouse"))),
                ("phonon-weights-download", p => DownloadAllAsync("phonon-weights-download", [_m.PhononWeights], Downloads, p)),
                ("phonon-weights-hash", _ => VerifyAllAsync("phonon-weights-hash", [_m.PhononWeights], Downloads)),
                ("phonon-weights-unpack", _ => UnpackPhononAsync()),
                ("nemotron-download", p => DownloadGgufAsync("nemotron-download", DysonGgufSpeechModels.NemotronId, nemotronFiles, p)),
                ("nemotron-hash", _ => VerifyGgufAsync("nemotron-hash", DysonGgufSpeechModels.NemotronId, nemotronFiles)),
                ("nemotron-unpack", _ => UnpackNemotronAsync(nemotronFiles)),
                ("phonon-gguf-download", p => DownloadGgufAsync("phonon-gguf-download", DysonGgufSpeechModels.PhononGgufId, phononGgufFiles, p)),
                ("phonon-gguf-hash", _ => VerifyGgufAsync("phonon-gguf-hash", DysonGgufSpeechModels.PhononGgufId, phononGgufFiles)),
                ("phonon-gguf-unpack", _ => Task.FromResult(MoveGguf("phonon-gguf-unpack", DysonGgufSpeechModels.PhononGgufId, phononGgufFiles, Tree))),
                ("phonon-gguf-q8-download", p => DownloadGgufAsync("phonon-gguf-q8-download", DysonGgufSpeechModels.PhononGgufQ8Id, phononGgufQ8Files, p)),
                ("phonon-gguf-q8-hash", _ => VerifyGgufAsync("phonon-gguf-q8-hash", DysonGgufSpeechModels.PhononGgufQ8Id, phononGgufQ8Files)),
                ("phonon-gguf-q8-unpack", _ => Task.FromResult(MoveGguf("phonon-gguf-q8-unpack", DysonGgufSpeechModels.PhononGgufQ8Id, phononGgufQ8Files, Tree))),
                ("whisper-tiny-download", p => DownloadGgufAsync("whisper-tiny-download", DysonGgufSpeechModels.WhisperTinyId, whisperTinyFiles, p)),
                ("whisper-tiny-hash", _ => VerifyGgufAsync("whisper-tiny-hash", DysonGgufSpeechModels.WhisperTinyId, whisperTinyFiles)),
                ("whisper-tiny-unpack", _ => Task.FromResult(MoveGguf("whisper-tiny-unpack", DysonGgufSpeechModels.WhisperTinyId, whisperTinyFiles, Tree))),
                ("whisper-tiny-openvino-download", p => DownloadOpenVinoAsync("whisper-tiny-openvino-download", DysonGgufSpeechModels.WhisperTinyId, whisperTinyIr, p)),
                ("whisper-tiny-openvino-hash", _ => VerifyOpenVinoAsync("whisper-tiny-openvino-hash", DysonGgufSpeechModels.WhisperTinyId, whisperTinyIr)),
                ("whisper-tiny-openvino-unpack", _ => Task.FromResult(MoveOpenVino("whisper-tiny-openvino-unpack", DysonGgufSpeechModels.WhisperTinyId, whisperTinyIr))),
                ("whisper-base-download", p => DownloadGgufAsync("whisper-base-download", DysonGgufSpeechModels.WhisperBaseId, whisperBaseFiles, p)),
                ("whisper-base-hash", _ => VerifyGgufAsync("whisper-base-hash", DysonGgufSpeechModels.WhisperBaseId, whisperBaseFiles)),
                ("whisper-base-unpack", _ => Task.FromResult(MoveGguf("whisper-base-unpack", DysonGgufSpeechModels.WhisperBaseId, whisperBaseFiles, Tree))),
                ("whisper-base-openvino-download", p => DownloadOpenVinoAsync("whisper-base-openvino-download", DysonGgufSpeechModels.WhisperBaseId, whisperBaseIr, p)),
                ("whisper-base-openvino-hash", _ => VerifyOpenVinoAsync("whisper-base-openvino-hash", DysonGgufSpeechModels.WhisperBaseId, whisperBaseIr)),
                ("whisper-base-openvino-unpack", _ => Task.FromResult(MoveOpenVino("whisper-base-openvino-unpack", DysonGgufSpeechModels.WhisperBaseId, whisperBaseIr))),
                ("pocket-wheels-download", p => DownloadAllAsync("pocket-wheels-download", _m.PocketWheels, pocketWheels, p)),
                ("pocket-wheels-hash", _ => VerifyAllAsync("pocket-wheels-hash", _m.PocketWheels, pocketWheels)),
                ("pocket-wheels-install", _ => PipInstallAsync("pocket-wheels-install", _m.PocketWheels, pocketWheels, DysonSpeechPaths.GetPocketSite(InstallTree(DysonPocketSpeech.Id)), Path.Combine(InstallTree(DysonPocketSpeech.Id), "pocket", "wheelhouse"))),
                ("pocket-weights-download", p => DownloadAllAsync("pocket-weights-download", [_m.PocketWeights, _m.PocketTokenizer], Downloads, p)),
                ("pocket-weights-hash", _ => VerifyAllAsync("pocket-weights-hash", [_m.PocketWeights, _m.PocketTokenizer], Downloads)),
                ("pocket-voice-download", p => DownloadAllAsync("pocket-voice-download", [_m.PocketVoice], Downloads, p)),
                ("pocket-voice-hash", _ => VerifyAllAsync("pocket-voice-hash", [_m.PocketVoice], Downloads)),
                ("pocket-config", _ => Task.FromResult(WritePocketConfig())),
                ("pocket-gguf-download", p => DownloadGgufAsync("pocket-gguf-download", DysonGgufSpeechModels.PocketGgufId, pocketGgufFiles, p)),
                ("pocket-gguf-hash", _ => VerifyGgufAsync("pocket-gguf-hash", DysonGgufSpeechModels.PocketGgufId, pocketGgufFiles)),
                ("pocket-gguf-unpack", _ => Task.FromResult(MoveGguf("pocket-gguf-unpack", DysonGgufSpeechModels.PocketGgufId, pocketGgufFiles, Tree))),
                ("pocket-gguf-q8-download", p => DownloadGgufAsync("pocket-gguf-q8-download", DysonGgufSpeechModels.PocketGgufQ8Id, pocketGgufQ8Files, p)),
                ("pocket-gguf-q8-hash", _ => VerifyGgufAsync("pocket-gguf-q8-hash", DysonGgufSpeechModels.PocketGgufQ8Id, pocketGgufQ8Files)),
                ("pocket-gguf-q8-unpack", _ => Task.FromResult(MoveGguf("pocket-gguf-q8-unpack", DysonGgufSpeechModels.PocketGgufQ8Id, pocketGgufQ8Files, Tree))),
                ("qwen-wheels-download", p => DownloadAllAsync("qwen-wheels-download", _m.QwenWheels, qwenWheels, p)),
                ("qwen-wheels-hash", _ => VerifyAllAsync("qwen-wheels-hash", _m.QwenWheels, qwenWheels)),
                ("qwen-wheels-install", _ => PipInstallAsync("qwen-wheels-install", _m.QwenWheels, qwenWheels, DysonSpeechPaths.GetQwenSite(InstallTree(DysonQwenSpeech.Id)), Path.Combine(InstallTree(DysonQwenSpeech.Id), "qwen", "wheelhouse"))),
                ("qwen-weights-download", async p =>
                {
                    var model = await DownloadAllAsync("qwen-weights-download", _m.QwenModel, qwenModel, p).ConfigureAwait(false);
                    return model.IsError ? model : await DownloadAllAsync("qwen-weights-download", _m.QwenSpeechTokenizer, qwenTokenizer, p).ConfigureAwait(false);
                }),
                ("qwen-weights-hash", async _ =>
                {
                    var model = await VerifyAllAsync("qwen-weights-hash", _m.QwenModel, qwenModel).ConfigureAwait(false);
                    return model.IsError ? model : await VerifyAllAsync("qwen-weights-hash", _m.QwenSpeechTokenizer, qwenTokenizer).ConfigureAwait(false);
                }),
                ("qwen-config", _ => Task.FromResult(WriteQwenConfig(qwenModel, qwenTokenizer))),
                ("qwen-gguf-download", p => DownloadGgufAsync("qwen-gguf-download", DysonGgufSpeechModels.QwenGgufId, qwenGgufFiles, p)),
                ("qwen-gguf-hash", _ => VerifyGgufAsync("qwen-gguf-hash", DysonGgufSpeechModels.QwenGgufId, qwenGgufFiles)),
                ("qwen-gguf-unpack", _ => Task.FromResult(MoveGguf("qwen-gguf-unpack", DysonGgufSpeechModels.QwenGgufId, qwenGgufFiles, Tree))),
                ("qwen-gguf-q4-download", p => DownloadGgufAsync("qwen-gguf-q4-download", DysonGgufSpeechModels.QwenGgufQ4Id, qwenGgufQ4Files, p)),
                ("qwen-gguf-q4-hash", _ => VerifyGgufAsync("qwen-gguf-q4-hash", DysonGgufSpeechModels.QwenGgufQ4Id, qwenGgufQ4Files)),
                ("qwen-gguf-q4-unpack", _ => Task.FromResult(MoveGguf("qwen-gguf-q4-unpack", DysonGgufSpeechModels.QwenGgufQ4Id, qwenGgufQ4Files, Tree))),
                ("qwen-gguf-f16-download", p => DownloadGgufAsync("qwen-gguf-f16-download", DysonGgufSpeechModels.QwenGgufF16Id, qwenGgufF16Files, p)),
                ("qwen-gguf-f16-hash", _ => VerifyGgufAsync("qwen-gguf-f16-hash", DysonGgufSpeechModels.QwenGgufF16Id, qwenGgufF16Files)),
                ("qwen-gguf-f16-unpack", _ => Task.FromResult(MoveGguf("qwen-gguf-f16-unpack", DysonGgufSpeechModels.QwenGgufF16Id, qwenGgufF16Files, Tree))),
                ("chatterbox-wheels-download", p => DownloadAllAsync("chatterbox-wheels-download", _m.ChatterboxWheels, chatterboxWheels, p)),
                ("chatterbox-wheels-hash", _ => VerifyAllAsync("chatterbox-wheels-hash", _m.ChatterboxWheels, chatterboxWheels)),
                ("chatterbox-wheels-install", _ => PipInstallAsync("chatterbox-wheels-install", _m.ChatterboxWheels, chatterboxWheels, DysonSpeechPaths.GetChatterboxSite(InstallTree(DysonChatterboxSpeech.Id)), Path.Combine(InstallTree(DysonChatterboxSpeech.Id), "chatterbox", "wheelhouse"))),
                ("chatterbox-weights-download", p => DownloadAllAsync("chatterbox-weights-download", _m.ChatterboxModel, chatterboxModel, p)),
                ("chatterbox-weights-hash", _ => VerifyAllAsync("chatterbox-weights-hash", _m.ChatterboxModel, chatterboxModel)),
                ("chatterbox-config", _ => Task.FromResult(WriteChatterboxConfig(chatterboxModel))),
                ("chatterbox-gguf-download", p => DownloadGgufAsync("chatterbox-gguf-download", DysonGgufSpeechModels.ChatterboxGgufId, chatterboxGgufFiles, p)),
                ("chatterbox-gguf-hash", _ => VerifyGgufAsync("chatterbox-gguf-hash", DysonGgufSpeechModels.ChatterboxGgufId, chatterboxGgufFiles)),
                ("chatterbox-gguf-unpack", _ => Task.FromResult(MoveGguf("chatterbox-gguf-unpack", DysonGgufSpeechModels.ChatterboxGgufId, chatterboxGgufFiles, Tree))),
                ("chatterbox-gguf-q8-download", p => DownloadGgufAsync("chatterbox-gguf-q8-download", DysonGgufSpeechModels.ChatterboxGgufQ8Id, chatterboxGgufQ8Files, p)),
                ("chatterbox-gguf-q8-hash", _ => VerifyGgufAsync("chatterbox-gguf-q8-hash", DysonGgufSpeechModels.ChatterboxGgufQ8Id, chatterboxGgufQ8Files)),
                ("chatterbox-gguf-q8-unpack", _ => Task.FromResult(MoveGguf("chatterbox-gguf-q8-unpack", DysonGgufSpeechModels.ChatterboxGgufQ8Id, chatterboxGgufQ8Files, Tree))),
                ("licenses", _ => Task.FromResult(WriteLicenses())),
                ("commit", _ => CommitAsync()),
                ("smoke", _ => SmokeAsync()),
            ];

            foreach (var (id, work) in steps)
            {
                if (_plan.Contains(id) && !await StepAsync(id, work))
                    return;
            }

            // A repair has no smoke step, which is what cleans staging after an install.
            if (_committed && !_plan.Contains("smoke"))
                DeleteTree(Staging);
        }

        private async Task<bool> StepAsync(
            string stepId,
            Func<IProgress<DysonDownloadProgress>, Task<VoidResult<string>>> work)
        {
            writer.TryWrite(new DysonSpeechInstallStep(stepId, DysonSpeechStepState.Running));
            VoidResult<string> result;
            _doneDetail = null;
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
                : new DysonSpeechInstallStep(stepId, DysonSpeechStepState.Done, _doneDetail));
            return !result.IsError;
        }

        private async Task<VoidResult<string>> PreflightAsync()
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

            _before = Probe(_root);
            if (kind == PipelineKind.RepairCrisp)
            {
                _plan = PlanRepairSteps(_before);
            }
            else if (kind == PipelineKind.RepairTorch)
            {
                var device = await settings.GetSettingAsync(DysonAppSettingKeys.SpeechTorchDevice, ct).ConfigureAwait(false);
                if (device.IsError)
                    return new VoidResult<string>("preflight: " + device.Error);
                var wantRocm = DysonSpeechTorchDevice.IsRocm(device.Value);
                _plan = PlanTorchRepairSteps(_before, wantRocm, RocmTorchReady(_root, _manifest));
            }
            else
            {
                _adopt = Adopt(_root, _manifest, _before, requested);
                _before = _adopt.Status;
                var device = await settings.GetSettingAsync(DysonAppSettingKeys.SpeechTorchDevice, ct).ConfigureAwait(false);
                if (device.IsError)
                    return new VoidResult<string>("preflight: " + device.Error);
                var wantRocm = DysonSpeechTorchDevice.IsRocm(device.Value);
                var rocmReady = RocmTorchReady(_root, _manifest);
                var pythonInvolved = requested.Any(id => !DysonGgufSpeechModels.IsGguf(id))
                    || _before.PhononInstalled || _before.PocketInstalled || _before.QwenInstalled || _before.ChatterboxInstalled;
                var installRocm = wantRocm && !rocmReady && pythonInvolved;
                _useRocmTorch = wantRocm && (rocmReady || installRocm);
                _plan = PlanSteps(_before, requested, _adopt, installRocm);
            }
            if (_plan.Count == 1)
            {
                var sync = await SyncDbAsync().ConfigureAwait(false);
                if (sync.IsError)
                    return new VoidResult<string>("preflight: " + sync.Error);
                _doneDetail = "Already installed.";
                return VoidResult<string>.Success;
            }

            _building = ModelIds.Where(id => requested.Contains(id) && !_before.IsInstalled(id)).ToList();
            _swapPython = _plan.Contains("python-extract");
            _swapTorch = _plan.Contains("torch-install") && !_adopt.SkipTorchWheel;
            _swapRocm = _plan.Contains("rocm-torch-install");
            _swapCrisp = _plan.Contains("crispasr-extract");
            _swapCrispVulkan = _plan.Contains("crispasr-vulkan-extract");
            _swapOpenVino = _plan.Contains("openvino-install");
            foreach (var id in _building)
            {
                if (DysonGgufSpeechModels.IsGguf(id) && !GgufAvailable(_m, id))
                    return new VoidResult<string>($"{id}: not available for this platform.");
                if (DysonGgufSpeechModels.IsWhisper(id)
                    && (OpenVinoFiles(_m, id) is null || _m.OpenVinoWheels is not { Count: > 0 }))
                    return new VoidResult<string>($"{id}: OpenVINO files are not in the speech manifest.");
            }

            Directory.CreateDirectory(_root);
            var downloadRuntime = _plan.Contains("python-download")
                || (_plan.Contains("torch-download") && !_adopt.SkipTorchWheel);
            var need = kind == PipelineKind.RepairCrisp
                ? 2 * CrispAsrBytes
                : RequiredFreeBytes(downloadRuntime, _building, _adopt, _plan.Contains("rocm-torch-download"));
            var free = new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace;
            if (free < need)
                return new VoidResult<string>($"preflight: needs {need:N0} free bytes on {Path.GetPathRoot(_root)}, has {free:N0}.");

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

        private Task<VoidResult<string>> DownloadOneAsync(
            string stepId, DysonSpeechFile? file, string dir, IProgress<DysonDownloadProgress> progress) =>
            file is null
                ? Task.FromResult(new VoidResult<string>($"{stepId}: not available for this platform."))
                : DownloadAllAsync(stepId, [file], dir, progress);

        private Task<VoidResult<string>> VerifyOneAsync(string stepId, DysonSpeechFile? file, string dir) =>
            file is null
                ? Task.FromResult(new VoidResult<string>($"{stepId}: not available for this platform."))
                : VerifyAllAsync(stepId, [file], dir);

        private Task<VoidResult<string>> DownloadGgufAsync(
            string stepId, string runtimeId, string dir, IProgress<DysonDownloadProgress> progress)
        {
            var files = GgufFilesForRun(runtimeId);
            return files is null
                ? Task.FromResult(new VoidResult<string>($"{stepId}: not available for this platform."))
                : DownloadAllAsync(stepId, files, dir, progress);
        }

        private Task<VoidResult<string>> VerifyGgufAsync(string stepId, string runtimeId, string dir)
        {
            var files = GgufFilesForRun(runtimeId);
            return files is null
                ? Task.FromResult(new VoidResult<string>($"{stepId}: not available for this platform."))
                : VerifyAllAsync(stepId, files, dir);
        }

        /// <summary>Nemotron keeps the half already on disk. Other ids download the whole pin list.</summary>
        private IReadOnlyList<DysonSpeechFile>? GgufFilesForRun(string runtimeId)
        {
            var files = GgufFiles(_m, runtimeId);
            if (files is null || runtimeId != DysonGgufSpeechModels.NemotronId
                || _m.NemotronNative is null || _m.NemotronWeights is null)
                return files;
            if (_adopt.NemotronWeightsOnDisk && !_adopt.NemotronDllOnDisk)
                return files.Where(f => f.FileName == _m.NemotronNative.FileName).ToList();
            if (_adopt.NemotronDllOnDisk && !_adopt.NemotronWeightsOnDisk)
                return files.Where(f => f.FileName == _m.NemotronWeights.FileName).ToList();
            return files;
        }

        /// <summary>Unzips one CrispASR runtime and flattens it into <paramref name="dest"/> (a staged runtime folder).</summary>
        private VoidResult<string> ExtractCrispasr(string stepId, DysonSpeechFile? file, string zipDir, string dest)
        {
            if (file is null)
                return new VoidResult<string>($"{stepId}: not available for this platform.");

            var extracted = Path.Combine(Staging, Path.GetFileName(dest) + "-zip");
            var clean = DeleteTree(extracted);
            if (clean.IsError)
                return new VoidResult<string>($"{stepId}: " + clean.Error);
            Directory.CreateDirectory(extracted);
            ZipFile.ExtractToDirectory(Path.Combine(zipDir, file.FileName), extracted);
            return FlattenMove(extracted, dest, stepId, "crispasr.exe");
        }

        private async Task<VoidResult<string>> UnpackNemotronAsync(string downloadDir)
        {
            var native = _m.NemotronNative;
            var weights = _m.NemotronWeights;
            if (native is null || weights is null)
                return new VoidResult<string>("nemotron-unpack: not available for this platform.");

            var live = _adopt.NemotronWeightsOnDisk || _adopt.NemotronDllOnDisk;
            var root = live ? _root : Tree;
            if (!_adopt.NemotronDllOnDisk)
            {
                var extracted = Path.Combine(Staging, "nemotron-native");
                var clean = DeleteTree(extracted);
                if (clean.IsError)
                    return new VoidResult<string>("nemotron-unpack: " + clean.Error);
                Directory.CreateDirectory(extracted);
                await using (var file = File.OpenRead(Path.Combine(downloadDir, native.FileName)))
                await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                    await TarFile.ExtractToDirectoryAsync(gzip, extracted, overwriteFiles: true, ct).ConfigureAwait(false);

                var runtime = FlattenMove(
                    extracted, DysonGgufSpeechModels.GetNemotronRuntimeDir(root), "nemotron-unpack", "transcribe.dll");
                if (runtime.IsError)
                    return runtime;
            }

            return _adopt.NemotronWeightsOnDisk
                ? VoidResult<string>.Success
                : MoveGguf("nemotron-unpack", DysonGgufSpeechModels.NemotronId, downloadDir, root, [weights]);
        }

        private Task<VoidResult<string>> DownloadOpenVinoAsync(
            string stepId, string runtimeId, string dir, IProgress<DysonDownloadProgress> progress)
        {
            var files = OpenVinoFiles(_m, runtimeId);
            return files is null
                ? Task.FromResult(new VoidResult<string>($"{stepId}: not available for this platform."))
                : DownloadAllAsync(stepId, files, dir, progress);
        }

        private Task<VoidResult<string>> VerifyOpenVinoAsync(string stepId, string runtimeId, string dir)
        {
            var files = OpenVinoFiles(_m, runtimeId);
            return files is null
                ? Task.FromResult(new VoidResult<string>($"{stepId}: not available for this platform."))
                : VerifyAllAsync(stepId, files, dir);
        }

        private Task<VoidResult<string>> InstallOpenVinoAsync(string wheelDir)
        {
            var files = _m.OpenVinoWheels;
            return files is not { Count: > 0 }
                ? Task.FromResult(new VoidResult<string>("openvino-install: not available for this platform."))
                : PipInstallAsync("openvino-install", files, wheelDir, DysonSpeechPaths.GetOpenVinoSite(Tree), keepWheelhouseIn: null);
        }

        private VoidResult<string> MoveOpenVino(string stepId, string runtimeId, string downloadDir)
        {
            var files = OpenVinoFiles(_m, runtimeId);
            if (files is null)
                return new VoidResult<string>($"{stepId}: not available for this platform.");
            try
            {
                var dir = DysonGgufSpeechModels.GetOpenVinoModelDir(Tree, runtimeId);
                Directory.CreateDirectory(dir);
                foreach (var file in files)
                    File.Move(Path.Combine(downloadDir, file.FileName), Path.Combine(dir, file.FileName), overwrite: true);
                if (!File.Exists(Path.Combine(dir, "openvino_encoder_model.xml")))
                    return new VoidResult<string>($"{stepId}: OpenVINO encoder is missing.");
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new VoidResult<string>($"{stepId}: {ex.Message}");
            }
        }

        private VoidResult<string> MoveGguf(string stepId, string runtimeId, string downloadDir, string treeRoot)
        {
            var files = GgufFiles(_m, runtimeId);
            return files is null
                ? new VoidResult<string>($"{stepId}: not available for this platform.")
                : MoveGguf(stepId, runtimeId, downloadDir, treeRoot, files);
        }

        private VoidResult<string> MoveGguf(
            string stepId, string runtimeId, string downloadDir, string treeRoot, IReadOnlyList<DysonSpeechFile> files)
        {
            try
            {
                var modelDir = DysonGgufSpeechModels.GetModelDir(treeRoot, runtimeId);
                Directory.CreateDirectory(modelDir);
                Directory.CreateDirectory(DysonGgufSpeechModels.GetVoicesDir(treeRoot, runtimeId));
                foreach (var file in files)
                    File.Move(Path.Combine(downloadDir, file.FileName), Path.Combine(modelDir, file.FileName), overwrite: true);
                if (!File.Exists(DysonGgufSpeechModels.GetMainModelFile(treeRoot, runtimeId)))
                    return new VoidResult<string>($"{stepId}: main model file is missing.");
                var companion = DysonGgufSpeechModels.GetCompanionFile(treeRoot, runtimeId);
                if (companion is not null && !File.Exists(companion))
                    return new VoidResult<string>($"{stepId}: companion model file is missing.");
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new VoidResult<string>($"{stepId}: {ex.Message}");
            }
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

        /// <summary>
        /// Deletes a live or staged <c>torch-rocm</c> that is not the pinned tree, then pip-installs into staging.
        /// The pins include the <c>rocm</c> sdist (<c>import rocm_sdk</c>); building it offline needs setuptools in
        /// the private interpreter and <c>--no-build-isolation</c>, so the pinned setuptools wheel goes in first.
        /// Already on the installer's thread-pool hop. A delete failure is a Failed step, not a throw.
        /// </summary>
        private async Task<VoidResult<string>> InstallRocmTorchAsync()
        {
            var live = DropUnreadyRocmTorch(_root, _manifest);
            if (live.IsError)
                return live;
            var staged = DropUnreadyRocmTorch(Tree, _manifest);
            if (staged.IsError)
                return staged;
            var wheels = Path.Combine(Downloads, "rocm-torch");
            if ((await RunPythonAsync("rocm-torch-install", [], ["-c", "import setuptools"]).ConfigureAwait(false)).IsError)
            {
                var setuptools = RocmPlanFiles().Where(f => f.FileName.StartsWith("setuptools-", StringComparison.OrdinalIgnoreCase)).ToList();
                if (setuptools.Count == 0)
                    return new VoidResult<string>("rocm-torch-install: the manifest rocmTorch pins have no setuptools wheel to build the rocm sdist.");
                var bootstrap = await PipInstallAsync("rocm-torch-setuptools", setuptools, wheels, target: null, keepWheelhouseIn: null).ConfigureAwait(false);
                if (bootstrap.IsError)
                    return bootstrap;
            }

            return await PipInstallAsync(
                "rocm-torch-install",
                RocmPlanFiles(),
                wheels,
                DysonSpeechPaths.GetRocmTorchDir(Tree),
                keepWheelhouseIn: null,
                noBuildIsolation: true).ConfigureAwait(false);
        }

        private async Task<VoidResult<string>> PipInstallAsync(
            string stepId,
            IReadOnlyList<DysonSpeechFile> wheels,
            string wheelDir,
            string? target,
            string? keepWheelhouseIn,
            bool noBuildIsolation = false)
        {
            var requirements = Path.Combine(Staging, stepId + ".txt");
            await File.WriteAllTextAsync(requirements, BuildRequirements(wheels, wheelDir), ct).ConfigureAwait(false);
            // target null installs into the private interpreter itself (the setuptools bootstrap).
            var pip = await RunPythonAsync(stepId, [],
            [
                "-m", "pip", "install", "--isolated", "--no-cache-dir", "--no-index", "--no-deps",
                "--require-hashes", "--no-warn-script-location",
                .. noBuildIsolation ? ["--no-build-isolation"] : Array.Empty<string>(),
                .. target is null ? Array.Empty<string>() : ["--target", target],
                "-r", requirements,
            ]).ConfigureAwait(false);
            if (pip.IsError || keepWheelhouseIn is null)
                return pip;

            Directory.CreateDirectory(Path.GetDirectoryName(keepWheelhouseIn)!);
            if (Directory.Exists(keepWheelhouseIn))
            {
                var cleared = DeleteTree(keepWheelhouseIn);
                if (cleared.IsError)
                    return cleared;
            }

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
            var adopted = _adopt.SkipWeightIds.Contains(DysonPocketSpeech.Id);
            var root = adopted ? _root : Tree;
            var modelDir = Path.Combine(root, "pocket", "model");
            var voicesDir = DysonSpeechPaths.GetPocketVoicesDir(root);
            Directory.CreateDirectory(modelDir);
            Directory.CreateDirectory(voicesDir);
            if (!adopted)
            {
                File.Copy(Path.Combine(Downloads, _m.PocketWeights.FileName), Path.Combine(modelDir, "model.safetensors"), overwrite: true);
                File.Copy(Path.Combine(Downloads, _m.PocketTokenizer.FileName), Path.Combine(modelDir, "tokenizer.json"), overwrite: true);
            }

            if (!adopted || _plan.Contains("pocket-voice-download"))
                File.Copy(Path.Combine(Downloads, _m.PocketVoice.FileName), Path.Combine(voicesDir, CatalogVoiceId + ".safetensors"), overwrite: true);

            // Paths point at the final location; a staged tree is swapped into place at commit.
            var config = DysonSpeechPaths.GetPocketConfig(root);
            if (!adopted || !File.Exists(config))
                File.WriteAllText(config, BuildPocketConfig(_root));
            var bridge = DysonSpeechPaths.GetPocketBridge(root);
            if (!adopted || !File.Exists(bridge))
                return WriteBridge("pocket-config", BridgeResourceName, bridge);
            return VoidResult<string>.Success;
        }

        /// <summary>Moves (not copies: same volume, no second 2.5 GB) the verified weights into <c>tree/qwen/model</c>.</summary>
        private VoidResult<string> WriteQwenConfig(string modelDownloads, string tokenizerDownloads)
        {
            var modelDir = DysonSpeechPaths.GetQwenModelDir(Tree);
            var tokenizerDir = Path.Combine(modelDir, "speech_tokenizer");
            Directory.CreateDirectory(tokenizerDir);
            Directory.CreateDirectory(DysonSpeechPaths.GetQwenVoicesDir(Tree));
            foreach (var file in _m.QwenModel)
                File.Move(Path.Combine(modelDownloads, file.FileName), Path.Combine(modelDir, file.FileName), overwrite: true);
            foreach (var file in _m.QwenSpeechTokenizer)
                File.Move(Path.Combine(tokenizerDownloads, file.FileName), Path.Combine(tokenizerDir, file.FileName), overwrite: true);
            return WriteBridge("qwen-config", DysonQwenSpeech.BridgeResourceName, DysonSpeechPaths.GetQwenBridge(Tree));
        }

        /// <summary>Moves the 9 verified files into <c>tree/chatterbox/model</c>; <c>conds.pt</c> becomes catalog voice <c>turbo</c>.</summary>
        private VoidResult<string> WriteChatterboxConfig(string modelDownloads)
        {
            var modelDir = DysonSpeechPaths.GetChatterboxModelDir(Tree);
            var voicesDir = DysonSpeechPaths.GetChatterboxVoicesDir(Tree);
            Directory.CreateDirectory(modelDir);
            Directory.CreateDirectory(voicesDir);
            foreach (var file in _m.ChatterboxModel)
                File.Move(Path.Combine(modelDownloads, file.FileName), Path.Combine(modelDir, file.FileName), overwrite: true);

            var conds = Path.Combine(modelDir, "conds.pt");
            if (!File.Exists(conds))
                return new VoidResult<string>("chatterbox-config: the manifest has no conds.pt (built-in turbo voice).");
            File.Copy(conds, Path.Combine(voicesDir, DysonChatterboxSpeech.CatalogVoiceId + ".pt"), overwrite: true);
            return WriteBridge("chatterbox-config", DysonChatterboxSpeech.BridgeResourceName, DysonSpeechPaths.GetChatterboxBridge(Tree));
        }

        private static VoidResult<string> WriteBridge(string stepId, string resourceName, string path)
        {
            using var bridge = typeof(DysonSpeechInstaller).Assembly.GetManifestResourceStream(resourceName);
            if (bridge is null)
                return new VoidResult<string>($"{stepId}: embedded {resourceName} is missing.");
            using (var output = File.Create(path))
                bridge.CopyTo(output);
            return VoidResult<string>.Success;
        }

        /// <summary>Written straight into <c>{root}/licenses</c>: not a swapped directory.</summary>
        private VoidResult<string> WriteLicenses()
        {
            var dir = Path.Combine(_root, "licenses");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "NOTICES.md"), Notices);
            return VoidResult<string>.Success;
        }

        private async Task<VoidResult<string>> CommitAsync()
        {
            StopSidecars();
            var pythonBuilding = _building.Where(id => !DysonGgufSpeechModels.IsGguf(id)).ToList();
            var ggufBuilding = _building.Where(DysonGgufSpeechModels.IsGguf).ToList();
            string[] dirs =
            [
                .. _swapPython ? ["python"] : Array.Empty<string>(),
                .. _swapTorch ? ["torch"] : Array.Empty<string>(),
                .. _swapRocm ? ["torch-rocm"] : Array.Empty<string>(),
                .. _swapCrisp ? ["crispasr"] : Array.Empty<string>(),
                .. _swapCrispVulkan ? ["crispasr-vulkan"] : Array.Empty<string>(),
                .. _swapOpenVino ? ["openvino"] : Array.Empty<string>(),
                .. _building.Where(SwapBuiltDir).Select(ModelDir),
            ];
            var swap = Swap(dirs);
            if (swap.IsError)
            {
                RestoreOld();
                return swap;
            }

            if (_swapRocm)
            {
                var bridges = RewriteRocmBridges();
                if (bridges.IsError)
                {
                    RestoreOld();
                    return bridges;
                }
            }

            _committed = true;
            if (_swapTorch || pythonBuilding.Count > 0)
            {
                var previous = ReadStamp(_root);
                var stamp = new DysonSpeechInstallStamp(
                    _manifest.SchemaVersion, _manifest.PythonVersion, _manifest.TorchRequirement,
                    previous?.PhononPackage, previous?.PocketPackage, SupportedRid, DateTime.UtcNow,
                    previous?.QwenPackage, previous?.ChatterboxPackage);
                foreach (var id in pythonBuilding)
                    stamp = WithPackage(stamp, id, PackageOf(_manifest, id));
                var write = WriteStamp(_root, stamp);
                if (write.IsError)
                {
                    RestoreOld();
                    return new VoidResult<string>("commit: " + write.Error);
                }
            }

            if (ggufBuilding.Count > 0 || _swapCrisp || _swapCrispVulkan)
            {
                var gguf = ReadGgufStamp(_root) ?? new Dictionary<string, string>(StringComparer.Ordinal);
                if (_swapCrisp && _m.CrispasrRuntime is not null)
                    gguf[CrispStampKey] = _m.CrispasrRuntime.Sha256;
                if (_swapCrispVulkan && _m.CrispasrVulkan is not null)
                    gguf[CrispVulkanStampKey] = _m.CrispasrVulkan.Sha256;
                if (_swapOpenVino)
                {
                    var wheel = OpenVinoWheelSha(_m);
                    if (wheel is null)
                    {
                        RestoreOld();
                        return new VoidResult<string>("commit: openvino wheels have no cp312 pin.");
                    }

                    gguf[OpenVinoStampKey] = wheel;
                }

                foreach (var id in ggufBuilding)
                {
                    var pin = GgufPin(_m, id);
                    if (pin is null)
                    {
                        RestoreOld();
                        return new VoidResult<string>($"commit: {id} has no GGUF pin.");
                    }

                    gguf[id] = pin;
                    if (!DysonGgufSpeechModels.IsWhisper(id))
                        continue;
                    var encoder = OpenVinoEncoderSha(_m, id);
                    if (encoder is null)
                    {
                        RestoreOld();
                        return new VoidResult<string>($"commit: {id} has no OpenVINO encoder pin.");
                    }

                    gguf[id + "-openvino"] = encoder;
                }

                var write = WriteGgufStamp(_root, gguf);
                if (write.IsError)
                {
                    RestoreOld();
                    return new VoidResult<string>("commit: " + write.Error);
                }
            }

            var sync = await SyncDbAsync().ConfigureAwait(false);
            return sync.IsError ? new VoidResult<string>("commit: " + sync.Error) : VoidResult<string>.Success;
        }

        /// <summary>Adopted weights stay in the live dir. A Nemotron half already on disk is not swapped away.</summary>
        private bool SwapBuiltDir(string id)
        {
            if (_adopt.SkipWeightIds.Contains(id))
                return false;
            return id != DysonGgufSpeechModels.NemotronId
                || (!_adopt.NemotronWeightsOnDisk && !_adopt.NemotronDllOnDisk);
        }

        private IReadOnlyList<DysonSpeechFile> TorchPlanFiles() =>
            _adopt.PipTorchDeps
                ? _m.Torch.Where(f => !DysonSpeechManifest.IsTorchWheel(f)).ToList()
                : _m.Torch;

        private IReadOnlyList<DysonSpeechFile> RocmPlanFiles() => _m.RocmTorch ?? [];

        private string SmokeTorchDir() =>
            _useRocmTorch ? DysonSpeechPaths.GetRocmTorchDir(_root) : DysonSpeechPaths.GetTorchDir(_root);

        /// <summary>Existing Qwen and Chatterbox bridges learn the ROCm device flag. Pocket stays on CPU inside the bridge.</summary>
        private VoidResult<string> RewriteRocmBridges()
        {
            if (Directory.Exists(Path.GetDirectoryName(DysonSpeechPaths.GetQwenBridge(_root))))
            {
                var qwen = WriteBridge("commit", DysonQwenSpeech.BridgeResourceName, DysonSpeechPaths.GetQwenBridge(_root));
                if (qwen.IsError)
                    return qwen;
            }

            var chatterboxDir = Path.GetDirectoryName(DysonSpeechPaths.GetChatterboxBridge(_root));
            if (chatterboxDir is not null && Directory.Exists(chatterboxDir))
            {
                var chatterbox = WriteBridge("commit", DysonChatterboxSpeech.BridgeResourceName, DysonSpeechPaths.GetChatterboxBridge(_root));
                if (chatterbox.IsError)
                    return chatterbox;
            }

            return VoidResult<string>.Success;
        }

        private string TorchInstallDir() =>
            _adopt.PipTorchDeps ? DysonSpeechPaths.GetTorchDir(_root) : DysonSpeechPaths.GetTorchDir(Tree);

        /// <summary>Pip of a matched weight set goes into the live tree. A full download still builds under staging.</summary>
        private string InstallTree(string runtimeId) =>
            _adopt.PipSiteIds.Contains(runtimeId) ? _root : Tree;

        /// <summary>
        /// Marks the requested models installed, seeds catalog voices (<c>alba</c>, <c>turbo</c>; insert-if-missing),
        /// sets <c>speech_output_model_id</c> when missing to the first installed of <see cref="ActiveDefaultOrder"/>,
        /// sets <c>speech_input_model_id</c> when missing and exactly one input model is installed, and writes
        /// <c>speech_input_language</c> <c>en-US</c> when missing and Nemotron is installed.
        /// </summary>
        private async Task<VoidResult<string>> SyncDbAsync()
        {
            var variant = Path.GetFileName(_root);
            var ensure = await models.EnsureBuiltInAsync(ct).ConfigureAwait(false);
            if (ensure.IsError)
                return ensure;

            foreach (var id in requested)
            {
                var mark = await models.MarkInstalledAsync(id, variant, ct).ConfigureAwait(false);
                if (mark.IsError)
                    return mark;
            }

            if (requested.Contains(DysonPocketSpeech.Id))
            {
                var alba = await models.EnsureCatalogVoiceAsync(DysonPocketSpeech.Id, CatalogVoiceId, "Alba", variant, ct).ConfigureAwait(false);
                if (alba.IsError)
                    return alba;
            }

            if (requested.Contains(DysonChatterboxSpeech.Id))
            {
                var turbo = await models
                    .EnsureCatalogVoiceAsync(DysonChatterboxSpeech.Id, DysonChatterboxSpeech.CatalogVoiceId, "Turbo default", variant, ct)
                    .ConfigureAwait(false);
                if (turbo.IsError)
                    return turbo;
            }

            var active = await settings.GetSettingAsync(DysonAppSettingKeys.SpeechOutputModelId, ct).ConfigureAwait(false);
            if (active.IsError)
                return new VoidResult<string>(active.Error);
            if (string.IsNullOrWhiteSpace(active.Value))
            {
                var first = ActiveDefaultOrder.FirstOrDefault(id => requested.Contains(id) || _before.IsInstalled(id));
                if (first is not null)
                {
                    var set = await settings.SetSettingAsync(DysonAppSettingKeys.SpeechOutputModelId, first, ct).ConfigureAwait(false);
                    if (set.IsError)
                        return set;
                }
            }

            var input = await settings.GetSettingAsync(DysonAppSettingKeys.SpeechInputModelId, ct).ConfigureAwait(false);
            if (input.IsError)
                return new VoidResult<string>(input.Error);
            if (string.IsNullOrWhiteSpace(input.Value))
            {
                var installed = InputModelIds.Where(id => requested.Contains(id) || _before.IsInstalled(id)).ToList();
                if (installed.Count == 1)
                {
                    var set = await settings.SetSettingAsync(DysonAppSettingKeys.SpeechInputModelId, installed[0], ct).ConfigureAwait(false);
                    if (set.IsError)
                        return set;
                }
            }

            if (requested.Contains(DysonGgufSpeechModels.NemotronId) || _before.IsInstalled(DysonGgufSpeechModels.NemotronId))
            {
                var language = await settings.GetSettingAsync(DysonAppSettingKeys.SpeechInputLanguage, ct).ConfigureAwait(false);
                if (language.IsError)
                    return new VoidResult<string>(language.Error);
                if (string.IsNullOrWhiteSpace(language.Value))
                    return await settings.SetSettingAsync(DysonAppSettingKeys.SpeechInputLanguage, "en-US", ct).ConfigureAwait(false);
            }

            if (requested.Any(DysonGgufSpeechModels.IsWhisper) || _before.IsInstalled(DysonGgufSpeechModels.WhisperTinyId) || _before.IsInstalled(DysonGgufSpeechModels.WhisperBaseId))
            {
                var whisper = await settings.GetSettingAsync(DysonAppSettingKeys.SpeechWhisperRuntime, ct).ConfigureAwait(false);
                if (whisper.IsError)
                    return new VoidResult<string>(whisper.Error);
                if (string.IsNullOrWhiteSpace(whisper.Value))
                {
                    var set = await settings.SetSettingAsync(
                        DysonAppSettingKeys.SpeechWhisperRuntime, DysonAppSettingKeys.SpeechWhisperRuntimeCpu, ct).ConfigureAwait(false);
                    if (set.IsError)
                        return set;
                }
            }

            return VoidResult<string>.Success;
        }

        /// <summary>
        /// Health / import checks for the models built this run only: Phonon <c>/health</c>, <c>import qwen_tts</c>,
        /// <c>from chatterbox.tts_turbo import ChatterboxTurboTTS</c> (proves the shared torch ABI without loading
        /// weights). Any failure restores the swapped directories.
        /// </summary>
        private async Task<VoidResult<string>> SmokeAsync()
        {
            var check = VoidResult<string>.Success;
            if (_building.Contains(DysonPhononSpeech.Id))
            {
                var health = await DysonPhononSpeech.CheckHealthAsync(_root, ct).ConfigureAwait(false);
                DysonPhononSpeech.StopSidecar();
                check = health;
            }

            if (check.IsSuccess && _swapRocm)
            {
                check = await RunPythonAsync("smoke",
                    [DysonSpeechPaths.GetRocmTorchDir(_root)],
                    ["-c", "import torch"]).ConfigureAwait(false);
            }

            if (check.IsSuccess && _building.Contains(DysonQwenSpeech.Id))
            {
                check = await RunPythonAsync("smoke",
                    [DysonSpeechPaths.GetQwenSite(_root), SmokeTorchDir()],
                    ["-c", "import qwen_tts"]).ConfigureAwait(false);
            }

            if (check.IsSuccess && _building.Contains(DysonChatterboxSpeech.Id))
            {
                check = await RunPythonAsync("smoke",
                    [DysonSpeechPaths.GetChatterboxSite(_root), SmokeTorchDir()],
                    ["-c", "from chatterbox.tts_turbo import ChatterboxTurboTTS"]).ConfigureAwait(false);
            }

            if (check.IsSuccess && _building.Contains(DysonGgufSpeechModels.NemotronId))
                check = await DysonNemotronSpeech.CheckRuntimeAsync(_root, ct).ConfigureAwait(false);

            if (check.IsSuccess)
            {
                foreach (var id in _building)
                {
                    if (!DysonGgufSpeechModels.IsCrisp(id))
                        continue;
                    check = await DysonCrispSpeech.CheckRuntimeAsync(_root, DysonGgufSpeechModels.CrispBackend(id)!, ct)
                        .ConfigureAwait(false);
                    if (check.IsError)
                        break;
                }
            }

            if (check.IsError)
            {
                RestoreOld();
                foreach (var id in _building)
                    await models.MarkUninstalledAsync(id, CancellationToken.None).ConfigureAwait(false);
                var error = check.Error.StartsWith("smoke: ", StringComparison.Ordinal) ? check.Error : "smoke: " + check.Error;
                return new VoidResult<string>(error + " The previous state was restored.");
            }

            DeleteTree(Staging);
            return VoidResult<string>.Success;
        }

        /// <summary>Moves only the directories built this run into place; installed models and their voices never move.</summary>
        private VoidResult<string> Swap(IReadOnlyList<string> dirs)
        {
            try
            {
                var clean = DeleteTree(Old);
                if (clean.IsError)
                    return new VoidResult<string>("commit: " + clean.Error);
                Directory.CreateDirectory(Old);

                var json = DysonSpeechPaths.GetInstallJson(_root);
                if (File.Exists(json))
                    File.Copy(json, Path.Combine(Old, "install.json"));
                var gguf = GgufStampPath(_root);
                if (File.Exists(gguf))
                    File.Copy(gguf, Path.Combine(Old, GgufStampFileName));

                foreach (var name in dirs)
                {
                    _swapped.Add(name);
                    if (Directory.Exists(Path.Combine(_root, name)))
                        Directory.Move(Path.Combine(_root, name), Path.Combine(Old, name));
                    Directory.Move(Path.Combine(Tree, name), Path.Combine(_root, name));
                }

                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new VoidResult<string>($"commit: could not swap the new tree into {_root}: {ex.Message}");
            }
        }

        /// <summary>Puts back each swapped directory; one with no previous copy is deleted. Restores both stamp files.</summary>
        private void RestoreOld()
        {
            try
            {
                StopSidecars();
                foreach (var name in _swapped)
                {
                    DeleteTree(Path.Combine(_root, name));
                    if (Directory.Exists(Path.Combine(Old, name)))
                        Directory.Move(Path.Combine(Old, name), Path.Combine(_root, name));
                }

                RestoreStamp(DysonSpeechPaths.GetInstallJson(_root), "install.json");
                RestoreStamp(GgufStampPath(_root), GgufStampFileName);

                void RestoreStamp(string dest, string name)
                {
                    var backup = Path.Combine(Old, name);
                    if (File.Exists(backup))
                        File.Copy(backup, dest, overwrite: true);
                    else if (File.Exists(dest))
                        File.Delete(dest);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // ponytail: a failed rollback leaves a tree whose install.json is missing or stale, so Probe
                // reports the model not installed and its checkbox installs it again.
            }
        }

        private async Task<VoidResult<string>> RunPythonAsync(string stepId, IReadOnlyList<string> pythonPath, IReadOnlyList<string> args)
        {
            var psi = PythonStartInfo(PythonRoot, pythonPath, args);
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
        - qwen-tts 0.1.1 code (Alibaba Qwen): Apache-2.0.
        - Qwen3-TTS-12Hz-0.6B-Base weights (Alibaba Qwen): Apache-2.0.
        - chatterbox-tts 0.1.7 code (Resemble AI): MIT.
        - Chatterbox Turbo weights (ResembleAI/chatterbox-turbo, Resemble AI): MIT. English only. Output carries Resemble's Perth implicit watermark.
        - resemble-perth: MIT. s3tokenizer: Apache-2.0. diffusers: Apache-2.0. conformer: MIT. omegaconf: BSD-3-Clause. pyloudnorm: MIT. onnx: Apache-2.0.
        - torchaudio: BSD-2-Clause. transformers and accelerate: Apache-2.0.
        - librosa: ISC. numba and llvmlite: BSD-2-Clause. onnxruntime: MIT. sox (Python package): BSD-3-Clause.
        - libsndfile inside the upstream soundfile wheel: LGPL-2.1.
        - Other wheels in phonon/, pocket/, qwen/ and chatterbox/ wheelhouse folders keep the license files inside each wheel's dist-info.
        - ggml Whisper tiny and base (MIT, ggerganov/whisper.cpp conversion of OpenAI Whisper). Downloaded at install, not redistributed.
        - OpenVINO Whisper tiny and base FP16 IR (Apache-2.0) and the OpenVINO runtime wheels. Downloaded at install, not redistributed.
        """;
}
