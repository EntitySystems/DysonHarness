using System.Text.Json;

namespace DysonHarness;

public static class DysonPluginHookProtocol
{
    public const string Version = "dyson.plugin-hook/1";
    public const int MaxDefinitionBytes = 64 * 1024;
    public const int MaxInputBytes = 64 * 1024;
    public const int MaxStderrBytes = 16 * 1024;
}

public sealed record DysonPluginHookDefinition
{
    public required string ProtocolVersion { get; init; }
    public required string ComponentId { get; init; }
    public required string EventName { get; init; }
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public required string PackageRoot { get; init; }
    public required string DefinitionPath { get; init; }
}

public sealed record DysonPluginHookInvocation
{
    public required DysonPluginInstallationEntity Installation { get; init; }
    public required DysonResolvedPluginComponent Component { get; init; }
    public required string EventName { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public enum DysonPluginHookExecutionOutcome
{
    Skipped = 0,
    Allowed = 1,
    Denied = 2,
    Failed = 3,
    TimedOut = 4,
}

public sealed record DysonPluginHookExecutionResult
{
    public required DysonPluginHookExecutionOutcome Outcome { get; init; }
    public bool WasLaunched { get; init; }
    public bool ShouldProceed { get; init; } = true;
    public DysonPluginHookFailureMode FailureMode { get; init; } = DysonPluginHookFailureMode.FailOpen;
    public required string DetailCode { get; init; }
}

public sealed record DysonPluginHookProcessRequest
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public required string WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public required string StandardInput { get; init; }
    public required int TimeoutMilliseconds { get; init; }
    public required int MaxStdoutBytes { get; init; }
    public required int MaxStderrBytes { get; init; }
}

public sealed record DysonPluginHookProcessResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = "";
    public int StandardOutputBytes { get; init; }
    public int StandardErrorBytes { get; init; }
    public int DurationMilliseconds { get; init; }
    public bool TimedOut { get; init; }
    public bool StandardOutputLimitExceeded { get; init; }
    public bool StandardErrorLimitExceeded { get; init; }
}

/// <summary>Process boundary used by the constrained hook runtime and by tests that must never launch package code.</summary>
public interface IDysonPluginHookProcessRunner
{
    Task<Result<DysonPluginHookProcessResult, string>> RunAsync(
        DysonPluginHookProcessRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed record DysonPluginHookInputEnvelope
{
    public required string ProtocolVersion { get; init; }
    public required string Event { get; init; }
    public required Guid InstallationId { get; init; }
    public required string ComponentId { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

internal sealed record DysonPluginHookOutputEnvelope
{
    public required string ProtocolVersion { get; init; }
    public required string Event { get; init; }
    public DysonPluginHookGateEnvelope? Gate { get; init; }
}

internal sealed record DysonPluginHookGateEnvelope
{
    public required string Decision { get; init; }
}
