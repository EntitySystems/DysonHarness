using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// <see cref="DysonCompactionFlow.FormatTodosForResume"/>: the plain-English todo block appended to a
/// post-compaction continuation (plan 252). The root Meta Agent gate is in <c>DysonContextCompactionSmokeTests</c>.
/// </summary>
public class DysonCompactionResumeTodosTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FormatTodosForResume_empty_returns_empty() =>
        Assert.Equal("", DysonCompactionFlow.FormatTodosForResume([]));

    [Fact]
    public void FormatTodosForResume_groups_in_progress_pending_done_in_order()
    {
        var text = DysonCompactionFlow.FormatTodosForResume(
        [
            Todo(1, "done1", "Explore the spawn path", DysonSessionTodoStatus.Complete, "first", "verified"),
            Todo(2, "p1", "Update docs", DysonSessionTodoStatus.Pending),
            Todo(3, "a", "Make todos required", DysonSessionTodoStatus.Ongoing, "schema done, handler next"),
            Todo(4, "p2", "Run tests", DysonSessionTodoStatus.Pending, "   "),
        ]);

        Assert.Equal(
            """
            Your todo list when this resume was queued:
            In progress:
            - [a] Make todos required — schema done, handler next
            Pending:
            - [p1] Update docs
            - [p2] Run tests
            Done:
            - [done1] Explore the spawn path — verified
            """.ReplaceLineEndings("\n"),
            text);
        Assert.DoesNotContain("{", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"taskCode\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatTodosForResume_caps_done_and_clips_long_text()
    {
        // Sequence 1..8; UpdatedUtc runs backwards so the newest five are sequences 1..5.
        var todos = Enumerable.Range(1, 8)
            .Select(i => Todo(i, $"d{i}", $"Done {i}", DysonSessionTodoStatus.Complete, updated: T0.AddMinutes(-i)))
            .Append(Todo(9, "long", "line one\n" + new string('x', 500), DysonSessionTodoStatus.Pending))
            .ToList();

        var lines = DysonCompactionFlow.FormatTodosForResume(todos).Split('\n');

        var doneAt = Array.IndexOf(lines, "Done (5 most recent of 8):");
        Assert.True(doneAt > 0);
        Assert.Equal(["- [d1] Done 1", "- [d2] Done 2", "- [d3] Done 3", "- [d4] Done 4", "- [d5] Done 5"], lines[(doneAt + 1)..]);

        var longLine = Assert.Single(lines, l => l.StartsWith("- [long] ", StringComparison.Ordinal));
        var name = longLine["- [long] ".Length..];
        Assert.StartsWith("line one x", name, StringComparison.Ordinal);
        Assert.EndsWith("…", name, StringComparison.Ordinal);
        Assert.True(name.Length <= 200, $"clipped name is {name.Length} chars");
    }

    private static DysonSessionTodo Todo(
        int sequence,
        string code,
        string name,
        DysonSessionTodoStatus status,
        params string[] comments) =>
        Todo(sequence, code, name, status, T0.AddMinutes(sequence), comments);

    private static DysonSessionTodo Todo(
        int sequence,
        string code,
        string name,
        DysonSessionTodoStatus status,
        DateTime updated,
        params string[] comments) =>
        new()
        {
            TaskCode = code,
            DisplayName = name,
            Status = status,
            Comments = comments,
            Sequence = sequence,
            CreatedUtc = T0,
            UpdatedUtc = updated,
        };
}
