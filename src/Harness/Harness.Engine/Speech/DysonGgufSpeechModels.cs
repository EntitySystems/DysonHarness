namespace DysonHarness;

/// <summary>
/// GGUF speech installs (plan 117), Windows win-x64 only, no Python. Nemotron 3.5 streaming STT runs in-process on
/// transcribe.cpp v0.4.0 (<c>transcribe.dll</c>, P/Invoke). Phonon 2, Pocket TTS, Qwen3-TTS 0.6B Base and Chatterbox
/// Turbo GGUF run on one CrispASR v0.8.43 <c>crispasr.exe --server</c> loopback sidecar (static exe: its ggml never
/// shares a process with transcribe.dll's ggml.dll). Each GGUF install is its own model row beside the
/// safetensors / Python row of the same model; the user can install either or both.
/// </summary>
public static class DysonGgufSpeechModels
{
    public const string NemotronId = "nemotron-3.5-asr-streaming-0.6b";
    public const string PhononGgufId = "phonon-2-gguf";
    public const string PhononGgufQ8Id = "phonon-2-gguf-q8";
    public const string PocketGgufId = "pocket-tts-gguf";
    public const string PocketGgufQ8Id = "pocket-tts-gguf-q8";
    public const string QwenGgufId = "qwen3-tts-0.6b-base-gguf";
    public const string ChatterboxGgufId = "chatterbox-turbo-gguf";
    public const string ChatterboxGgufQ8Id = "chatterbox-turbo-gguf-q8";

    public const string NemotronDisplayName = "Nemotron 3.5 ASR 0.6B · GGUF Q5_K_M, streaming (560 MB)";
    public const string PhononGgufDisplayName = "Phonon 2 · GGUF Q4_K (402 MB)";
    public const string PhononGgufQ8DisplayName = "Phonon 2 · GGUF Q8_0 (674 MB)";
    public const string PocketGgufDisplayName = "Pocket TTS · GGUF Q4_K, voice cloning (73 MB)";
    public const string PocketGgufQ8DisplayName = "Pocket TTS · GGUF Q8_0, voice cloning (124 MB)";
    public const string QwenGgufDisplayName = "Qwen3-TTS 0.6B Base · GGUF Q8_0 + F16 codec (1.34 GB)";
    public const string ChatterboxGgufDisplayName = "Chatterbox Turbo (English only) · GGUF Q4_K (735 MB)";
    public const string ChatterboxGgufQ8DisplayName = "Chatterbox Turbo (English only) · GGUF Q8_0 (1.03 GB)";

    /// <summary>Models served by the shared CrispASR runtime (<see cref="GetCrispExe"/>). Q8 ids are siblings of the Q4 ids, not replacements.</summary>
    public static readonly IReadOnlyList<string> CrispIds =
    [
        PhononGgufId, PhononGgufQ8Id, PocketGgufId, PocketGgufQ8Id, QwenGgufId, ChatterboxGgufId, ChatterboxGgufQ8Id,
    ];

    /// <summary>Every GGUF id, in install / UI order.</summary>
    public static readonly IReadOnlyList<string> AllIds = [NemotronId, .. CrispIds];

    public const string NemotronModelFile = "nemotron-3.5-asr-streaming-0.6b-Q5_K_M.gguf";
    public const string PhononGgufFile = "phonon2-q4_k.gguf";
    public const string PhononGgufQ8File = "phonon2-q8_0.gguf";
    public const string PocketGgufFile = "pocket-tts-english-q4_k.gguf";
    public const string PocketGgufQ8File = "pocket-tts-english-q8_0.gguf";
    public const string QwenGgufFile = "qwen3-tts-12hz-0.6b-base-q8_0.gguf";
    public const string QwenTokenizerFile = "qwen3-tts-tokenizer-12hz.gguf";
    public const string ChatterboxT3File = "chatterbox-turbo-t3-q4_k.gguf";
    public const string ChatterboxS3GenFile = "chatterbox-turbo-s3gen-q4_k.gguf";
    public const string ChatterboxT3Q8File = "chatterbox-turbo-t3-q8_0.gguf";
    public const string ChatterboxS3GenQ8File = "chatterbox-turbo-s3gen-q8_0.gguf";

    public static bool IsGguf(string? runtimeId) => runtimeId is not null && AllIds.Contains(runtimeId);

