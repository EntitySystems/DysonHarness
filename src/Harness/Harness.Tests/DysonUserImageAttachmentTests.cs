using System.Text.Json.Nodes;

using DysonHarness;
using ImageMagick;

namespace Harness.Tests;

/// <summary>
/// ponytail: composer user images persist + Completions/Responses multimodal parts.
/// </summary>
public class DysonUserImageAttachmentTests
{
    [Fact]
    public void Run()
    {
        AssertFactoryCompresses();
        AssertPersistenceRoundTrip();
        AssertHtmlRefRoundTrip();
        AssertRemoteFieldsRoundTrip();
        AssertDipSelectionMapsToPixelRect();
        AssertCompletionsTranscriptIncludesImageUrl();
        AssertResponsesTranscriptIncludesInputImage();
        AssertRemoteUrlUsesHttpsAndWinsOverFileId();
    }

    [Fact]
    public void MapDipSelectionToPixelRect_OneByOneShot_ClampsToOnePixel()
    {
        var mapped = DysonBrowserSnipCrop.MapDipSelectionToPixelRect(
            selectionX: 10,
            selectionY: 20,
            selectionWidth: 400,
            selectionHeight: 300,
            contentWidthDip: 800,
            contentHeightDip: 600,
            shotWidthPx: 1,
            shotHeightPx: 1);
        if (mapped is not { } rect
            || rect.X != 0
            || rect.Y != 0
            || rect.Width != 1
            || rect.Height != 1)
        {
            throw new InvalidOperationException(
                $"Collapsed-HWND 1×1 shot must clamp a large DIP selection to 1×1, got {mapped}.");
        }
    }

    [Fact]
    public void FormatPromptLine_UrlAndPercentCases()
    {
        var both = DysonBrowserSnipCrop.FormatPromptLine("https://example.com/page", 25);
        if (both != "Snip: https://example.com/page · 25% down the page")
            throw new InvalidOperationException($"url+percent mismatch: {both}");

        var urlOnly = DysonBrowserSnipCrop.FormatPromptLine("https://example.com/page", null);
        if (urlOnly != "Snip: https://example.com/page")
            throw new InvalidOperationException($"url-only mismatch: {urlOnly}");

        var percentOnly = DysonBrowserSnipCrop.FormatPromptLine("  ", 40);
        if (percentOnly != "Snip: 40% down the page")
            throw new InvalidOperationException($"percent-only mismatch: {percentOnly}");

        if (DysonBrowserSnipCrop.FormatPromptLine(null, null) is not null)
            throw new InvalidOperationException("empty url+percent must return null.");
        if (DysonBrowserSnipCrop.FormatPromptLine("", null) is not null)
            throw new InvalidOperationException("blank url without percent must return null.");

        if (DysonBrowserSnipCrop.PercentDownThePage(250, 1000) != 25)
            throw new InvalidOperationException("250/1000 must be 25%.");
        if (DysonBrowserSnipCrop.PercentDownThePage(-10, 100) != 0)
            throw new InvalidOperationException("negative documentY must clamp to 0.");
        if (DysonBrowserSnipCrop.PercentDownThePage(200, 100) != 100)
            throw new InvalidOperationException("past end must clamp to 100.");
        if (DysonBrowserSnipCrop.PercentDownThePage(0, 0) != 0)
            throw new InvalidOperationException("zero scrollHeight must floor at 1 (0%).");

        var documentY = DysonBrowserSnipCrop.DocumentY(
            scrollY: 200,
            selectionY: 50,
            contentHeightDip: 400,
            viewportHeight: 400);
        if (Math.Abs(documentY - 250) > 0.001)
            throw new InvalidOperationException($"documentY expected 250, got {documentY}.");
        if (DysonBrowserSnipCrop.PercentDownThePage(documentY, 1000) != 25)
            throw new InvalidOperationException("documentY 250 / scrollHeight 1000 must be 25%.");
    }

