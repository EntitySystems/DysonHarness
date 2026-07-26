using System.Text.Json;
using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert-only Grep text-only / binary path-only + LoadBinary filename+ext multimodal wiring.
/// 
/// </summary>
public class DysonGrepLoadBinaryTests
{
    [Fact]
    public void Run()
    {
        AssertCatalog();
        AssertMimeMap();
        AssertGrepTextAndBinaryPaths();
        AssertLoadBinaryAttachmentAndTranscript();
    }

    private static void AssertCatalog()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        if (!pipeline.Tools.TryGetValue("Grep", out var grep)
            || !grep.Description.Contains("Text-only", StringComparison.Ordinal)
            || !grep.Description.Contains("LoadBinary", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Grep catalog must describe text-only + LoadBinary.");
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

    private static void AssertGrepTextAndBinaryPaths()
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
            var executor = new DysonWorkspaceToolExecutor(session, root, new HttpClient());
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

    private static void AssertLoadBinaryAttachmentAndTranscript()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-loadbin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string fileName = "ui-agent-shell.png";
            var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 };
            File.WriteAllBytes(Path.Combine(root, fileName), bytes);

            var session = new StubSession();
            var executor = new DysonWorkspaceToolExecutor(session, root, new HttpClient());
            var call = new DysonToolCall
            {
                CallId = "lb1",
                ToolName = "LoadBinary",
                Stage = 0,
                ArgumentsJson = $$"""{"path":"{{fileName}}"}""",
            };

            var result = executor.ExecuteAsync(call).GetAwaiter().GetResult();
            if (result.IsError)
                throw new InvalidOperationException($"LoadBinary failed: {result.Content}");

            if (result.BinaryAttachment is null)
                throw new InvalidOperationException("LoadBinary must set BinaryAttachment.");

            var att = result.BinaryAttachment;
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
                    "Completions transcript must include image part with filename + data URL.");
            }

            AssertFilenameOnCompletionsMediaPart(completions.Messages, fileName);

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
                    "Responses transcript must include input_image with filename + data URL.");
            }

            AssertFilenameOnResponsesMediaPart(responses.Input, fileName);

            // Non-image: filename on input_file / file.file_data
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

    private static void AssertFilenameOnCompletionsMediaPart(JsonArray messages, string fileName)
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
                if (part is not JsonObject p)
                    continue;
                if (p["filename"]?.GetValue<string>() == fileName)
                    return;
                if (p["file"] is JsonObject file
                    && file["filename"]?.GetValue<string>() == fileName)
                {
                    return;
                }
            }
        }

        throw new InvalidOperationException(
            "Completions multimodal part must carry filename including extension.");
    }

    private static void AssertFilenameOnResponsesMediaPart(JsonArray input, string fileName)
    {
        foreach (var node in input)
        {
            if (node is not JsonObject msg || msg["content"] is not JsonArray parts)
                continue;

            foreach (var part in parts)
            {
                if (part is JsonObject p && p["filename"]?.GetValue<string>() == fileName)
                    return;
            }
        }

        throw new InvalidOperationException(
            "Responses multimodal part must carry filename including extension.");
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Explore,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
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
