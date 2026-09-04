using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using DysonHarness;

namespace Harness.Tests;

/// <summary>ponytail: S3 exception map (403 credentials, 404 bucket, network).</summary>
public class DysonS3ClientErrorsTests
{
    [Fact]
    public void Forbidden_and_signature_errors_are_wrong_credentials()
    {
        AssertMaps(
            new AmazonS3Exception(
                "denied",
                ErrorType.Sender,
                "AccessDenied",
                "req",
                HttpStatusCode.Forbidden),
            DysonS3ClientErrors.WrongCredentials);

        AssertMaps(
            new AmazonS3Exception(
                "sig",
                ErrorType.Sender,
                "SignatureDoesNotMatch",
                "req",
                HttpStatusCode.Forbidden),
            DysonS3ClientErrors.WrongCredentials);

        AssertMaps(
            new AmazonS3Exception(
                "key",
                ErrorType.Sender,
                "InvalidAccessKeyId",
                "req",
                HttpStatusCode.Forbidden),
            DysonS3ClientErrors.WrongCredentials);
    }

    [Fact]
    public void Not_found_and_no_such_bucket_are_bucket_not_found()
    {
        AssertMaps(
            new AmazonS3Exception(
                "gone",
                ErrorType.Sender,
                "NoSuchBucket",
                "req",
                HttpStatusCode.NotFound),
            DysonS3ClientErrors.BucketNotFound);

        AssertMaps(
            new AmazonS3Exception(
                "404",
                ErrorType.Sender,
                "NotFound",
                "req",
                HttpStatusCode.NotFound),
            DysonS3ClientErrors.BucketNotFound);
    }

    [Fact]
    public void Connection_dns_and_timeout_are_unreachable()
    {
        AssertMaps(
            new HttpRequestException("No such host is known"),
            DysonS3ClientErrors.Unreachable);
        AssertMaps(
            new TimeoutException("The request timed out"),
            DysonS3ClientErrors.Unreachable);
        AssertMaps(
            new AmazonClientException("Unable to connect to the remote server"),
            DysonS3ClientErrors.Unreachable);
    }

    [Fact]
    public void Other_service_messages_are_truncated()
    {
        var longMessage = new string('x', DysonS3ClientErrors.MaxServiceMessageChars + 40);
        var mapped = DysonS3ClientErrors.Map(new InvalidOperationException(longMessage));
        if (mapped.Length != DysonS3ClientErrors.MaxServiceMessageChars
            || mapped != longMessage[..DysonS3ClientErrors.MaxServiceMessageChars])
        {
            throw new InvalidOperationException($"Truncation failed: len={mapped.Length}");
        }
    }

    private static void AssertMaps(Exception exception, string expected)
    {
        var mapped = DysonS3ClientErrors.Map(exception);
        if (mapped != expected)
            throw new InvalidOperationException($"Expected '{expected}', got '{mapped}'");
    }
}
