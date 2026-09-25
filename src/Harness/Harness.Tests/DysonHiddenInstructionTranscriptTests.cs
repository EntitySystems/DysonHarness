using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: completed-turn transcript echoes HiddenInstruction paths and the live image URL.
/// </summary>
public class DysonHiddenInstructionTranscriptTests
{
    [Fact]
    public void CompletedTurn_EmitsHiddenLocalPathAndLiveImageUrl()
    {
        const string instruction = "see the notes";
        const string localPath = ".dyson/composer-uploads/notes.txt";
        const string imagePath = ".dyson/composer-uploads/shot.jpg";
        const string remoteUrl = "https://s3.example.com/dyson/shot.jpg?X-Amz-Signature=abc";
        const string refreshedUrl = "https://s3.example.com/dyson/shot.jpg?X-Amz-Signature=refreshed";
        const string hidden = "Attached paths:\n- " + localPath;

        var image = new DysonBinaryAttachment
        {
            FileName = "shot.jpg",
            Extension = ".jpg",
            MimeType = "image/jpeg",
            Base64Data = "abc",
            RemoteUrl = remoteUrl,
        };

        var session = new StubSession();
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = instruction,
            HiddenInstruction = hidden,
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
            AssistantText = "# Done\n\nok",
            AgentTitle = "Done",
        };
        turn.AddUserImage(image);
        session.AddTurnForTest(turn);

        if (turn.Instruction.Contains("Attached paths:", StringComparison.Ordinal)
            || turn.Instruction.Contains("Attached urls:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Instruction itself must not contain path or url blocks.");
        }

        AssertTranscript(
            OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session,
                currentUserPrompt: null,
                currentFilePaths: null).Messages,
            turn,
            localPath,
            imagePath,
            remoteUrl,
            "Completions");
        AssertTranscript(
            OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session,
                currentUserPrompt: null,
                currentFilePaths: null).Input,
            turn,
            localPath,
            imagePath,
            remoteUrl,
            "Responses");

        image.RemoteUrl = refreshedUrl;
        var refreshed = FindTurnText(
            OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session,
                currentUserPrompt: null,
                currentFilePaths: null).Messages,
            turn.Id);
        if (refreshed.Contains(remoteUrl, StringComparison.Ordinal)
            || !refreshed.Contains("- shot.jpg " + refreshedUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Attached urls must use the attachment's current RemoteUrl.");
        }

        if (turn.HiddenInstruction != hidden
            || turn.HiddenInstruction.Contains(refreshedUrl, StringComparison.Ordinal)
            || turn.Instruction != instruction
            || image.Base64Data != "abc")
        {
            throw new InvalidOperationException(
                "Formatter must not write URLs into HiddenInstruction or clear Base64Data.");
        }
    }

    [Fact]
    public void Round0_DoesNotDuplicatePathsAlreadyInTheUserText()
    {
        const string path = ".dyson/composer-uploads/notes.txt";
        var block = "Attached paths:" + Environment.NewLine + "- " + path;
        var text = "[turnId=x]\nsee the notes\n\n" + block;
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = text,
            },
        };

        OpenAiCompatibleAgentSession.AppendPathsToLastUser(messages, [path]);

        var after = messages[0]!["content"]!.GetValue<string>();
        if (after != text || after.Split("Attached paths:").Length != 2)
            throw new InvalidOperationException("Round 0 must not append a second Attached paths block.");

        var older = "older turn";
        var multimodal = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = older,
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = text,
                    },
                },
            },
        };

        OpenAiCompatibleAgentSession.AppendPathsToLastUser(multimodal, [path]);

        if (multimodal[0]!["content"]!.GetValue<string>() != older)
            throw new InvalidOperationException("Paths must not be painted onto an older user turn.");

        var part = multimodal[1]!["content"]![0]!["text"]!.GetValue<string>();
        if (part != text || part.Split("Attached paths:").Length != 2)
            throw new InvalidOperationException("Multimodal text must keep a single Attached paths block.");
    }

    private static void AssertTranscript(
        JsonArray items,
        DysonAgentTurn turn,
        string localPath,
        string imagePath,
        string remoteUrl,
        string label)
    {
        var text = FindTurnText(items, turn.Id);
        var pathsAt = text.IndexOf("Attached paths:", StringComparison.Ordinal);
        var pathAt = text.IndexOf("- " + localPath, StringComparison.Ordinal);
        var urlsAt = text.IndexOf("Attached urls:", StringComparison.Ordinal);
        var urlAt = text.IndexOf("- shot.jpg " + remoteUrl, StringComparison.Ordinal);
        if (pathsAt < 0 || pathAt < pathsAt || urlsAt < pathAt || urlAt < urlsAt)
        {
            throw new InvalidOperationException(
                $"{label}: expected path under Attached paths: and URL under Attached urls:.\n{text}");
        }

        if (!text.Contains(turn.Instruction!, StringComparison.Ordinal))
            throw new InvalidOperationException($"{label}: instruction missing from user content.");

        if (text.Contains(imagePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label}: must not include a composer-uploads image path.");
        }
    }

    private static string FindTurnText(JsonArray items, Guid turnId)
    {
        var marker = "[turnId=" + turnId.ToString("D") + "]";
        foreach (var node in items)
        {
            if (node is not JsonObject msg || msg["role"]?.GetValue<string>() != "user")
                continue;

            if (msg["content"] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && text.Contains(marker, StringComparison.Ordinal))
            {
                return text;
            }

            if (msg["content"] is not JsonArray parts)
                continue;

            foreach (var part in parts)
            {
                if (part is not JsonObject obj)
                    continue;
                var type = obj["type"]?.GetValue<string>();
                if (type is not ("text" or "input_text"))
                    continue;
                var partText = obj["text"]?.GetValue<string>();
                if (partText is not null && partText.Contains(marker, StringComparison.Ordinal))
                    return partText;
            }
        }

        throw new InvalidOperationException("Missing [turnId] user text.");
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
