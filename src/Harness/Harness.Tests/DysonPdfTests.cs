using System.Text;
using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// PDF guard + render: LoadBinary rejects PDFs, ConvertImage renders pages via PDFtoImage/PDFium.
/// Fixture PDF is built in code (no binary test asset) so the Facts run on Linux CI too.
/// </summary>
public sealed class DysonPdfTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dyson-pdf-" + Guid.NewGuid().ToString("N"));

    public DysonPdfTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }

    /// <summary>Two pages: 200x100 pt and 100x200 pt, empty content, valid xref.</summary>
    internal static byte[] TinyPdf()
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] >>",
        ];
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append($"{off:D10} 00000 n \n");
        sb.Append($"trailer << /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Theory]
    [InlineData("1", new[] { 1 })]
    [InlineData("1-2", new[] { 1, 2 })]
    [InlineData("2,1", new[] { 1, 2 })]
    [InlineData(" all ", new[] { 1, 2 })]
    [InlineData(null, new[] { 1 })]
    [InlineData("", new[] { 1 })]
    public void ParsePages_accepts(string? spec, int[] expected)
    {
        var parsed = DysonPdf.ParsePages(spec, 2);
        Assert.True(parsed.IsSuccess, parsed.IsError ? parsed.Error : null);
        Assert.Equal(expected, parsed.Value);
    }

    [Theory]
    [InlineData("3", 2, "out of range")]
    [InlineData("0", 2, "invalid pages")]
    [InlineData("a-b", 2, "invalid pages")]
    [InlineData("2-1", 2, "invalid pages")]
    [InlineData("1-21", 30, "at most 20")]
    [InlineData("1-15,16-21", 30, "at most 20")]
    public void ParsePages_rejects(string spec, int pageCount, string expected)
    {
        var parsed = DysonPdf.ParsePages(spec, pageCount);
        Assert.True(parsed.IsError);
        Assert.Contains(expected, parsed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void IsPdf_uses_extension_or_magic()
    {
        Assert.True(DysonPdf.IsPdf("a/Doc.PDF", []));
        Assert.True(DysonPdf.IsPdf("blob", "%PDF-"u8));
        Assert.False(DysonPdf.IsPdf("a.png", "%PDF"u8));
    }

    [Theory]
    [InlineData("doc.pdf")]
    [InlineData("noext")]
    public async Task LoadBinary_rejects_pdf_with_convert_hint(string name)
    {
        File.WriteAllBytes(Path.Combine(_root, name), TinyPdf());
        var result = await CallAsync("LoadBinary", $$"""{"path":"{{name}}"}""");
        Assert.True(result.IsError);
        Assert.Contains("ConvertImage", result.Content, StringComparison.Ordinal);
        Assert.Null(result.BinaryAttachment);
    }

    [Fact]
    public async Task LoadBinary_pdf_rejection_beats_size_cap()
    {
        using (var fs = new FileStream(Path.Combine(_root, "big.pdf"), FileMode.Create))
            fs.SetLength((5L * 1024 * 1024) + 1);
        var result = await CallAsync("LoadBinary", """{"path":"big.pdf"}""");
        Assert.True(result.IsError);
        Assert.Contains("ConvertImage", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertImage_renders_single_page()
    {
        File.WriteAllBytes(Path.Combine(_root, "doc.pdf"), TinyPdf());
        var result = await CallAsync(
            "ConvertImage",
            """{"inputFile":"doc.pdf","outputFile":"out/doc.png","desiredFormat":"png","pages":"1","dpi":72}""");
        Assert.False(result.IsError, result.Content);
        Assert.Null(result.BinaryAttachment);

        using var ack = JsonDocument.Parse(result.Content);
        Assert.Equal(200, ack.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(100, ack.RootElement.GetProperty("height").GetInt32());
        Assert.Equal("out/doc.png", ack.RootElement.GetProperty("outputFile").GetString());
        Assert.True(File.Exists(Path.Combine(_root, "out", "doc.png")));
    }

    [Fact]
    public async Task ConvertImage_renders_page_range_to_suffixed_files()
    {
        File.WriteAllBytes(Path.Combine(_root, "doc.pdf"), TinyPdf());
        var result = await CallAsync(
            "ConvertImage",
            """{"inputFile":"doc.pdf","outputFile":"doc.jpg","desiredFormat":"jpeg","pages":"1-2","dpi":72}""");
        Assert.False(result.IsError, result.Content);

        using var ack = JsonDocument.Parse(result.Content);
        var outputs = ack.RootElement.GetProperty("outputs").EnumerateArray().ToList();
        Assert.Equal(2, outputs.Count);
        Assert.Equal("doc-p1.jpg", outputs[0].GetProperty("outputFile").GetString());
        Assert.Equal((200, 100), (outputs[0].GetProperty("width").GetInt32(), outputs[0].GetProperty("height").GetInt32()));
        Assert.Equal("doc-p2.jpg", outputs[1].GetProperty("outputFile").GetString());
        Assert.Equal((100, 200), (outputs[1].GetProperty("width").GetInt32(), outputs[1].GetProperty("height").GetInt32()));
        Assert.True(File.Exists(Path.Combine(_root, "doc-p1.jpg")));
        Assert.True(File.Exists(Path.Combine(_root, "doc-p2.jpg")));
        Assert.False(File.Exists(Path.Combine(_root, "doc.jpg")));
    }

    [Fact]
    public async Task ConvertImage_pdf_errors()
    {
        File.WriteAllBytes(Path.Combine(_root, "doc.pdf"), TinyPdf());
        File.WriteAllBytes(Path.Combine(_root, "bad.pdf"), Encoding.ASCII.GetBytes("%PDF-1.4 garbage"));

        var outOfRange = await CallAsync("ConvertImage", """{"inputFile":"doc.pdf","outputFile":"x.png","desiredFormat":"png","pages":"3"}""");
        Assert.True(outOfRange.IsError);
        Assert.Contains("out of range", outOfRange.Content, StringComparison.Ordinal);

        var corrupt = await CallAsync("ConvertImage", """{"inputFile":"bad.pdf","outputFile":"x.png","desiredFormat":"png"}""");
        Assert.True(corrupt.IsError);
        Assert.Contains("not a valid PDF", corrupt.Content, StringComparison.Ordinal);

        var badDpi = await CallAsync("ConvertImage", """{"inputFile":"doc.pdf","outputFile":"x.png","desiredFormat":"png","dpi":500}""");
        Assert.True(badDpi.IsError);
        Assert.Contains("dpi", badDpi.Content, StringComparison.Ordinal);

        Assert.False(File.Exists(Path.Combine(_root, "x.png")));
    }

    [Fact]
    public async Task ConvertImage_pdf_checks_every_target_before_writing()
    {
        File.WriteAllBytes(Path.Combine(_root, "doc.pdf"), TinyPdf());
        File.WriteAllBytes(Path.Combine(_root, "doc-p2.png"), [0x01]);

        var result = await CallAsync(
            "ConvertImage",
            """{"inputFile":"doc.pdf","outputFile":"doc.png","desiredFormat":"png","pages":"all","dpi":72}""");
        Assert.True(result.IsError);
        Assert.Contains("already exists", result.Content, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "doc-p1.png")));
        Assert.Single(File.ReadAllBytes(Path.Combine(_root, "doc-p2.png")));
    }

    [Fact]
    public void Catalog_mentions_pdf_handling()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        Assert.Contains("PDF", pipeline.Tools["LoadBinary"].Description, StringComparison.Ordinal);
        var convert = pipeline.Tools["ConvertImage"];
        Assert.Contains("\"pages\"", convert.InputSchemaJson, StringComparison.Ordinal);
        Assert.Contains("\"dpi\"", convert.InputSchemaJson, StringComparison.Ordinal);
    }

    [Fact]
    public void System_prompt_and_contextFiles_text_mention_pdfs()
    {
        Assert.Contains("PDFs", DysonAgentSystemPrompts.SharedPreamble, StringComparison.Ordinal);
        var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        Assert.Contains("PDFs are rejected", pipeline.Tools["StartSubagent"].InputSchemaJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextFiles_reject_pdf_attach_image_and_load_text()
    {
        File.WriteAllBytes(Path.Combine(_root, "doc.pdf"), TinyPdf());
        File.WriteAllText(Path.Combine(_root, "notes.md"), "# Notes");
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Red, 8, 8))
        {
            image.Format = ImageMagick.MagickFormat.Png;
            File.WriteAllBytes(Path.Combine(_root, "shot.png"), image.ToByteArray());
        }

        var pdfTurn = DysonSessionInitialization.CreateTurn("task");
        var pdf = await DysonAgentSession.AttachContextFilesToChildTurnAsync(
            pdfTurn, ["doc.pdf"], _root, null, CancellationToken.None, DysonAgentModes.MetaAgent);
        Assert.True(pdf.IsError);
        Assert.Contains("ConvertImage", pdf.Error, StringComparison.Ordinal);
        Assert.Contains("Meta Agent", pdf.Error, StringComparison.Ordinal);

        var turn = DysonSessionInitialization.CreateTurn("task");
        var ok = await DysonAgentSession.AttachContextFilesToChildTurnAsync(
            turn, ["shot.png", "notes.md"], _root, null, CancellationToken.None);
        Assert.True(ok.IsSuccess, ok.IsError ? ok.Error : null);
        Assert.Single(turn.UserImages);
        var entry = Assert.Single(turn.ContextFiles);
        Assert.Equal("notes.md", entry.ResolvedPath);
        Assert.Equal("# Notes", entry.MarkdownContent);
    }

    private async Task<DysonToolCallResult> CallAsync(string tool, string argumentsJson)
    {
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(new StubSession(), _root, new HttpClient());
        return await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = Guid.NewGuid().ToString("N"),
            ToolName = tool,
            Stage = 0,
            ArgumentsJson = argumentsJson,
        });
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Explore,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode, string task, string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null, string? reasoningEffort = null,
            IReadOnlyList<string>? contextFiles = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(string prompt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(string prompt, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(DysonAgentTurn turn, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(string planRelativePath, IReadOnlyList<string>? reportBlocks = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(DysonAgentInterrupt interrupt, string? title = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(string instruction, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptShellExitedAsync(DysonAgentInterrupt interrupt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
