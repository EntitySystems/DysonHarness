using DysonHarness;

namespace Harness.Tests;

/// <summary>ponytail: file_storage_s3 JSON round-trip; empty/whitespace is missing.</summary>
public class DysonS3FileStorageSettingsTests
{
    [Fact]
    public void Round_trip_camel_case_json()
    {
        var json = DysonS3FileStorageSettings.Serialize(new DysonS3FileStorageSettings
        {
            EndpointUrl = " https://s3.example.com/my-bucket ",
            AccessKeyId = " AKIAEXAMPLE ",
            SecretAccessKey = " super-secret ",
        });
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Complete settings must serialize.");
        if (!json.Contains("\"endpointUrl\"", StringComparison.Ordinal)
            || !json.Contains("\"accessKeyId\"", StringComparison.Ordinal)
            || !json.Contains("\"secretAccessKey\"", StringComparison.Ordinal)
            || json.Contains("EndpointUrl", StringComparison.Ordinal)
            || json.Contains("AccessKeyId", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("JSON must use camelCase keys: " + json);
        }

        var parsed = DysonS3FileStorageSettings.TryParse(json);
        if (parsed.IsError)
            throw new InvalidOperationException(parsed.Error);
        if (parsed.Value.EndpointUrl != "https://s3.example.com/my-bucket"
            || parsed.Value.AccessKeyId != "AKIAEXAMPLE"
            || parsed.Value.SecretAccessKey != "super-secret")
        {
            throw new InvalidOperationException(
                $"Round-trip mismatch url={parsed.Value.EndpointUrl} key={parsed.Value.AccessKeyId}");
        }
    }

    [Fact]
    public void Empty_or_whitespace_json_is_missing()
    {
        AssertParseFails(null);
        AssertParseFails("");
        AssertParseFails("   ");
        AssertParseFails("{}");
        AssertParseFails("""{"endpointUrl":"https://s3.example.com/b","accessKeyId":"ak","secretAccessKey":""}""");
        AssertParseFails("""{"endpointUrl":"https://s3.example.com/b","accessKeyId":"ak","secretAccessKey":"  "}""");
        AssertParseFails("""{"endpointUrl":"  ","accessKeyId":"ak","secretAccessKey":"secret"}""");
    }

    [Fact]
    public void Incomplete_settings_serialize_to_null()
    {
        if (DysonS3FileStorageSettings.Serialize(null) is not null)
            throw new InvalidOperationException("null settings must serialize to null.");
        if (DysonS3FileStorageSettings.Serialize(new DysonS3FileStorageSettings()) is not null)
            throw new InvalidOperationException("empty settings must serialize to null.");
        if (DysonS3FileStorageSettings.Serialize(new DysonS3FileStorageSettings
            {
                EndpointUrl = "https://s3.example.com/b",
                AccessKeyId = "ak",
                SecretAccessKey = "  ",
            }) is not null)
        {
            throw new InvalidOperationException("whitespace secret must serialize to null (delete row).");
        }
    }

    [Fact]
    public void TryCreate_builds_client_without_network()
    {
        var created = DysonS3FileStorage.TryCreate(new DysonS3FileStorageSettings
        {
            EndpointUrl = "https://s3.example.com/my-bucket",
            AccessKeyId = "ak",
            SecretAccessKey = "secret",
        });
        if (created.IsError)
            throw new InvalidOperationException(created.Error);
        using var storage = created.Value;
        if (storage.Endpoint.Bucket != "my-bucket" || !storage.Endpoint.ForcePathStyle)
            throw new InvalidOperationException("TryCreate must keep the parsed endpoint.");

        var key = storage.BuildObjectKey("shot.png");
        if (!key.StartsWith("dyson/", StringComparison.Ordinal)
            || !key.EndsWith("-shot.png", StringComparison.Ordinal)
            || key.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Object key shape mismatch: " + key);
        }
    }

    private static void AssertParseFails(string? json)
    {
        var parsed = DysonS3FileStorageSettings.TryParse(json);
        if (!parsed.IsError)
            throw new InvalidOperationException("Expected parse failure for: " + json);
    }
}
