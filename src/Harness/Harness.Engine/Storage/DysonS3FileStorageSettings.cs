using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>
/// Subject setting blob for S3-compatible file storage
/// (<c>{"endpointUrl","accessKeyId","secretAccessKey"}</c>). Never log <see cref="SecretAccessKey"/>.
/// </summary>
public sealed class DysonS3FileStorageSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public string EndpointUrl { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";

    /// <summary>
    /// Parses JSON. Null/whitespace or incomplete fields (empty/whitespace URL, key, or secret)
    /// are a parse failure so callers can treat the setting as missing.
    /// </summary>
    public static Result<DysonS3FileStorageSettings, string> TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result<DysonS3FileStorageSettings, string>.AsError("File storage settings are missing.");

        try
        {
            var settings = JsonSerializer.Deserialize<DysonS3FileStorageSettings>(json, JsonOptions);
            if (settings is null || !IsComplete(settings))
                return Result<DysonS3FileStorageSettings, string>.AsError("File storage settings are incomplete.");

            return Result<DysonS3FileStorageSettings, string>.AsValue(Normalize(settings));
        }
        catch (JsonException)
        {
            return Result<DysonS3FileStorageSettings, string>.AsError("File storage settings are invalid JSON.");
        }
    }

    /// <summary>
    /// Serializes complete settings. Returns null for null/incomplete/whitespace fields so
    /// <c>SetSettingAsync</c> can delete the row.
    /// </summary>
    public static string? Serialize(DysonS3FileStorageSettings? settings)
    {
        if (settings is null || !IsComplete(settings))
            return null;

        return JsonSerializer.Serialize(Normalize(settings), JsonOptions);
    }

    private static bool IsComplete(DysonS3FileStorageSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.EndpointUrl)
        && !string.IsNullOrWhiteSpace(settings.AccessKeyId)
        && !string.IsNullOrWhiteSpace(settings.SecretAccessKey);

    private static DysonS3FileStorageSettings Normalize(DysonS3FileStorageSettings settings) =>
        new()
        {
            EndpointUrl = settings.EndpointUrl.Trim(),
            AccessKeyId = settings.AccessKeyId.Trim(),
            SecretAccessKey = settings.SecretAccessKey.Trim(),
        };
}