    public static bool IsCrisp(string? runtimeId) => runtimeId is not null && CrispIds.Contains(runtimeId);

    /// <summary>CrispASR <c>--backend</c> name; null for a non-CrispASR id.</summary>
    public static string? CrispBackend(string runtimeId) => runtimeId switch
    {
        PhononGgufId or PhononGgufQ8Id => "phonon2",
        PocketGgufId or PocketGgufQ8Id => "pocket-tts",
        QwenGgufId => "qwen3-tts",
        ChatterboxGgufId or ChatterboxGgufQ8Id => "chatterbox-turbo",
        _ => null,
    };

    /// <summary>Folder under the install root; null for a non-GGUF id.</summary>
    public static string? Dir(string runtimeId) => runtimeId switch
    {
        NemotronId => "nemotron",
        PhononGgufId => "phonon-gguf",
        PhononGgufQ8Id => "phonon-gguf-q8",
        PocketGgufId => "pocket-gguf",
        PocketGgufQ8Id => "pocket-gguf-q8",
        QwenGgufId => "qwen-gguf",
        ChatterboxGgufId => "chatterbox-gguf",
        ChatterboxGgufQ8Id => "chatterbox-gguf-q8",
        _ => null,
    };

    /// <summary>Shared CrispASR runtime folder (<c>crispasr.exe</c>, <c>openblas.dll</c>, licenses).</summary>
    public static string GetCrispRuntimeDir(string installRoot) => Path.Combine(installRoot, "crispasr");

    public static string GetCrispExe(string installRoot) => Path.Combine(GetCrispRuntimeDir(installRoot), "crispasr.exe");

    /// <summary>CrispASR Vulkan build (<c>crispasr-windows-x86_64-vulkan.zip</c>). It does not fall back to CPU.</summary>
    public static string GetCrispVulkanRuntimeDir(string installRoot) => Path.Combine(installRoot, "crispasr-vulkan");

    public static string GetCrispVulkanExe(string installRoot) =>
        Path.Combine(GetCrispVulkanRuntimeDir(installRoot), "crispasr.exe");

    /// <summary>transcribe.cpp native folder (<c>transcribe.dll</c> + ggml modules), inside the Nemotron folder.</summary>
    public static string GetNemotronRuntimeDir(string installRoot) => Path.Combine(installRoot, "nemotron", "runtime");

    public static string GetTranscribeDll(string installRoot) => Path.Combine(GetNemotronRuntimeDir(installRoot), "transcribe.dll");

    public static string GetModelDir(string installRoot, string runtimeId) =>
        Path.Combine(installRoot, Dir(runtimeId) ?? runtimeId, "model");

    /// <summary>Enrolled reference clips (<c>{voiceId}.wav</c>; Qwen3-TTS is 24 kHz mono plus <c>{voiceId}.txt</c>).</summary>
    public static string GetVoicesDir(string installRoot, string runtimeId) =>
        Path.Combine(installRoot, Dir(runtimeId) ?? runtimeId, "voices");

    /// <summary>The model GGUF passed as <c>-m</c> (Nemotron: the file <c>transcribe_model_load_file</c> opens).</summary>
    public static string GetMainModelFile(string installRoot, string runtimeId) => Path.Combine(
        GetModelDir(installRoot, runtimeId),
        runtimeId switch
        {
            NemotronId => NemotronModelFile,
            PhononGgufId => PhononGgufFile,
            PhononGgufQ8Id => PhononGgufQ8File,
            PocketGgufId => PocketGgufFile,
            PocketGgufQ8Id => PocketGgufQ8File,
            QwenGgufId => QwenGgufFile,
            ChatterboxGgufQ8Id => ChatterboxT3Q8File,
            _ => ChatterboxT3File,
        });

    /// <summary><c>--codec-model</c> companion: Qwen3-TTS tokenizer or Chatterbox S3Gen; null when the model has none.</summary>
    public static string? GetCompanionFile(string installRoot, string runtimeId) => runtimeId switch
    {
        QwenGgufId => Path.Combine(GetModelDir(installRoot, runtimeId), QwenTokenizerFile),
        ChatterboxGgufId => Path.Combine(GetModelDir(installRoot, runtimeId), ChatterboxS3GenFile),
        ChatterboxGgufQ8Id => Path.Combine(GetModelDir(installRoot, runtimeId), ChatterboxS3GenQ8File),
        _ => null,
    };
}
