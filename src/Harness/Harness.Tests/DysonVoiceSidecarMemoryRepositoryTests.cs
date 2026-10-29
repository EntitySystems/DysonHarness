using DysonHarness;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harness.Tests;

/// <summary>
/// ponytail: missing row is empty/version 0, writes bump version, transform error and the 16,384 cap
/// write nothing, session delete cascades the row, and another subject cannot see or write it.
/// </summary>
public class DysonVoiceSidecarMemoryRepositoryTests
{
    [Fact]
    public async Task Get_MissingRow_IsEmptyVersionZero()
    {
        await using var fixture = await MemoryFixture.CreateAsync();
        var got = await fixture.Memory.GetAsync(fixture.SessionId);
        AssertOk(got);
        Assert.Equal(fixture.SessionId, got.Value.SessionId);
        Assert.Equal("", got.Value.Content);
        Assert.Equal(0, got.Value.Version);
        Assert.Null(got.Value.UpdatedUtc);
        Assert.Equal(0, await fixture.CountRowsAsync());
    }

    [Fact]
    public async Task Apply_WritesBumpVersion_ThenAgain()
    {
        await using var fixture = await MemoryFixture.CreateAsync();
        var first = await fixture.Memory.ApplyAsync(fixture.SessionId, _ => Result<string, string>.AsValue("hello"));
        AssertOk(first);
        Assert.Equal("hello", first.Value.Content);
        Assert.Equal(1, first.Value.Version);
        Assert.NotNull(first.Value.UpdatedUtc);

        await Task.Delay(20);

        var second = await fixture.Memory.ApplyAsync(
            fixture.SessionId,
            current => Result<string, string>.AsValue(current + "!"));
        AssertOk(second);
        Assert.Equal("hello!", second.Value.Content);
        Assert.Equal(2, second.Value.Version);
        Assert.True(second.Value.UpdatedUtc > first.Value.UpdatedUtc);

        var got = await fixture.Memory.GetAsync(fixture.SessionId);
        AssertOk(got);
        Assert.Equal("hello!", got.Value.Content);
        Assert.Equal(2, got.Value.Version);
    }

    [Fact]
    public async Task Apply_TransformError_WritesNothing()
    {
        await using var fixture = await MemoryFixture.CreateAsync();
        var seeded = await fixture.Memory.ApplyAsync(fixture.SessionId, _ => Result<string, string>.AsValue("keep"));
        AssertOk(seeded);

        var rejected = await fixture.Memory.ApplyAsync(
            fixture.SessionId,
            _ => Result<string, string>.AsError("no match"));
        Assert.True(rejected.IsError);
        Assert.Equal("no match", rejected.Error);

        var got = await fixture.Memory.GetAsync(fixture.SessionId);
        AssertOk(got);
        Assert.Equal("keep", got.Value.Content);
        Assert.Equal(1, got.Value.Version);
        Assert.Equal(seeded.Value.UpdatedUtc, got.Value.UpdatedUtc);
    }

    [Fact]
    public async Task Apply_OverCap_WritesNothing_AndKeepsVersion()
    {
        await using var fixture = await MemoryFixture.CreateAsync();
        var over = new string('x', DysonVoiceSidecarMemoryLimits.MaxChars + 1);
        var rejected = await fixture.Memory.ApplyAsync(fixture.SessionId, _ => Result<string, string>.AsValue(over));
        Assert.True(rejected.IsError);
        Assert.Equal("Memory cannot grow past 16,384 characters.", rejected.Error);
        Assert.Equal(0, await fixture.CountRowsAsync());

        var exact = new string('y', DysonVoiceSidecarMemoryLimits.MaxChars);
        var ok = await fixture.Memory.ApplyAsync(fixture.SessionId, _ => Result<string, string>.AsValue(exact));
        AssertOk(ok);
        Assert.Equal(1, ok.Value.Version);

        var grown = await fixture.Memory.ApplyAsync(fixture.SessionId, _ => Result<string, string>.AsValue(over));
        Assert.True(grown.IsError);
        Assert.Equal("Memory cannot grow past 16,384 characters.", grown.Error);

        var got = await fixture.Memory.GetAsync(fixture.SessionId);
        AssertOk(got);
        Assert.Equal(exact, got.Value.Content);
        Assert.Equal(1, got.Value.Version);
        Assert.Equal(ok.Value.UpdatedUtc, got.Value.UpdatedUtc);
    }

