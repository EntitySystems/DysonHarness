using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;

namespace DysonHarness;

/// <summary>Maps AWS/S3 SDK exceptions to short user-facing strings. Never logs secrets.</summary>
public static class DysonS3ClientErrors
{
    public const string WrongCredentials = "Wrong credentials";
    public const string BucketNotFound = "Bucket not found";
    public const string Unreachable = "Could not reach the bucket endpoint";
    public const int MaxServiceMessageChars = 240;

    public static string Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is AmazonS3Exception s3)
            {
                if (IsWrongCredentials(s3))
                    return WrongCredentials;
                if (IsBucketNotFound(s3))
                    return BucketNotFound;
                if (IsUnreachableStatus(s3.StatusCode))
                    return Unreachable;
            }
            else if (ex is AmazonServiceException service)
            {
                if (IsWrongCredentials(service.StatusCode, service.ErrorCode))
                    return WrongCredentials;
                if (IsBucketNotFound(service.StatusCode, service.ErrorCode))
                    return BucketNotFound;
                if (IsUnreachableStatus(service.StatusCode))
                    return Unreachable;
            }

            if (IsNetwork(ex))
                return Unreachable;
        }

        return Truncate(FirstMessage(exception));
    }

    private static bool IsWrongCredentials(AmazonS3Exception s3) =>
        IsWrongCredentials(s3.StatusCode, s3.ErrorCode);

    private static bool IsWrongCredentials(HttpStatusCode statusCode, string? errorCode) =>
        statusCode == HttpStatusCode.Forbidden
        || statusCode == HttpStatusCode.Unauthorized
        || ErrorCodeIs(errorCode, "SignatureDoesNotMatch")
        || ErrorCodeIs(errorCode, "InvalidAccessKeyId")
        || ErrorCodeIs(errorCode, "InvalidClientTokenId")
        || ErrorCodeIs(errorCode, "AuthFailure")
        || ErrorCodeIs(errorCode, "AccessDenied");

    private static bool IsBucketNotFound(AmazonS3Exception s3) =>
        IsBucketNotFound(s3.StatusCode, s3.ErrorCode);

    private static bool IsBucketNotFound(HttpStatusCode statusCode, string? errorCode) =>
        statusCode == HttpStatusCode.NotFound
        || ErrorCodeIs(errorCode, "NoSuchBucket")
        || ErrorCodeIs(errorCode, "NotFound");

    private static bool IsUnreachableStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or 0;

    // AmazonClientException is SDK-local (network/timeouts). AmazonS3Exception
    // is an AmazonServiceException, not a subclass of AmazonClientException in AWSSDK v4.
    private static bool IsNetwork(Exception ex) =>
        ex is HttpRequestException
            or SocketException
            or TimeoutException
            or IOException
            or AmazonClientException;

    private static bool ErrorCodeIs(string? errorCode, string expected) =>
        string.Equals(errorCode, expected, StringComparison.OrdinalIgnoreCase);

    private static string FirstMessage(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(ex.Message))
                return ex.Message;
        }

        return "S3 request failed.";
    }

    private static string Truncate(string message)
    {
        var trimmed = string.IsNullOrWhiteSpace(message) ? "S3 request failed." : message.Trim();
        return trimmed.Length <= MaxServiceMessageChars
            ? trimmed
            : trimmed[..MaxServiceMessageChars];
    }
}
