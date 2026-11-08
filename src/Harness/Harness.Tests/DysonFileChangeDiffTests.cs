using DysonHarness;

namespace Harness.Tests;

/// <summary>ponytail: single-hunk diff, caps, redaction, display lines.</summary>
public class DysonFileChangeDiffTests
{
    [Fact]
    public void Edit_in_the_middle_keeps_one_context_line_each_side()
    {
        var hunk = DysonFileChangeDiff.Compute("a\nb\nc\nd\n", "a\nb\nX\nd\n");
        Assert.NotNull(hunk);
        Assert.Equal(3, hunk.Value.StartLine);
        Assert.Equal("b", hunk.Value.ContextBefore);
        Assert.Equal("c", hunk.Value.RemovedText);
        Assert.Equal("X", hunk.Value.AddedText);
        Assert.Equal("d", hunk.Value.ContextAfter);
        Assert.False(hunk.Value.Truncated);
    }

    [Fact]
    public void Edit_on_the_first_or_last_line_drops_that_context()
    {
        var first = DysonFileChangeDiff.Compute("a\nb\n", "Z\nb\n");
        Assert.Null(first!.Value.ContextBefore);
        Assert.Equal("b", first.Value.ContextAfter);
        Assert.Equal(1, first.Value.StartLine);

        var last = DysonFileChangeDiff.Compute("a\nb\n", "a\nZ\n");
        Assert.Equal("a", last!.Value.ContextBefore);
        Assert.Null(last.Value.ContextAfter);
        Assert.Equal(2, last.Value.StartLine);
    }

    [Fact]
    public void Created_is_the_full_text_with_no_context()
    {
        var hunk = DysonFileChangeDiff.Compute(null, "one\ntwo\n");
        Assert.Equal(1, hunk!.Value.StartLine);
        Assert.Null(hunk.Value.ContextBefore);
        Assert.Null(hunk.Value.RemovedText);
        Assert.Equal("one\ntwo", hunk.Value.AddedText);
        Assert.Null(hunk.Value.ContextAfter);
    }

    [Fact]
    public void Identical_text_returns_null()
    {
        Assert.Null(DysonFileChangeDiff.Compute("a\nb\n", "a\nb\n"));
        Assert.Null(DysonFileChangeDiff.Compute("", ""));
    }

    [Fact]
    public void Crlf_versus_lf_is_not_a_change()
    {
        Assert.Null(DysonFileChangeDiff.Compute("a\r\nb\r\n", "a\nb\n"));
    }

    [Fact]
    public void Text_over_64_kib_is_truncated_with_the_marker()
    {
        var big = new string('x', DysonFileChangeDiff.TextCap + 25);
        var hunk = DysonFileChangeDiff.Compute(null, big);
        Assert.True(hunk!.Value.Truncated);
        Assert.EndsWith("\n… [truncated 25 chars]", hunk.Value.AddedText, StringComparison.Ordinal);
        Assert.Equal(DysonFileChangeDiff.TextCap, hunk.Value.AddedText!.IndexOf('\n'));
    }

    [Fact]
    public void Context_line_over_500_chars_is_clipped()
    {
        var line = new string('c', DysonFileChangeDiff.ContextCap + 40);
        var hunk = DysonFileChangeDiff.Compute(line + "\nold\n", line + "\nnew\n");
        Assert.Equal(DysonFileChangeDiff.ContextCap, hunk!.Value.ContextBefore!.Length);
        Assert.False(hunk.Value.Truncated);
    }

    [Fact]
    public void ToLines_orders_context_removed_added_context()
    {
        var row = new DysonFileChangeEntity
        {
            ContextBefore = "before",
            RemovedText = "old1\nold2",
            AddedText = "new",
            ContextAfter = "after",
        };

        var lines = DysonFileChangeDiff.ToLines(row);
        Assert.Equal(
            [
                (DysonFileChangeLineKind.Context, "before"),
                (DysonFileChangeLineKind.Removed, "old1"),
                (DysonFileChangeLineKind.Removed, "old2"),
                (DysonFileChangeLineKind.Added, "new"),
                (DysonFileChangeLineKind.Context, "after"),
            ],
            lines.Select(l => (l.Kind, l.Text)).ToArray());
    }

    [Fact]
    public void ShouldRedact_matches_the_denylist_on_the_file_name()
    {
        Assert.True(DysonFileChangeDiff.ShouldRedact(".env"));
        Assert.True(DysonFileChangeDiff.ShouldRedact("dir/.env.local"));
        Assert.True(DysonFileChangeDiff.ShouldRedact("keys/server.pem"));
        Assert.True(DysonFileChangeDiff.ShouldRedact("a.key"));
        Assert.True(DysonFileChangeDiff.ShouldRedact("cert.PFX"));
        Assert.True(DysonFileChangeDiff.ShouldRedact("secrets.json"));
        Assert.True(DysonFileChangeDiff.ShouldRedact("app.secrets.json"));
        Assert.False(DysonFileChangeDiff.ShouldRedact("src/Program.cs"));
        Assert.False(DysonFileChangeDiff.ShouldRedact("notes/keyring.txt"));
    }
}
