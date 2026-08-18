using System.Diagnostics;
using DysonHarness;

namespace Harness.Tests;

public class DysonWorkDirectoryServiceTests
{
    [Theory]
    [InlineData("github", null, DysonGitProvider.GitHub)]
    [InlineData("gitlab", null, DysonGitProvider.GitLab)]
    [InlineData("azure-devops", null, DysonGitProvider.AzureDevOps)]
    [InlineData("cursor-origin", null, DysonGitProvider.CursorOrigin)]
    [InlineData("other", null, DysonGitProvider.Other)]
    [InlineData(null, "https://github.com/acme/repo.git", DysonGitProvider.GitHub)]
    [InlineData("", "git@gitlab.com:acme/repo.git", DysonGitProvider.GitLab)]
    [InlineData(null, null, DysonGitProvider.None)]
    [InlineData("", null, DysonGitProvider.None)]
    [InlineData("unknown", "https://github.com/acme/repo.git", DysonGitProvider.None)]
    public void GetGitProvider_maps_stored_slug_or_classifies_origin(
        string? stored,
        string? origin,
        DysonGitProvider expected)
    {
        var service = new DysonWorkDirectoryService(new FakeWorkDirectories());
        Assert.Equal(expected, service.GetGitProvider(stored, origin));
        Assert.Equal(
            expected,
            service.GetGitProvider(new DysonWorkDirectoryEntity
            {
                GitProvider = stored,
                GitOrigin = origin,
            }));
    }

    [Fact]
    public async Task RefreshGitOriginAsync_writes_origin_and_provider()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-wd-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            RunGitOrThrow(root, ["init"]);
            RunGitOrThrow(root, ["remote", "add", "origin", "https://github.com/acme/repo.git"]);

            var id = Guid.NewGuid();
            var repo = new FakeWorkDirectories();
            repo.Add(new DysonWorkDirectoryEntity
            {
                Id = id,
                AbsolutePath = root,
                GitOrigin = "stale",
                GitProvider = "other",
            });

            var refresh = await new DysonWorkDirectoryService(repo).RefreshGitOriginAsync(id);
            Assert.True(refresh.IsSuccess, refresh.IsError ? refresh.Error : null);

            var stored = repo.Items[id];
            Assert.Equal("https://github.com/acme/repo.git", stored.GitOrigin);
            Assert.Equal("github", stored.GitProvider);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task RefreshGitOriginAsync_writes_nulls_when_not_a_git_repo()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-wd-nogit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var id = Guid.NewGuid();
            var repo = new FakeWorkDirectories();
            repo.Add(new DysonWorkDirectoryEntity
            {
                Id = id,
                AbsolutePath = root,
                GitOrigin = "https://github.com/acme/repo.git",
                GitProvider = "github",
            });

            var refresh = await new DysonWorkDirectoryService(repo).RefreshGitOriginAsync(id);
            Assert.True(refresh.IsSuccess, refresh.IsError ? refresh.Error : null);

            var stored = repo.Items[id];
            Assert.Null(stored.GitOrigin);
            Assert.Null(stored.GitProvider);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task RefreshGitOriginAsync_writes_nulls_when_no_origin()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-wd-no-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            RunGitOrThrow(root, ["init"]);

            var id = Guid.NewGuid();
            var repo = new FakeWorkDirectories();
            repo.Add(new DysonWorkDirectoryEntity
            {
                Id = id,
                AbsolutePath = root,
                GitOrigin = "https://github.com/acme/repo.git",
                GitProvider = "github",
            });

            var refresh = await new DysonWorkDirectoryService(repo).RefreshGitOriginAsync(id);
            Assert.True(refresh.IsSuccess, refresh.IsError ? refresh.Error : null);

            var stored = repo.Items[id];
            Assert.Null(stored.GitOrigin);
            Assert.Null(stored.GitProvider);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task RefreshGitOriginAsync_returns_get_error_without_inventing_a_row()
    {
        var repo = new FakeWorkDirectories();
        var missing = Guid.NewGuid();

        var refresh = await new DysonWorkDirectoryService(repo).RefreshGitOriginAsync(missing);

        Assert.True(refresh.IsError);
        Assert.Contains("not found", refresh.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(repo.Items);
    }

    private static void RunGitOrThrow(string workingDirectory, string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(workingDirectory);
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        if (!process.Start())
            throw new InvalidOperationException("Failed to start git for test setup.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            throw new TimeoutException("git setup timed out.");
        }

        Task.WhenAll(stdoutTask, stderrTask).GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderrTask.Result}");
    }

    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // ignore cleanup races
        }
    }

    private sealed class FakeWorkDirectories : IDysonWorkDirectoryRepository
    {
        public Dictionary<Guid, DysonWorkDirectoryEntity> Items { get; } = [];

        public void Add(DysonWorkDirectoryEntity entity) => Items[entity.Id] = entity;

        public Task<Result<Guid, string>> CreateAsync(
            string absolutePath,
            string? name = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Guid, string>.AsError("not used"));

        public Task<Result<DysonWorkDirectoryEntity, string>> GetAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                Items.TryGetValue(id, out var entity)
                    ? Result<DysonWorkDirectoryEntity, string>.AsValue(entity)
                    : Result<DysonWorkDirectoryEntity, string>.AsError($"Work directory '{id}' not found."));
        }

        public Task<Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>.AsValue(Items.Values.ToList()));

        public Task<VoidResult<string>> TouchOpenedAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Items.ContainsKey(id)
                    ? VoidResult<string>.Success
                    : VoidResult<string>.AsError($"Work directory '{id}' not found."));

        public Task<VoidResult<string>> UpdateGitMetadataAsync(
            Guid id,
            string? gitOrigin,
            string? gitProvider,
            CancellationToken cancellationToken = default)
        {
            if (!Items.TryGetValue(id, out var entity))
                return Task.FromResult(VoidResult<string>.AsError($"Work directory '{id}' not found."));

            entity.GitOrigin = gitOrigin;
            entity.GitProvider = gitProvider;
            return Task.FromResult(VoidResult<string>.Success);
        }

        public Task<VoidResult<string>> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.AsError("not used"));
    }
}