    private static byte[] TinyPng()
    {
        using var image = new MagickImage(MagickColors.Red, 32, 24);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    private static void AssertFactoryCompresses()
    {
        var created = DysonUserImageFactory.CreateFromBytes("shot.png", TinyPng());
        if (created.IsError)
            throw new InvalidOperationException(created.Error);
        if (created.Value.MimeType != "image/jpeg"
            || !created.Value.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(created.Value.Base64Data))
        {
            throw new InvalidOperationException("Factory must emit compressed JPEG attachment.");
        }

        var dataUrl = $"data:image/png;base64,{Convert.ToBase64String(TinyPng())}";
        var fromUrl = DysonUserImageFactory.CreateFromDataUrl("clip.png", dataUrl);
        if (fromUrl.IsError || fromUrl.Value.MimeType != "image/jpeg")
            throw new InvalidOperationException("Data URL path must compress to JPEG.");
    }

    private static void AssertPersistenceRoundTrip()
    {
        var created = DysonUserImageFactory.CreateFromBytes("photo.png", TinyPng());
        if (created.IsError)
            throw new InvalidOperationException(created.Error);

        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "what is in this image?",
            StartedUtc = DateTime.UtcNow,
        };
        turn.AddUserImage(created.Value);

        var entity = DysonTurnPersistence.ToEntity(turn, Guid.NewGuid(), sequence: 0);
        if (string.IsNullOrWhiteSpace(entity.UserImagesJson))
            throw new InvalidOperationException("ToEntity must serialize UserImagesJson.");

        var restored = new DysonAgentTurn { Id = entity.Id, Kind = entity.Kind };
        restored.RestoreUserImages(DysonUserImagesSerializer.Deserialize(entity.UserImagesJson));
        if (restored.UserImages.Count != 1
            || restored.UserImages[0].MimeType != "image/jpeg"
            || restored.UserImages[0].Base64Data != created.Value.Base64Data
            || restored.UserImages[0].FileId is not null)
        {
            throw new InvalidOperationException("UserImagesJson round-trip lost fields.");
        }
    }

