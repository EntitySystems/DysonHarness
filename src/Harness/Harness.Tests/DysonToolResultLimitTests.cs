using System.Text;
using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: TruncateContent leaves short/exact-cap strings; long strings get prefix + footer.
/// ReadFile fail-closes at 32KiB; ShellExecute/LRS clamp at 64KiB.
/// </summary>
public class DysonToolResultLimitTests
{
    [Fact]
    public async Task Run()
    {
        AssertShortUnchanged();
        AssertExactCapUnchanged();
        AssertLongTruncatedWithFooter();
        await AssertReadFileSmallSuccess();
        await AssertReadFileHugeErrorsWithoutBody();
        await AssertReadFileOffsetSlice();
        await AssertReadFileTail();
        await AssertReadFileGiantLineClipped();
        await AssertReadFileBinaryUsesLoadBinary();
        await AssertShellExecuteOverflowTruncated();
        AssertCatalogCaps();
        await AssertSubscribeClampsIncludeTailMaxChars();
    }

    private static void AssertShortUnchanged()
    {
        if (DysonToolResultLimits.TruncateContent(string.Empty) != string.Empty)
            throw new InvalidOperationException("Empty TruncateContent must return empty.");

        const string shortText = "hello";
        if (DysonToolResultLimits.TruncateContent(shortText) != shortText)
            throw new InvalidOperationException("Short TruncateContent must return the original string.");
    }

    private static void AssertExactCapUnchanged()
    {
        var exact = new string('x', DysonToolResultLimits.MaxContentChars);
        var truncated = DysonToolResultLimits.TruncateContent(exact);
        if (truncated != exact)
            throw new InvalidOperationException("Exact MaxContentChars TruncateContent must be unchanged.");
    }

