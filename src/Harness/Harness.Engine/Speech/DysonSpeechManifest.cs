using System.Text.Json;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>One pinned download: URL, byte size, lowercase sha256.</summary>
public sealed record DysonSpeechFile(string FileName, string Url, long Size, string Sha256);

/// <summary>Everything the installer downloads for one RID.</summary>
public sealed record DysonSpeechRidManifest(
    DysonSpeechFile Python,
    IReadOnlyList<DysonSpeechFile> Torch,
    IReadOnlyList<DysonSpeechFile> PhononWheels,
    IReadOnlyList<DysonSpeechFile> PocketWheels,
    DysonSpeechFile PhononWeights,
    DysonSpeechFile PocketWeights,
    DysonSpeechFile PocketTokenizer,
    DysonSpeechFile PocketVoice,
    IReadOnlyList<DysonSpeechFile> QwenWheels,
    IReadOnlyList<DysonSpeechFile> QwenModel,
    IReadOnlyList<DysonSpeechFile> QwenSpeechTokenizer,
    IReadOnlyList<DysonSpeechFile> ChatterboxWheels,
    IReadOnlyList<DysonSpeechFile> ChatterboxModel,
    DysonSpeechFile? NemotronNative = null,
    DysonSpeechFile? NemotronWeights = null,
    DysonSpeechFile? CrispasrRuntime = null,
    DysonSpeechFile? PhononGguf = null,
    DysonSpeechFile? PocketGguf = null,
    IReadOnlyList<DysonSpeechFile>? QwenGguf = null,
    IReadOnlyList<DysonSpeechFile>? ChatterboxGguf = null,
    DysonSpeechFile? CrispasrVulkan = null);

