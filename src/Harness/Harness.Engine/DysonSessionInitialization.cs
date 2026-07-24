namespace DysonHarness;

/// <summary>
/// Factory for InitializeSession turns and periodic RenameSession review cadence.
/// </summary>
public static class DysonSessionInitialization
{
    /// <summary>1-based turn indices 1, 9, 17, … receive a rename review mandate.</summary>
    public const int RenameSessionReviewInterval = 8;

    /// <summary>
    /// Ephemeral yes/no rename review appended at API/transcript time only (never stored on
    /// <see cref="DysonAgentTurn.Instruction"/>, never re-emitted for completed history turns).
    /// </summary>
    public const string RenameSessionReviewMandate = """
        Decide whether to rename this session:
        - Yes: if the current title is wrong, generic, or outdated for the conversation so far, call RenameSession with a short human-readable title.
        - No: do not call RenameSession; continue with the user request.
        """;

    /// <summary>
    /// True when the 1-based turn index is a rename-review slot (1, 9, 17, …).
    /// Equivalent to <c>TurnHistory.Count % 8 == 0</c> before adding the new turn.
    /// </summary>
    public static bool IsRenameReviewTurn(int oneBasedTurnIndex) =>
        oneBasedTurnIndex > 0
        && (oneBasedTurnIndex - 1) % RenameSessionReviewInterval == 0;

    /// <summary>
    /// Creates a turn with <see cref="DysonAgentTurnKind.InitializeSession"/> and the user's prompt.
    /// The rename review mandate is appended at API/transcript time, not stored on
    /// <see cref="DysonAgentTurn.Instruction"/>.
    /// </summary>
    public static DysonAgentTurn CreateTurn(string userPrompt)
    {
        ArgumentNullException.ThrowIfNull(userPrompt);

        return new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.InitializeSession,
            Instruction = userPrompt,
        };
    }
}
