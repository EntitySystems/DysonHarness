using System.Text;
using System.Text.Json;

using DysonHarness;
using ImageMagick;

namespace Harness.Tests;

/// <summary>
/// ponytail: ConvertImage MCP — SVG→ICO, JPEG quality shrink, overwrite, validation.
/// </summary>
public class DysonConvertImageTests
{
    [Fact]
    public void Run()
    {
        AssertCatalogAndFormatMap();
        AssertSvgToIco();
        AssertJpegQualityShrinks();
        AssertOverwriteSemantics();
        AssertValidation();
    }

    private static void AssertCatalogAndFormatMap()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        if (!pipeline.Tools.TryGetValue("ConvertImage", out var tool)
            || !tool.Description.Contains("quality", StringComparison.OrdinalIgnoreCase)
            || !tool.Description.Contains("same-format", StringComparison.OrdinalIgnoreCase)
            || !tool.InputSchemaJson.Contains("\"inputFile\"", StringComparison.Ordinal)
            || !tool.InputSchemaJson.Contains("\"quality\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ConvertImage must be in the MCP catalog with quality + same-format docs.");
        }

        if (!DysonAgentSystemPrompts.SharedPreamble.Contains("ConvertImage", StringComparison.Ordinal))
            throw new InvalidOperationException("System prompt MCP list must mention ConvertImage.");

        var jpeg = DysonImageConvert.TryParseDesiredFormat("JPEG");
        if (jpeg.IsError || jpeg.Value != MagickFormat.Jpeg)
            throw new InvalidOperationException("desiredFormat jpeg/jpg must map to MagickFormat.Jpeg.");

        var bad = DysonImageConvert.TryParseDesiredFormat("avif");
        if (bad.IsSuccess)
            throw new InvalidOperationException("Unsupported desiredFormat must fail.");

