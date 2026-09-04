using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace DysonHarness;

/// <summary>Result of a successful image <c>PutObject</c> plus a persisted presigned GET URL.</summary>
public sealed class DysonS3Upload
{
    public required string ObjectKey { get; init; }
    public required string PresignedUrl { get; init; }

    /// <summary>UTC expiry of the persisted presigned GET URL (20-day target, 7-day fallback).</summary>
    public required DateTime ExpiresUtc { get; init; }
}

/// <summary>
/// Concrete AWSSDK.S3 client for Dyson image uploads. One instance per configured subject.
/// Never logs <see cref="DysonS3FileStorageSettings.SecretAccessKey"/>.
/// </summary>
public sealed class DysonS3FileStorage : IDisposable
{
    public const int LifecycleExpirationDays = 20;
    public const int PresignTargetDays = 20;
    public const int PresignFallbackDays = 7;
    public const string ObjectKeyFolder = "dyson/";

    /// <summary>
    /// Stable token so the UI can open the connect modal from a tool error without scraping prose.
    /// </summary>
    public const string FileStorageRequiredToken = "file_storage_required";

    public const string NotConfiguredMessage =
        "File storage is not configured. Connect an S3-compatible bucket to send images. (file_storage_required)";

    private readonly AmazonS3Client _client;
    private readonly DysonS3Endpoint _endpoint;

    public DysonS3FileStorage(DysonS3FileStorageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var parsed = DysonS3EndpointParser.Parse(settings.EndpointUrl);
        if (parsed.IsError)
            throw new ArgumentException(parsed.Error, nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.AccessKeyId)
            || string.IsNullOrWhiteSpace(settings.SecretAccessKey))
        {
            throw new ArgumentException("Access key and secret are required.", nameof(settings));
        }

