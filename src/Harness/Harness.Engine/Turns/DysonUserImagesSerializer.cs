using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>JSON serialize/restore helpers for <see cref="DysonAgentTurn.UserImages"/>.</summary>
public static class DysonUserImagesSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string? Serialize(IReadOnlyList<DysonBinaryAttachment> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
            return null;

        // ponytail: FileId is provider-ephemeral; always re-upload on Responses rebuild.
        var stored = new List<StoredUserImage>(images.Count);
        foreach (var image in images)
        {
            stored.Add(new StoredUserImage
            {
                FileName = image.FileName,
                Extension = image.Extension,
                MimeType = image.MimeType,
                Base64Data = image.Base64Data,
                HtmlRef = string.IsNullOrWhiteSpace(image.HtmlRef) ? null : image.HtmlRef.Trim(),
                RemoteUrl = string.IsNullOrWhiteSpace(image.RemoteUrl) ? null : image.RemoteUrl.Trim(),
                ObjectKey = string.IsNullOrWhiteSpace(image.ObjectKey) ? null : image.ObjectKey.Trim(),
                RemoteUrlExpiresUtc = image.RemoteUrlExpiresUtc,
            });
        }

        return JsonSerializer.Serialize(stored, Options);
    }

    public static List<DysonBinaryAttachment> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var stored = JsonSerializer.Deserialize<List<StoredUserImage>>(json, Options);
        if (stored is null || stored.Count == 0)
            return [];

        var list = new List<DysonBinaryAttachment>(stored.Count);
        foreach (var item in stored)
        {
            if (string.IsNullOrWhiteSpace(item.Base64Data)
                || string.IsNullOrWhiteSpace(item.MimeType)
                || string.IsNullOrWhiteSpace(item.FileName))
            {
                continue;
            }

            list.Add(new DysonBinaryAttachment
            {
                FileName = item.FileName.Trim(),
                Extension = item.Extension?.Trim() ?? "",
                MimeType = item.MimeType.Trim(),
                Base64Data = item.Base64Data.Trim(),
                HtmlRef = string.IsNullOrWhiteSpace(item.HtmlRef) ? null : item.HtmlRef.Trim(),
                RemoteUrl = string.IsNullOrWhiteSpace(item.RemoteUrl) ? null : item.RemoteUrl.Trim(),
                ObjectKey = string.IsNullOrWhiteSpace(item.ObjectKey) ? null : item.ObjectKey.Trim(),
                RemoteUrlExpiresUtc = item.RemoteUrlExpiresUtc is { } expires
                    ? DateTime.SpecifyKind(expires, DateTimeKind.Utc)
                    : null,
            });
        }

        return list;
    }

    private sealed class StoredUserImage
    {
        public string FileName { get; set; } = "";
        public string Extension { get; set; } = "";
        public string MimeType { get; set; } = "";
        public string Base64Data { get; set; } = "";
        public string? HtmlRef { get; set; }
        public string? RemoteUrl { get; set; }
        public string? ObjectKey { get; set; }
        public DateTime? RemoteUrlExpiresUtc { get; set; }
    }
}
