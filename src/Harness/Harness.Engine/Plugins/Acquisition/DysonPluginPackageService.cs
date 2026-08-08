using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DysonHarness;

public sealed partial class DysonPluginPackageService : IDysonPluginPackageService, IDisposable
{
    private static readonly JsonSerializerOptions StorageJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly IDysonPluginPackageParser _parser;
    private readonly IDysonPluginInstallationRepository _repository;
    private readonly DysonPluginPackageLimits _limits;
    private readonly ConcurrentDictionary<Guid, RetainedPreview> _previews = new();
    private bool _disposed;

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,98}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubNameRegex();

    [GeneratedRegex("^[0-9a-fA-F]{40,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitRegex();

    public DysonPluginPackageService(
        HttpClient http,
        IDysonPluginPackageParser parser,
        IDysonPluginInstallationRepository repository,
        DysonPluginPackageLimits? limits = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _limits = limits ?? new DysonPluginPackageLimits();
    }

    public async Task<Result<DysonPluginPreview, string>> PreviewAsync(
        DysonPluginPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var validation = DysonPluginRequestValidation.Validate(request);
        if (validation.IsError)
            return Result<DysonPluginPreview, string>.AsError(validation.Error);

        PruneExpiredPreviews();
        var previewId = Guid.NewGuid();
        var container = DysonPluginPackageSecurity.CreatePreviewDirectory(previewId);
        if (container.IsError)
            return Result<DysonPluginPreview, string>.AsError(container.Error);

        try
        {
            var acquired = await AcquireAsync(request, container.Value, cancellationToken).ConfigureAwait(false);
            if (acquired.IsError)
                return FailPreview<DysonPluginPreview>(container.Value, acquired.Error);

            var selectedSubdirectory = request.PluginSubdirectory ?? acquired.Value.UrlSubdirectory;
            var selected = SelectSubdirectory(acquired.Value.PackageRoot, selectedSubdirectory);
            if (selected.IsError)
                return FailPreview<DysonPluginPreview>(container.Value, selected.Error);

            var checksum = DysonPluginPackageSecurity.ComputeTreeChecksum(selected.Value, _limits);
            if (checksum.IsError)
                return FailPreview<DysonPluginPreview>(container.Value, checksum.Error);

            var source = new DysonPluginSource
            {
                Kind = request.SourceKind,
                Location = request.SourceLocation.Trim(),
                RequestedRef = acquired.Value.RequestedRef,
                ResolvedCommit = acquired.Value.ResolvedCommit,
                Subdirectory = NormalizeOptional(selectedSubdirectory),
                ContentChecksum = checksum.Value,
            };
            var parsed = await _parser.ParseAsync(new DysonPluginParseRequest
            {
                StagedPackageRoot = selected.Value,
                Source = source,
            }, cancellationToken).ConfigureAwait(false);
            if (parsed.IsError)
                return FailPreview<DysonPluginPreview>(container.Value, parsed.Error);

            var createdUtc = DateTime.UtcNow;
            var preview = new DysonPluginPreview
            {
                PreviewId = previewId,
                Plugin = parsed.Value,
                StagedPackageRoot = selected.Value,
                CreatedUtc = createdUtc,
            };
            if (!_previews.TryAdd(previewId, new RetainedPreview(preview, container.Value, checksum.Value)))
                return FailPreview<DysonPluginPreview>(container.Value, "Failed to retain plugin preview.");
            return Result<DysonPluginPreview, string>.AsValue(preview);
        }
        catch (OperationCanceledException)
        {
            DysonPluginPackageSecurity.TryDeleteDirectory(container.Value);
            throw;
        }
        catch (Exception ex)
        {
            return FailPreview<DysonPluginPreview>(container.Value, $"Failed to preview plugin package: {ex.Message}");
        }
    }

    public async Task<Result<DysonPluginInstallResult, string>> InstallAsync(
        DysonPluginInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var validation = DysonPluginRequestValidation.Validate(request);
        if (validation.IsError)
            return Result<DysonPluginInstallResult, string>.AsError(validation.Error);
        if (!_previews.TryGetValue(request.PreviewId, out var retained))
            return Result<DysonPluginInstallResult, string>.AsError("Plugin preview was not found or is no longer owned by this package service.");
        if (DateTime.UtcNow - retained.Preview.CreatedUtc > _limits.PreviewRetention)
        {
            RemovePreview(retained.Preview.PreviewId, retained);
            return Result<DysonPluginInstallResult, string>.AsError("Plugin preview expired; create a new preview before installing.");
        }

        await retained.InstallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (retained.Installed)
                return Result<DysonPluginInstallResult, string>.AsError("Plugin preview has already been installed.");

            var checksum = DysonPluginPackageSecurity.ComputeTreeChecksum(retained.Preview.StagedPackageRoot, _limits);
            if (checksum.IsError)
                return Result<DysonPluginInstallResult, string>.AsError(checksum.Error);
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(checksum.Value),
                    System.Text.Encoding.ASCII.GetBytes(retained.Checksum)))
            {
                return Result<DysonPluginInstallResult, string>.AsError(
                    "Staged plugin content changed after preview; create a new preview before installing.");
            }

            var reparsed = await _parser.ParseAsync(new DysonPluginParseRequest
            {
                StagedPackageRoot = retained.Preview.StagedPackageRoot,
                Source = retained.Preview.Plugin.Source,
                ExpectedFormat = retained.Preview.Plugin.Format,
            }, cancellationToken).ConfigureAwait(false);
            if (reparsed.IsError)
                return Result<DysonPluginInstallResult, string>.AsError($"Plugin failed install-time revalidation: {reparsed.Error}");
            if (!string.Equals(
                    reparsed.Value.Manifest.NormalizedId,
                    retained.Preview.Plugin.Manifest.NormalizedId,
                    StringComparison.Ordinal))
            {
                return Result<DysonPluginInstallResult, string>.AsError("Plugin identity changed after preview.");
            }

            DysonPluginInstallationEntity? replacedInstallation = null;
            var replacementInstallationId = request.ReplacesInstallationId;
            if (replacementInstallationId is Guid replacesInstallationId)
            {
                var existing = await _repository.GetAsync(replacesInstallationId, cancellationToken).ConfigureAwait(false);
                if (existing.IsError)
                    return Result<DysonPluginInstallResult, string>.AsError(existing.Error);

                var replacementOwnership = ValidateReplacementOwnership(existing.Value, request.Target, reparsed.Value);
                if (replacementOwnership.IsError)
                    return Result<DysonPluginInstallResult, string>.AsError(replacementOwnership.Error);
                replacedInstallation = existing.Value;
            }

            var roots = DysonPluginPaths.EnsureScopeRoots(request.Target);
            if (roots.IsError)
                return Result<DysonPluginInstallResult, string>.AsError(roots.Error);
            var contentId = GetVersionOrContentId(reparsed.Value, checksum.Value);
            var paths = DysonPluginPaths.Resolve(
                request.Target, reparsed.Value.Manifest.NormalizedId, contentId);
            if (paths.IsError)
                return Result<DysonPluginInstallResult, string>.AsError(paths.Error);
            var ownership = DysonPluginPaths.ValidatePackageRootOwnership(request.Target, paths.Value.PackageRoot);
            if (ownership.IsError)
                return Result<DysonPluginInstallResult, string>.AsError(ownership.Error);

            var promoted = false;
            var destinationAlreadyPresent = Directory.Exists(paths.Value.PackageRoot);
            var promotionTemp = paths.Value.PackageRoot + $".staging-{Guid.NewGuid():N}";
            try
            {
                if (destinationAlreadyPresent)
                {
                    var existingChecksum = DysonPluginPackageSecurity.ComputeTreeChecksum(paths.Value.PackageRoot, _limits);
                    if (existingChecksum.IsError || !string.Equals(existingChecksum.Value, checksum.Value, StringComparison.Ordinal))
                    {
                        return Result<DysonPluginInstallResult, string>.AsError(
                            "The immutable plugin destination already exists with different content.");
                    }
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(paths.Value.PackageRoot)!);
                    var copied = DysonPluginPackageSecurity.CopyFolder(
                        retained.Preview.StagedPackageRoot, promotionTemp, _limits);
                    if (copied.IsError)
                        return Result<DysonPluginInstallResult, string>.AsError(copied.Error);
                    Directory.Move(promotionTemp, paths.Value.PackageRoot);
                    promoted = true;
                }

                Directory.CreateDirectory(paths.Value.PluginDataRoot);
                var now = DateTime.UtcNow;
                var entity = CreateInstallationEntity(
                    reparsed.Value, request.Target, paths.Value.PackageRoot, now);
                entity.IsEnabled = replacedInstallation?.IsEnabled ?? true;
                entity.Status = entity.IsEnabled
                    ? DysonPluginStatus.Installed.ToString()
                    : DysonPluginStatus.Disabled.ToString();
                Guid installationId;
                if (replacementInstallationId is Guid replacementId)
                {
                    var replaced = await _repository.ReplaceAsync(
                        replacementId, entity, cancellationToken).ConfigureAwait(false);
                    if (replaced.IsError)
                    {
                        if (promoted)
                            DysonPluginPackageSecurity.TryDeleteDirectory(paths.Value.PackageRoot);
                        return Result<DysonPluginInstallResult, string>.AsError(
                            $"Plugin files were rolled back because the installation record could not be replaced: {replaced.Error}");
                    }
                    installationId = replacementId;
                }
                else
                {
                    var saved = await _repository.UpsertAsync(entity, cancellationToken).ConfigureAwait(false);
                    if (saved.IsError)
                    {
                        if (promoted)
                            DysonPluginPackageSecurity.TryDeleteDirectory(paths.Value.PackageRoot);
                        return Result<DysonPluginInstallResult, string>.AsError(
                            $"Plugin files were rolled back because the installation record could not be saved: {saved.Error}");
                    }
                    installationId = saved.Value;
                }

                retained.Installed = true;
                _previews.TryRemove(retained.Preview.PreviewId, out _);
                DysonPluginPackageSecurity.TryDeleteDirectory(retained.ContainerRoot);
                return Result<DysonPluginInstallResult, string>.AsValue(new DysonPluginInstallResult
                {
                    InstallationId = installationId,
                    Plugin = reparsed.Value,
                    Scope = request.Target.Scope,
                    WorkDirectoryId = request.Target.WorkDirectoryId,
                    PackageRoot = paths.Value.PackageRoot,
                    PluginDataRoot = paths.Value.PluginDataRoot,
                    InstalledUtc = now,
                });
            }
            catch (OperationCanceledException)
            {
                DysonPluginPackageSecurity.TryDeleteDirectory(promotionTemp);
                if (promoted)
                    DysonPluginPackageSecurity.TryDeleteDirectory(paths.Value.PackageRoot);
                throw;
            }
            catch (Exception ex)
            {
                DysonPluginPackageSecurity.TryDeleteDirectory(promotionTemp);
                if (promoted)
                    DysonPluginPackageSecurity.TryDeleteDirectory(paths.Value.PackageRoot);
                return Result<DysonPluginInstallResult, string>.AsError($"Failed to promote plugin package atomically: {ex.Message}");
            }
            finally
            {
                DysonPluginPackageSecurity.TryDeleteDirectory(promotionTemp);
            }
        }
        finally
        {
            retained.InstallGate.Release();
        }
    }

    public async Task<VoidResult<string>> DiscardPreviewAsync(
        Guid previewId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (previewId == Guid.Empty)
            return VoidResult<string>.AsError("Plugin preview id is required.");
        if (!_previews.TryGetValue(previewId, out var retained))
            return VoidResult<string>.AsError("Plugin preview was not found or is no longer owned by this package service.");

        await retained.InstallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (retained.Installed)
                return VoidResult<string>.AsError("An installed plugin preview cannot be discarded.");

            if (_previews.TryRemove(new KeyValuePair<Guid, RetainedPreview>(previewId, retained)))
                DysonPluginPackageSecurity.TryDeleteDirectory(retained.ContainerRoot);
            return VoidResult<string>.Success;
        }
        finally
        {
            retained.InstallGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var pair in _previews)
            RemovePreview(pair.Key, pair.Value);
    }

    private async Task<Result<AcquiredPackage, string>> AcquireAsync(
        DysonPluginPreviewRequest request,
        string container,
        CancellationToken cancellationToken)
    {
        var packageRoot = Path.Combine(container, "package");
        if (request.SourceKind == DysonPluginSourceKind.LocalZip)
        {
            var extracted = DysonPluginPackageSecurity.ExtractZip(request.ArchiveBytes, packageRoot, _limits);
            return extracted.IsError
                ? Result<AcquiredPackage, string>.AsError(extracted.Error)
                : Result<AcquiredPackage, string>.AsValue(new AcquiredPackage(extracted.Value, null, null, null));
        }
        if (request.SourceKind == DysonPluginSourceKind.LocalFolder)
        {
            var copied = DysonPluginPackageSecurity.CopyFolder(
                request.SourceLocation.Trim(), packageRoot, _limits);
            return copied.IsError
                ? Result<AcquiredPackage, string>.AsError(copied.Error)
                : Result<AcquiredPackage, string>.AsValue(new AcquiredPackage(copied.Value, null, null, null));
        }

        var source = ParseGitHubSource(request.SourceLocation, request.RequestedRef, request.PluginSubdirectory);
        if (source.IsError)
            return Result<AcquiredPackage, string>.AsError(source.Error);
        var resolved = await ResolveGitHubCommitAsync(source.Value, cancellationToken).ConfigureAwait(false);
        if (resolved.IsError)
            return Result<AcquiredPackage, string>.AsError(resolved.Error);
        var archive = await DownloadGitHubArchiveAsync(source.Value, resolved.Value, cancellationToken).ConfigureAwait(false);
        if (archive.IsError)
            return Result<AcquiredPackage, string>.AsError(archive.Error);
        var extractedGitHub = DysonPluginPackageSecurity.ExtractZip(archive.Value, packageRoot, _limits);
        return extractedGitHub.IsError
            ? Result<AcquiredPackage, string>.AsError(extractedGitHub.Error)
            : Result<AcquiredPackage, string>.AsValue(new AcquiredPackage(
                extractedGitHub.Value, source.Value.Ref, resolved.Value, source.Value.Subdirectory));
    }

    private async Task<Result<string, string>> ResolveGitHubCommitAsync(
        GitHubSource source,
        CancellationToken cancellationToken)
    {
        var reference = Uri.EscapeDataString(source.Ref ?? "HEAD");
        var url = $"https://api.github.com/repos/{source.Owner}/{source.Repository}/commits/{reference}";
        using var request = CreateGitHubRequest(HttpMethod.Get, url, "application/vnd.github+json");
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var origin = ValidateResponseOrigin(response, request, "api.github.com");
            if (origin.IsError)
                return Result<string, string>.AsError(origin.Error);
            if (!response.IsSuccessStatusCode)
                return Result<string, string>.AsError($"GitHub commit resolution failed with HTTP {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("sha", out var shaElement) ||
                shaElement.ValueKind != JsonValueKind.String ||
                shaElement.GetString() is not { } sha || !CommitRegex().IsMatch(sha))
            {
                return Result<string, string>.AsError("GitHub returned an invalid immutable commit identifier.");
            }
            return Result<string, string>.AsValue(sha.ToLowerInvariant());
        }
        catch (HttpRequestException ex)
        {
            return Result<string, string>.AsError($"GitHub commit resolution failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return Result<string, string>.AsError($"GitHub commit response was malformed: {ex.Message}");
        }
    }

    private async Task<Result<byte[], string>> DownloadGitHubArchiveAsync(
        GitHubSource source,
        string commit,
        CancellationToken cancellationToken)
    {
        var url = $"https://codeload.github.com/{source.Owner}/{source.Repository}/zip/{commit}";
        using var request = CreateGitHubRequest(HttpMethod.Get, url, "application/zip");
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var origin = ValidateResponseOrigin(response, request, "codeload.github.com");
            if (origin.IsError)
                return Result<byte[], string>.AsError(origin.Error);
            if (!response.IsSuccessStatusCode)
                return Result<byte[], string>.AsError($"GitHub archive download failed with HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is > 0 and var length && length > _limits.MaxArchiveBytes)
                return Result<byte[], string>.AsError("GitHub plugin archive exceeds the compressed-byte quota.");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > _limits.MaxArchiveBytes)
                    return Result<byte[], string>.AsError("GitHub plugin archive exceeds the compressed-byte quota.");
                output.Write(buffer, 0, read);
            }
            return Result<byte[], string>.AsValue(output.ToArray());
        }
        catch (HttpRequestException ex)
        {
            return Result<byte[], string>.AsError($"GitHub archive download failed: {ex.Message}");
        }
    }

    private static VoidResult<string> ValidateResponseOrigin(
        HttpResponseMessage response,
        HttpRequestMessage originalRequest,
        string expectedHost)
    {
        var uri = response.RequestMessage?.RequestUri ?? originalRequest.RequestUri;
        if (uri is null || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase))
        {
            return VoidResult<string>.AsError("GitHub request redirected to an unexpected origin.");
        }
        return VoidResult<string>.Success;
    }

    private static HttpRequestMessage CreateGitHubRequest(HttpMethod method, string url, string accept)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DysonHarness", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return request;
    }

    private static Result<GitHubSource, string> ParseGitHubSource(
        string input,
        string? requestedRef,
        string? requestedSubdirectory)
    {
        var value = input.Trim();
        string owner;
        string repository;
        string? urlRef = null;
        string? urlSubdirectory = null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                return Result<GitHubSource, string>.AsError("GitHub source must be an HTTPS github.com repository URL without userinfo, query, or fragment.");
            }
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString).ToArray();
            if (segments.Length < 2)
                return Result<GitHubSource, string>.AsError("GitHub URL must include owner and repository.");
            owner = segments[0];
            repository = segments[1];
            if (segments.Length > 2)
            {
                if (segments.Length < 4 || !string.Equals(segments[2], "tree", StringComparison.Ordinal))
                    return Result<GitHubSource, string>.AsError("Only GitHub repository and /tree/{ref}/{path} URLs are supported.");
                urlRef = segments[3];
                if (segments.Length > 4)
                    urlSubdirectory = string.Join('/', segments[4..]);
            }
        }
        else
        {
            var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 2)
                return Result<GitHubSource, string>.AsError("GitHub source must be owner/repository or an HTTPS github.com URL.");
            owner = segments[0];
            repository = segments[1];
        }

        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repository = repository[..^4];
        if (!GitHubNameRegex().IsMatch(owner) || !GitHubNameRegex().IsMatch(repository))
            return Result<GitHubSource, string>.AsError("GitHub owner and repository contain unsafe characters.");

        var reference = NormalizeOptional(requestedRef) ?? urlRef;
        if (reference is not null && !IsSafeGitReference(reference))
            return Result<GitHubSource, string>.AsError("GitHub ref contains unsafe or ambiguous characters.");
        var subdirectory = NormalizeOptional(requestedSubdirectory) ?? urlSubdirectory;
        if (subdirectory is not null)
        {
            var safe = DysonPluginPackageSecurity.ValidateRelativePath(subdirectory, 32);
            if (safe.IsError)
                return Result<GitHubSource, string>.AsError($"Unsafe GitHub plugin subdirectory: {safe.Error}");
            subdirectory = safe.Value;
        }
        return Result<GitHubSource, string>.AsValue(new GitHubSource(owner, repository, reference, subdirectory));
    }

    private static bool IsSafeGitReference(string reference)
    {
        if (reference.Length > 256 || reference.StartsWith('/') || reference.EndsWith('/') ||
            reference.Contains("..", StringComparison.Ordinal) || reference.Contains("@{", StringComparison.Ordinal))
            return false;
        return reference.All(ch => !char.IsControl(ch) && ch is not '\\' and not '~' and not '^' and not ':' and not '?' and not '*' and not '[');
    }

    private static Result<string, string> SelectSubdirectory(string root, string? requestedSubdirectory)
    {
        if (string.IsNullOrWhiteSpace(requestedSubdirectory))
            return Result<string, string>.AsValue(root);
        var selected = DysonPluginPackageSecurity.ResolveContainedPath(root, requestedSubdirectory);
        if (selected.IsError)
            return selected;
        if (!Directory.Exists(selected.Value))
            return Result<string, string>.AsError($"Plugin subdirectory was not found: '{requestedSubdirectory}'.");
        return Result<string, string>.AsValue(selected.Value);
    }

    private static VoidResult<string> ValidateReplacementOwnership(
        DysonPluginInstallationEntity installation,
        DysonPluginInstallTarget target,
        DysonResolvedPlugin plugin)
    {
        var expectedScope = target.Scope == DysonPluginInstallScope.Project
            ? DysonPluginStorageValues.ProjectScope
            : DysonPluginStorageValues.GlobalScope;
        if (!string.Equals(installation.InstallScope, expectedScope, StringComparison.Ordinal) ||
            installation.WorkDirectoryId != target.WorkDirectoryId)
        {
            return VoidResult<string>.AsError(
                "Plugin updates cannot change the installation scope or owning work directory.");
        }
        if (!string.Equals(installation.NormalizedPluginId, plugin.Manifest.NormalizedId, StringComparison.Ordinal))
        {
            return VoidResult<string>.AsError(
                "Plugin updates cannot change the installed plugin identity.");
        }

        return DysonPluginPaths.ValidatePackageRootOwnership(target, installation.PackageRoot);
    }

    private static string GetVersionOrContentId(DysonResolvedPlugin plugin, string checksum)
    {
        var version = plugin.Manifest.Version?.Trim();
        if (!string.IsNullOrWhiteSpace(version) && version.Length <= 128 &&
            version.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'))
        {
            return version;
        }
        return "sha256-" + checksum["sha256:".Length..][..20];
    }

    private static DysonPluginInstallationEntity CreateInstallationEntity(
        DysonResolvedPlugin plugin,
        DysonPluginInstallTarget target,
        string packageRoot,
        DateTime now) =>
        new()
        {
            NormalizedPluginId = plugin.Manifest.NormalizedId,
            DisplayName = plugin.Manifest.DisplayName,
            Version = plugin.Manifest.Version,
            SourceKind = plugin.Source.Kind.ToString(),
            SourceLocation = plugin.Source.Location,
            RequestedRef = plugin.Source.RequestedRef,
            SourceSubdirectory = plugin.Source.Subdirectory,
            ResolvedCommit = plugin.Source.ResolvedCommit,
            ContentChecksum = plugin.Source.ContentChecksum,
            PackageFormat = plugin.Format.ToString(),
            SchemaVersion = plugin.Manifest.SchemaVersion,
            InstallScope = target.Scope.ToString(),
            WorkDirectoryId = target.WorkDirectoryId,
            IsEnabled = true,
            Status = DysonPluginStatus.Installed.ToString(),
            PackageRoot = packageRoot,
            ComponentInventoryJson = JsonSerializer.Serialize(plugin.Components, StorageJsonOptions),
            ConfigurationSchemaJson = plugin.ConfigurationSchemaJson,
            DiagnosticsJson = JsonSerializer.Serialize(plugin.Diagnostics, StorageJsonOptions),
            InstalledUtc = now,
            UpdatedUtc = now,
        };

    private void PruneExpiredPreviews()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _previews)
        {
            if (now - pair.Value.Preview.CreatedUtc > _limits.PreviewRetention)
                RemovePreview(pair.Key, pair.Value);
        }
    }

    private void RemovePreview(Guid id, RetainedPreview retained)
    {
        if (_previews.TryRemove(new KeyValuePair<Guid, RetainedPreview>(id, retained)))
        {
            retained.InstallGate.Dispose();
            DysonPluginPackageSecurity.TryDeleteDirectory(retained.ContainerRoot);
        }
    }

    private static Result<T, string> FailPreview<T>(string container, string error)
    {
        DysonPluginPackageSecurity.TryDeleteDirectory(container);
        return Result<T, string>.AsError(error);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record AcquiredPackage(
        string PackageRoot,
        string? RequestedRef,
        string? ResolvedCommit,
        string? UrlSubdirectory);

    private sealed record GitHubSource(
        string Owner,
        string Repository,
        string? Ref,
        string? Subdirectory);

    private sealed class RetainedPreview(
        DysonPluginPreview preview,
        string containerRoot,
        string checksum)
    {
        public DysonPluginPreview Preview { get; } = preview;
        public string ContainerRoot { get; } = containerRoot;
        public string Checksum { get; } = checksum;
        public SemaphoreSlim InstallGate { get; } = new(1, 1);
        public bool Installed { get; set; }
    }
}
