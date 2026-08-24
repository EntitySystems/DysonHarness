using DysonHarness;

namespace Harness.Tests;

public class DysonGeneratedImageArtifactTests
{
    [Fact]
    public void TryCreate_AcceptsOnlySafePngMetadata()
    {
        var artifact = CreateArtifact();

        Assert.Equal(".dyson/image-gen/20260821-123456789-02.png", artifact.RelativePath);
        Assert.Equal("20260821-123456789-02.png", artifact.FileName);
        Assert.Equal("image/png", artifact.MimeType);
        Assert.Equal(1536, artifact.Width);
        Assert.Equal(1024, artifact.Height);
        Assert.Equal(42_000, artifact.ByteLength);
        Assert.Equal("GPT Image 1", artifact.ModelLabel);
        Assert.Equal("gpt-image-1", artifact.ModelSlug);
    }

    [Theory]
    [InlineData(".dyson/image-gen/../outside.png", "outside.png")]
    [InlineData(".dyson/image-gen\\outside.png", "outside.png")]
    [InlineData("/tmp/outside.png", "outside.png")]
    [InlineData(".dyson/temp/outside.png", "outside.png")]
    [InlineData(".dyson/image-gen/not-a-png.jpg", "not-a-png.jpg")]
    [InlineData(".dyson/image-gen/example.png", "different.png")]
    public void TryCreate_RejectsUnsafeOrMismatchedPaths(string path, string fileName)
    {
        var result = DysonGeneratedImageArtifact.TryCreate(
            path,
            fileName,
            "image/png",
            1,
            1,
            1,
            "GPT Image 1",
            "gpt-image-1");

        Assert.True(result.IsError, result.IsError ? result.Error : "Expected artifact validation to fail.");
    }

    [Fact]
    public void TurnState_PersistsArtifactsWithoutBinaryOrPreviewPayloads()
    {
        var artifact = CreateArtifact();
        var call = new DysonToolCall
        {
            CallId = "image-1",
            ToolName = "GenerateImage",
            Stage = 1,
            ArgumentsJson = "{\"prompt\":\"A safe test image\"}",
        };
        var result = new DysonToolCallResult
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            Content = "{\"artifactCount\":1}",
            BinaryAttachment = new DysonBinaryAttachment
            {
                FileName = "ephemeral.png",
                Extension = ".png",
                MimeType = "image/png",
                Base64Data = "ephemeral-base64-data",
            },
            GeneratedImageArtifacts = [artifact],
        };
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "Create an image.",
            AssistantText = "Saved the image.",
            StartedUtc = DateTime.UtcNow,
        };
        turn.ToolCalls.Add(call);
        turn.RestoreTrackedCalls(
        [
            new DysonPersistedTrackedToolCall
            {
                CallId = call.CallId,
                Status = DysonToolCallStatus.Completed,
                Result = result,
            },
        ]);
        turn.RestoreResponseLog([result]);

        var withoutAttachment = result.WithoutBinaryAttachment();
        Assert.Null(withoutAttachment.BinaryAttachment);
        Assert.Single(withoutAttachment.GeneratedImageArtifacts);

        var persisted = DysonTurnToolStateSerializer.CaptureFromTurn(turn);
        Assert.Contains("generatedImageArtifacts", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("base64Data", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ephemeral-base64-data", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("previewId", persisted, StringComparison.OrdinalIgnoreCase);

        var restoredState = DysonTurnToolStateSerializer.Deserialize(persisted);
        AssertArtifact(restoredState.ResponseLog.Single().GeneratedImageArtifacts.Single());
        AssertArtifact(restoredState.Tracked.Single().Result!.GeneratedImageArtifacts.Single());

        var restoredTurn = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal };
        DysonTurnToolStateSerializer.ApplyToTurn(restoredTurn, persisted);
        AssertArtifact(restoredTurn.ResponseLog.Single().GeneratedImageArtifacts.Single());
        AssertArtifact(restoredTurn.TrackedToolCalls.Single().Result!.GeneratedImageArtifacts.Single());
    }

    [Fact]
    public void TurnState_DropsUnsafeOrUnknownPersistedArtifactProperties()
    {
        var artifact = CreateArtifact();
        var state = new DysonTurnToolState
        {
            ResponseLog =
            [
                new DysonToolCallResult
                {
                    CallId = "image-1",
                    ToolName = "GenerateImage",
                    Stage = 1,
                    GeneratedImageArtifacts = [artifact],
                },
            ],
        };
        var persisted = DysonTurnToolStateSerializer.Serialize(state)
            .Replace(
                "\"modelSlug\":\"gpt-image-1\"",
                "\"base64Data\":\"must-not-survive\",\"previewId\":\"ephemeral\",\"modelSlug\":\"gpt-image-1\"",
                StringComparison.Ordinal)
            .Replace(".dyson/image-gen/20260821-123456789-02.png", "../outside.png", StringComparison.Ordinal);

        var restored = DysonTurnToolStateSerializer.Deserialize(persisted);
        Assert.Empty(restored.ResponseLog.Single().GeneratedImageArtifacts);

        var reserialized = DysonTurnToolStateSerializer.Serialize(restored);
        Assert.DoesNotContain("base64Data", reserialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("previewId", reserialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outside.png", reserialized, StringComparison.Ordinal);

        var directUnsafe = DysonTurnToolStateSerializer.Serialize(new DysonTurnToolState
        {
            ResponseLog =
            [
                new DysonToolCallResult
                {
                    CallId = "image-unsafe",
                    ToolName = "GenerateImage",
                    Stage = 1,
                    GeneratedImageArtifacts =
                    [
                        new DysonGeneratedImageArtifact
                        {
                            RelativePath = "../outside.png",
                            FileName = "outside.png",
                            MimeType = "image/png",
                            Width = 1,
                            Height = 1,
                            ByteLength = 1,
                            ModelLabel = "GPT Image 1",
                            ModelSlug = "gpt-image-1",
                        },
                    ],
                },
            ],
        });
        Assert.DoesNotContain("outside.png", directUnsafe, StringComparison.Ordinal);
    }

    private static DysonGeneratedImageArtifact CreateArtifact()
    {
        var created = DysonGeneratedImageArtifact.TryCreate(
            ".dyson/image-gen/20260821-123456789-02.png",
            "20260821-123456789-02.png",
            "image/png",
            1536,
            1024,
            42_000,
            "GPT Image 1",
            "gpt-image-1");
        Assert.False(created.IsError, created.IsError ? created.Error : "Artifact creation failed.");
        return created.Value;
    }

    private static void AssertArtifact(DysonGeneratedImageArtifact artifact)
    {
        Assert.Equal(".dyson/image-gen/20260821-123456789-02.png", artifact.RelativePath);
        Assert.Equal("20260821-123456789-02.png", artifact.FileName);
        Assert.Equal("image/png", artifact.MimeType);
        Assert.Equal(1536, artifact.Width);
        Assert.Equal(1024, artifact.Height);
        Assert.Equal(42_000, artifact.ByteLength);
        Assert.Equal("GPT Image 1", artifact.ModelLabel);
        Assert.Equal("gpt-image-1", artifact.ModelSlug);
    }
}
