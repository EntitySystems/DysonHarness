using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: tool-state persist keeps slim RemoteUrl image attachments and still strips legacy JPEGs.
/// </summary>
public class DysonTurnToolStateSerializerTests
{
    [Fact]
    public void CaptureFromTurn_ImageWithRemoteUrl_PersistsSlimAttachmentWithoutJpegBytes()
    {
        const string jpegPayload = "jpeg-bytes-must-not-persist";
        var expires = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        var result = new DysonToolCallResult
        {
            CallId = "shot1",
            ToolName = "BrowserTakeScreenshot",
            Stage = 0,
            Content = """{"mimeType":"image/jpeg","byteLength":12}""",
            BinaryAttachment = new DysonBinaryAttachment
            {
                FileName = "screenshot.jpg",
                Extension = ".jpg",
                MimeType = "image/jpeg",
                Base64Data = jpegPayload,
                RemoteUrl = "https://s3.example.com/dyson/shot.jpg?X-Amz-Signature=abc",
                ObjectKey = "dyson/2026/09/abc-shot.jpg",
                RemoteUrlExpiresUtc = expires,
                HtmlRef = "#snip",
                FileId = "file-ephemeral",
            },
        };
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "look",
            AssistantText = "Saw the page.",
            StartedUtc = DateTime.UtcNow,
        };
        turn.ToolCalls.Add(new DysonToolCall
        {
            CallId = "shot1",
            ToolName = "BrowserTakeScreenshot",
            Stage = 0,
            ArgumentsJson = "{}",
        });
        turn.RestoreResponseLog([result]);
        turn.RestoreTrackedCalls(
        [
            new DysonPersistedTrackedToolCall
            {
                CallId = "shot1",
                Status = DysonToolCallStatus.Completed,
                Result = result,
            },
        ]);

        var persisted = DysonTurnToolStateSerializer.CaptureFromTurn(turn);
        if (persisted.Contains(jpegPayload, StringComparison.Ordinal))
            throw new InvalidOperationException("Slim persist must omit JPEG base64 payload.");
        if (!persisted.Contains("remoteUrl", StringComparison.Ordinal)
            || !persisted.Contains("https://s3.example.com/dyson/shot.jpg?X-Amz-Signature=abc", StringComparison.Ordinal)
            || !persisted.Contains("objectKey", StringComparison.Ordinal)
            || !persisted.Contains("dyson/2026/09/abc-shot.jpg", StringComparison.Ordinal)
            || !persisted.Contains("remoteUrlExpiresUtc", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Slim persist must keep RemoteUrl/ObjectKey/RemoteUrlExpiresUtc.");
        }

        var live = turn.ResponseLog.Single();
        if (live.BinaryAttachment is null
            || live.BinaryAttachment.Base64Data != ""
            || live.BinaryAttachment.RemoteUrl != result.BinaryAttachment!.RemoteUrl)
        {
            throw new InvalidOperationException("CaptureFromTurn must keep a slim RemoteUrl attachment on the turn.");
        }

        var restored = DysonTurnToolStateSerializer.Deserialize(persisted);
        var att = restored.ResponseLog.Single().BinaryAttachment
            ?? throw new InvalidOperationException("Restored state must keep BinaryAttachment.");
        if (att.Base64Data != ""
            || att.RemoteUrl != "https://s3.example.com/dyson/shot.jpg?X-Amz-Signature=abc"
            || att.ObjectKey != "dyson/2026/09/abc-shot.jpg"
            || att.RemoteUrlExpiresUtc != expires
            || att.FileName != "screenshot.jpg"
            || att.MimeType != "image/jpeg"
            || att.HtmlRef != "#snip"
            || att.FileId != "file-ephemeral")
        {
            throw new InvalidOperationException("Slim attachment fields lost on deserialize.");
        }

        var trackedAtt = restored.Tracked.Single().Result?.BinaryAttachment;
        if (trackedAtt?.RemoteUrl != att.RemoteUrl || trackedAtt.Base64Data != "")
            throw new InvalidOperationException("Tracked slim attachment mismatch.");
    }

    [Fact]
    public void FinishStreaming_ImageWithoutRemoteUrl_StripsBinaryAttachment()
    {
        var result = new DysonToolCallResult
        {
            CallId = "shot1",
            ToolName = "BrowserTakeScreenshot",
            Stage = 0,
            Content = """{"mimeType":"image/jpeg"}""",
            BinaryAttachment = new DysonBinaryAttachment
            {
                FileName = "screenshot.jpg",
                Extension = ".jpg",
                MimeType = "image/jpeg",
                Base64Data = "jpeg-bytes-must-not-persist",
            },
        };
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "look",
            AssistantText = "Saw the page.",
            StartedUtc = DateTime.UtcNow,
        };
        turn.RestoreResponseLog([result]);

        turn.FinishStreaming();
        if (turn.ResponseLog.Any(r => r.BinaryAttachment is not null))
            throw new InvalidOperationException("FinishStreaming must strip BinaryAttachment without RemoteUrl.");

        var persisted = DysonTurnToolStateSerializer.CaptureFromTurn(turn);
        if (persisted.Contains("base64Data", StringComparison.OrdinalIgnoreCase)
            || persisted.Contains("binaryAttachment", StringComparison.OrdinalIgnoreCase)
            || persisted.Contains("jpeg-bytes-must-not-persist", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Persisted tool state must omit BinaryAttachment after turn complete.");
        }
    }
}
