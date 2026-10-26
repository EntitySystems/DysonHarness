#if DYSON_PREVIEW
using DysonHarness;

namespace Harness.UI.Demo;

public sealed class AesterShardingDemoScenario : IDysonDemoScenario
{
    private const string ChoiceAccount =
        "Shard by account id — one account's whole history stays together";

    private const string ChoiceMonth =
        "Shard by entry month — time-based partitions, simpler archival";

    private const string ChoiceNone =
        "Don't shard yet — revisit after the PostgreSQL cutover";

    private const string BuildReport =
        "Build finished. The ledger schema is in: accounts, journal entries and lines, balance snapshots, the double-entry balance check, the SQLite backfill, and schema tests. All six slices are merged and the schema tests pass.";

    private const string PlanMarkdown =
        "\u0023\u0023 Context\n\n" +
        "Aester Financials' ledger is one SQLite file. This plan moves it to PostgreSQL.\n\n" +
        "\u0023\u0023 Data model\n\n" +
        "- `account` — code, kind, currency\n" +
        "- `journal_entry` — posted_at, memo\n" +
        "- `journal_line` — entry, account, debit, credit\n" +
        "- `balance_snapshot` — account, as_of, amount\n\n" +
        "\u0023\u0023 Invariants\n\n" +
        "- Every entry balances (sum of debit equals sum of credit).\n" +
        "- Lines are immutable once posted.\n" +
        "- Balances are derived. Snapshots are a cache.\n\n" +
        "\u0023\u0023 Migration\n\n" +
        "Create the schema, backfill from SQLite, dual-run reads, then cut over.\n\n" +
        "\u0023\u0023 Out of scope\n\n" +
        "Sharding (a follow-up), reporting, and auth.\n";

    /// <summary>How long the finished schema visualization stays on screen before the run ends.</summary>
    public const int VisualizationHoldMs = 2000;

    public string Id => "aester-sharding";

    public string DisplayName => "Planning + database sharding";

    public string Description =>
        "Runs a scripted Meta Agent session for a fictional Aester Financials ledger — plan, build, a sharding question, then a schema visualization — with no provider calls.";

    public async Task<VoidResult<string>> RunAsync(IDysonDemoHooks hooks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hooks);

        async Task Step(int delayMs) => await Task.Delay(delayMs, ct).ConfigureAwait(true);

        var started = await hooks.StartAsync("Aester Financials Backend Services", ct).ConfigureAwait(true);
        if (started.IsError)
            return VoidResult<string>.AsError(started.Error);

        var seeded = await SeedHistoryAsync(hooks, ct).ConfigureAwait(true);
        if (seeded.IsError)
            return seeded;

        await Step(500).ConfigureAwait(true);
        var planned = await hooks.AppendUserTurnAsync(
            "Plan a PostgreSQL schema for Aester Financials' ledger: double-entry journal, accounts, and balances. Write it up as a plan.",
            assistantText: "",
            ct).ConfigureAwait(true);
        if (planned.IsError)
            return planned;

        await Step(500).ConfigureAwait(true);
        var confirmed = await hooks.AppendAssistantMessageAsync(
            "You asked for a PostgreSQL schema plan for Aester Financials' ledger — a double-entry journal, accounts, and balances. I'm starting a planning drone to look around the backend and write it up.",
            question: null,
            ct).ConfigureAwait(true);
        if (confirmed.IsError)
            return VoidResult<string>.AsError(confirmed.Error);

        await Step(500).ConfigureAwait(true);
        var planner = await hooks.AddAgentAsync(null, "Draft the schema plan", DysonAgentModes.MetaAgentDrone, ct)
            .ConfigureAwait(true);
        if (planner.IsError)
            return VoidResult<string>.AsError(planner.Error);

        await Step(1000).ConfigureAwait(true);
        var domain = await hooks.AddAgentAsync(
            planner.Value.PersistenceId,
            "Explore the ledger domain model",
            DysonAgentModes.Explore,
            ct).ConfigureAwait(true);
        if (domain.IsError)
            return VoidResult<string>.AsError(domain.Error);

