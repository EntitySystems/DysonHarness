using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>Outcome of <see cref="DysonAgentSession.TryApplyA2ui"/>. <see cref="TurnId"/> is the surface's DisplayInfo turn.</summary>
public sealed record DysonA2uiApplied(Guid TurnId, string SurfaceId, int Revision, bool Deleted);

/// <summary>A question answer computed from an A2UI Button action. Feed it to the existing answer path.</summary>
public sealed record DysonA2uiReturn(
    Guid QuestionId,
    IReadOnlyList<string> Selected,
    string? CustomText,
    DysonA2uiActionEvent Action);

/// <summary>
/// Return path for A2UI Button actions on question-bound surfaces. The surface and question come from the
/// session, never from the client: only <c>sourceComponentId</c> and the local data model are client input,
/// and the answer still passes <see cref="DysonUserQuestion.ValidateAnswer"/>.
/// </summary>
public static class DysonA2uiReturnHandler
{
    /// <summary>
    /// Resolves the action against the stored (or, for <c>dyson_question_*</c>, server-rebuilt) surface and maps
    /// it to <c>(selected, customText)</c>. Records nothing; the caller hands the result to
    /// <c>SubmitMetaUserQuestionAnswerAsync</c>. <paramref name="turnId"/> is ignored for derived cards.
    /// </summary>
    public static Result<DysonA2uiReturn, string> Prepare(
        DysonAgentSession session,
        Guid turnId,
        string surfaceId,
        string sourceComponentId,
        JsonObject localModel)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(surfaceId))
            return Fail("A2UI: surfaceId is required.");
        if (string.IsNullOrWhiteSpace(sourceComponentId))
            return Fail("A2UI: sourceComponentId is required.");
        if (localModel is null)
            return Fail("A2UI: data model is required.");

        DysonUserQuestion question;
        DysonA2uiSurface surface;
        var prefix = DysonA2uiProtocol.QuestionSurfacePrefix;
        if (surfaceId.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (!Guid.TryParseExact(surfaceId[prefix.Length..], "N", out var questionId))
                return Fail("A2UI: unknown surface.");
            if (session.FindUserQuestion(questionId) is not { } found)
                return Fail("PostUserQuestion: not found.");
            if (found.Answer is not null)
                return Fail("PostUserQuestion: already answered.");

            var built = DysonA2uiQuestionCard.Build(found);
            if (built.IsError)
                return Fail(built.Error.Message);
            question = found;
            surface = built.Value;
        }
        else
        {
            // Snapshot reads: the turn fields hold immutable records, and the answer lock re-checks.
            var turn = session.Turns.FirstOrDefault(t => t.Id == turnId);
            if (turn?.A2uiSurface is not { } stored
                || !string.Equals(stored.SurfaceId, surfaceId, StringComparison.Ordinal))
                return Fail("A2UI: surface not found.");
            if (stored.QuestionId is not { } boundId)
                return Fail("A2UI: this surface is not bound to a question.");
            if (turn.UserQuestion is not { } card || card.Id != boundId)
                return Fail("PostUserQuestion: not found.");
            if (card.Answer is not null)
                return Fail("PostUserQuestion: already answered.");
            question = card;
            surface = stored;
        }

        var action = DysonA2uiAction.Resolve(surface, sourceComponentId, localModel, DateTime.UtcNow);
        if (action.IsError)
            return Fail(action.Error.Message);

        var answer = DysonA2uiQuestionBridge.ToAnswer(question, action.Value);
        if (answer.IsError)
            return Fail(answer.Error);

        return Result<DysonA2uiReturn, string>.AsValue(new DysonA2uiReturn(
            question.Id,
            answer.Value.Selected,
            answer.Value.CustomText,
            action.Value));
    }

    private static Result<DysonA2uiReturn, string> Fail(string error) =>
        Result<DysonA2uiReturn, string>.AsError(error);
}