        _endpoint = parsed.Value;
        _client = CreateClient(_endpoint, settings);
    }

    public static Result<DysonS3FileStorage, string> TryCreate(DysonS3FileStorageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var parsed = DysonS3EndpointParser.Parse(settings.EndpointUrl);
        if (parsed.IsError)
            return Result<DysonS3FileStorage, string>.AsError(parsed.Error);
        if (string.IsNullOrWhiteSpace(settings.AccessKeyId)
            || string.IsNullOrWhiteSpace(settings.SecretAccessKey))
        {
            return Result<DysonS3FileStorage, string>.AsError("Access key and secret are required.");
        }

        try
        {
            return Result<DysonS3FileStorage, string>.AsValue(new DysonS3FileStorage(settings));
        }
        catch (ArgumentException ex)
        {
            return Result<DysonS3FileStorage, string>.AsError(ex.Message);
        }
        catch (Exception ex)
        {
            return Result<DysonS3FileStorage, string>.AsError(DysonS3ClientErrors.Map(ex));
        }
    }

    public DysonS3Endpoint Endpoint => _endpoint;

    public async Task<VoidResult<string>> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var head = await TryHeadBucketAsync(cancellationToken).ConfigureAwait(false);
            if (head.IsError)
            {
                if (head.Error is DysonS3ClientErrors.WrongCredentials
                    or DysonS3ClientErrors.BucketNotFound
                    or DysonS3ClientErrors.Unreachable)
                {
                    return VoidResult<string>.AsError(head.Error);
                }

                var list = await TryListOneAsync(cancellationToken).ConfigureAwait(false);
                if (list.IsError)
                    return list;
            }

            await TryApplyLifecycleAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError(DysonS3ClientErrors.Map(ex));
        }
    }

    public async Task<Result<DysonS3Upload, string>> UploadImageAsync(
        byte[] bytes,
        string contentType,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
            return Result<DysonS3Upload, string>.AsError("Image is empty.");
        if (string.IsNullOrWhiteSpace(contentType))
            return Result<DysonS3Upload, string>.AsError("Content type is required.");

        var key = BuildObjectKey(fileName);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var put = new PutObjectRequest
            {
                BucketName = _endpoint.Bucket,
                Key = key,
                InputStream = stream,
                ContentType = contentType.Trim(),
                DisablePayloadSigning = true,
                DisableDefaultChecksumValidation = true,
                AutoCloseStream = false,
                UseChunkEncoding = false,
            };
            await _client.PutObjectAsync(put, cancellationToken).ConfigureAwait(false);
            return PresignGet(key);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<DysonS3Upload, string>.AsError(DysonS3ClientErrors.Map(ex));
        }
    }

    /// <summary>
    /// Builds a client from a <c>file_storage_s3</c> JSON blob. Incomplete/missing JSON is an error
    /// (treat as not configured — do not throw).
    /// </summary>
    public static Result<DysonS3FileStorage, string> TryCreateFromJson(string? json)
    {
        var parsed = DysonS3FileStorageSettings.TryParse(json);
        if (parsed.IsError)
            return Result<DysonS3FileStorage, string>.AsError(parsed.Error);

        return TryCreate(parsed.Value);
    }

    /// <summary>
    /// No-op when <see cref="DysonBinaryAttachment.RemoteUrl"/> is still valid (non-empty and
    /// unexpired). Otherwise uploads <see cref="DysonBinaryAttachment.Base64Data"/> and sets
    /// RemoteUrl / ObjectKey / RemoteUrlExpiresUtc on the same instance.
    /// Does not re-sign a still-valid URL (signature rotation would bust prompt cache).
    /// </summary>
    public async Task<Result<DysonBinaryAttachment, string>> EnsureRemoteUrlAsync(
        DysonBinaryAttachment image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.IsImage)
            return Result<DysonBinaryAttachment, string>.AsError("Only images can be uploaded to file storage.");

        if (HasValidRemoteUrl(image))
            return Result<DysonBinaryAttachment, string>.AsValue(image);

        if (string.IsNullOrWhiteSpace(image.Base64Data))
            return Result<DysonBinaryAttachment, string>.AsError("Image has no local bytes to upload.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(image.Base64Data);
        }
        catch (FormatException ex)
        {
            return Result<DysonBinaryAttachment, string>.AsError($"Invalid image base64: {ex.Message}");
        }

        var uploaded = await UploadImageAsync(
                bytes,
                image.MimeType,
                image.FileName,
                cancellationToken)
            .ConfigureAwait(false);
        if (uploaded.IsError)
            return Result<DysonBinaryAttachment, string>.AsError(uploaded.Error);

        image.RemoteUrl = uploaded.Value.PresignedUrl;
        image.ObjectKey = uploaded.Value.ObjectKey;
        image.RemoteUrlExpiresUtc = uploaded.Value.ExpiresUtc;
        return Result<DysonBinaryAttachment, string>.AsValue(image);
    }

    public void Dispose() => _client.Dispose();

    internal static bool HasValidRemoteUrl(DysonBinaryAttachment image) =>
        !string.IsNullOrWhiteSpace(image.RemoteUrl)
        && (image.RemoteUrlExpiresUtc is not { } expires || expires > DateTime.UtcNow);

    internal string BuildObjectKey(string? fileName)
    {
        var now = DateTime.UtcNow;
        return $"{_endpoint.KeyPrefix}{ObjectKeyFolder}{now:yyyy}/{now:MM}/{Guid.NewGuid():N}-{SanitizeFileName(fileName)}";
    }

    private async Task<VoidResult<string>> TryHeadBucketAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client
                .HeadBucketAsync(new HeadBucketRequest { BucketName = _endpoint.Bucket }, cancellationToken)
                .ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError(DysonS3ClientErrors.Map(ex));
        }
    }

    private async Task<VoidResult<string>> TryListOneAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client
                .ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = _endpoint.Bucket,
                        MaxKeys = 1,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError(DysonS3ClientErrors.Map(ex));
        }
    }

    private async Task TryApplyLifecycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var prefix = _endpoint.KeyPrefix + ObjectKeyFolder;
            await _client
                .PutLifecycleConfigurationAsync(
                    new PutLifecycleConfigurationRequest
                    {
                        BucketName = _endpoint.Bucket,
                        Configuration = new LifecycleConfiguration
                        {
                            Rules =
                            [
                                new LifecycleRule
                                {
                                    Id = "dyson-expire-20d",
                                    Filter = new LifecycleFilter { Prefix = prefix },
                                    Status = LifecycleRuleStatus.Enabled,
                                    Expiration = new LifecycleRuleExpiration
                                    {
                                        Days = LifecycleExpirationDays,
                                    },
                                },
                            ],
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // ponytail: best-effort TTL; AccessDenied / unsupported hosts are expected. Ceiling: user enables 20-day lifecycle on prefix dyson/ themselves.
        }
    }

    private Result<DysonS3Upload, string> PresignGet(string objectKey)
    {
        // AWSSDK.S3 4.x dropped AWSConfigsS3.UseSignatureVersion4; SigV4 is the only S3 signer.
        try
        {
            return Result<DysonS3Upload, string>.AsValue(SignGet(objectKey, TimeSpan.FromDays(PresignTargetDays)));
        }
        catch (Exception)
        {
            try
            {
                return Result<DysonS3Upload, string>.AsValue(SignGet(objectKey, TimeSpan.FromDays(PresignFallbackDays)));
            }
            catch (Exception ex)
            {
                return Result<DysonS3Upload, string>.AsError(DysonS3ClientErrors.Map(ex));
            }
        }
    }

    private DysonS3Upload SignGet(string objectKey, TimeSpan ttl)
    {
        var expiresUtc = DateTime.UtcNow.Add(ttl);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _endpoint.Bucket,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = expiresUtc,
            Protocol = Protocol.HTTPS,
        };
        var url = _client.GetPreSignedURL(request);
        return new DysonS3Upload
        {
            ObjectKey = objectKey,
            PresignedUrl = url,
            ExpiresUtc = expiresUtc,
        };
    }

    private static AmazonS3Client CreateClient(DysonS3Endpoint endpoint, DysonS3FileStorageSettings settings)
    {
        var credentials = new BasicAWSCredentials(settings.AccessKeyId.Trim(), settings.SecretAccessKey.Trim());
        var config = new AmazonS3Config
        {
            ForcePathStyle = endpoint.ForcePathStyle,
            AuthenticationRegion = endpoint.Region,
            Timeout = TimeSpan.FromSeconds(30),
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        if (!string.IsNullOrWhiteSpace(endpoint.ServiceUrl))
            config.ServiceURL = endpoint.ServiceUrl;
        else
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(endpoint.Region);

        return new AmazonS3Client(credentials, config);
    }

    private static string SanitizeFileName(string? fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName)
            ? "image"
            : Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(name))
            name = "image";

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        if (name.Length > 80)
            name = name[..80];

        return name;
    }
}
