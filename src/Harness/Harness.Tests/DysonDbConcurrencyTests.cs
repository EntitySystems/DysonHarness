using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: file-backed parallel store ops must not surface EF concurrent-context or SQLITE_BUSY.
/// </summary>
public class DysonDbConcurrencyTests
{
    [Fact]
    public async Task Parallel_CreateTodo_AppendLog_UpsertTurn_And_Settings_Succeed()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var sessions = new DysonSessionStore(accessor);
            var settings = new DysonAppSettingsStore(accessor);

            var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
            {
                RuntimeId = 1,
                AgentMode = DysonAgentModes.Work,
                SystemPromptSnapshot = "test",
            });
            if (created.IsError)
                throw new InvalidOperationException(created.Error);

            var sessionId = created.Value;
            var errors = new List<string>();

            var tasks = new List<Task>();
            for (var i = 0; i < 20; i++)
            {
                var n = i;
                tasks.Add(Task.Run(async () =>
                {
                    var todo = await sessions.CreateTodoAsync(new DysonSessionTodoCreateRequest
                    {
                        SessionId = sessionId,
                        TaskCode = $"T{n:D3}",
                        DisplayName = $"Todo {n}",
                    });
                    if (todo.IsError)
                        lock (errors) errors.Add(todo.Error);
                }));

                tasks.Add(Task.Run(async () =>
                {
                    var log = await sessions.AppendLogAsync(new DysonSessionLogEntry
                    {
                        SessionId = sessionId,
                        Kind = DysonSessionLogKind.SessionCreated.ToString(),
                        PayloadJson = $"{{\"n\":{n}}}",
                    });
                    if (log.IsError)
                        lock (errors) errors.Add(log.Error);
                }));

                tasks.Add(Task.Run(async () =>
                {
                    var turn = await sessions.UpsertTurnAsync(new DysonTurnEntity
                    {
                        SessionId = sessionId,
                        Sequence = n + 1,
                        Kind = DysonAgentTurnKind.Normal,
                        ToolStateJson = "{}",
                        Instruction = $"turn-{n}",
                    });
                    if (turn.IsError)
                        lock (errors) errors.Add(turn.Error);
                }));

                tasks.Add(Task.Run(async () =>
                {
                    var set = await settings.SetAsync($"k{n}", $"v{n}");
                    if (set.IsError)
                        lock (errors) errors.Add(set.Error);
                }));
            }

            await Task.WhenAll(tasks);

            if (errors.Count > 0)
            {
                var joined = string.Join("\n", errors.Take(10));
                throw new InvalidOperationException(
                    $"Parallel store ops failed ({errors.Count}):\n{joined}");
            }

            foreach (var err in errors)
            {
                if (err.Contains("second operation", StringComparison.OrdinalIgnoreCase)
                    || err.Contains("Error 5", StringComparison.OrdinalIgnoreCase)
                    || err.Contains("SQLITE_BUSY", StringComparison.OrdinalIgnoreCase)
                    || err.Contains("database is locked", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Contention leaked into Result: {err}");
                }
            }

            var full = await sessions.GetFullSessionAsync(sessionId);
            if (full.IsError)
                throw new InvalidOperationException(full.Error);

            if (full.Value.Todos.Count != 20)
                throw new InvalidOperationException($"Expected 20 todos, got {full.Value.Todos.Count}.");
            if (full.Value.Logs.Count != 20)
                throw new InvalidOperationException($"Expected 20 logs, got {full.Value.Logs.Count}.");
            if (full.Value.Turns.Count != 20)
                throw new InvalidOperationException($"Expected 20 turns, got {full.Value.Turns.Count}.");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Contended_Parallel_UpsertTurn_Succeeds()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var sessions = new DysonSessionStore(accessor);
            var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
            {
                RuntimeId = 2,
                AgentMode = DysonAgentModes.Work,
                SystemPromptSnapshot = "test",
            });
            if (created.IsError)
                throw new InvalidOperationException(created.Error);

            var sessionId = created.Value;
            var turnId = Guid.NewGuid();

            // Seed once so all writers update the same row.
            var seed = await sessions.UpsertTurnAsync(new DysonTurnEntity
            {
                Id = turnId,
                SessionId = sessionId,
                Sequence = 1,
                Kind = DysonAgentTurnKind.Normal,
                ToolStateJson = "{}",
                AssistantText = "seed",
            });
            if (seed.IsError)
                throw new InvalidOperationException(seed.Error);

            var errors = new List<string>();
            var writers = Enumerable.Range(0, 40).Select(n => Task.Run(async () =>
            {
                var result = await sessions.UpsertTurnAsync(new DysonTurnEntity
                {
                    Id = turnId,
                    SessionId = sessionId,
                    Sequence = 1,
                    Kind = DysonAgentTurnKind.Normal,
                    ToolStateJson = "{}",
                    AssistantText = $"w{n}",
                });
                if (result.IsError)
                    lock (errors) errors.Add(result.Error);
            }));

            await Task.WhenAll(writers);

            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"UpsertTurn contention retries failed: {string.Join("; ", errors.Take(5))}");
            }

            var full = await sessions.GetFullSessionAsync(sessionId);
            if (full.IsError)
                throw new InvalidOperationException(full.Error);
            if (full.Value.Turns.Count != 1)
                throw new InvalidOperationException($"Expected 1 turn, got {full.Value.Turns.Count}.");
            if (string.IsNullOrEmpty(full.Value.Turns[0].AssistantText))
                throw new InvalidOperationException("Turn text missing after contended upserts.");
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
