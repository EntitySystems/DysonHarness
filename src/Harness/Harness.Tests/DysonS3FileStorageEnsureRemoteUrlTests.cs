using DysonHarness;

namespace Harness.Tests;

/// <summary>ponytail: EnsureRemoteUrlAsync no-ops a valid URL and fails without bytes (no live bucket).</summary>
public class DysonS3FileStorageEnsureRemoteUrlTests
{
    [Fact]
    public async Task EnsureRemoteUrlAsync_noops_when_url_unexpired()
    {
        using var storage = CreateClient();
        const string url = "https://s3.example.com/dyson/shot.jpg?X-Amz-Signature=abc";
        var image = new DysonBinaryAttachment
        {
            FileName = "shot.jpg",
            Extension = ".jpg",
            MimeType = "image/jpeg",
            Base64Data = "not-used-when-url-valid",
            RemoteUrl = url,
            ObjectKey = "dyson/2026/09/abc-shot.jpg",
            RemoteUrlExpiresUtc = DateTime.UtcNow.AddDays(10),
        };

        var result = await storage.EnsureRemoteUrlAsync(image);
        if (result.IsError)
            throw new InvalidOperationException(result.Error);
        if (!ReferenceEquals(result.Value, image)
            || image.RemoteUrl != url
            || image.ObjectKey != "dyson/2026/09/abc-shot.jpg")
        {
            throw new InvalidOperationException("Valid RemoteUrl must be left unchanged (no re-sign).");
        }
    }

    [Fact]
    public async Task EnsureRemoteUrlAsync_fails_without_bytes_when_url_missing_or_expired()
    {
        using var storage = CreateClient();
        var missing = new DysonBinaryAttachment
        {
            FileName = "shot.jpg",
            Extension = ".jpg",
            MimeType = "image/jpeg",
            Base64Data = "",
        };
        var missingResult = await storage.EnsureRemoteUrlAsync(missing);
        if (!missingResult.IsError)
            throw new InvalidOperationException("Empty Base64Data without RemoteUrl must fail without network.");

        var expired = new DysonBinaryAttachment
        {
            FileName = "shot.jpg",
            Extension = ".jpg",
            MimeType = "image/jpeg",
            Base64Data = "",
            RemoteUrl = "https://s3.example.com/dyson/expired.jpg",
            ObjectKey = "dyson/expired.jpg",
            RemoteUrlExpiresUtc = DateTime.UtcNow.AddMinutes(-1),
        };
        var expiredResult = await storage.EnsureRemoteUrlAsync(expired);
        if (!expiredResult.IsError)
            throw new InvalidOperationException("Expired RemoteUrl without local bytes must fail without network.");
    }

    [Fact]
    public void TryCreateFromJson_round_trips_complete_blob()
    {
        var json = DysonS3FileStorageSettings.Serialize(new DysonS3FileStorageSettings
        {
            EndpointUrl = "https://s3.example.com/my-bucket",
            AccessKeyId = "ak",
            SecretAccessKey = "secret",
        });
        var created = DysonS3FileStorage.TryCreateFromJson(json);
        if (created.IsError)
            throw new InvalidOperationException(created.Error);
        using var storage = created.Value;
        if (storage.Endpoint.Bucket != "my-bucket")
            throw new InvalidOperationException("TryCreateFromJson must parse the endpoint.");

        var missing = DysonS3FileStorage.TryCreateFromJson("  ");
        if (!missing.IsError)
            throw new InvalidOperationException("Whitespace JSON must not create a client.");
    }

    private static DysonS3FileStorage CreateClient()
    {
        var created = DysonS3FileStorage.TryCreate(new DysonS3FileStorageSettings
        {
            EndpointUrl = "https://s3.example.com/my-bucket",
            AccessKeyId = "ak",
            SecretAccessKey = "secret",
        });
        if (created.IsError)
            throw new InvalidOperationException(created.Error);
        return created.Value;
    }
}