        if (DysonImageConvert.TryMagickFormatFromExtension(".svg") != MagickFormat.Svg)
            throw new InvalidOperationException(".svg must hint MagickFormat.Svg.");
        if (DysonImageConvert.TryMagickFormatFromExtension(".ico") != MagickFormat.Ico)
            throw new InvalidOperationException(".ico must hint MagickFormat.Ico.");
    }

    private static void AssertSvgToIco()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-cimg-svg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string svg = """
                <svg xmlns="http://www.w3.org/2000/svg" width="32" height="32">
                  <rect width="32" height="32" fill="#c00"/>
                </svg>
                """;
            File.WriteAllText(Path.Combine(root, "icon.svg"), svg, Encoding.UTF8);

            var session = new StubSession();
            var executor = DysonWorkspaceTestFs.CreateExecutor(session, root, new HttpClient());
            var result = executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "cimg-svg",
                ToolName = "ConvertImage",
                Stage = 0,
                ArgumentsJson =
                    """{"inputFile":"icon.svg","outputFile":"out/icon.ico","desiredFormat":"ico"}""",
            }).GetAwaiter().GetResult();

            if (result.IsError)
                throw new InvalidOperationException($"ConvertImage SVG→ICO failed: {result.Content}");
            if (result.BinaryAttachment is not null)
                throw new InvalidOperationException("ConvertImage must not set BinaryAttachment.");

            using var ack = JsonDocument.Parse(result.Content);
            var rootEl = ack.RootElement;
            if (rootEl.GetProperty("desiredFormat").GetString() != "ico"
                || rootEl.GetProperty("quality").GetInt32() != DysonImageConvert.DefaultQuality
                || rootEl.GetProperty("width").GetInt32() < 1
                || rootEl.GetProperty("height").GetInt32() < 1
                || rootEl.GetProperty("byteLength").GetInt32() < 1)
            {
                throw new InvalidOperationException($"ConvertImage SVG→ICO ack mismatch: {result.Content}");
            }

            var icoPath = Path.Combine(root, "out", "icon.ico");
            if (!File.Exists(icoPath))
                throw new InvalidOperationException("ConvertImage must write outputFile (parents created).");

            using var check = new MagickImage(File.ReadAllBytes(icoPath), MagickFormat.Ico);
            if (check.Width < 1 || check.Height < 1)
                throw new InvalidOperationException("ICO from SVG must have positive dimensions.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertJpegQualityShrinks()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-cimg-jpg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] largeJpeg;
            using (var image = new MagickImage(MagickColors.Red, 800, 600))
            {
                // Noise makes lower quality shrink more reliably than a flat fill.
                image.AddNoise(NoiseType.Gaussian, 1.0);
                image.Quality = 95;
                image.Format = MagickFormat.Jpeg;
                largeJpeg = image.ToByteArray();
            }

            File.WriteAllBytes(Path.Combine(root, "photo.jpg"), largeJpeg);

            var session = new StubSession();
            var executor = DysonWorkspaceTestFs.CreateExecutor(session, root, new HttpClient());
            var result = executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "cimg-jpg",
                ToolName = "ConvertImage",
                Stage = 0,
                ArgumentsJson =
                    """{"inputFile":"photo.jpg","outputFile":"photo-q20.jpg","desiredFormat":"jpeg","quality":20}""",
            }).GetAwaiter().GetResult();

            if (result.IsError)
                throw new InvalidOperationException($"ConvertImage JPEG re-encode failed: {result.Content}");

            using var ack = JsonDocument.Parse(result.Content);
            var outLen = ack.RootElement.GetProperty("byteLength").GetInt32();
            var inLen = ack.RootElement.GetProperty("inputByteLength").GetInt32();
            if (ack.RootElement.GetProperty("desiredFormat").GetString() != "jpeg"
                || ack.RootElement.GetProperty("quality").GetInt32() != 20
                || outLen >= inLen)
            {
                throw new InvalidOperationException(
                    $"Same-format JPEG quality=20 must shrink bytes (in={inLen}, out={outLen}): {result.Content}");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertOverwriteSemantics()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-cimg-ow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] png;
            using (var image = new MagickImage(MagickColors.Blue, 16, 16))
            {
                image.Format = MagickFormat.Png;
                png = image.ToByteArray();
            }

            File.WriteAllBytes(Path.Combine(root, "in.png"), png);
            File.WriteAllBytes(Path.Combine(root, "out.webp"), [0x00, 0x01]);

            var session = new StubSession();
            var executor = DysonWorkspaceTestFs.CreateExecutor(session, root, new HttpClient());

            var blocked = executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "cimg-ow-fail",
                ToolName = "ConvertImage",
                Stage = 0,
                ArgumentsJson =
                    """{"inputFile":"in.png","outputFile":"out.webp","desiredFormat":"webp"}""",
            }).GetAwaiter().GetResult();

            if (!blocked.IsError || !blocked.Content.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ConvertImage must fail when output exists and overwrite is false.");

            var ok = executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "cimg-ow-ok",
                ToolName = "ConvertImage",
                Stage = 0,
                ArgumentsJson =
                    """{"inputFile":"in.png","outputFile":"out.webp","desiredFormat":"webp","overwrite":true}""",
            }).GetAwaiter().GetResult();

            if (ok.IsError)
                throw new InvalidOperationException($"ConvertImage overwrite=true failed: {ok.Content}");

            var written = File.ReadAllBytes(Path.Combine(root, "out.webp"));
            if (written.Length <= 2)
                throw new InvalidOperationException("overwrite=true must replace the existing output file.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertValidation()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-cimg-val-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] png;
            using (var image = new MagickImage(MagickColors.Green, 8, 8))
            {
                image.Format = MagickFormat.Png;
                png = image.ToByteArray();
            }

            File.WriteAllBytes(Path.Combine(root, "tiny.png"), png);

            // Soft ceiling check: length gate runs before Magick decode.
            var hugePath = Path.Combine(root, "huge.bin");
            using (var fs = new FileStream(hugePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.SetLength((50L * 1024 * 1024) + 1);
            }

            var session = new StubSession();
            var executor = DysonWorkspaceTestFs.CreateExecutor(session, root, new HttpClient());

            var badQuality = executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "cimg-q",
                ToolName = "ConvertImage",
                Stage = 0,
                ArgumentsJson =
                    """{"inputFile":"tiny.png","outputFile":"x.jpg","desiredFormat":"jpeg","quality":0}""",
            }).GetAwaiter().GetResult();
            if (!badQuality.IsError || !badQuality.Content.Contains("quality", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ConvertImage must reject quality outside 1–100.");

            var badQualityHi = executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "cimg-q2",
                ToolName = "ConvertImage",
                Stage = 0,
                ArgumentsJson =
                    """{"inputFile":"tiny.png","outputFile":"x.jpg","desiredFormat":"jpeg","quality":101}""",
            }).GetAwaiter().GetResult();
            if (!badQualityHi.IsError)
                throw new InvalidOperationException("ConvertImage must reject quality 101.");

            var tooBig = executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "cimg-big",
                ToolName = "ConvertImage",
                Stage = 0,
                ArgumentsJson =
                    """{"inputFile":"huge.bin","outputFile":"x.png","desiredFormat":"png"}""",
            }).GetAwaiter().GetResult();
            if (!tooBig.IsError
                || !tooBig.Content.Contains("52428800", StringComparison.Ordinal)
                || !tooBig.Content.Contains("bytes", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"ConvertImage must reject inputs over 50 MB: {tooBig.Content}");
            }

            var badFormat = executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "cimg-fmt",
                ToolName = "ConvertImage",
                Stage = 0,
                ArgumentsJson =
                    """{"inputFile":"tiny.png","outputFile":"x.avif","desiredFormat":"avif"}""",
            }).GetAwaiter().GetResult();
            if (!badFormat.IsError
                || !badFormat.Content.Contains("Unsupported", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("ConvertImage must reject unsupported desiredFormat.");
            }
        }
        finally
        {
            TryDelete(root);
        }
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
