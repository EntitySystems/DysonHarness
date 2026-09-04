using DysonHarness;

namespace Harness.Tests;

/// <summary>ponytail: S3 bucket URL parse (path-style, AWS virtual-hosted, R2, prefix, missing bucket).</summary>
public class DysonS3EndpointParserTests
{
    [Fact]
    public void Path_style_parses_endpoint_and_bucket()
    {
        var result = DysonS3EndpointParser.Parse("https://s3.example.com/my-bucket");
        if (result.IsError)
            throw new InvalidOperationException(result.Error);
        var ep = result.Value;
        if (!ep.ForcePathStyle
            || ep.ServiceUrl != "https://s3.example.com"
            || ep.Bucket != "my-bucket"
            || ep.KeyPrefix != ""
            || ep.Region != DysonS3EndpointParser.DefaultRegion)
        {
            throw new InvalidOperationException(
                $"Path-style mismatch: style={ep.ForcePathStyle} url={ep.ServiceUrl} bucket={ep.Bucket} prefix='{ep.KeyPrefix}' region={ep.Region}");
        }
    }

    [Fact]
    public void Path_style_keeps_extra_prefix()
    {
        var result = DysonS3EndpointParser.Parse("https://s3.example.com/my-bucket/optional/prefix");
        if (result.IsError)
            throw new InvalidOperationException(result.Error);
        var ep = result.Value;
        if (!ep.ForcePathStyle
            || ep.ServiceUrl != "https://s3.example.com"
            || ep.Bucket != "my-bucket"
            || ep.KeyPrefix != "optional/prefix/")
        {
            throw new InvalidOperationException(
                $"Prefix mismatch: url={ep.ServiceUrl} bucket={ep.Bucket} prefix='{ep.KeyPrefix}'");
        }
    }

    [Fact]
    public void Aws_virtual_hosted_parses_bucket_and_region()
    {
        var result = DysonS3EndpointParser.Parse("https://my-bucket.s3.us-east-1.amazonaws.com");
        if (result.IsError)
            throw new InvalidOperationException(result.Error);
        var ep = result.Value;
        if (ep.ForcePathStyle
            || ep.ServiceUrl is not null
            || ep.Bucket != "my-bucket"
            || ep.Region != "us-east-1"
            || ep.KeyPrefix != "")
        {
            throw new InvalidOperationException(
                $"AWS virtual-hosted mismatch: style={ep.ForcePathStyle} url={ep.ServiceUrl} bucket={ep.Bucket} region={ep.Region}");
        }
    }

    [Fact]
    public void R2_style_host_is_path_style_custom_endpoint()
    {
        var result = DysonS3EndpointParser.Parse("https://abc123.r2.cloudflarestorage.com/my-bucket");
        if (result.IsError)
            throw new InvalidOperationException(result.Error);
        var ep = result.Value;
        if (!ep.ForcePathStyle
            || ep.ServiceUrl != "https://abc123.r2.cloudflarestorage.com"
            || ep.Bucket != "my-bucket"
            || ep.Region != DysonS3EndpointParser.DefaultRegion)
        {
            throw new InvalidOperationException(
                $"R2 mismatch: style={ep.ForcePathStyle} url={ep.ServiceUrl} bucket={ep.Bucket} region={ep.Region}");
        }
    }

    [Fact]
    public void Missing_bucket_fails_with_exact_message()
    {
        AssertMissing("https://s3.example.com");
        AssertMissing("https://s3.example.com/");
        AssertMissing("not-a-url");
        AssertMissing(null);
        AssertMissing("  ");
    }

    private static void AssertMissing(string? url)
    {
        var result = DysonS3EndpointParser.Parse(url);
        if (!result.IsError || result.Error != DysonS3EndpointParser.MissingBucketMessage)
        {
            throw new InvalidOperationException(
                $"Expected missing-bucket message for '{url}', got error={result.IsError} '{result.Error}'");
        }
    }
}