/// <summary>
/// <c>packaging/speech/manifest.json</c>, embedded in Harness.Engine. Git holds URLs and hashes, never the bytes.
/// </summary>
public sealed partial record DysonSpeechManifest(
    int SchemaVersion,
    string PythonVersion,
    string TorchRequirement,
    string PhononPackage,
    string PocketPackage,
    string QwenPackage,
    string ChatterboxPackage,
    IReadOnlyDictionary<string, DysonSpeechRidManifest> Rids)
{
    public const string ResourceName = "DysonHarness.Speech.manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static Result<DysonSpeechManifest, string> LoadEmbedded()
    {
        using var stream = typeof(DysonSpeechManifest).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return Result<DysonSpeechManifest, string>.AsError($"Embedded speech manifest '{ResourceName}' is missing.");

        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Parses and validates. Rejects any Pocket URL into the gated <c>kyutai/pocket-tts/</c> repo (only
    /// <c>kyutai/pocket-tts-without-voice-cloning</c> is allowed), torch or gradio in a model site, and any
    /// <c>chatterboxModel</c> file outside <c>ResembleAI/chatterbox-turbo/</c> (the multilingual repo is not used).
    /// GGUF entries are optional; <c>chatterboxGguf</c> is not the Resemble repo and is not checked against it.
    /// </summary>
    public static Result<DysonSpeechManifest, string> Parse(string json)
    {
        DysonSpeechManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DysonSpeechManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Result<DysonSpeechManifest, string>.AsError($"Speech manifest is not valid JSON: {ex.Message}");
        }

        if (manifest?.Rids is null || manifest.Rids.Count == 0)
            return Result<DysonSpeechManifest, string>.AsError("Speech manifest has no RIDs.");
        if (string.IsNullOrWhiteSpace(manifest.QwenPackage) || string.IsNullOrWhiteSpace(manifest.ChatterboxPackage))
            return Result<DysonSpeechManifest, string>.AsError("Speech manifest has no qwenPackage or chatterboxPackage.");

        foreach (var (rid, m) in manifest.Rids)
        {
            if (m?.Python is null || m.Torch is not { Count: > 0 } || m.PhononWheels is null || m.PocketWheels is null
                || m.PhononWeights is null || m.PocketWeights is null || m.PocketTokenizer is null || m.PocketVoice is null
                || m.QwenWheels is not { Count: > 0 } || m.QwenModel is not { Count: > 0 } || m.QwenSpeechTokenizer is not { Count: > 0 }
                || m.ChatterboxWheels is not { Count: > 0 } || m.ChatterboxModel is not { Count: > 0 })
            {
                return Result<DysonSpeechManifest, string>.AsError($"Speech manifest RID '{rid}' is incomplete.");
            }

            IEnumerable<DysonSpeechFile> all =
            [
                m.Python, .. m.Torch, .. m.PhononWheels, .. m.PocketWheels,
                m.PhononWeights, m.PocketWeights, m.PocketTokenizer, m.PocketVoice,
                .. m.QwenWheels, .. m.QwenModel, .. m.QwenSpeechTokenizer, .. m.ChatterboxWheels, .. m.ChatterboxModel,
            ];
            foreach (var file in all)
            {
                if (FileEntryError(rid, file) is { } error)
                    return Result<DysonSpeechManifest, string>.AsError(error);
            }

            foreach (var file in OptionalFiles(m))
            {
                if (file is null)
                    continue;
                if (FileEntryError(rid, file) is { } error)
                    return Result<DysonSpeechManifest, string>.AsError(error);
            }

            IEnumerable<DysonSpeechFile> sites = [.. m.PhononWheels, .. m.PocketWheels, .. m.QwenWheels, .. m.ChatterboxWheels];
            if (sites.Any(IsTorchWheel))
                return Result<DysonSpeechManifest, string>.AsError($"Speech manifest RID '{rid}' lists torch in a model site.");
            if (sites.Any(w => w.FileName.StartsWith("gradio", StringComparison.OrdinalIgnoreCase)))
                return Result<DysonSpeechManifest, string>.AsError($"Speech manifest RID '{rid}' lists gradio in a model site.");
            if (m.ChatterboxModel.FirstOrDefault(f => !f.Url.Contains(ChatterboxTurboRepo, StringComparison.OrdinalIgnoreCase)) is { } other)
            {
                return Result<DysonSpeechManifest, string>.AsError(
                    $"Speech manifest chatterboxModel points outside {ChatterboxTurboRepo} ({other.Url}). Only Chatterbox Turbo (English only) is used.");
            }
        }

        return Result<DysonSpeechManifest, string>.AsValue(manifest);
    }

    internal const string ChatterboxTurboRepo = "huggingface.co/ResembleAI/chatterbox-turbo/";

    internal static bool IsGatedPocketUrl(string url) =>
        url.Contains("kyutai/pocket-tts/", StringComparison.OrdinalIgnoreCase)
        && !url.Contains("pocket-tts-without-voice-cloning", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTorchWheel(DysonSpeechFile file) =>
        file.FileName.StartsWith("torch-", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<DysonSpeechFile?> OptionalFiles(DysonSpeechRidManifest m)
    {
        yield return m.NemotronNative;
        yield return m.NemotronWeights;
        yield return m.CrispasrRuntime;
        yield return m.CrispasrVulkan;
        yield return m.PhononGguf;
        yield return m.PocketGguf;
        if (m.QwenGguf is not null)
        {
            foreach (var file in m.QwenGguf)
                yield return file;
        }

        if (m.ChatterboxGguf is not null)
        {
            foreach (var file in m.ChatterboxGguf)
                yield return file;
        }
    }

    private static string? FileEntryError(string rid, DysonSpeechFile? file)
    {
        if (file is null
            || string.IsNullOrWhiteSpace(file.FileName)
            || file.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !Uri.TryCreate(file.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || file.Size <= 0
            || !Sha256Pattern().IsMatch(file.Sha256 ?? ""))
        {
            return $"Speech manifest RID '{rid}' has an invalid file entry: {file?.FileName ?? "(null)"}.";
        }

        if (IsGatedPocketUrl(file.Url))
        {
            return $"Speech manifest points at the gated kyutai/pocket-tts repo ({file.Url}). "
                + "Only kyutai/pocket-tts-without-voice-cloning is allowed.";
        }

        return null;
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();
}
