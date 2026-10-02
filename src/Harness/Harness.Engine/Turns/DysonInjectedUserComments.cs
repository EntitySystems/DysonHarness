using System.Text;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// Transcript shape for mid-turn user comments (<see cref="DysonReasoningSegmentKind.UserComment"/>).
/// Each comment is its own <c>role: user</c> message: <see cref="Marker"/>, blank line, verbatim text.
/// Same JSON for Completions messages and Responses input. Position is decided by the caller
/// from <see cref="DysonReasoningSegment.DeliveredAfterToolCalls"/>.
/// </summary>
public static class DysonInjectedUserComments
{
    /// <summary>Tag naming who (the user) and when (during a running turn).</summary>
    public const string Marker = "[User comment during turn]";

    /// <summary>Non-empty UserComment segments in log order.</summary>
    public static List<DysonReasoningSegment> From(IEnumerable<DysonReasoningSegment> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return log
            .Where(s => s.Kind == DysonReasoningSegmentKind.UserComment && !string.IsNullOrWhiteSpace(s.Text))
            .ToList();
    }

    /// <summary>One comment as message text: marker line, blank line, verbatim text.</summary>
    public static string FormatMessage(string text) => Marker + "\n\n" + text;

    /// <summary>Appends one <c>role: user</c> message per comment.</summary>
    public static void AppendMessages(JsonArray target, IEnumerable<DysonReasoningSegment> comments)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(comments);
        foreach (var comment in comments)
        {
            target.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = FormatMessage(comment.Text),
            });
        }
    }

    /// <summary>
    /// Plain-text twin (summary stubs, summarizer body): every comment in the log as
    /// <see cref="FormatMessage"/> blocks separated by a blank line. Empty string when none.
    /// </summary>
    public static string FormatPlainText(IEnumerable<DysonReasoningSegment> log)
    {
        var sb = new StringBuilder();
        foreach (var comment in From(log))
        {
            if (sb.Length > 0)
                sb.Append("\n\n");
            sb.Append(FormatMessage(comment.Text));
        }

        return sb.ToString();
    }
}
