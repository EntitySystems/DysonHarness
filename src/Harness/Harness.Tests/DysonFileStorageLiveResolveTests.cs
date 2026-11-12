using System.Net;
using System.Net.Sockets;
using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: image tools resolve file storage from the saved setting at call time, so a child session
/// (shared config) and a session created before credentials were saved both see it, and a host tearing
/// down cannot null it. Failures surface their real reason, never file_storage_required.
/// Bucket is a closed loopback port; no external network.
/// </summary>
public class DysonFileStorageLiveResolveTests
{
    [Fact]
    public async Task Existing_child_session_sees_storage_saved_after_creation_and_real_errors_surface()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-fs-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "shot.png"),
                [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01]);
            var settings = new MemorySettings();
            var parent = new DysonS3ImageToolGateTests.StubSession(new DysonAgentSessionConfig { AppSettings = settings });
            var child = new DysonS3ImageToolGateTests.StubSession(parent.Config);
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(child, root, new HttpClient());

            var before = await LoadAsync(executor);
            Assert.True(before.IsError);
            Assert.Contains(DysonS3FileStorage.FileStorageRequiredToken, before.Content, StringComparison.Ordinal);

            // Saved after both sessions existed; nothing is pushed onto them.
            settings.Values[DysonAppSettingKeys.FileStorageS3] = DysonS3FileStorageSettings.Serialize(
                new DysonS3FileStorageSettings
                {
                    EndpointUrl = $"https://127.0.0.1:{ClosedPort()}/my-bucket",
                    AccessKeyId = "ak",
                    SecretAccessKey = "secret",
                })!;
            // Old hosts nulled this on circuit dispose; it must not matter.
            parent.Config.FileStorage = null;

            var resolved = await child.Config.ResolveFileStorageAsync();
            Assert.True(resolved.IsSuccess, resolved.IsError ? resolved.Error : null);
            Assert.Equal("my-bucket", resolved.Value?.Endpoint.Bucket);

            var upload = await LoadAsync(executor);
            Assert.True(upload.IsError, "Closed port cannot accept the upload.");
            Assert.DoesNotContain(DysonS3FileStorage.FileStorageRequiredToken, upload.Content, StringComparison.Ordinal);
            Assert.Contains(DysonS3ClientErrors.Unreachable, upload.Content, StringComparison.Ordinal);

            settings.Fail = true;
            var failed = await LoadAsync(executor);
            Assert.True(failed.IsError);
            Assert.DoesNotContain(DysonS3FileStorage.FileStorageRequiredToken, failed.Content, StringComparison.Ordinal);
            Assert.Contains("db busy", failed.Content, StringComparison.Ordinal);

            settings.Fail = false;
            settings.Values[DysonAppSettingKeys.FileStorageS3] = "{not json";
            var invalid = await LoadAsync(executor);
            Assert.True(invalid.IsError);
            Assert.DoesNotContain(DysonS3FileStorage.FileStorageRequiredToken, invalid.Content, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Task<DysonToolCallResult> LoadAsync(DysonWorkspaceToolExecutor executor) =>
        executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "lb-" + Guid.NewGuid().ToString("N"),
            ToolName = "LoadBinary",
            Stage = 0,
            ArgumentsJson = """{"path":"shot.png"}""",
        });

    private sealed class MemorySettings : IDysonSubjectSettingsRepository
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool Fail { get; set; }

        public Task<VoidResult<string>> EnsureSubjectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.Success);

        public Task<Result<string?, string>> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            if (Fail)
                return Task.FromResult(Result<string?, string>.AsError("db busy"));
            Values.TryGetValue(key, out var value);
            return Task.FromResult(Result<string?, string>.AsValue(value));
        }

        public Task<VoidResult<string>> SetSettingAsync(string key, string? value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
