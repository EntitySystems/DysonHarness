using System.Text.Json;
using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: SQLite todo timestamps come back as UTC (JSON keeps Z), UpdateTodo bumps
/// UpdatedUtc only, and the 50-todo cap is a Result error until one is deleted.
/// </summary>
public class DysonSessionTodoTimestampTests
{
    [Fact]
    public async Task RoundTrip_KindIsUtc_AndJsonEndsWithZ()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var sessions = DysonTempDb.Sessions(accessor);
            var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
            {
                RuntimeId = 1,
                AgentMode = DysonAgentModes.Work,
                SystemPromptSnapshot = "test",
            });
            if (created.IsError)
                throw new InvalidOperationException(created.Error);

            var todo = await sessions.CreateTodoAsync(new DysonSessionTodoCreateRequest
            {
                SessionId = created.Value,
                TaskCode = "t1",
                DisplayName = "One",
            });
            if (todo.IsError)
                throw new InvalidOperationException(todo.Error);

            var listed = await sessions.ListTodosAsync(created.Value);
            if (listed.IsError)
                throw new InvalidOperationException(listed.Error);
            if (listed.Value.Count != 1)
                throw new InvalidOperationException($"Expected 1 todo, got {listed.Value.Count}.");

            var row = listed.Value[0];
            if (row.CreatedUtc.Kind != DateTimeKind.Utc || row.UpdatedUtc.Kind != DateTimeKind.Utc)
            {
                throw new InvalidOperationException(
                    $"Expected Utc kind, got created={row.CreatedUtc.Kind} updated={row.UpdatedUtc.Kind}.");
            }

            var json = JsonSerializer.Serialize(new { createdUtc = row.CreatedUtc, updatedUtc = row.UpdatedUtc });
            if (!json.Contains("\"createdUtc\":\"", StringComparison.Ordinal)
                || !json.Contains("Z\"", StringComparison.Ordinal)
                || json.IndexOf("Z\"", StringComparison.Ordinal) == json.LastIndexOf("Z\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected both timestamps to end with Z, got: {json}");
            }
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task UpdateTodo_BumpsUpdatedUtc_LeavesCreatedUtc()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var sessions = DysonTempDb.Sessions(accessor);
            var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
            {
                RuntimeId = 1,
                AgentMode = DysonAgentModes.Work,
                SystemPromptSnapshot = "test",
            });
            if (created.IsError)
                throw new InvalidOperationException(created.Error);

            var todo = await sessions.CreateTodoAsync(new DysonSessionTodoCreateRequest
            {
                SessionId = created.Value,
                TaskCode = "t1",
                DisplayName = "One",
            });
            if (todo.IsError)
                throw new InvalidOperationException(todo.Error);

            await Task.Delay(20);

            var status = await sessions.UpdateTodoAsync(new DysonSessionTodoUpdateRequest
            {
                SessionId = created.Value,
                TaskCode = "t1",
                Status = DysonSessionTodoStatus.Ongoing,
            });
            if (status.IsError)
                throw new InvalidOperationException(status.Error);
            if (status.Value.CreatedUtc != todo.Value.CreatedUtc)
                throw new InvalidOperationException("Status update changed CreatedUtc.");
            if (status.Value.UpdatedUtc <= todo.Value.CreatedUtc)
                throw new InvalidOperationException("Status update did not bump UpdatedUtc.");

            await Task.Delay(20);

            var comment = await sessions.UpdateTodoAsync(new DysonSessionTodoUpdateRequest
            {
                SessionId = created.Value,
                TaskCode = "t1",
                AppendComment = "note",
            });
            if (comment.IsError)
                throw new InvalidOperationException(comment.Error);
            if (comment.Value.CreatedUtc != todo.Value.CreatedUtc)
                throw new InvalidOperationException("Comment append changed CreatedUtc.");
            if (comment.Value.UpdatedUtc <= status.Value.UpdatedUtc)
                throw new InvalidOperationException("Comment append did not bump UpdatedUtc.");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task CreateTodo_FiftySucceeds_FiftyFirstReturnsLimit_DeleteThenCreateSucceeds()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var sessions = DysonTempDb.Sessions(accessor);
            var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
            {
                RuntimeId = 1,
                AgentMode = DysonAgentModes.Work,
                SystemPromptSnapshot = "test",
            });
            if (created.IsError)
                throw new InvalidOperationException(created.Error);

            for (var i = 0; i < DysonSessionTodoLimits.MaxPerSession; i++)
            {
                var todo = await sessions.CreateTodoAsync(new DysonSessionTodoCreateRequest
                {
                    SessionId = created.Value,
                    TaskCode = $"t{i}",
                    DisplayName = $"Todo {i}",
                });
                if (todo.IsError)
                    throw new InvalidOperationException($"Create {i} failed: {todo.Error}");
            }

            var over = await sessions.CreateTodoAsync(new DysonSessionTodoCreateRequest
            {
                SessionId = created.Value,
                TaskCode = "over",
                DisplayName = "Over",
            });
            if (!over.IsError || over.Error != DysonSessionTodoLimits.LimitMessage)
            {
                throw new InvalidOperationException(
                    $"Expected exact limit message, got: {(over.IsError ? over.Error : "ok")}");
            }

            var deleted = await sessions.DeleteTodoAsync(created.Value, "t0");
            if (deleted.IsError)
                throw new InvalidOperationException(deleted.Error);

            var again = await sessions.CreateTodoAsync(new DysonSessionTodoCreateRequest
            {
                SessionId = created.Value,
                TaskCode = "again",
                DisplayName = "Again",
            });
            if (again.IsError)
                throw new InvalidOperationException($"Create after delete failed: {again.Error}");
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
