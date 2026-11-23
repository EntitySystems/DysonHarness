namespace DysonHarness;

/// <summary>
/// What a SubagentTasks row shows: either <see cref="Message"/> (card text: no session, unknown id, stopped) or the
/// agent title, its status, whether it runs, and the X/N todo pill.
/// </summary>
public sealed record DysonA2uiSubagentTasksCard(
    string? Message,
    int AgentId,
    string Title,
    DysonSessionStatus Status,
    bool IsRunning,
    int Complete,
    int Total)
{
    public string Pill => $"{Complete}/{Total}";
}

/// <summary>
/// Dyson-catalog <c>SubagentTasks</c> (<c>agentId</c>): a live row for one direct child of the session that owns the
/// surface. Counts come from the child's own todo list; updates arrive on the bus
/// (<see cref="DysonSessionTodosChangedEvent"/> for the counts, <see cref="DysonSubagentStatusChangedEvent"/> for the status).
/// </summary>
public static class DysonA2uiSubagentTasks
{
    public const string ComponentName = "SubagentTasks";

    public static (int Complete, int Total) Count(IReadOnlyList<DysonSessionTodo> todos) =>
        (todos.Count(static t => t.Status == DysonSessionTodoStatus.Complete), todos.Count);

    /// <summary>In-memory only (todo snapshot under the session's lock), so safe on the circuit. Never throws.</summary>
    public static DysonA2uiSubagentTasksCard Build(DysonAgentSession? owner, int agentId)
    {
        if (owner is null)
            return Text(agentId, "No agent session.");
        if (!owner.TryGetSubagent(agentId, out var child))
            return Text(agentId, $"Agent #{agentId} not found.");

        var status = child.Status;
        if (status == DysonSessionStatus.Stopped)
            return Text(agentId, $"Agent #{agentId} was stopped.");

        var (complete, total) = Count(child.Todos);
        var title = string.IsNullOrWhiteSpace(child.DisplayTitle) ? "Agent" : child.DisplayTitle;
        // Same one-field rule as DysonSubagentHostLogic.IsRunning: the latest turn is not read.
        return new DysonA2uiSubagentTasksCard(null, agentId, title, status, status == DysonSessionStatus.Active, complete, total);
    }

    /// <summary>
    /// Calls <paramref name="changed"/> with a rebuilt card whenever the child's todos or status change.
    /// An unknown agent gets a no-op watch.
    /// ponytail: an id that does not exist at first render is never re-resolved (no spawn watch); upgrade path =
    /// also subscribe the owner's scope for <see cref="DysonSubagentSpawnedEvent"/>.
    /// </summary>
    public static Result<IDisposable, string> Watch(
        DysonMessageBus bus,
        DysonAgentSession? owner,
        int agentId,
        Action<DysonA2uiSubagentTasksCard> changed)
    {
        if (bus is null || changed is null)
            return Result<IDisposable, string>.AsError("bus and callback are required");
        if (owner is null || !owner.TryGetSubagent(agentId, out var child) || child.PersistenceId == Guid.Empty)
            return Result<IDisposable, string>.AsValue(new Subscriptions([]));

        var id = child.PersistenceId;
        var scope = DysonBusScopes.Session(id);
        var todos = bus.Subscribe<DysonSessionTodosChangedEvent>(scope, e =>
        {
            if (e.PersistenceId == id)
                changed(Build(owner, agentId));
        });
        if (todos.IsError)
            return Result<IDisposable, string>.AsError(todos.Error);

        var status = bus.Subscribe<DysonSubagentStatusChangedEvent>(scope, e =>
        {
            if (e.PersistenceId == id)
                changed(Build(owner, agentId));
        });
        if (status.IsError)
        {
            todos.Value.Dispose();
            return Result<IDisposable, string>.AsError(status.Error);
        }

        return Result<IDisposable, string>.AsValue(new Subscriptions([todos.Value, status.Value]));
    }

    private static DysonA2uiSubagentTasksCard Text(int agentId, string message) =>
        new(message, agentId, "", DysonSessionStatus.Active, false, 0, 0);

    private sealed class Subscriptions(IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
                item.Dispose();
        }
    }
}
