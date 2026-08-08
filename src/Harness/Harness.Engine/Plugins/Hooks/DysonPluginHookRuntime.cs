using System.Text;
using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// Executes the reviewed Dyson hook subset. This type is intentionally not connected to live
/// session/tool loops; callers must supply a catalog-validated installation and component.
/// </summary>
public sealed class DysonPluginHookRuntime(
    DysonPluginHookSecurityService security,
    DysonPluginHookResolver resolver,
    IDysonPluginHookProcessRunner processRunner)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<string> OutputProperties = new HashSet<string>(
        ["protocolVersion", "event", "gate"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> GateProperties = new HashSet<string>(
        ["decision"], StringComparer.Ordinal);

    private readonly DysonPluginHookSecurityService _security = security ?? throw new ArgumentNullException(nameof(security));
    private readonly DysonPluginHookResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly IDysonPluginHookProcessRunner _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));

    public async Task<Result<DysonPluginHookExecutionResult, string>> ExecuteAsync(
        DysonPluginHookInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(invocation.Installation);
        ArgumentNullException.ThrowIfNull(invocation.Component);

        if (!DysonPluginHookEvents.Supported.Contains(invocation.EventName))
        {
            return Result<DysonPluginHookExecutionResult, string>.AsValue(new DysonPluginHookExecutionResult
            {
                Outcome = DysonPluginHookExecutionOutcome.Skipped,
                DetailCode = "unsupported-event",
            });
        }

        var status = await _security.GetStatusAsync(
            invocation.Installation.Id,
            invocation.Component.Id,
            invocation.EventName,
            cancellationToken).ConfigureAwait(false);
        if (status.IsError)
            return Result<DysonPluginHookExecutionResult, string>.AsError(status.Error, status.Exception);
        if (!status.Value.IsGranted || status.Value.Grant is null)
        {
            var detailCode = DenialDetailCode(status.Value.DenialReason);
            var denied = new DysonPluginHookExecutionResult
            {
                Outcome = DysonPluginHookExecutionOutcome.Skipped,
                WasLaunched = false,
                ShouldProceed = true,
                DetailCode = detailCode,
            };
            var audit = await AuditAsync(invocation, denied, 0, 0, 0, cancellationToken).ConfigureAwait(false);
            return audit.IsError
                ? Result<DysonPluginHookExecutionResult, string>.AsError(audit.Error, audit.Exception)
                : Result<DysonPluginHookExecutionResult, string>.AsValue(denied);
        }

        var grant = status.Value.Grant;
        var resolved = _resolver.Resolve(invocation.Installation, invocation.Component, invocation.EventName);
        if (resolved.IsError)
        {
            return await FailAsync(
                invocation, grant, "resolution-failed", wasLaunched: false, 0, 0, 0, cancellationToken).ConfigureAwait(false);
        }

        var filteredMetadata = FilterMetadata(invocation.EventName, grant.Permissions, invocation.Metadata);
        var input = JsonSerializer.Serialize(new DysonPluginHookInputEnvelope
        {
            ProtocolVersion = DysonPluginHookProtocol.Version,
            Event = invocation.EventName,
            InstallationId = invocation.Installation.Id,
            ComponentId = invocation.Component.Id,
            Metadata = filteredMetadata,
        }, JsonOptions);
        var inputBytes = Encoding.UTF8.GetByteCount(input);
        if (inputBytes > DysonPluginHookProtocol.MaxInputBytes)
        {
            return await FailAsync(
                invocation, grant, "process-failed", wasLaunched: false, 0, inputBytes, 0, cancellationToken).ConfigureAwait(false);
        }

        var processRequest = new DysonPluginHookProcessRequest
        {
            FileName = resolved.Value.Executable,
            Arguments = resolved.Value.Arguments,
            WorkingDirectory = resolved.Value.PackageRoot,
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DYSON_PLUGIN_ROOT"] = resolved.Value.PackageRoot,
                ["DYSON_HOOK_DEFINITION"] = resolved.Value.DefinitionPath,
                ["DYSON_HOOK_EVENT"] = invocation.EventName,
                ["DYSON_HOOK_PROTOCOL"] = DysonPluginHookProtocol.Version,
            },
            StandardInput = input,
            TimeoutMilliseconds = grant.TimeoutMilliseconds,
            MaxStdoutBytes = grant.MaxOutputBytes,
            MaxStderrBytes = DysonPluginHookProtocol.MaxStderrBytes,
        };

        var process = await _processRunner.RunAsync(processRequest, cancellationToken).ConfigureAwait(false);
        if (process.IsError)
        {
            return await FailAsync(
                invocation, grant, "process-failed", wasLaunched: true, 0, inputBytes, 0, cancellationToken).ConfigureAwait(false);
        }

        var processResult = process.Value;
        var stdoutBytes = Math.Max(processResult.StandardOutputBytes, Encoding.UTF8.GetByteCount(processResult.StandardOutput));
        if (processResult.TimedOut)
        {
            return await FailAsync(
                invocation, grant, "timeout", wasLaunched: true, processResult.DurationMilliseconds, inputBytes, stdoutBytes,
                cancellationToken, DysonPluginHookExecutionOutcome.TimedOut).ConfigureAwait(false);
        }
        if (processResult.StandardOutputLimitExceeded || stdoutBytes > grant.MaxOutputBytes)
        {
            return await FailAsync(
                invocation, grant, "output-limit", wasLaunched: true, processResult.DurationMilliseconds, inputBytes, stdoutBytes,
                cancellationToken).ConfigureAwait(false);
        }
        if (processResult.StandardErrorLimitExceeded || processResult.StandardErrorBytes > DysonPluginHookProtocol.MaxStderrBytes)
        {
            return await FailAsync(
                invocation, grant, "stderr-limit", wasLaunched: true, processResult.DurationMilliseconds, inputBytes, stdoutBytes,
                cancellationToken).ConfigureAwait(false);
        }
        if (processResult.ExitCode != 0)
        {
            return await FailAsync(
                invocation, grant, "process-failed", wasLaunched: true, processResult.DurationMilliseconds, inputBytes, stdoutBytes,
                cancellationToken).ConfigureAwait(false);
        }

        var output = ParseOutput(processResult.StandardOutput, invocation.EventName, grant.Permissions);
        if (output.IsError)
        {
            return await FailAsync(
                invocation, grant, "invalid-output", wasLaunched: true, processResult.DurationMilliseconds, inputBytes, stdoutBytes,
                cancellationToken).ConfigureAwait(false);
        }

        var execution = new DysonPluginHookExecutionResult
        {
            Outcome = output.Value ? DysonPluginHookExecutionOutcome.Allowed : DysonPluginHookExecutionOutcome.Denied,
            WasLaunched = true,
            ShouldProceed = output.Value,
            FailureMode = grant.FailureMode,
            DetailCode = "completed",
        };
        var appended = await AuditAsync(
            invocation, execution, processResult.DurationMilliseconds, inputBytes, stdoutBytes, cancellationToken).ConfigureAwait(false);
        return appended.IsError
            ? Result<DysonPluginHookExecutionResult, string>.AsError(appended.Error, appended.Exception)
            : Result<DysonPluginHookExecutionResult, string>.AsValue(execution);
    }

    private async Task<Result<DysonPluginHookExecutionResult, string>> FailAsync(
        DysonPluginHookInvocation invocation,
        DysonPluginHookReviewGrant grant,
        string detailCode,
        bool wasLaunched,
        int durationMilliseconds,
        int inputBytes,
        int outputBytes,
        CancellationToken cancellationToken,
        DysonPluginHookExecutionOutcome outcome = DysonPluginHookExecutionOutcome.Failed)
    {
        var failed = new DysonPluginHookExecutionResult
        {
            Outcome = outcome,
            WasLaunched = wasLaunched,
            ShouldProceed = grant.FailureMode == DysonPluginHookFailureMode.FailOpen,
            FailureMode = grant.FailureMode,
            DetailCode = detailCode,
        };
        var audit = await AuditAsync(
            invocation, failed, durationMilliseconds, inputBytes, outputBytes, cancellationToken).ConfigureAwait(false);
        return audit.IsError
            ? Result<DysonPluginHookExecutionResult, string>.AsError(audit.Error, audit.Exception)
            : Result<DysonPluginHookExecutionResult, string>.AsValue(failed);
    }

    private async Task<VoidResult<string>> AuditAsync(
        DysonPluginHookInvocation invocation,
        DysonPluginHookExecutionResult result,
        int durationMilliseconds,
        int inputBytes,
        int outputBytes,
        CancellationToken cancellationToken)
    {
        var outcome = result.Outcome switch
        {
            DysonPluginHookExecutionOutcome.Allowed => "allowed",
            DysonPluginHookExecutionOutcome.Denied => "denied",
            DysonPluginHookExecutionOutcome.Failed => "failed",
            DysonPluginHookExecutionOutcome.TimedOut => "timeout",
            _ => "skipped",
        };
        return await _security.AppendAuditAsync(new DysonPluginHookAuditWrite
        {
            InstallationId = invocation.Installation.Id,
            HookComponentId = invocation.Component.Id,
            EventName = invocation.EventName,
            Outcome = outcome,
            DetailCode = result.DetailCode,
            DurationMilliseconds = durationMilliseconds,
            InputBytes = inputBytes,
            OutputBytes = outputBytes,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Result<bool, string> ParseOutput(
        string json,
        string eventName,
        IReadOnlyList<string> permissions)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Result<bool, string>.AsError("Plugin hook output must be an object.");
            var properties = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (properties.Any(property => !OutputProperties.Contains(property)) ||
                !root.TryGetProperty("protocolVersion", out var protocol) || protocol.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("event", out var outputEvent) || outputEvent.ValueKind != JsonValueKind.String ||
                !string.Equals(protocol.GetString(), DysonPluginHookProtocol.Version, StringComparison.Ordinal) ||
                !string.Equals(outputEvent.GetString(), eventName, StringComparison.Ordinal))
            {
                return Result<bool, string>.AsError("Plugin hook output protocol or event mismatch.");
            }

            if (!root.TryGetProperty("gate", out var gate))
                return Result<bool, string>.AsValue(true);
            if (!string.Equals(eventName, DysonPluginHookEvents.ToolBefore, StringComparison.Ordinal) ||
                !permissions.Contains(DysonPluginHookPermissions.GateTool, StringComparer.Ordinal) ||
                gate.ValueKind != JsonValueKind.Object)
            {
                return Result<bool, string>.AsError("Plugin hook gate output is not authorized for this event.");
            }
            var gateProperties = gate.EnumerateObject().Select(property => property.Name).ToArray();
            if (gateProperties.Length != 1 || gateProperties.Any(property => !GateProperties.Contains(property)) ||
                !gate.TryGetProperty("decision", out var decision) || decision.ValueKind != JsonValueKind.String)
            {
                return Result<bool, string>.AsError("Plugin hook gate output is malformed.");
            }
            return decision.GetString() switch
            {
                "allow" => Result<bool, string>.AsValue(true),
                "deny" => Result<bool, string>.AsValue(false),
                _ => Result<bool, string>.AsError("Plugin hook gate decision is unsupported."),
            };
        }
        catch (JsonException ex)
        {
            return Result<bool, string>.AsError("Plugin hook output is malformed JSON.", ex);
        }
    }

    private static IReadOnlyDictionary<string, string> FilterMetadata(
        string eventName,
        IReadOnlyList<string> permissions,
        IReadOnlyDictionary<string, string> metadata)
    {
        var requiredPermission = eventName switch
        {
            DysonPluginHookEvents.ContextPrepared => DysonPluginHookPermissions.ReadContextMetadata,
            DysonPluginHookEvents.ToolBefore or DysonPluginHookEvents.ToolAfter => DysonPluginHookPermissions.ReadToolMetadata,
            DysonPluginHookEvents.McpBefore or DysonPluginHookEvents.McpAfter => DysonPluginHookPermissions.ReadMcpMetadata,
            DysonPluginHookEvents.ShellBefore or DysonPluginHookEvents.ShellAfter => DysonPluginHookPermissions.ReadShellMetadata,
            _ => "",
        };
        return permissions.Contains(requiredPermission, StringComparer.Ordinal)
            ? new Dictionary<string, string>(metadata, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static string DenialDetailCode(string reason)
    {
        if (reason.Contains("stale", StringComparison.OrdinalIgnoreCase))
            return "stale-review";
        if (reason.Contains("revoked", StringComparison.OrdinalIgnoreCase))
            return "revoked";
        if (reason.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
            return "unsupported-event";
        if (reason.Contains("dormant", StringComparison.OrdinalIgnoreCase))
            return "review-denied";
        return "default-denied";
    }
}
