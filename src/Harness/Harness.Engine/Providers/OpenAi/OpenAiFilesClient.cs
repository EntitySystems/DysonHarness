using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>POST /files (multipart) for Responses vision / input_file ids.</summary>
public sealed class OpenAiFilesClient(HttpClient http)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>
    /// Uploads bytes to <c>{BaseUrl}/files</c>. Images use <c>purpose=vision</c>;
    /// non-images use <c>purpose=user_data</c>.
    /// </summary>
    public async Task<Result<string, string>> UploadAsync(
        OpenAiCompatibleAgentProvider provider,
        string fileName,
        string mimeType,
        byte[] bytes,
        string purpose,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(bytes);

        var baseUrl = OpenAiCompatibleHttp.NormalizeBaseUrl(provider.BaseUrl);
        var url = $"{baseUrl}/files";

        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(purpose.Trim()), "purpose");

            var fileContent = new ByteArrayContent(bytes);
            var media = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType.Trim();
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(media);
            form.Add(fileContent, "file", fileName);

            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            OpenAiCompatibleHttp.ApplyBearerAuth(request, provider.ApiKey);

            using var response = await _http
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = text.Length > 800 ? text[..800] + "…" : text;
                return Result<string, string>.AsError(
                    $"OpenAI Files {(int)response.StatusCode} {response.ReasonPhrase}: {snippet}");
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                return Result<string, string>.AsError($"Invalid JSON from Files API: {ex.Message}");
            }

            var id = parsed?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
                return Result<string, string>.AsError("Files API response missing id.");

            return Result<string, string>.AsValue(id.Trim());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<string, string>.AsError("Files upload was cancelled.");
        }
        catch (HttpRequestException ex)
        {
            return Result<string, string>.AsError($"Files upload HTTP error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Files upload failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Uploads missing <see cref="DysonBinaryAttachment.FileId"/> values for Responses.
    /// Failures leave <see cref="DysonBinaryAttachment.FileId"/> unset (data-URL fallback).
    /// </summary>
    public static async Task EnsureBinaryFileIdsAsync(
        HttpClient http,
        OpenAiCompatibleAgentProvider provider,
        IEnumerable<DysonToolCallResult> results,
        Action<string>? onNote = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(results);

        OpenAiFilesClient? client = null;
        foreach (var result in results)
        {
            if (result.IsError || result.BinaryAttachment is not { } attachment)
                continue;
            if (!string.IsNullOrEmpty(attachment.FileId))
                continue;

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(attachment.Base64Data);
            }
            catch (FormatException ex)
            {
                onNote?.Invoke($"Files upload skipped for {attachment.FileName}: invalid base64 ({ex.Message})");
                continue;
            }

            client ??= new OpenAiFilesClient(http);
            var purpose = attachment.IsImage ? "vision" : "user_data";
            var upload = await client
                .UploadAsync(
                    provider,
                    attachment.FileName,
                    attachment.MimeType,
                    bytes,
                    purpose,
                    cancellationToken)
                .ConfigureAwait(false);

            if (upload.IsError)
            {
                onNote?.Invoke($"Files upload failed for {attachment.FileName} (purpose={purpose}): {upload.Error}");
                continue;
            }

            attachment.FileId = upload.Value;
        }
    }
}