        await Step(1500).ConfigureAwait(true);
        var domainDone = await hooks.SetAgentStatusAsync(
            domain.Value.PersistenceId,
            DysonSessionStatus.Completed,
            "Mapped journal, account, and balance types",
            ct).ConfigureAwait(true);
        if (domainDone.IsError)
            return domainDone;

        await Step(500).ConfigureAwait(true);
        var persistence = await hooks.AddAgentAsync(
            planner.Value.PersistenceId,
            "Explore the persistence setup",
            DysonAgentModes.Explore,
            ct).ConfigureAwait(true);
        if (persistence.IsError)
            return VoidResult<string>.AsError(persistence.Error);

        await Step(2000).ConfigureAwait(true);
        var persistenceDone = await hooks.SetAgentStatusAsync(
            persistence.Value.PersistenceId,
            DysonSessionStatus.Completed,
            "Found a single SQLite database, no sharding",
            ct).ConfigureAwait(true);
        if (persistenceDone.IsError)
            return persistenceDone;

        await Step(1000).ConfigureAwait(true);
        var migrations = await hooks.AddAgentAsync(
            planner.Value.PersistenceId,
            "Check the existing migration history",
            DysonAgentModes.Explore,
            ct).ConfigureAwait(true);
        if (migrations.IsError)
            return VoidResult<string>.AsError(migrations.Error);

        await Step(1500).ConfigureAwait(true);
        var migrationsDone = await hooks.SetAgentStatusAsync(
            migrations.Value.PersistenceId,
            DysonSessionStatus.Completed,
            "No PostgreSQL migrations exist yet",
            ct).ConfigureAwait(true);
        if (migrationsDone.IsError)
            return migrationsDone;

        await Step(2500).ConfigureAwait(true);
        var plan = await hooks.CreatePlanAsync("Ledger schema for Aester Financials", PlanMarkdown, ct)
            .ConfigureAwait(true);
        if (plan.IsError)
            return VoidResult<string>.AsError(plan.Error);

        var opened = await hooks.OpenPlanAsync(plan.Value, ct).ConfigureAwait(true);
        if (opened.IsError)
            return opened;

        var plannerDone = await hooks.SetAgentStatusAsync(
            planner.Value.PersistenceId,
            DysonSessionStatus.Completed,
            "Submitted the plan",
            ct).ConfigureAwait(true);
        if (plannerDone.IsError)
            return plannerDone;

        await Step(1000).ConfigureAwait(true);
        var buildPrompt = await hooks.AppendUserTurnAsync(
            DysonMetaPlanDisplayPath.FormatBuildPrompt(plan.Value, "Ledger schema for Aester Financials"),
            "Starting the build. One coordinator will split the schema work across the backend.",
            ct).ConfigureAwait(true);
        if (buildPrompt.IsError)
            return buildPrompt;

        await Step(500).ConfigureAwait(true);
        var building = await hooks.SetPlanStatusAsync(plan.Value, DysonPlanStatus.Building, ct).ConfigureAwait(true);
        if (building.IsError)
            return building;

        var orchestrator = await hooks.AddAgentAsync(
            null,
            "Build the Aester ledger schema",
            DysonAgentModes.MetaAgentDrone,
            ct).ConfigureAwait(true);
        if (orchestrator.IsError)
            return VoidResult<string>.AsError(orchestrator.Error);

        await Step(500).ConfigureAwait(true);
        string[] slices =
        [
            "Add the account aggregate and its EF mapping",
            "Add the journal entry and journal line model",
            "Add the balance snapshot table and recompute query",
            "Enforce the double-entry balance check",
            "Write the SQLite-to-PostgreSQL backfill",
            "Add ledger schema tests",
        ];
        var workers = new Guid[slices.Length];
        for (var i = 0; i < slices.Length; i++)
        {
            var worker = await hooks.AddAgentAsync(
                orchestrator.Value.PersistenceId,
                slices[i],
                DysonAgentModes.Drone,
                ct).ConfigureAwait(true);
            if (worker.IsError)
                return VoidResult<string>.AsError(worker.Error);
            workers[i] = worker.Value.PersistenceId;
        }

