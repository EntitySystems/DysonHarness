using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: subject filter + shared provider visibility + ManageSharedProviders gate.
/// </summary>
public class DysonSubjectIsolationTests
{
    [Fact]
    public void Run()
    {
        AssertSessionIsolation();
        AssertWorkDirectoryIsolation();
        AssertSharedProviderListVisibility();
        AssertManageSharedProvidersDenial();
    }

    private static void AssertSessionIsolation()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var subject = DysonTempDb.Subject("subject-a");
        var sessions = DysonTempDb.Sessions(accessor, subject);

        var created = sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 1,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "iso",
        }).GetAwaiter().GetResult();
        if (created.IsError)
            throw new InvalidOperationException(created.Error);

        var sessionId = created.Value;

        subject.SubjectId = "subject-b";
        var listed = sessions.ListSessionsAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);
        if (listed.Value.Count != 0)
            throw new InvalidOperationException("Subject B must not list Subject A sessions.");

        var get = sessions.GetFullSessionAsync(sessionId).GetAwaiter().GetResult();
        if (!get.IsError)
            throw new InvalidOperationException("Cross-subject GetFullSessionAsync must error.");
        if (!get.Error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Expected not-found error, got: {get.Error}");
    }

    private static void AssertWorkDirectoryIsolation()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var subject = DysonTempDb.Subject("subject-a");
        var workdirs = DysonTempDb.WorkDirectories(accessor, subject);

        var path = Path.Combine(Path.GetTempPath(), $"dyson-wd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            var created = workdirs.CreateAsync(path, "A").GetAwaiter().GetResult();
            if (created.IsError)
                throw new InvalidOperationException(created.Error);

            var id = created.Value;

            subject.SubjectId = "subject-b";
            var listed = workdirs.ListAsync().GetAwaiter().GetResult();
            if (listed.IsError)
                throw new InvalidOperationException(listed.Error);
            if (listed.Value.Count != 0)
                throw new InvalidOperationException("Subject B must not list Subject A work directories.");

            var get = workdirs.GetAsync(id).GetAwaiter().GetResult();
            if (!get.IsError)
                throw new InvalidOperationException("Cross-subject GetAsync must error.");
            if (!get.Error.Contains("not found", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Expected not-found error, got: {get.Error}");
        }
        finally
        {
            try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static void AssertSharedProviderListVisibility()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var subject = DysonTempDb.Subject("subject-a");
        var models = DysonTempDb.Models(accessor, subject);

        var own = models.CreateProviderAsync(new DysonModelProviderEntity
        {
            DisplayName = "Own A",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "http://127.0.0.1/a",
            ApiKey = "a",
        }).GetAwaiter().GetResult();
        if (own.IsError)
            throw new InvalidOperationException(own.Error);

        var shared = models.CreateProviderAsync(
            new DysonModelProviderEntity
            {
                DisplayName = "Shared P",
                ProviderKind = DysonProviderKinds.OpenAICompatible,
                BaseUrl = "http://127.0.0.1/shared",
                ApiKey = "s",
            },
            shared: true).GetAwaiter().GetResult();
        if (shared.IsError)
            throw new InvalidOperationException(shared.Error);

        subject.SubjectId = "subject-b";
        var ownB = models.CreateProviderAsync(new DysonModelProviderEntity
        {
            DisplayName = "Own B",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "http://127.0.0.1/b",
            ApiKey = "b",
        }).GetAwaiter().GetResult();
        if (ownB.IsError)
            throw new InvalidOperationException(ownB.Error);

        var listed = models.ListProvidersAsync().GetAwaiter().GetResult();
        if (listed.IsError)
            throw new InvalidOperationException(listed.Error);

        var names = listed.Value.Select(p => p.DisplayName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (names is not ["Own B", "Shared P"])
        {
            throw new InvalidOperationException(
                "Subject B must see shared + own only, not A's owned. Got: " + string.Join(", ", names));
        }

        var sharedRow = listed.Value.Single(p => p.DisplayName == "Shared P");
        if (!string.Equals(sharedRow.SubjectId, DysonSubjects.Shared, StringComparison.Ordinal))
            throw new InvalidOperationException("Shared provider SubjectId must be the shared sentinel.");
    }

    private static void AssertManageSharedProvidersDenial()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var models = DysonTempDb.Models(
            accessor,
            DysonTempDb.Subject("subject-a"),
            new DysonTempDb.DenyingAccessEvaluator());

        var denied = models.CreateProviderAsync(
            new DysonModelProviderEntity
            {
                DisplayName = "Denied Shared",
                ProviderKind = DysonProviderKinds.OpenAICompatible,
                BaseUrl = "http://127.0.0.1/x",
                ApiKey = "x",
            },
            shared: true).GetAwaiter().GetResult();
        if (!denied.IsError)
            throw new InvalidOperationException("Shared create must fail when ManageSharedProviders is denied.");
        if (!denied.Error.Contains("ManageSharedProviders", StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected ManageSharedProviders denial, got: {denied.Error}");

        // Subject-owned create still allowed (permission gate is shared-only).
        var own = models.CreateProviderAsync(new DysonModelProviderEntity
        {
            DisplayName = "Still Own",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "http://127.0.0.1/own",
            ApiKey = "o",
        }).GetAwaiter().GetResult();
        if (own.IsError)
            throw new InvalidOperationException(own.Error);
    }
}