    private static void AssertHtmlRefRoundTrip()
    {
        var created = DysonUserImageFactory.CreateFromBytes("snip.png", TinyPng());
        if (created.IsError)
            throw new InvalidOperationException(created.Error);

        var withRef = new DysonBinaryAttachment
        {
            FileName = created.Value.FileName,
            Extension = created.Value.Extension,
            MimeType = created.Value.MimeType,
            Base64Data = created.Value.Base64Data,
            HtmlRef = "  #future-dom-ref  ",
        };

        var json = DysonUserImagesSerializer.Serialize([withRef]);
        var restored = DysonUserImagesSerializer.Deserialize(json);
        if (restored.Count != 1 || restored[0].HtmlRef != "#future-dom-ref")
            throw new InvalidOperationException("HtmlRef must round-trip (trimmed) through UserImagesJson.");

        var emptyRef = new DysonBinaryAttachment
        {
            FileName = created.Value.FileName,
            Extension = created.Value.Extension,
            MimeType = created.Value.MimeType,
            Base64Data = created.Value.Base64Data,
            HtmlRef = "",
        };
        var emptyJson = DysonUserImagesSerializer.Serialize([emptyRef]);
        if (emptyJson is not null && emptyJson.Contains("htmlRef", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Empty HtmlRef should be omitted from JSON.");
    }

    private static void AssertRemoteFieldsRoundTrip()
    {
        var created = DysonUserImageFactory.CreateFromBytes("photo.png", TinyPng());
        if (created.IsError)
            throw new InvalidOperationException(created.Error);

        var expires = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        var withRemote = new DysonBinaryAttachment
        {
            FileName = created.Value.FileName,
            Extension = created.Value.Extension,
            MimeType = created.Value.MimeType,
            Base64Data = created.Value.Base64Data,
            RemoteUrl = "https://s3.example.com/dyson/shot.jpg?X-Amz-Signature=abc",
            ObjectKey = "dyson/2026/09/abc-shot.jpg",
            RemoteUrlExpiresUtc = expires,
        };

        var json = DysonUserImagesSerializer.Serialize([withRemote]);
        if (json is null
            || !json.Contains("remoteUrl", StringComparison.Ordinal)
            || !json.Contains("objectKey", StringComparison.Ordinal)
            || !json.Contains("remoteUrlExpiresUtc", StringComparison.Ordinal)
            || !json.Contains("base64Data", StringComparison.Ordinal)
            || !json.Contains(created.Value.Base64Data, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("UserImagesJson must persist remote fields and Base64Data.");
        }

        var restored = DysonUserImagesSerializer.Deserialize(json);
        if (restored.Count != 1
            || restored[0].Base64Data != created.Value.Base64Data
            || restored[0].RemoteUrl != withRemote.RemoteUrl
            || restored[0].ObjectKey != withRemote.ObjectKey
            || restored[0].RemoteUrlExpiresUtc != expires)
        {
            throw new InvalidOperationException("RemoteUrl fields must round-trip with Base64Data.");
        }

        var noRemote = new DysonBinaryAttachment
        {
            FileName = created.Value.FileName,
            Extension = created.Value.Extension,
            MimeType = created.Value.MimeType,
            Base64Data = created.Value.Base64Data,
            RemoteUrl = "",
            ObjectKey = "  ",
        };
        var omitted = DysonUserImagesSerializer.Serialize([noRemote]);
        if (omitted is not null
            && (omitted.Contains("remoteUrl", StringComparison.Ordinal)
                || omitted.Contains("objectKey", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Empty RemoteUrl/ObjectKey should be omitted from JSON.");
        }
    }

    private static void AssertDipSelectionMapsToPixelRect()
    {
        var mapped = DysonBrowserSnipCrop.MapDipSelectionToPixelRect(
            selectionX: 10,
            selectionY: 20,
            selectionWidth: 100,
            selectionHeight: 50,
            contentWidthDip: 200,
            contentHeightDip: 100,
            shotWidthPx: 400,
            shotHeightPx: 200);
        if (mapped is not { } rect
            || rect.X != 20
            || rect.Y != 40
            || rect.Width != 200
            || rect.Height != 100)
        {
            throw new InvalidOperationException(
                $"DIP→pixel map mismatch: got {mapped}.");
        }

        if (DysonBrowserSnipCrop.MapDipSelectionToPixelRect(0, 0, 0, 10, 100, 100, 100, 100) is not null)
            throw new InvalidOperationException("Zero-width selection must return null.");
    }

    private static void AssertCompletionsTranscriptIncludesImageUrl()
    {
        var created = DysonUserImageFactory.CreateFromBytes("ui.png", TinyPng());
        if (created.IsError)
            throw new InvalidOperationException(created.Error);

        var session = new StubSession();
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "describe",
            StartedUtc = DateTime.UtcNow,
            AssistantText = "# Done\n\nok",
            AgentTitle = "Done",
        };
        turn.AddUserImage(created.Value);
        session.AddTurnForTest(turn);

        var built = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session,
            currentUserPrompt: null,
            currentFilePaths: null);

        JsonObject? userMsg = null;
        foreach (var node in built.Messages)
        {
            if (node is not JsonObject msg)
                continue;
            if (msg["role"]?.GetValue<string>() != "user")
                continue;
            if (msg["content"] is JsonArray)
            {
                userMsg = msg;
                break;
            }
        }

        if (userMsg?["content"] is not JsonArray parts || parts.Count < 2)
            throw new InvalidOperationException("Completions user message must be multimodal parts.");

        var hasImage = false;
        foreach (var part in parts)
        {
            if (part is not JsonObject obj)
                continue;
            if (obj["type"]?.GetValue<string>() != "image_url")
                continue;
            var url = obj["image_url"]?["url"]?.GetValue<string>();
            if (url is not null
                && url.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase))
            {
                hasImage = true;
                break;
            }
        }

        if (!hasImage)
            throw new InvalidOperationException("Completions must include nested image_url data URL.");
    }

    private static void AssertResponsesTranscriptIncludesInputImage()
    {
        var created = DysonUserImageFactory.CreateFromBytes("ui.png", TinyPng());
        if (created.IsError)
            throw new InvalidOperationException(created.Error);

        created.Value.FileId = "file-user-img";

        var session = new StubSession();
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "describe",
            StartedUtc = DateTime.UtcNow,
            AssistantText = "# Done\n\nok",
            AgentTitle = "Done",
        };
        turn.AddUserImage(created.Value);
        session.AddTurnForTest(turn);

        var built = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null);

        JsonObject? userMsg = null;
        foreach (var node in built.Input)
        {
            if (node is not JsonObject msg)
                continue;
            if (msg["role"]?.GetValue<string>() != "user")
                continue;
            if (msg["content"] is JsonArray)
            {
                userMsg = msg;
                break;
            }
        }

        if (userMsg?["content"] is not JsonArray parts || parts.Count < 2)
            throw new InvalidOperationException("Responses user message must be multimodal parts.");

        var hasImage = false;
        foreach (var part in parts)
        {
            if (part is not JsonObject obj)
                continue;
            if (obj["type"]?.GetValue<string>() != "input_image")
                continue;
            if (obj["file_id"]?.GetValue<string>() == "file-user-img")
            {
                hasImage = true;
                break;
            }
        }

        if (!hasImage)
            throw new InvalidOperationException("Responses must prefer input_image.file_id for user images.");
    }

