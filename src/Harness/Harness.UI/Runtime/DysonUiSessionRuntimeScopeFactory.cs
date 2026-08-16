using Microsoft.Extensions.DependencyInjection;

namespace DysonHarness;

/// <summary>
/// Host composition for a retained per-subject scope. Binds Cloud subjects via
/// <see cref="DysonCloudSubjectScope.TryBind"/>; Local hosting keeps
/// <see cref="DysonSubjects.Local"/>. Circuit disposal must not dispose the lease.
/// </summary>
internal sealed class DysonUiSessionRuntimeScopeFactory(IServiceScopeFactory scopeFactory)
    : IDysonSessionRuntimeScopeFactory
{
    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    public async Task<Result<RuntimeScopeLease, string>> CreateAsync(
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = DysonSessionRuntimeRegistry.NormalizeSubjectId(subjectId);
        if (normalized.IsError)
            return Result<RuntimeScopeLease, string>.AsError(normalized.Error);

        var subject = normalized.Value;
        var scope = _scopeFactory.CreateAsyncScope();
        var transferred = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            DysonCloudSubjectScope.TryBind(scope.ServiceProvider, subject);

            var bound = ValidateBoundSubject(scope.ServiceProvider, subject);
            if (bound.IsError)
                return Result<RuntimeScopeLease, string>.AsError(bound.Error);

            DysonSessionRuntime runtime;
            try
            {
                runtime = scope.ServiceProvider.GetRequiredService<DysonSessionRuntime>();
            }
            catch (InvalidOperationException ex)
            {
                return Result<RuntimeScopeLease, string>.AsError(
                    $"Failed to resolve session runtime: {ex.Message}");
            }

            if (!string.Equals(runtime.SubjectId, subject, StringComparison.Ordinal))
            {
                return Result<RuntimeScopeLease, string>.AsError(
                    "Runtime scope factory resolved a runtime for a different subject.");
            }

            var retained = scope;
            transferred = true;
            return Result<RuntimeScopeLease, string>.AsValue(
                new RuntimeScopeLease(subject, runtime, () => retained.DisposeAsync()));
        }
        finally
        {
            if (!transferred)
                await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Result<string, string> ValidateBoundSubject(
        IServiceProvider services,
        string subject)
    {
        if (services.GetService<DysonScopedSubjectContext>() is { } cloud)
        {
            if (!cloud.IsSet)
            {
                return Result<string, string>.AsError(
                    "Cloud subject was not bound for the retained runtime scope.");
            }

            if (!string.Equals(cloud.SubjectId, subject, StringComparison.Ordinal))
            {
                return Result<string, string>.AsError(
                    "Runtime scope factory bound a different subject.");
            }

            return Result<string, string>.AsValue(subject);
        }

        var context = services.GetService<IDysonSubjectContext>();
        if (context is null)
            return Result<string, string>.AsError("Subject context is not registered.");

        if (!string.Equals(context.SubjectId, subject, StringComparison.Ordinal))
        {
            return Result<string, string>.AsError(
                "Local hosting can only bind the local subject.");
        }

        return Result<string, string>.AsValue(subject);
    }
}