    [Fact]
    public async Task DeleteSession_CascadesMemoryRow()
    {
        await using var fixture = await MemoryFixture.CreateAsync();
        var written = await fixture.Memory.ApplyAsync(fixture.SessionId, _ => Result<string, string>.AsValue("gone"));
        AssertOk(written);
        Assert.Equal(1, await fixture.CountRowsAsync());

        var deleted = await fixture.Sessions.DeleteSessionAsync(fixture.SessionId);
        Assert.True(deleted.IsSuccess, deleted.IsError ? deleted.Error : null);

        Assert.Equal(0, await fixture.CountRowsAsync());
    }

    [Fact]
    public async Task SubjectIsolation_OtherSubjectSeesNothing()
    {
        await using var fixture = await MemoryFixture.CreateAsync();
        var written = await fixture.Memory.ApplyAsync(fixture.SessionId, _ => Result<string, string>.AsValue("mine"));
        AssertOk(written);

        fixture.Subject.SubjectId = "subject-b";
        var get = await fixture.Memory.GetAsync(fixture.SessionId);
        Assert.True(get.IsError);
        Assert.Equal($"Session '{fixture.SessionId}' not found.", get.Error);

        var apply = await fixture.Memory.ApplyAsync(fixture.SessionId, _ => Result<string, string>.AsValue("theirs"));
        Assert.True(apply.IsError);
        Assert.Equal($"Session '{fixture.SessionId}' not found.", apply.Error);

        fixture.Subject.SubjectId = "subject-a";
        var still = await fixture.Memory.GetAsync(fixture.SessionId);
        AssertOk(still);
        Assert.Equal("mine", still.Value.Content);
        Assert.Equal(1, still.Value.Version);
    }

    private static void AssertOk(Result<DysonVoiceSidecarMemory, string> result)
    {
        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
    }

    private sealed class MemoryFixture : IAsyncDisposable
    {
        private MemoryFixture(
            DysonDbAccessor accessor,
            SqliteConnection connection,
            DysonTempDb.MutableSubjectContext subject,
            DysonSessionRepository sessions,
            DysonVoiceSidecarMemoryRepository memory,
            Guid sessionId)
        {
            Accessor = accessor;
            Connection = connection;
            Subject = subject;
            Sessions = sessions;
            Memory = memory;
            SessionId = sessionId;
        }

        public DysonDbAccessor Accessor { get; }
        public SqliteConnection Connection { get; }
        public DysonTempDb.MutableSubjectContext Subject { get; }
        public DysonSessionRepository Sessions { get; }
        public DysonVoiceSidecarMemoryRepository Memory { get; }
        public Guid SessionId { get; }

        public static async Task<MemoryFixture> CreateAsync()
        {
            var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
            var subject = DysonTempDb.Subject("subject-a");
            var sessions = DysonTempDb.Sessions(accessor, subject);
            var memory = DysonTempDb.VoiceSidecarMemories(accessor, subject);
            var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
            {
                RuntimeId = 0,
                AgentMode = DysonSessionPolicy.VoiceSidecarMode,
                SystemPromptSnapshot = "sidecar",
            });
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            return new MemoryFixture(accessor, connection, subject, sessions, memory, created.Value);
        }

        public Task<int> CountRowsAsync() =>
            Accessor.RunAsync((db, ct) => db.VoiceSidecarMemories.CountAsync(ct));

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
    }
}
