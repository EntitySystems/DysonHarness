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
        AssertDipSelectionMapsToPixelRect();
        AssertCompletionsTranscriptIncludesImageUrl();
        AssertResponsesTranscriptIncludesInputImage();
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
