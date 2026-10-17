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
    DysonSpeechFile PocketVoice);

/// <summary>
/// <c>packaging/speech/manifest.json</c>, embedded in Harness.Engine. Git holds URLs and hashes, never the bytes.
/// </summary>
public sealed partial record DysonSpeechManifest(
    int SchemaVersion,
    string PythonVersion,
    string TorchRequirement,
    string PhononPackage,
    string PocketPackage,
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
    /// Parses and validates. Rejects any Pocket URL into the gated <c>kyutai/pocket-tts/</c> repo:
    /// only <c>kyutai/pocket-tts-without-voice-cloning</c> is allowed.
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

        foreach (var (rid, m) in manifest.Rids)
        {
            if (m?.Python is null || m.Torch is not { Count: > 0 } || m.PhononWheels is null || m.PocketWheels is null
                || m.PhononWeights is null || m.PocketWeights is null || m.PocketTokenizer is null || m.PocketVoice is null)
            {
                return Result<DysonSpeechManifest, string>.AsError($"Speech manifest RID '{rid}' is incomplete.");
            }

            IEnumerable<DysonSpeechFile> all =
            [
                m.Python, .. m.Torch, .. m.PhononWheels, .. m.PocketWheels,
                m.PhononWeights, m.PocketWeights, m.PocketTokenizer, m.PocketVoice,
            ];
            foreach (var file in all)
            {
                if (file is null
                    || string.IsNullOrWhiteSpace(file.FileName)
                    || file.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || !Uri.TryCreate(file.Url, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps
                    || file.Size <= 0
                    || !Sha256Pattern().IsMatch(file.Sha256 ?? ""))
                {
                    return Result<DysonSpeechManifest, string>.AsError(
                        $"Speech manifest RID '{rid}' has an invalid file entry: {file?.FileName ?? "(null)"}.");
                }

                if (IsGatedPocketUrl(file.Url))
                {
                    return Result<DysonSpeechManifest, string>.AsError(
                        $"Speech manifest points at the gated kyutai/pocket-tts repo ({file.Url}). "
                        + "Only kyutai/pocket-tts-without-voice-cloning is allowed.");
                }
            }

            if (m.PhononWheels.Concat(m.PocketWheels).Any(IsTorchWheel))
                return Result<DysonSpeechManifest, string>.AsError($"Speech manifest RID '{rid}' lists torch in a model site.");
        }

        return Result<DysonSpeechManifest, string>.AsValue(manifest);
    }

    internal static bool IsGatedPocketUrl(string url) =>
        url.Contains("kyutai/pocket-tts/", StringComparison.OrdinalIgnoreCase)
        && !url.Contains("pocket-tts-without-voice-cloning", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTorchWheel(DysonSpeechFile file) =>
        file.FileName.StartsWith("torch-", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();
}