        await Step(1500).ConfigureAwait(true);
        foreach (var workerId in workers)
        {
            var done = await hooks.SetAgentStatusAsync(workerId, DysonSessionStatus.Completed, null, ct)
                .ConfigureAwait(true);
            if (done.IsError)
                return done;
            if (workerId != workers[^1])
                await Step(1500).ConfigureAwait(true);
        }

        await Step(500).ConfigureAwait(true);
        var orchestratorDone = await hooks.SetAgentStatusAsync(
            orchestrator.Value.PersistenceId,
            DysonSessionStatus.Completed,
            "Build finished. The ledger schema is in: accounts, journal entries and lines, balance snapshots, the double-entry balance check, the SQLite backfill, and schema tests.",
            ct).ConfigureAwait(true);
        if (orchestratorDone.IsError)
            return orchestratorDone;

        await Step(500).ConfigureAwait(true);
        var reported = await hooks.AppendAssistantMessageAsync(BuildReport, question: null, ct).ConfigureAwait(true);
        if (reported.IsError)
            return VoidResult<string>.AsError(reported.Error);

        await Step(500).ConfigureAwait(true);
        var completed = await hooks.SetPlanStatusAsync(plan.Value, DysonPlanStatus.Completed, ct).ConfigureAwait(true);
        if (completed.IsError)
            return completed;

        await Step(1500).ConfigureAwait(true);
        var shardAsk = await hooks.AppendUserTurnAsync(
            "How would we shard the ledger once volume grows?",
            "Short version: not yet, but here is the shape of it.",
            ct).ConfigureAwait(true);
        if (shardAsk.IsError)
            return shardAsk;

        await Step(2000).ConfigureAwait(true);
        var shardDetail = await hooks.AppendUserTurnAsync(
            "What would it actually take — shard key, cross-account entries, the rest?",
            "Pulling the sharding choices together.",
            ct).ConfigureAwait(true);
        if (shardDetail.IsError)
            return shardDetail;

        await Step(500).ConfigureAwait(true);
        var question = await hooks.AppendAssistantMessageAsync(
            "The ledger is one PostgreSQL database. Here is how sharding it would look.\n\n" +
            "1. Pick a shard key (account id vs. entry date).\n" +
            "2. Decide where cross-shard entries are coordinated (a transaction that touches two accounts).\n" +
            "3. Plan the re-shard story before any data lands.\n",
            new DysonDemoQuestion(
                "Which shard key should the ledger use?",
                [ChoiceAccount, ChoiceMonth, ChoiceNone]),
            ct).ConfigureAwait(true);
        if (question.IsError)
            return VoidResult<string>.AsError(question.Error);
        if (question.Value is not Guid questionId)
            return VoidResult<string>.AsError("Demo question id was missing.");

        await Step(2500).ConfigureAwait(true);
        var answered = await hooks.AnswerQuestionAsync(questionId, ChoiceNone, ct).ConfigureAwait(true);
        if (answered.IsError)
            return answered;

        await Step(500).ConfigureAwait(true);
        var noted = await hooks.AppendAssistantMessageAsync(
            "Noted — no sharding for now. I'll keep the ledger on a single PostgreSQL database and treat sharding as a follow-up once the cutover is done.",
            question: null,
            ct).ConfigureAwait(true);
        if (noted.IsError)
            return VoidResult<string>.AsError(noted.Error);