    private static void AssertLongTruncatedWithFooter()
    {
        const int original = 70_000;
        var longText = new string('a', original);
        var truncated = DysonToolResultLimits.TruncateContent(longText);
        var footer =
            $"… truncated at {DysonToolResultLimits.MaxContentChars} chars (original {original}). Page with ReadFile offset/limit or a smaller shell/tail read.";

        if (!truncated.EndsWith(footer, StringComparison.Ordinal))
            throw new InvalidOperationException("Long TruncateContent must end with the truncation footer.");
        if (!truncated.Contains(original.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException("Long TruncateContent footer must contain original length.");
        if (truncated[..DysonToolResultLimits.MaxContentChars] != longText[..DysonToolResultLimits.MaxContentChars])
            throw new InvalidOperationException("Long TruncateContent prefix must be MaxContentChars of the original.");
        if (truncated.Length != DysonToolResultLimits.MaxContentChars + footer.Length)
            throw new InvalidOperationException("Long TruncateContent length must be MaxContentChars + footer.");
    }

    private static Task AssertReadFileSmallSuccess() =>
        WithTempWorkAsync(async (root, executor) =>
        {
            var body = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line-{i}"));
            File.WriteAllText(Path.Combine(root, "small.txt"), body);

            var result = await ReadFileAsync(executor, "small.txt");
            if (result.IsError)
                throw new InvalidOperationException($"Small ReadFile must succeed: {result.Content}");

            for (var i = 1; i <= 20; i++)
            {
                if (!result.Content.Contains($"{i}|line-{i}", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Small ReadFile missing {i}|line-{i}.");
            }

            if (result.Content.Contains("32KiB cap", StringComparison.Ordinal)
                || result.Content.Contains("Do not re-read", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Small ReadFile must not include the over-cap instruction.");
            }
        });

    private static Task AssertReadFileHugeErrorsWithoutBody() =>
        WithTempWorkAsync(async (root, executor) =>
        {
            var sb = new StringBuilder(220_000);
            for (var i = 1; i <= 10_000; i++)
                sb.Append("line-").Append(i).Append("-xxxxxxxx").Append('\n');
            File.WriteAllText(Path.Combine(root, "huge.txt"), sb.ToString());

            var result = await ReadFileAsync(executor, "huge.txt");
            if (!result.IsError)
                throw new InvalidOperationException("Huge ReadFile with no limit must be IsError.");
            if (result.Content.Length >= 2048)
                throw new InvalidOperationException($"Huge ReadFile error must be <2KB, got {result.Content.Length}.");
            if (!result.Content.Contains("offset", StringComparison.OrdinalIgnoreCase)
                || !result.Content.Contains("limit", StringComparison.OrdinalIgnoreCase)
                || !result.Content.Contains("Grep", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Huge ReadFile error must mention offset/limit and Grep.");
            }

            if (result.Content.Contains("1|line-1", StringComparison.Ordinal)
                || result.Content.Contains("|line-2-xxxxxxxx", StringComparison.Ordinal)
                || result.Content.Contains("@p", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Huge ReadFile error must not contain the file body.");
            }
        });

    private static Task AssertReadFileOffsetSlice() =>
        WithTempWorkAsync(async (root, executor) =>
        {
            var sb = new StringBuilder(220_000);
            for (var i = 1; i <= 10_000; i++)
                sb.Append("line-").Append(i).Append("-xxxxxxxx").Append('\n');
            File.WriteAllText(Path.Combine(root, "paged.txt"), sb.ToString());

            var result = await ReadFileAsync(executor, "paged.txt", offset: 9950, limit: 5);
            if (result.IsError)
                throw new InvalidOperationException($"Paged ReadFile must succeed: {result.Content}");
            if (!result.Content.Contains("9950|line-9950", StringComparison.Ordinal)
                || !result.Content.Contains("9954|line-9954", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Paged ReadFile must number lines from the requested offset.");
            }

            if (result.Content.Contains("1|line-1", StringComparison.Ordinal)
                || result.Content.Contains("|line-2-xxxxxxxx", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Paged ReadFile must not return the start of the file.");
            }
        });

    private static Task AssertReadFileTail() =>
        WithTempWorkAsync(async (root, executor) =>
        {
            var ten = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line-{i}"));
            File.WriteAllText(Path.Combine(root, "ten.txt"), ten);

            var tailTwo = await ReadFileAsync(executor, "ten.txt", offset: -2);
            if (tailTwo.IsError)
                throw new InvalidOperationException($"Tail -2 must succeed: {tailTwo.Content}");
            if (!tailTwo.Content.Contains("9|line-9", StringComparison.Ordinal)
                || !tailTwo.Content.Contains("10|line-10", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Tail -2 must return lines 9| and 10|.");
            }

            if (tailTwo.Content.Contains("1|line-1", StringComparison.Ordinal)
                || tailTwo.Content.Contains("8|line-8", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Tail -2 must not return earlier lines.");
            }

            var twoHundred = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"row-{i}"));
            File.WriteAllText(Path.Combine(root, "twohundred.txt"), twoHundred);
            var tailEighty = await ReadFileAsync(executor, "twohundred.txt", offset: -80);
            if (tailEighty.IsError)
                throw new InvalidOperationException($"Tail -80 must succeed: {tailEighty.Content}");

            var numbered = tailEighty.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (numbered.Length is 0 or > 80)
                throw new InvalidOperationException($"Tail -80 must return 1–80 lines, got {numbered.Length}.");
            if (!numbered[0].StartsWith("121|", StringComparison.Ordinal)
                || !numbered[^1].TrimEnd('\r').StartsWith("200|", StringComparison.Ordinal)
                || numbered.Any(l => l.StartsWith("1|", StringComparison.Ordinal)
                    || l.StartsWith("120|", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Tail -80 on 200 lines must start near the end.");
            }

            var giant = new StringBuilder();
            for (var i = 1; i <= 80; i++)
            {
                giant.Append("@p").Append(i.ToString("D4")).Append("='?' ");
                giant.Append('x', 9000);
                giant.Append('\n');
            }

            File.WriteAllText(Path.Combine(root, "efdump.txt"), giant.ToString());
            var giantTail = await ReadFileAsync(executor, "efdump.txt", offset: -80);
            if (!giantTail.IsError)
                throw new InvalidOperationException("Giant-line tail must be IsError.");
            if (giantTail.Content.Contains("@p", StringComparison.Ordinal)
                || giantTail.Content.Contains("xxxx", StringComparison.Ordinal)
                || giantTail.Content.Length >= 2048)
            {
                throw new InvalidOperationException("Giant-line tail error must be a short instruction with no @pNNNN body.");
            }

            if (!giantTail.Content.Contains("offset", StringComparison.OrdinalIgnoreCase)
                || !giantTail.Content.Contains("Grep", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Giant-line tail error must mention offset/limit and Grep.");
            }
        });

    private static Task AssertReadFileGiantLineClipped() =>
        WithTempWorkAsync(async (root, executor) =>
        {
            File.WriteAllText(Path.Combine(root, "oneline.txt"), new string('A', 200_000));
            var result = await ReadFileAsync(executor, "oneline.txt");
            if (result.IsError)
                throw new InvalidOperationException($"Single giant line must succeed (clipped): {result.Content}");
            if (!result.Content.StartsWith("1|", StringComparison.Ordinal))
                throw new InvalidOperationException("Clipped giant line must keep 1| numbering.");
            if (!result.Content.Contains(
                    $"line 1 clipped at {DysonToolResultLimits.MaxReadFileLineChars} chars",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Clipped giant line must include the clip marker.");
            }

            if (result.Content.Length > DysonToolResultLimits.MaxReadFileChars + 256)
                throw new InvalidOperationException($"Clipped giant line Content too large: {result.Content.Length}.");
        });

    private static Task AssertReadFileBinaryUsesLoadBinary() =>
        WithTempWorkAsync(async (root, executor) =>
        {
            File.WriteAllBytes(Path.Combine(root, "blob.bin"), [0x4D, 0x5A, 0x00, 0x01, 0x02]);
            var result = await ReadFileAsync(executor, "blob.bin");
            if (!result.IsError)
                throw new InvalidOperationException("Binary ReadFile must be IsError.");
            if (!result.Content.Contains("LoadBinary", StringComparison.Ordinal))
                throw new InvalidOperationException("Binary ReadFile must mention LoadBinary.");
        });

    private static async Task AssertShellExecuteOverflowTruncated()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTempDir();
        try
        {
            var session = new StubSession(
                new DysonAgentSessionConfig
                {
                    AvailableShells = [new DysonConfiguredShellSpec("PowerShell", "powershell.exe")],
                });
            using var http = new HttpClient();
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, root, http);
            var call = new DysonToolCall
            {
                CallId = "shell-overflow",
                ToolName = "ShellExecute",
                Stage = 0,
                ArgumentsJson =
                    """{"shell":"PowerShell","command":"[Console]::Out.Write(('x'*200000))","timeoutMs":30000}""",
            };

            var result = await executor.ExecuteAsync(call);
            if (result.IsError)
            {
                if (result.Content.Contains("not available", StringComparison.OrdinalIgnoreCase)
                    || result.Content.Contains("Failed to start", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                throw new InvalidOperationException($"ShellExecute overflow should exit 0: {result.Content}");
            }

            if (result.Content.Length >= 200_000)
                throw new InvalidOperationException("ShellExecute overflow must not dump the full 200k stdout.");
            if (!result.Content.Contains("truncated", StringComparison.OrdinalIgnoreCase)
                && result.Content.Length <= DysonToolResultLimits.MaxContentChars)
            {
                throw new InvalidOperationException("ShellExecute overflow must truncate (footer or TruncateContent).");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertCatalogCaps()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess, ["PowerShell"]);
        if (!pipeline.Tools.TryGetValue("ReadFile", out var readFile))
            throw new InvalidOperationException("ReadFile must be in the catalog.");

        var desc = readFile.Description;
        if (!desc.Contains("32KiB", StringComparison.Ordinal)
            || !desc.Contains("<20K", StringComparison.Ordinal)
            || !desc.Contains("error", StringComparison.OrdinalIgnoreCase)
            || !desc.Contains("offset", StringComparison.OrdinalIgnoreCase)
            || !desc.Contains("limit", StringComparison.OrdinalIgnoreCase)
            || !desc.Contains("Grep", StringComparison.Ordinal)
            || !desc.Contains("negative", StringComparison.OrdinalIgnoreCase)
            || !desc.Contains("tail", StringComparison.OrdinalIgnoreCase)
            || !desc.Contains("LoadBinary", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ReadFile catalog must mention 32KiB, <20K, error, offset/limit, Grep, negative-offset tail, LoadBinary.");
        }

        if (!readFile.InputSchemaJson.Contains("negative = tail", StringComparison.Ordinal)
            || !readFile.InputSchemaJson.Contains("over 32KiB", StringComparison.Ordinal)
            || !readFile.InputSchemaJson.Contains("Grep", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ReadFile offset/limit schema must describe tail and 32KiB error.");
        }

        if (!pipeline.Tools.TryGetValue("ShellExecute", out var shell)
            || !shell.Description.Contains("64KiB", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ShellExecute catalog must mention 64KiB capture.");
        }

        if (!pipeline.Tools.TryGetValue("ReadLongRunningShellTail", out var tail)
            || !tail.Description.Contains("8KiB", StringComparison.Ordinal)
            || !tail.Description.Contains("64KiB", StringComparison.Ordinal)
            || !tail.InputSchemaJson.Contains("64KiB", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ReadLongRunningShellTail catalog must mention 8KiB default and 64KiB clamp.");
        }

        if (!pipeline.Tools.TryGetValue("SubscribeToLongRunningShellCompletion", out var sub)
            || !sub.InputSchemaJson.Contains("64KiB", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Subscribe includeTailMaxChars schema must mention 64KiB clamp.");
        }
    }

    private static async Task AssertSubscribeClampsIncludeTailMaxChars()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var workDirId = Guid.NewGuid();
        var root = CreateTempDir();
        try
        {
            var session = new StubSession(
                new DysonAgentSessionConfig
                {
                    AvailableShells = [new DysonConfiguredShellSpec("Cmd", "cmd.exe")],
                });
            using var http = new HttpClient();
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, root, http, store: null, workDirId);

            var started = await DysonLongRunningShellRegistry
                .StartAsync(workDirId, "Cmd", "cmd.exe", "echo clamp-tail", root);
            if (started.IsError)
                return;

            var call = new DysonToolCall
            {
                CallId = "sub-clamp",
                ToolName = "SubscribeToLongRunningShellCompletion",
                Stage = 0,
                ArgumentsJson = $$"""{"longRunningShellId":{{started.Value.Id}},"includeTailMaxChars":10000000}""",
            };

            var result = await executor.ExecuteAsync(call);
            if (result.IsError)
                throw new InvalidOperationException($"Subscribe clamp failed: {result.Content}");
            if (!result.Content.Contains("includeTailMaxChars=65536", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Subscribe must report clamped includeTailMaxChars=65536. Got:\n{result.Content}");
            }
        }
        finally
        {
            DysonLongRunningShellRegistry.ClearForTests(workDirId);
            TryDelete(root);
        }
    }

    private static Task<DysonToolCallResult> ReadFileAsync(
        DysonWorkspaceToolExecutor executor,
        string path,
        int? offset = null,
        int? limit = null)
    {
        var payload = new Dictionary<string, object> { ["path"] = path };
        if (offset is int off)
            payload["offset"] = off;
        if (limit is int lim)
            payload["limit"] = lim;

        var call = new DysonToolCall
        {
            CallId = "rf",
            ToolName = "ReadFile",
            Stage = 0,
            ArgumentsJson = JsonSerializer.Serialize(payload),
        };
        return executor.ExecuteAsync(call);
    }

    private static async Task WithTempWorkAsync(Func<string, DysonWorkspaceToolExecutor, Task> body)
    {
        var root = CreateTempDir();
        try
        {
            var session = new StubSession();
            using var http = new HttpClient();
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, root, http);
            await body(root, executor);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-tool-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
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

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(DysonAgentSessionConfig? config = null) : DysonAgentSession(
        DysonAgentModes.Explore,
        config ?? new DysonAgentSessionConfig(),
        new StubProvider())
    {
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
