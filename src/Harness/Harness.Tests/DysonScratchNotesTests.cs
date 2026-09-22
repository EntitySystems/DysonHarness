using System.Text;
using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>Scratch-note jail, caps, and the root Meta Agent tool boundary.</summary>
public class DysonScratchNotesTests
{
    private static readonly string[] NoteTools =
    [
        "ListNotes",
        "CanCreateNote",
        "CreateNote",
        "UpdateNote",
        "DeleteNote",
    ];

    [Fact]
    public async Task Jail_rejects_non_leaf_names()
    {
        var root = CreateTempRoot();
        try
        {
            var fs = await DysonWorkspaceTestFs.CreateLocalAsync(root);
            foreach (var name in new[]
            {
                "../x.md",
                "sub/a.md",
                "a.txt",
                "a.md/b",
                "a.md.txt",
                "..",
                Path.Combine(root, "secret.md"),
            })
            {
                var leaf = DysonScratchNotes.ResolveLeaf(fs, name);
                Assert.True(leaf.IsError, name);
                Assert.DoesNotContain(".dyson", leaf.Error, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("directory", leaf.Error, StringComparison.OrdinalIgnoreCase);
            }

            var ok = DysonScratchNotes.ResolveLeaf(fs, "ok.md");
            Assert.False(ok.IsError);
            Assert.Equal(".dyson/scratch/ok.md", ok.Value);
            Assert.False(Directory.Exists(Path.Combine(root, ".dyson")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Create_20_succeeds_21st_fails_update_still_succeeds_and_can_create_hides_roster()
    {
        await WithMetaAsync(async (_, executor) =>
        {
            for (var i = 0; i < DysonScratchNotes.MaxNotes; i++)
            {
                var created = await CallAsync(executor, "CreateNote", Args("name", $"n{i:00}.md", "content", ""));
                Assert.False(created.IsError, created.Content);
            }

            var extra = await CallAsync(executor, "CreateNote", Args("name", "overflow.md", "content", "x"));
            Assert.True(extra.IsError);
            Assert.False(string.IsNullOrWhiteSpace(extra.Content));
            Assert.DoesNotContain(".dyson", extra.Content, StringComparison.OrdinalIgnoreCase);

            var updated = await CallAsync(executor, "UpdateNote", Args("path", "n00.md", "content", "revised"));
            Assert.False(updated.IsError, updated.Content);

            var probe = await CallAsync(executor, "CanCreateNote", "{}");
            Assert.False(probe.IsError, probe.Content);
            using var doc = JsonDocument.Parse(probe.Content);
            var root = doc.RootElement;
            Assert.False(root.GetProperty("allowed").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("reason").GetString()));
            Assert.Equal(DysonScratchNotes.MaxNotes, root.GetProperty("noteCount").GetInt32());
            Assert.True(root.TryGetProperty("totalTokens", out JsonElement _));
            Assert.Equal(DysonScratchNotes.MaxNotes, root.GetProperty("maxNotes").GetInt32());
            Assert.Equal(DysonScratchNotes.MaxTokensPerNote, root.GetProperty("maxTokensPerNote").GetInt32());
            Assert.Equal(DysonScratchNotes.MaxTokensTotal, root.GetProperty("maxTokensTotal").GetInt32());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("noteTokens").ValueKind);
            Assert.False(root.TryGetProperty("notes", out JsonElement __));
            Assert.DoesNotContain("revised", probe.Content, StringComparison.Ordinal);

            var named = await CallAsync(
                executor,
                "CanCreateNote",
                Args("name", "overflow.md", "content", "nope"));
            using var namedDoc = JsonDocument.Parse(named.Content);
            Assert.False(namedDoc.RootElement.GetProperty("allowed").GetBoolean());
            Assert.False(namedDoc.RootElement.TryGetProperty("notes", out JsonElement _));
            Assert.Equal(JsonValueKind.Number, namedDoc.RootElement.GetProperty("noteTokens").ValueKind);
            Assert.DoesNotContain("nope", named.Content, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Per_note_token_cap_rejects_1001_and_accepts_1000()
    {
        var counter = new DysonTiktokenTokenCounter();
        var exact = TextOfTokenCount(counter, DysonScratchNotes.MaxTokensPerNote);
        var over = TextOfTokenCount(counter, DysonScratchNotes.MaxTokensPerNote + 1);
        Assert.Equal(DysonScratchNotes.MaxTokensPerNote, counter.CountTokens(exact));
        Assert.Equal(DysonScratchNotes.MaxTokensPerNote + 1, counter.CountTokens(over));

        await WithMetaAsync(async (root, executor) =>
        {
            var fs = await DysonWorkspaceTestFs.CreateLocalAsync(root);
            var denied = await DysonScratchNotes.CheckWriteAsync(
                fs, counter, "over.md", over, DysonScratchNotes.Kind.Create, CancellationToken.None);
            Assert.False(denied.IsError);
            Assert.False(denied.Value.Allowed);
            Assert.False(string.IsNullOrWhiteSpace(denied.Value.Reason));

            var allowed = await DysonScratchNotes.CheckWriteAsync(
                fs, counter, "cap.md", exact, DysonScratchNotes.Kind.Create, CancellationToken.None);
            Assert.True(allowed.Value.Allowed);

            var created = await CallAsync(executor, "CreateNote", Args("name", "cap.md", "content", exact));
            Assert.False(created.IsError, created.Content);
            using (var doc = JsonDocument.Parse(created.Content))
                Assert.Equal(DysonScratchNotes.MaxTokensPerNote, doc.RootElement.GetProperty("tokens").GetInt32());

            var tooBig = await CallAsync(executor, "CreateNote", Args("name", "over.md", "content", over));
            Assert.True(tooBig.IsError);

            var updated = await CallAsync(executor, "UpdateNote", Args("path", "cap.md", "content", over));
            Assert.True(updated.IsError);
            Assert.Equal(exact, File.ReadAllText(Path.Combine(root, ".dyson", "scratch", "cap.md")));
        });
    }

    [Fact]
    public async Task Total_cap_blocks_create_and_update_delete_still_succeeds()
    {
        var counter = new DysonTiktokenTokenCounter();
        var huge = TextOfTokenCount(counter, 19_100);
        var full = TextOfTokenCount(counter, DysonScratchNotes.MaxTokensPerNote);

        await WithMetaAsync(async (root, executor) =>
        {
            var dir = Path.Combine(root, ".dyson", "scratch");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "big.md"), huge);
            File.WriteAllText(Path.Combine(dir, "small.md"), "");

            var fs = await DysonWorkspaceTestFs.CreateLocalAsync(root);
            var createCheck = await DysonScratchNotes.CheckWriteAsync(
                fs, counter, "extra.md", full, DysonScratchNotes.Kind.Create, CancellationToken.None);
            Assert.False(createCheck.Value.Allowed);
            Assert.Equal(DysonScratchNotes.TotalReason, createCheck.Value.Reason);

            var updateCheck = await DysonScratchNotes.CheckWriteAsync(
                fs, counter, "small.md", full, DysonScratchNotes.Kind.Update, CancellationToken.None);
            Assert.False(updateCheck.Value.Allowed);
            Assert.Equal(DysonScratchNotes.TotalReason, updateCheck.Value.Reason);

            var created = await CallAsync(executor, "CreateNote", Args("name", "extra.md", "content", full));
            Assert.True(created.IsError);
            Assert.False(File.Exists(Path.Combine(dir, "extra.md")));

            var updated = await CallAsync(executor, "UpdateNote", Args("path", "small.md", "content", full));
            Assert.True(updated.IsError);
            Assert.Equal("", File.ReadAllText(Path.Combine(dir, "small.md")));

            var deleted = await CallAsync(executor, "DeleteNote", One("name", "big.md"));
            Assert.False(deleted.IsError, deleted.Content);
            Assert.False(File.Exists(Path.Combine(dir, "big.md")));
        });
    }

    [Fact]
    public async Task ListNotes_returns_sorted_names_and_tokens_without_bodies()
    {
        await WithMetaAsync(async (_, executor) =>
        {
            const string secret = "SECRET_BODY_XYZ";
            Assert.False((await CallAsync(executor, "CreateNote", Args("name", "b.md", "content", secret))).IsError);
            Assert.False((await CallAsync(executor, "CreateNote", Args("name", "a.md", "content", "other"))).IsError);

            var listed = await CallAsync(executor, "ListNotes", "{}");
            Assert.False(listed.IsError, listed.Content);
            using var doc = JsonDocument.Parse(listed.Content);
            Assert.False(doc.RootElement.TryGetProperty("allowed", out JsonElement _));
            var notes = doc.RootElement.GetProperty("notes");
            Assert.Equal(2, notes.GetArrayLength());
            Assert.Equal("a.md", notes[0].GetProperty("name").GetString());
            Assert.Equal("b.md", notes[1].GetProperty("name").GetString());
            Assert.True(notes[0].GetProperty("tokens").GetInt32() >= 0);
            Assert.True(notes[1].GetProperty("tokens").GetInt32() > 0);
            Assert.False(notes[0].TryGetProperty("content", out JsonElement _));
            Assert.DoesNotContain(secret, listed.Content, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task UpdateNote_applies_one_edit_and_rejects_an_ambiguous_match()
    {
        await WithMetaAsync(async (root, executor) =>
        {
            var created = await CallAsync(
                executor,
                "CreateNote",
                Args("name", "edit.md", "content", "foo\nbar\nfoo\n"));
            Assert.False(created.IsError, created.Content);

            var unique = await CallAsync(
                executor,
                "UpdateNote",
                """{"path":"edit.md","old_text":"bar","new_text":"BAR"}""");
            Assert.False(unique.IsError, unique.Content);
            Assert.Equal(
                "foo\nBAR\nfoo\n",
                File.ReadAllText(Path.Combine(root, ".dyson", "scratch", "edit.md")).Replace("\r\n", "\n"));

            var ambiguous = await CallAsync(
                executor,
                "UpdateNote",
                """{"path":"edit.md","old_text":"foo","new_text":"baz"}""");
            Assert.True(ambiguous.IsError);
            Assert.Contains("matched", ambiguous.Content, StringComparison.Ordinal);
            Assert.Contains("foo", File.ReadAllText(Path.Combine(root, ".dyson", "scratch", "edit.md")));
        });
    }

    [Fact]
    public async Task Catalogs_hide_note_tools_except_root_meta_and_work_mode_rejects_a_forced_call()
    {
        var meta = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent);
        var work = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.Work);
        var drone = DysonSessionToolsetBuilder.Build(
            new DysonAgentSessionConfig(),
            DysonAgentModes.MetaAgentDrone,
            interAgentDepth: 1,
            omitRootTaskCompletionTools: true);

        foreach (var name in NoteTools)
        {
            Assert.Contains(name, meta.Tools.Keys);
            Assert.DoesNotContain(name, work.Tools.Keys);
            Assert.DoesNotContain(name, drone.Tools.Keys);
            Assert.DoesNotContain(".dyson/scratch", meta.Tools[name].Description, StringComparison.Ordinal);
            Assert.DoesNotContain("The only disk access", meta.Tools[name].Description, StringComparison.Ordinal);
            Assert.DoesNotContain("filesystem", meta.Tools[name].Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("directory", meta.Tools[name].Description, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var absent in new[] { "ReadFile", "WriteFile", "ReadNote" })
            Assert.DoesNotContain(absent, meta.Tools.Keys);

        var root = CreateTempRoot();
        try
        {
            using var http = new HttpClient();
            var session = new StubSession(DysonAgentModes.Work);
            session.McpPipeline.Tools["CreateNote"] = new DysonMcpTool
            {
                Name = "CreateNote",
                Description = "forced",
                InputSchemaJson = "{}",
            };
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, root, http);
            var result = await CallAsync(executor, "CreateNote", Args("name", "a.md", "content", "x"));
            Assert.True(result.IsError);
            Assert.Contains("only available in Meta Agent mode", result.Content, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string TextOfTokenCount(DysonTiktokenTokenCounter counter, int count)
    {
        if (count == 0)
            return "";

        string? atom = null;
        foreach (var candidate in new[] { "\n", "0", "a", " x", " the", " z", " hello" })
        {
            if (counter.CountTokens(candidate) == 1
                && counter.CountTokens(candidate + candidate) == 2)
            {
                atom = candidate;
                break;
            }
        }

        if (atom is null)
            throw new InvalidOperationException("No 1-token atom for cap tests.");

        var sb = new StringBuilder(atom.Length * count);
        for (var i = 0; i < count; i++)
            sb.Append(atom);

        var text = sb.ToString();
        var actual = counter.CountTokens(text);
        if (actual != count)
            throw new InvalidOperationException($"Token atom drifted: wanted {count}, got {actual}.");
        return text;
    }

    private static string Args(string key, string value, string key2, string value2) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [key] = value,
            [key2] = value2,
        });

    private static string One(string key, string value) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { [key] = value });

    private static Task<DysonToolCallResult> CallAsync(
        DysonWorkspaceToolExecutor executor,
        string tool,
        string argumentsJson) =>
        executor.ExecuteAsync(new DysonToolCall
        {
            CallId = Guid.NewGuid().ToString("N"),
            ToolName = tool,
            Stage = 0,
            ArgumentsJson = argumentsJson,
        });

    private static async Task WithMetaAsync(Func<string, DysonWorkspaceToolExecutor, Task> body)
    {
        var root = CreateTempRoot();
        try
        {
            using var http = new HttpClient();
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(
                new StubSession(DysonAgentModes.MetaAgent),
                root,
                http);
            await body(root, executor);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-notes-" + Guid.NewGuid().ToString("N"));
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

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
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
