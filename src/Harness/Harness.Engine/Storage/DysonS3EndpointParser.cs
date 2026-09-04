using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>Parses a bucket URL into <see cref="DysonS3Endpoint"/>.</summary>
public static class DysonS3EndpointParser
{
    public const string MissingBucketMessage =
        "URL must include the bucket (e.g. https://s3.example.com/my-bucket)";

    public const string DefaultRegion = "us-east-1";

    // my-bucket.s3.us-east-1.amazonaws.com  /  my-bucket.s3.amazonaws.com  /  my-bucket.s3-us-west-2.amazonaws.com
    private static readonly Regex AwsVirtualHosted = new(
        @"^(?<bucket>.+)\.s3(?:[.-](?<region>[a-z0-9-]+))?\.amazonaws\.com$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // s3.us-east-1.amazonaws.com  /  s3.amazonaws.com
    private static readonly Regex AwsPathStyleHost = new(
        @"^s3(?:[.-](?<region>[a-z0-9-]+))?\.amazonaws\.com$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Result<DysonS3Endpoint, string> Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return Result<DysonS3Endpoint, string>.AsError(MissingBucketMessage);
        }

        var virtualHosted = TryParseAwsVirtualHosted(uri);
        if (virtualHosted is not null)
            return Result<DysonS3Endpoint, string>.AsValue(virtualHosted);

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return Result<DysonS3Endpoint, string>.AsError(MissingBucketMessage);

        var bucket = Uri.UnescapeDataString(segments[0]).Trim();
        if (string.IsNullOrWhiteSpace(bucket))
            return Result<DysonS3Endpoint, string>.AsError(MissingBucketMessage);

        var prefix = "";
        if (segments.Length > 1)
        {
            prefix = string.Join('/', segments.Skip(1).Select(Uri.UnescapeDataString));
            if (!prefix.EndsWith('/'))
                prefix += "/";
        }

        return Result<DysonS3Endpoint, string>.AsValue(new DysonS3Endpoint
        {
            Bucket = bucket,
            KeyPrefix = prefix,
            Region = TryAwsPathStyleRegion(uri.Host) ?? DefaultRegion,
            ServiceUrl = uri.GetLeftPart(UriPartial.Authority),
            ForcePathStyle = true,
        });
    }

    private static DysonS3Endpoint? TryParseAwsVirtualHosted(Uri uri)
    {
        var match = AwsVirtualHosted.Match(uri.Host);
        if (!match.Success)
            return null;

        var bucket = match.Groups["bucket"].Value.Trim();
        if (string.IsNullOrWhiteSpace(bucket) || AwsPathStyleHost.IsMatch(uri.Host))
            return null;

        var region = match.Groups["region"].Success
            ? match.Groups["region"].Value
            : DefaultRegion;
        if (string.IsNullOrWhiteSpace(region) || region.Equals("accelerate", StringComparison.OrdinalIgnoreCase))
            region = DefaultRegion;

        return new DysonS3Endpoint
        {
            Bucket = bucket,
            KeyPrefix = "",
            Region = region.ToLowerInvariant(),
            ServiceUrl = null,
            ForcePathStyle = false,
        };
    }

    private static string? TryAwsPathStyleRegion(string host)
    {
        var match = AwsPathStyleHost.Match(host);
        if (!match.Success)
            return null;
        var region = match.Groups["region"].Value;
        return string.IsNullOrWhiteSpace(region) ? DefaultRegion : region.ToLowerInvariant();
    }
}
