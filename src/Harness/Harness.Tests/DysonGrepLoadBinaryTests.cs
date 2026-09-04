using System.Text.Json;
using System.Text.Json.Nodes;

using DysonHarness;
using ImageMagick;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert-only Grep text-only / binary path-only + LoadBinary filename+ext multimodal wiring.
/// 
/// </summary>
public class DysonGrepLoadBinaryTests
{
    [Fact]
    public async Task Run()
    {
        AssertCatalog();
        AssertMimeMap();
        await AssertGrepTextAndBinaryPaths();
        await AssertLoadBinaryAttachmentAndTranscript();
        await AssertLoadBinaryNormalizesIcoAndBmpToPng();
        AssertRemoteUrlLoadBinaryImageUsesHttpsAndReemits();
    }

    private static void AssertCatalog()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        if (!pipeline.Tools.TryGetValue("Grep", out var grep)
            || !grep.Description.Contains("Text-only", StringComparison.Ordinal)
            || !grep.Description.Contains("LoadBinary", StringComparison.Ordinal)
            || !grep.Description.Contains("System.Text.RegularExpressions", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Grep catalog must describe text-only + LoadBinary + System.Text.RegularExpressions.");
        }

        if (!grep.InputSchemaJson.Contains("Not a literal", StringComparison.Ordinal)
            || !grep.InputSchemaJson.Contains("filename-only", StringComparison.Ordinal)
            || !grep.InputSchemaJson.Contains("default 100", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Grep InputSchemaJson must contain Not a literal, filename-only, and default 100.");
        }

        if (!pipeline.Tools.TryGetValue("LoadBinary", out var load)
            || !load.Description.Contains("filename", StringComparison.OrdinalIgnoreCase)
            || !load.InputSchemaJson.Contains("\"path\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("LoadBinary must be in the MCP catalog with path arg.");
        }
    }

    private static void AssertMimeMap()
    {
        if (DysonWorkspaceToolExecutor.MimeTypeFromExtension(".png") != "image/png")
            throw new InvalidOperationException("MimeTypeFromExtension(.png) must be image/png.");
        if (DysonWorkspaceToolExecutor.MimeTypeFromExtension(".DLL") != "application/octet-stream")
            throw new InvalidOperationException("MimeTypeFromExtension(.DLL) must be octet-stream.");
        if (DysonWorkspaceToolExecutor.MimeTypeFromExtension(".weird") != "application/octet-stream")
            throw new InvalidOperationException("Unknown extension must be octet-stream.");
    }

    private static async Task AssertGrepTextAndBinaryPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-grep-lb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "note.txt"), "hello alpha world\nsecond line\n");
            // Fake DLL / PNG with NULs so extension + sniff both treat as non-text.
            File.WriteAllBytes(Path.Combine(root, "alpha.dll"), [0x4D, 0x5A, 0x00, 0x01, 0x02]);
            File.WriteAllBytes(Path.Combine(root, "alpha.png"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0x0A]);
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            File.WriteAllText(Path.Combine(root, "bin", "secret.txt"), "alpha must not appear from bin");

