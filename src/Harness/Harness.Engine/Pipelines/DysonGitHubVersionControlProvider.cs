using System.Globalization;
using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// GitHub Actions through <c>gh api -i</c>. Holds no repo state; the gh path cache and the
/// missing-gh probe gate live on the instance, which belongs to one work directory.
/// </summary>
public sealed class DysonGitHubVersionControlProvider : IVersionControlProvider
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    private static readonly IReadOnlyDictionary<string, string?> Env = new Dictionary<string, string?>
    {
        ["GH_PROMPT_DISABLED"] = "1",
        ["GH_NO_UPDATE_NOTIFIER"] = "1",
        ["NO_COLOR"] = "1",
        ["GH_SPINNER_DISABLED"] = "1",
        ["CLICOLOR"] = "0",
    };

    private readonly Func<string?> _locateGh;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<Result<DysonCliOutput, string>>> _runGh;
    private readonly object _gate = new();
    private string? _ghPath;
    private DateTimeOffset _nextProbe;

    public DysonGitHubVersionControlProvider()
        : this(
            static () => DysonExecutableLocator.Find("gh", GitHubCliDirs),
            static (path, args, ct) => DysonCliProcess.RunAsync(path, args, null, Env, CommandTimeout, "gh", ct))
    {
    }

    internal DysonGitHubVersionControlProvider(
        Func<string?> locateGh,
        Func<IReadOnlyList<string>, CancellationToken, Task<Result<DysonCliOutput, string>>> runGh)
        : this(locateGh, (path, args, ct) => runGh(args, ct))
    {
    }

    private DysonGitHubVersionControlProvider(
        Func<string?> locateGh,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<Result<DysonCliOutput, string>>> runGh)
    {
        _locateGh = locateGh;
        _runGh = runGh;
    }

    public string Id => "github";

    public string DisplayName => "GitHub";

    /// <summary>Clears the cached gh path and the 120 s missing-gh probe gate.</summary>
    public void ResetProbe()
    {
        lock (_gate)
        {
            _ghPath = null;
            _nextProbe = default;
        }
    }

    public Result<DysonPipelineTarget, DysonPipelineError> TryResolveTarget(string origin)
    {
        if (DysonGitInfo.ClassifyProvider(origin) != DysonGitProvider.GitHub)
            return NotDetected();

        if (!TryParseRemote(origin, out var host, out var owner, out var name))
            return NotDetected();

        return Result<DysonPipelineTarget, DysonPipelineError>.AsValue(
            new DysonPipelineTarget(host, owner, name, $"https://{host}/{owner}/{name}"));
    }

    public Task<Result<DysonPipelineFetch<DysonPipelineRun>, DysonPipelineError>> ListRunsAsync(
        DysonPipelineTarget target, string? etag, CancellationToken ct) =>
        FetchAsync(target, etag, RunsPath(target), ParseRuns, ct);

    public Task<Result<DysonPipelineFetch<DysonPipelineJob>, DysonPipelineError>> ListJobsAsync(
        DysonPipelineTarget target, long runId, string? etag, CancellationToken ct) =>
        FetchAsync(target, etag, JobsPath(target, runId), body => ParseJobs(body, runId), ct);

    private async Task<Result<DysonPipelineFetch<T>, DysonPipelineError>> FetchAsync<T>(
        DysonPipelineTarget target, string? etag, string path, Func<string, IReadOnlyList<T>> parse,
        CancellationToken ct)
    {
        var gh = Locate();
        if (gh is null)
            return Result<DysonPipelineFetch<T>, DysonPipelineError>.AsError(new DysonPipelineError(
                DysonPipelineErrorKind.CliMissing,
                "GitHub CLI (gh) not found. Install it from https://cli.github.com and run gh auth login."));

        var args = new List<string> { "api", "-i", "--hostname", target.Host };
        if (!string.IsNullOrEmpty(etag))
            args.AddRange(["-H", $"If-None-Match: {etag}"]);
        args.Add(path);

        Result<DysonCliOutput, string> run;
        try
        {
            run = await _runGh(gh, args, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed<T>(ex.Message);
        }

        if (run.IsError)
            return Failed<T>(run.Error);

        var output = run.Value;
        if (!ParseHttp(output.Stdout, out var status, out var headers, out var body))
        {
            var auth = AuthError<T>(target, status, output.Stderr);
            return auth ?? Failed<T>("Unexpected gh output; update GitHub CLI.");
        }

        return MapOutcome(target, etag, status, headers, body, output.Stderr, parse);
    }

    private Result<DysonPipelineFetch<T>, DysonPipelineError> MapOutcome<T>(
        DysonPipelineTarget target, string? etagIn, int status, IReadOnlyDictionary<string, string> headers,
        string body, string stderr, Func<string, IReadOnlyList<T>> parse)
    {
        var auth = AuthError<T>(target, status, stderr);
        if (auth is not null)
            return auth;

        if (status is 403 or 429 && IsRateLimited(headers, out var retryAfter))
            return Result<DysonPipelineFetch<T>, DysonPipelineError>.AsError(new DysonPipelineError(
                DysonPipelineErrorKind.RateLimited, FirstLine(stderr) ?? "GitHub rate limit reached.", retryAfter));

        if (status == 404)
            return Result<DysonPipelineFetch<T>, DysonPipelineError>.AsError(new DysonPipelineError(
                DysonPipelineErrorKind.RepoNotFound,
                $"{target.Owner}/{target.Name} not found, or the gh account has no access."));

        if (status == 304)
            return Result<DysonPipelineFetch<T>, DysonPipelineError>.AsValue(new DysonPipelineFetch<T>(
                NotModified: true, [], etagIn, RateRemaining(headers), RateReset(headers)));

        if (status == 200)
        {
            try
            {
                return Result<DysonPipelineFetch<T>, DysonPipelineError>.AsValue(new DysonPipelineFetch<T>(
                    NotModified: false, parse(body), Header(headers, "etag"), RateRemaining(headers), RateReset(headers)));
            }
            catch (JsonException)
            {
                return Failed<T>("Unexpected gh output; update GitHub CLI.");
            }
        }

        if (status == 403)
            return Failed<T>(FirstLine(stderr) ?? "GitHub refused the request (403).");

        return Failed<T>(FirstLine(stderr) ?? $"gh failed (HTTP {status}).");
    }

    private static Result<DysonPipelineFetch<T>, DysonPipelineError>? AuthError<T>(
        DysonPipelineTarget target, int status, string stderr)
    {
        if (status != 401 && !LooksUnauthenticated(stderr))
            return null;
        return Result<DysonPipelineFetch<T>, DysonPipelineError>.AsError(new DysonPipelineError(
            DysonPipelineErrorKind.NotAuthenticated,
            $"gh is not signed in to {target.Host}. Run: gh auth login --hostname {target.Host}"));
    }

    private string? Locate()
    {
        lock (_gate)
        {
            if (_ghPath is not null)
                return _ghPath;
            if (DateTimeOffset.UtcNow < _nextProbe)
                return null;

            var found = _locateGh();
            if (found is not null)
            {
                _ghPath = found;
                return found;
            }

            _nextProbe = DateTimeOffset.UtcNow + ProbeInterval;
            return null;
        }
    }

    /// <summary>Splits <c>gh api -i</c> stdout into status, headers and body. False when there is no status line.</summary>
    internal static bool ParseHttp(
        string stdout, out int status, out IReadOnlyDictionary<string, string> headers, out string body)
    {
        status = 0;
        headers = EmptyHeaders;
        body = "";
        if (string.IsNullOrEmpty(stdout))
            return false;

        var lineEnd = stdout.IndexOf('\n');
        var first = (lineEnd < 0 ? stdout : stdout[..lineEnd]).TrimEnd('\r').Trim();
        if (!first.StartsWith("HTTP/", StringComparison.Ordinal))
            return false;
        var parts = first.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out status))
            return false;

        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = lineEnd < 0 ? stdout.Length : lineEnd + 1;
        while (index < stdout.Length)
        {
            var next = stdout.IndexOf('\n', index);
            var raw = (next < 0 ? stdout[index..] : stdout[index..next]).TrimEnd('\r');
            if (raw.Length == 0)
            {
                body = next < 0 ? "" : stdout[(next + 1)..];
                headers = parsed;
                return true;
            }

            var colon = raw.IndexOf(':');
            if (colon > 0)
                parsed[raw[..colon].Trim()] = raw[(colon + 1)..].Trim();
            if (next < 0)
                break;
            index = next + 1;
        }

        headers = parsed;
        return true;
    }

    /// <summary>Maps a GitHub Actions status and conclusion to a pipeline state and its label.</summary>
    internal static (DysonPipelineState State, string Label) MapState(string? status, string? conclusion)
    {
        switch (status)
        {
            case "queued":
            case "requested":
            case "pending":
                return (DysonPipelineState.Queued, "Queued");
            case "waiting":
                return (DysonPipelineState.Waiting, "Waiting");
            case "in_progress":
                return (DysonPipelineState.Running, "Running");
            case "completed":
                return conclusion switch
                {
                    "success" => (DysonPipelineState.Succeeded, "Succeeded"),
                    "failure" => (DysonPipelineState.Failed, "Failed"),
                    "timed_out" => (DysonPipelineState.Failed, "Timed out"),
                    "startup_failure" => (DysonPipelineState.Failed, "Startup failure"),
                    "cancelled" => (DysonPipelineState.Cancelled, "Cancelled"),
                    "skipped" => (DysonPipelineState.Skipped, "Skipped"),
                    "neutral" => (DysonPipelineState.Neutral, "Neutral"),
                    "stale" => (DysonPipelineState.Neutral, "Stale"),
                    "action_required" => (DysonPipelineState.Neutral, "Action required"),
                    _ => (DysonPipelineState.Neutral, string.IsNullOrEmpty(conclusion) ? "completed" : conclusion),
                };
            default:
                return (DysonPipelineState.Neutral, string.IsNullOrEmpty(status) ? "unknown" : status);
        }
    }

    private static IReadOnlyList<DysonPipelineRun> ParseRuns(string body)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        if (!doc.RootElement.TryGetProperty("workflow_runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<DysonPipelineRun>(runs.GetArrayLength());
        foreach (var run in runs.EnumerateArray())
        {
            var (state, label) = MapState(Text(run, "status"), Text(run, "conclusion"));
            list.Add(new DysonPipelineRun(
                Id: Long(run, "id"),
                WorkflowName: Text(run, "name") ?? "",
                Title: Text(run, "display_title") ?? "",
                Branch: Text(run, "head_branch") ?? "",
                HeadSha: Text(run, "head_sha") ?? "",
                Event: Text(run, "event") ?? "",
                RunNumber: Int(run, "run_number"),
                Attempt: Int(run, "run_attempt"),
                State: state,
                StateLabel: label,
                CreatedAt: Time(run, "created_at") ?? default,
                StartedAt: Time(run, "run_started_at"),
                UpdatedAt: Time(run, "updated_at") ?? default,
                HtmlUrl: Text(run, "html_url") ?? ""));
        }

        return list;
    }

    private static IReadOnlyList<DysonPipelineJob> ParseJobs(string body, long runId)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        if (!doc.RootElement.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<DysonPipelineJob>(jobs.GetArrayLength());
        foreach (var job in jobs.EnumerateArray())
        {
            var (state, label) = MapState(Text(job, "status"), Text(job, "conclusion"));
            list.Add(new DysonPipelineJob(
                Id: Long(job, "id"),
                RunId: job.TryGetProperty("run_id", out _) ? Long(job, "run_id") : runId,
                Name: Text(job, "name") ?? "",
                State: state,
                StateLabel: label,
                StartedAt: Time(job, "started_at"),
                CompletedAt: Time(job, "completed_at"),
                HtmlUrl: Text(job, "html_url") ?? ""));
        }

        return list;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Text(element, name) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static bool TryParseRemote(string origin, out string host, out string owner, out string name)
    {
        host = "";
        owner = "";
        name = "";
        var value = origin.Trim();

        string path;
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
                return false;
            host = uri.Host;
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        else
        {
            var at = value.IndexOf('@');
            var colon = at > 0 ? value.IndexOf(':', at + 1) : -1;
            if (colon <= at + 1)
                return false;
            host = value[(at + 1)..colon];
            if (host.Length == 0 || host.Contains('/') || host.Contains('\\'))
                return false;
            path = value[(colon + 1)..];
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        var slash = path.IndexOf('/');
        if (slash <= 0 || slash == path.Length - 1 || path[(slash + 1)..].Contains('/'))
            return false;
        owner = path[..slash];
        name = path[(slash + 1)..];
        return owner.Length > 0 && name.Length > 0;
    }

    private static bool LooksUnauthenticated(string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return false;
        return stderr.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("authentication", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRateLimited(IReadOnlyDictionary<string, string> headers, out DateTimeOffset? retryAfter)
    {
        retryAfter = null;
        var remaining = Header(headers, "x-ratelimit-remaining");
        var limited = remaining is "0" || Header(headers, "retry-after") is not null;
        if (!limited)
            return false;

        if (Header(headers, "x-ratelimit-reset") is { } reset
            && long.TryParse(reset, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
            retryAfter = DateTimeOffset.FromUnixTimeSeconds(unix);
        else if (Header(headers, "retry-after") is { } after
            && int.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            retryAfter = DateTimeOffset.UtcNow.AddSeconds(seconds);
        return true;
    }

    private static int? RateRemaining(IReadOnlyDictionary<string, string> headers) =>
        Header(headers, "x-ratelimit-remaining") is { } text
        && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static DateTimeOffset? RateReset(IReadOnlyDictionary<string, string> headers) =>
        Header(headers, "x-ratelimit-reset") is { } text
        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;

    private static string? Header(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) ? value : null;

    private static string? FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var line = text.Trim().Split('\n', 2)[0].TrimEnd('\r').Trim();
        return line.Length == 0 ? null : line;
    }

    private static string RunsPath(DysonPipelineTarget target) =>
        $"repos/{target.Owner}/{target.Name}/actions/runs?per_page=20&exclude_pull_requests=true";

    private static string JobsPath(DysonPipelineTarget target, long runId) =>
        $"repos/{target.Owner}/{target.Name}/actions/runs/{runId}/jobs?per_page=100&filter=latest";

    private static Result<DysonPipelineTarget, DysonPipelineError> NotDetected() =>
        Result<DysonPipelineTarget, DysonPipelineError>.AsError(
            new DysonPipelineError(DysonPipelineErrorKind.NotDetected, "Not a GitHub remote."));

    private static Result<DysonPipelineFetch<T>, DysonPipelineError> Failed<T>(string message) =>
        Result<DysonPipelineFetch<T>, DysonPipelineError>.AsError(
            new DysonPipelineError(DysonPipelineErrorKind.Failed, message));

    private static readonly Dictionary<string, string> EmptyHeaders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Install dirs probed after PATH. macOS GUI apps do not inherit the Homebrew PATH.</summary>
    internal static IReadOnlyList<string> GitHubCliDirs { get; } = BuildGitHubCliDirs();

    private static string[] BuildGitHubCliDirs()
    {
        if (OperatingSystem.IsWindows())
        {
            return Existing(
                Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI"),
                Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)"), "GitHub CLI"),
                Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI"),
                Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
                Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims"));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Existing(
            "/opt/homebrew/bin",
            "/usr/local/bin",
            "/usr/bin",
            "/home/linuxbrew/.linuxbrew/bin",
            Combine(home, ".local", "bin"));
    }

    private static string? Combine(string? root, params string[] parts)
    {
        if (string.IsNullOrEmpty(root))
            return null;
        var path = root;
        foreach (var part in parts)
            path = Path.Combine(path, part);
        return path;
    }

    private static string[] Existing(params string?[] dirs) =>
        dirs.Where(dir => !string.IsNullOrEmpty(dir)).Select(dir => dir!).ToArray();
}
