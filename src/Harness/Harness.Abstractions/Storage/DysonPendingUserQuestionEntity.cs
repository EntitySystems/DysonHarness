namespace DysonHarness;

/// <summary>
/// EF row for table <c>pending_user_questions</c>: one unanswered Meta <c>PostUserQuestion</c> card.
/// The live card stays on <c>turns.UserQuestionJson</c>; this row is the pending index the Meta header
/// and the Questions modal read. Answering or dismissing deletes the row.
/// </summary>
public sealed class DysonPendingUserQuestionEntity
{
    /// <summary>
    /// Auto-increment id (the <c>#id</c> in the answer prompt). Stored as signed <c>INTEGER</c> through a
    /// checked converter in <c>DysonDbContext</c>; leave 0 on insert so SQLite assigns it.
    /// </summary>
    public ulong Id { get; set; }

    /// <summary>Owning subject (<see cref="DysonSubjects.Local"/> or cloud subject id).</summary>
    public string SubjectId { get; set; } = "";

    /// <summary>The Meta session that posted the question. Cascade-deleted with the session.</summary>
    public Guid SessionId { get; set; }

    /// <summary>The DisplayInfo turn holding the card. No FK (turns are rewritten).</summary>
    public Guid TurnId { get; set; }

    /// <summary>The card id (<c>DysonUserQuestion.Id</c>).</summary>
    public Guid QuestionGuid { get; set; }

    /// <summary>Bubble body posted with the card.</summary>
    public string Message { get; set; } = "";

    /// <summary>Card title.</summary>
    public string Question { get; set; } = "";

    /// <summary>Choices in card order. Column <c>ChoicesJson</c> (JSON TEXT).</summary>
    public List<string> Choices { get; set; } = [];

    public bool MultiSelect { get; set; }

    public bool AllowCustomAnswer { get; set; }

    /// <summary>Parked parent event snapshot at post time (newest <c>AwaitingUserAnswer</c>); null when none.</summary>
    public int? SubagentId { get; set; }

    /// <summary>Parked parent event snapshot at post time; null when none.</summary>
    public Guid? EventId { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }

    public DysonSessionEntity? Session { get; set; }
}

/// <summary>
/// Runtime / UI mirror of a <c>pending_user_questions</c> row (non-EF). On create, <see cref="Id"/> and
/// <see cref="CreatedUtc"/> are ignored: the database assigns the id and the repository stamps the time.
/// </summary>
public sealed record DysonPendingUserQuestion(
    ulong Id,
    Guid SessionId,
    Guid TurnId,
    Guid QuestionGuid,
    string Message,
    string Question,
    IReadOnlyList<string> Choices,
    bool MultiSelect,
    bool AllowCustomAnswer,
    int? SubagentId,
    Guid? EventId,
    DateTime CreatedUtc);