            var session = new StubSession();
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, root, new HttpClient());
            var call = new DysonToolCall
            {
                CallId = "grep1",
                ToolName = "Grep",
                Stage = 0,
                ArgumentsJson = """{"pattern":"alpha","path":"."}""",
            };

            var result = executor.ExecuteAsync(call).GetAwaiter().GetResult();
            if (result.IsError)
                throw new InvalidOperationException($"Grep failed: {result.Content}");

            var content = result.Content;
            if (!content.Contains("note.txt:", StringComparison.Ordinal)
                || !content.Contains("hello alpha world", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Grep must return text matches for note.txt.");
            }

            if (!content.Contains("binary\talpha.dll", StringComparison.Ordinal))
                throw new InvalidOperationException("Grep must emit path-only binary line for alpha.dll.");

            if (!content.Contains("image\talpha.png", StringComparison.Ordinal))
                throw new InvalidOperationException("Grep must emit path-only image line for alpha.png.");

            if (content.Contains("must not appear from bin", StringComparison.Ordinal))
                throw new InvalidOperationException("Grep must skip excluded bin/ directory.");

            if (content.Contains('\0') || content.Length > 8 * 1024)
                throw new InvalidOperationException("Grep result must stay small and non-binary.");

            if (!content.Contains("Use LoadBinary", StringComparison.Ordinal))
                throw new InvalidOperationException("Grep binary/image hits must hint LoadBinary.");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }

    private static async Task AssertLoadBinaryAttachmentAndTranscript()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-loadbin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string fileName = "ui-agent-shell.png";
            var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 };
            File.WriteAllBytes(Path.Combine(root, fileName), bytes);

            var session = new StubSession();
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, root, new HttpClient());
            var call = new DysonToolCall
            {
                CallId = "lb1",
                ToolName = "LoadBinary",
                Stage = 0,
                ArgumentsJson = $$"""{"path":"{{fileName}}"}""",
            };

            var gated = executor.ExecuteAsync(call).GetAwaiter().GetResult();
            AssertImageLoadBinaryGated(gated);

            var pngB64 = Convert.ToBase64String(bytes);
            var result = new DysonToolCallResult
            {
                CallId = "lb1",
                ToolName = "LoadBinary",
                Stage = 0,
                Content = JsonSerializer.Serialize(new
                {
                    path = fileName,
                    fileName,
                    extension = ".png",
                    mimeType = "image/png",
                    byteLength = bytes.Length,
                }),
                BinaryAttachment = new DysonBinaryAttachment
                {
                    FileName = fileName,
                    Extension = ".png",
                    MimeType = "image/png",
                    Base64Data = pngB64,
                },
            };

            var att = result.BinaryAttachment
                ?? throw new InvalidOperationException("Hand-built LoadBinary attachment missing.");
            if (att.FileName != fileName
                || !att.FileName.EndsWith(".png", StringComparison.Ordinal)
                || att.Extension != ".png"
                || att.MimeType != "image/png"
                || string.IsNullOrEmpty(att.Base64Data))
            {
                throw new InvalidOperationException(
                    "LoadBinary attachment must preserve filename+extension and mime.");
            }

            if (result.Content.Contains(att.Base64Data, StringComparison.Ordinal))
                throw new InvalidOperationException("LoadBinary Content must not embed base64.");

            using var ack = JsonDocument.Parse(result.Content);
            if (ack.RootElement.GetProperty("fileName").GetString() != fileName
                || ack.RootElement.GetProperty("extension").GetString() != ".png"
                || ack.RootElement.GetProperty("byteLength").GetInt32() != bytes.Length)
            {
                throw new InvalidOperationException("LoadBinary ack JSON metadata mismatch.");
            }

            var toolCall = new DysonToolCall
            {
                CallId = "lb1",
                ToolName = "LoadBinary",
                Stage = 0,
                ArgumentsJson = call.ArgumentsJson,
            };

            var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds:
                [
                    new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([toolCall], [result]),
                ]);

            var completionsJson = completions.Messages.ToJsonString();
            if (!completionsJson.Contains(fileName, StringComparison.Ordinal)
                || !completionsJson.Contains("image_url", StringComparison.Ordinal)
                || !completionsJson.Contains("data:image/png;base64,", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Completions transcript must include image part label + nested data URL.");
            }

            AssertCompletionsImageShape(completions.Messages, fileName, "data:image/png;base64,");

            var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds:
                [
                    new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([toolCall], [result]),
                ]);

            var responsesJson = responses.Input.ToJsonString();
            if (!responsesJson.Contains(fileName, StringComparison.Ordinal)
                || !responsesJson.Contains("input_image", StringComparison.Ordinal)
                || !responsesJson.Contains("data:image/png;base64,", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Responses transcript must include input_image label + data URL fallback.");
            }

            AssertResponsesImageDataUrlFallback(responses.Input, fileName, "data:image/png;base64,");

            // Mocked upload → Responses file_id preferred.
            AssertResponsesImageFileIdViaUpload(session, toolCall, result, fileName);

            // Non-image: filename on Completions file object; Responses file_id or filename+file_data
            const string dllName = "dxcompiler.dll";
            File.WriteAllBytes(Path.Combine(root, dllName), [0x4D, 0x5A, 0x00, 0x90]);
            var dllCall = new DysonToolCall
            {
                CallId = "lb2",
                ToolName = "LoadBinary",
                Stage = 0,
                ArgumentsJson = $$"""{"path":"{{dllName}}"}""",
            };
            var dllResult = executor.ExecuteAsync(dllCall).GetAwaiter().GetResult();
            if (dllResult.IsError || dllResult.BinaryAttachment is null
                || dllResult.BinaryAttachment.FileName != dllName
                || !dllResult.BinaryAttachment.FileName.EndsWith(".dll", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("LoadBinary must preserve .dll filename+extension.");
            }

            var dllCompletions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds:
                [
                    new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([dllCall], [dllResult]),
                ]);
            var dllJson = dllCompletions.Messages.ToJsonString();
            if (!dllJson.Contains(dllName, StringComparison.Ordinal)
                || !dllJson.Contains("file_data", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Completions must emit file part with filename+ext for non-image binaries.");
            }

            AssertFilenameOnCompletionsFilePart(dllCompletions.Messages, dllName);

            var dllResponsesFallback = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds:
                [
                    new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([dllCall], [dllResult]),
                ]);
            AssertResponsesInputFileFallback(dllResponsesFallback.Input, dllName);

            AssertResponsesInputFileViaUpload(session, dllCall, dllResult, dllName);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }

    private static async Task AssertLoadBinaryNormalizesIcoAndBmpToPng()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-loadbin-norm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] ico;
            using (var image = new MagickImage(MagickColors.Transparent, 16, 16))
            {
                image.Format = MagickFormat.Ico;
                ico = image.ToByteArray();
            }

            File.WriteAllBytes(Path.Combine(root, "favicon.ico"), ico);

            byte[] bmp;
            using (var image = new MagickImage(MagickColors.Blue, 40, 20))
            {
                image.Format = MagickFormat.Bmp;
                bmp = image.ToByteArray();
            }

            File.WriteAllBytes(Path.Combine(root, "sprite.bmp"), bmp);

            // Allowlisted PNG must pass through unchanged (filename + mime).
            var pngPassthrough = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 };
            File.WriteAllBytes(Path.Combine(root, "keep.png"), pngPassthrough);

            var session = new StubSession();
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, root, new HttpClient());

            var icoResult = executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "ico1",
                ToolName = "LoadBinary",
                Stage = 0,
                ArgumentsJson = """{"path":"favicon.ico"}""",
            }).GetAwaiter().GetResult();
            AssertImageLoadBinaryGated(icoResult, "ICO conversion must finish before the FileStorage gate.");

            var bmpResult = executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "bmp1",
                ToolName = "LoadBinary",
                Stage = 0,
                ArgumentsJson = """{"path":"sprite.bmp"}""",
            }).GetAwaiter().GetResult();
            AssertImageLoadBinaryGated(bmpResult, "BMP conversion must finish before the FileStorage gate.");

            var pngResult = executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "png1",
                ToolName = "LoadBinary",
                Stage = 0,
                ArgumentsJson = """{"path":"keep.png"}""",
            }).GetAwaiter().GetResult();
            AssertImageLoadBinaryGated(pngResult);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }

    private static void AssertRemoteUrlLoadBinaryImageUsesHttpsAndReemits()
    {
        const string remoteUrl = "https://s3.example.com/dyson/ui-agent-shell.png?X-Amz-Signature=lb";
        const string fileName = "ui-agent-shell.png";
        var session = new StubSession();
        var call = new DysonToolCall
        {
            CallId = "lb1",
            ToolName = "LoadBinary",
            Stage = 0,
            ArgumentsJson = $$"""{"path":"{{fileName}}"}""",
        };
        var result = new DysonToolCallResult
        {
            CallId = "lb1",
            ToolName = "LoadBinary",
            Stage = 0,
            Content = """{"fileName":"ui-agent-shell.png","extension":".png","mimeType":"image/png","byteLength":12}""",
            BinaryAttachment = new DysonBinaryAttachment
            {
                FileName = fileName,
                Extension = ".png",
                MimeType = "image/png",
                Base64Data = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
                RemoteUrl = remoteUrl,
                FileId = "file-should-not-win",
            },
        };

        var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds:
            [
                new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([call], [result]),
            ]);
        AssertCompletionsImageShape(completions.Messages, fileName, remoteUrl);
        if (completions.Messages.ToJsonString().Contains("data:image", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "LoadBinary RemoteUrl Completions must not emit data:image.");
        }

        var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds:
            [
                new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([call], [result]),
            ]);
        var imagePart = FindResponsesPart(responses.Input, "input_image")
            ?? throw new InvalidOperationException("Responses must include input_image for RemoteUrl LoadBinary.");
        if (imagePart["image_url"]?.GetValue<string>() != remoteUrl
            || imagePart["file_id"] is not null
            || imagePart["detail"]?.GetValue<string>() != "auto"
            || responses.Input.ToJsonString().Contains("data:image", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "LoadBinary RemoteUrl Responses must emit HTTPS image_url and no file_id.");
        }

        var laterCall = new DysonToolCall
        {
            CallId = "grep1",
            ToolName = "Grep",
            Stage = 1,
            ArgumentsJson = """{"pattern":"alpha"}""",
        };
        var laterResult = new DysonToolCallResult
        {
            CallId = "grep1",
            ToolName = "Grep",
            Stage = 1,
            Content = "note.txt:1:alpha",
        };
        var later = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds:
            [
                new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([call], [result]),
                new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([laterCall], [laterResult]),
            ]);
        AssertCompletionsImageShape(later.Messages, fileName, remoteUrl);

        var turn = new DysonAgentTurn
        {
            Instruction = "load the png",
            Kind = DysonAgentTurnKind.Normal,
            AssistantText = "Loaded.",
        };
        turn.ToolCalls.Add(call);
        turn.ResponseLog.Enqueue(result);
        session.AddTurnForTest(turn);

        var history = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session,
            currentUserPrompt: null,
            currentFilePaths: null);
        AssertCompletionsImageShape(history.Messages, fileName, remoteUrl);
        if (history.Messages.ToJsonString().Contains("data:image", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Completed history must re-emit LoadBinary RemoteUrl HTTPS, not data:image.");
        }
    }

    private static void AssertCompletionsImageShape(
        JsonArray messages,
        string fileName,
        string expectDataUrlPrefix)
    {
        JsonObject? imagePart = null;
        var labelOk = false;
        foreach (var node in messages)
        {
            if (node is not JsonObject msg
                || msg["role"]?.GetValue<string>() != "user"
                || msg["content"] is not JsonArray parts)
            {
                continue;
            }

            foreach (var part in parts)
            {
                if (part is not JsonObject p)
                    continue;
                if (p["type"]?.GetValue<string>() == "text"
                    && (p["text"]?.GetValue<string>()?.Contains(fileName, StringComparison.Ordinal) ?? false))
                {
                    labelOk = true;
                }

                if (string.Equals(p["type"]?.GetValue<string>(), "image_url", StringComparison.Ordinal))
                    imagePart = p;
            }
        }

        if (!labelOk)
            throw new InvalidOperationException("Completions must label the image with filename in text.");
        if (imagePart is null)
            throw new InvalidOperationException("Completions must include an image_url part.");
        if (imagePart["filename"] is not null)
            throw new InvalidOperationException("Completions image_url must not carry top-level filename.");
        if (imagePart["image_url"] is not JsonObject nested
            || nested["url"]?.GetValue<string>() is not { } url
            || !url.StartsWith(expectDataUrlPrefix, StringComparison.Ordinal)
            || nested["detail"]?.GetValue<string>() != "auto")
        {
            throw new InvalidOperationException(
                "Completions image_url must be nested { url: data URL, detail: auto }.");
        }
    }

    private static void AssertResponsesImageDataUrlFallback(
        JsonArray input,
        string fileName,
        string expectDataUrlPrefix)
    {
        var imagePart = FindResponsesPart(input, "input_image")
            ?? throw new InvalidOperationException("Responses must include an input_image part.");
        if (!HasInputTextLabel(input, fileName))
            throw new InvalidOperationException("Responses must label the image with filename in input_text.");
        if (imagePart["filename"] is not null)
            throw new InvalidOperationException("Responses input_image must not carry filename.");
        if (imagePart["file_id"] is not null)
            throw new InvalidOperationException("Fallback Responses input_image must not set file_id.");
        if (imagePart["image_url"]?.GetValue<string>() is not { } url
            || !url.StartsWith(expectDataUrlPrefix, StringComparison.Ordinal)
            || imagePart["detail"]?.GetValue<string>() != "auto")
        {
            throw new InvalidOperationException(
                "Responses fallback input_image must use top-level image_url string + detail auto.");
        }
    }

    private static void AssertResponsesImageFileIdViaUpload(
        StubSession session,
        DysonToolCall call,
        DysonToolCallResult result,
        string fileName)
    {
        var attachment = result.BinaryAttachment
            ?? throw new InvalidOperationException("Expected BinaryAttachment for upload test.");
        attachment.FileId = null;

        var handler = new StubFilesUploadHandler("file-vision-png-1", expectedPurpose: "vision");
        using var http = new HttpClient(handler);
        var provider = MakeFilesTestProvider();
        OpenAiFilesClient.EnsureBinaryFileIdsAsync(http, provider, [result]).GetAwaiter().GetResult();

        if (attachment.FileId != "file-vision-png-1" || handler.LastPurpose != "vision")
            throw new InvalidOperationException("Image upload must set FileId with purpose=vision.");

        var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds:
            [
                new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([call], [result]),
            ]);

        var imagePart = FindResponsesPart(responses.Input, "input_image")
            ?? throw new InvalidOperationException("Responses must include input_image after upload.");
        if (imagePart["file_id"]?.GetValue<string>() != "file-vision-png-1"
            || imagePart["detail"]?.GetValue<string>() != "auto"
            || imagePart["image_url"] is not null
            || imagePart["filename"] is not null
            || !HasInputTextLabel(responses.Input, fileName)
            || responses.Input.ToJsonString().Contains("data:image/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Responses with FileId must emit input_image.file_id + detail and no data URL/filename.");
        }

        attachment.FileId = null;
    }

    private static void AssertFilenameOnCompletionsFilePart(JsonArray messages, string fileName)
    {
        foreach (var node in messages)
        {
            if (node is not JsonObject msg
                || msg["role"]?.GetValue<string>() != "user"
                || msg["content"] is not JsonArray parts)
            {
                continue;
            }

            foreach (var part in parts)
            {
                if (part is JsonObject p
                    && p["file"] is JsonObject file
                    && file["filename"]?.GetValue<string>() == fileName)
                {
                    return;
                }
            }
        }

        throw new InvalidOperationException(
            "Completions non-image file part must carry filename including extension.");
    }

    private static void AssertResponsesInputFileFallback(JsonArray input, string fileName)
    {
        var filePart = FindResponsesPart(input, "input_file")
            ?? throw new InvalidOperationException("Responses must include input_file for non-image.");
        if (filePart["filename"]?.GetValue<string>() != fileName
            || filePart["file_data"] is null
            || filePart["file_id"] is not null)
        {
            throw new InvalidOperationException(
                "Responses fallback input_file must use filename + file_data (no file_id).");
        }
    }

    private static void AssertResponsesInputFileViaUpload(
        StubSession session,
        DysonToolCall call,
        DysonToolCallResult result,
        string fileName)
    {
        var attachment = result.BinaryAttachment
            ?? throw new InvalidOperationException("Expected BinaryAttachment for dll upload test.");
        attachment.FileId = null;

        var handler = new StubFilesUploadHandler("file-userdata-dll-1", expectedPurpose: "user_data");
        using var http = new HttpClient(handler);
        var provider = MakeFilesTestProvider();
        OpenAiFilesClient.EnsureBinaryFileIdsAsync(http, provider, [result]).GetAwaiter().GetResult();

        if (attachment.FileId != "file-userdata-dll-1" || handler.LastPurpose != "user_data")
            throw new InvalidOperationException("Non-image upload must set FileId with purpose=user_data.");

        var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds:
            [
                new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([call], [result]),
            ]);

        var filePart = FindResponsesPart(responses.Input, "input_file")
            ?? throw new InvalidOperationException("Responses must include input_file after upload.");
        if (filePart["file_id"]?.GetValue<string>() != "file-userdata-dll-1"
            || filePart["filename"] is not null
            || filePart["file_data"] is not null
            || !HasInputTextLabel(responses.Input, fileName))
        {
            throw new InvalidOperationException(
                "Responses with FileId must emit input_file.file_id only (label carries filename).");
        }

        attachment.FileId = null;
    }

    private static bool HasInputTextLabel(JsonArray input, string fileName)
    {
        foreach (var node in input)
        {
            if (node is not JsonObject msg || msg["content"] is not JsonArray parts)
                continue;
            foreach (var part in parts)
            {
                if (part is JsonObject p
                    && p["type"]?.GetValue<string>() == "input_text"
                    && (p["text"]?.GetValue<string>()?.Contains(fileName, StringComparison.Ordinal) ?? false))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static JsonObject? FindResponsesPart(JsonArray input, string type)
    {
        foreach (var node in input)
        {
            if (node is not JsonObject msg || msg["content"] is not JsonArray parts)
                continue;

            foreach (var part in parts)
            {
                if (part is JsonObject p
                    && string.Equals(p["type"]?.GetValue<string>(), type, StringComparison.Ordinal))
                {
                    return p;
                }
            }
        }

        return null;
    }

    private static OpenAiCompatibleAgentProvider MakeFilesTestProvider()
    {
        var entity = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "test",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-test",
            OpenAiApiMode = DysonOpenAiApiModes.Responses,
        };
        return new OpenAiCompatibleAgentProvider(
            entity,
            new DysonModelSlugEntity
            {
                Id = Guid.NewGuid(),
                ProviderId = entity.Id,
                Slug = "gpt-4o",
                DisplayAlias = "gpt-4o",
                Provider = entity,
            });
    }

    private sealed class StubFilesUploadHandler(string fileId, string expectedPurpose) : HttpMessageHandler
    {
        public string? LastPurpose { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post
                || request.RequestUri is null
                || !request.RequestUri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                {
                    Content = new StringContent("unexpected request"),
                };
            }

            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    if (part.Headers.ContentDisposition?.Name?.Trim('"') == "purpose")
                        LastPurpose = await part.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            _ = expectedPurpose;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"id":"{{fileId}}","object":"file","purpose":"{{LastPurpose ?? expectedPurpose}}"}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private static void AssertImageLoadBinaryGated(DysonToolCallResult result, string? extra = null)
    {
        if (!result.IsError)
            throw new InvalidOperationException(extra ?? "Image LoadBinary without FileStorage must error.");
        if (result.BinaryAttachment is not null)
            throw new InvalidOperationException("Gated LoadBinary must not set BinaryAttachment.");
        if (result.Content.Contains("could not convert", StringComparison.Ordinal))
            throw new InvalidOperationException(extra ?? "Image conversion failed before the FileStorage gate.");
        if (!result.Content.Contains(DysonS3FileStorage.FileStorageRequiredToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected file_storage_required, got: {result.Content}");
        }
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Explore,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
            IReadOnlyList<string>? contextFiles = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