        return await VisualizeAsync(hooks, ct).ConfigureAwait(true);
    }

    /// <summary>Final segment: the user asks for a picture of the schema; one drone draws it; it stays up, then the run ends.</summary>
    private static async Task<VoidResult<string>> VisualizeAsync(IDysonDemoHooks hooks, CancellationToken ct)
    {
        async Task Step(int delayMs) => await Task.Delay(delayMs, ct).ConfigureAwait(true);

        await Step(1500).ConfigureAwait(true);
        var asked = await hooks.AppendUserTurnAsync(
            "Can you visualize the data schema objects we've been discussing?",
            "Sure. I'm starting a drone to draw the ledger schema: the tables, their fields and keys, and where a shard key would go.",
            ct).ConfigureAwait(true);
        if (asked.IsError)
            return asked;

        await Step(500).ConfigureAwait(true);
        var drawer = await hooks.AddAgentAsync(null, "Visualize Aester ledger schema", DysonAgentModes.MetaAgentDrone, ct)
            .ConfigureAwait(true);
        if (drawer.IsError)
            return VoidResult<string>.AsError(drawer.Error);

        foreach (var progress in new[] { "Reading the ledger plan's data model", "Laying out account, journal, and balance tables" })
        {
            await Step(1000).ConfigureAwait(true);
            var stepped = await hooks.SetAgentStatusAsync(drawer.Value.PersistenceId, DysonSessionStatus.Active, progress, ct)
                .ConfigureAwait(true);
            if (stepped.IsError)
                return stepped;
        }

        await Step(1000).ConfigureAwait(true);
        var drawn = await hooks.SetAgentStatusAsync(
            drawer.Value.PersistenceId,
            DysonSessionStatus.Completed,
            "Rendered the ledger schema diagram",
            ct).ConfigureAwait(true);
        if (drawn.IsError)
            return drawn;

        await Step(500).ConfigureAwait(true);
        var posted = await hooks.PostVisualizationAsync(
            "Here is the ledger schema: account, journal_entry, journal_line, and balance_snapshot with their keys. journal_line.account_id is the shard key we'd use later; for now it stays one database.",
            AesterSchemaVisualization.Create(),
            ct).ConfigureAwait(true);
        if (posted.IsError)
            return VoidResult<string>.AsError(posted.Error);

        var shown = await hooks.OpenVisualizationAsync(ct).ConfigureAwait(true);
        if (shown.IsError)
            return shown;

        // Hold, then return: the runner's finally runs cleanup and restores the previous work dir.
        await Step(VisualizationHoldMs).ConfigureAwait(true);
        return VoidResult<string>.Success;
    }

    private static readonly (string Title, string Markdown, DysonPlanStatus Status, TimeSpan Age, bool Archived)[] History =
    [
        ("Auth service token rotation",
            "## Context\n\nAccess tokens live for an hour and refresh tokens never expire.\n\n## Change\n\nRotate refresh tokens on use and revoke the previous one.\n",
            DysonPlanStatus.Completed, TimeSpan.FromHours(6), false),
        ("Payments webhook retry queue",
            "## Context\n\nA failed webhook delivery is dropped.\n\n## Change\n\nQueue retries with backoff and a dead-letter after five attempts.\n",
            DysonPlanStatus.Completed, TimeSpan.FromDays(2), false),
        ("Statement PDF export",
            "## Context\n\nCustomers ask for statements as PDFs.\n\n## Change\n\nRender the monthly statement server-side. Parked until the ledger schema lands.\n",
            DysonPlanStatus.Stale, TimeSpan.FromDays(3), false),
        ("Customer notification preferences",
            "## Context\n\nEvery customer gets every email.\n\n## Change\n\nPer-customer opt-out for statements and payment receipts. Superseded.\n",
            DysonPlanStatus.Completed, TimeSpan.FromDays(9), true),
        ("Legacy CSV statement import",
            "## Context\n\nOld statements arrive as CSV.\n\n## Change\n\nOne-off importer. Dropped once the SQLite backfill covered it.\n",
            DysonPlanStatus.Stale, TimeSpan.FromDays(14), true),
    ];

    private static async Task<VoidResult<string>> SeedHistoryAsync(IDysonDemoHooks hooks, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var (title, markdown, status, age, archived) in History)
        {
            var seeded = await hooks.CreatePlanAsync(title, markdown, ct, status, now - age, archived)
                .ConfigureAwait(true);
            if (seeded.IsError)
                return VoidResult<string>.AsError(seeded.Error);
        }

        return VoidResult<string>.Success;
    }
}
#endif