    private static void AssertRemoteUrlUsesHttpsAndWinsOverFileId()
    {
        var created = DysonUserImageFactory.CreateFromBytes("ui.png", TinyPng());
        if (created.IsError)
            throw new InvalidOperationException(created.Error);

        const string remoteUrl = "https://s3.example.com/dyson/ui.jpg?X-Amz-Signature=xyz";
        var withRemote = new DysonBinaryAttachment
        {
            FileName = created.Value.FileName,
            Extension = created.Value.Extension,
            MimeType = created.Value.MimeType,
            Base64Data = created.Value.Base64Data,
            RemoteUrl = "  " + remoteUrl + "  ",
            FileId = "file-user-img",
        };

        var session = new StubSession();
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "describe",
            StartedUtc = DateTime.UtcNow,
            AssistantText = "# Done\n\nok",
            AgentTitle = "Done",
        };
        turn.AddUserImage(withRemote);
        session.AddTurnForTest(turn);

        var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session,
            currentUserPrompt: null,
            currentFilePaths: null);
        var completionsJson = completions.Messages.ToJsonString();
        if (!completionsJson.Contains(remoteUrl, StringComparison.Ordinal)
            || completionsJson.Contains("data:image", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Completions UserImages with RemoteUrl must emit HTTPS and no data:image.");
        }

        JsonObject? completionsImage = null;
        foreach (var node in completions.Messages)
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
                    && string.Equals(p["type"]?.GetValue<string>(), "image_url", StringComparison.Ordinal))
                {
                    completionsImage = p;
                    break;
                }
            }

            if (completionsImage is not null)
                break;
        }

        if (completionsImage?["image_url"]?["url"]?.GetValue<string>() != remoteUrl)
        {
            throw new InvalidOperationException(
                "Completions nested image_url.url must be the trimmed RemoteUrl.");
        }

        var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null);
        var responsesJson = responses.Input.ToJsonString();
        if (!responsesJson.Contains(remoteUrl, StringComparison.Ordinal)
            || responsesJson.Contains("data:image", StringComparison.Ordinal)
            || responsesJson.Contains("file-user-img", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Responses UserImages with RemoteUrl must emit HTTPS image_url and no file_id.");
        }

        JsonObject? responsesImage = null;
        foreach (var node in responses.Input)
        {
            if (node is not JsonObject msg || msg["content"] is not JsonArray parts)
                continue;
            foreach (var part in parts)
            {
                if (part is JsonObject p
                    && string.Equals(p["type"]?.GetValue<string>(), "input_image", StringComparison.Ordinal))
                {
                    responsesImage = p;
                    break;
                }
            }

            if (responsesImage is not null)
                break;
        }

        if (responsesImage is null
            || responsesImage["image_url"]?.GetValue<string>() != remoteUrl
            || responsesImage["file_id"] is not null
            || responsesImage["detail"]?.GetValue<string>() != "auto")
        {
            throw new InvalidOperationException(
                "Responses input_image must use HTTPS RemoteUrl (not file_id) when RemoteUrl is set.");
        }
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
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
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
