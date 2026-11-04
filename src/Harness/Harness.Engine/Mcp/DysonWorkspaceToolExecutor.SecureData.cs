using System.Text.Json;

namespace DysonHarness;

public sealed partial class DysonWorkspaceToolExecutor
{
    private DysonToolCallResult RequestSecureData(DysonToolCall call)
    {
        var denied = RejectUnlessMetaAgent(call, "RequestSecureData");
        if (denied is not null)
            return denied;

        List<DysonSecureDataField> fields;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var parsed = ParseSecureDataFields(doc.RootElement);
            if (parsed.IsError)
                return Error(call, parsed.Error);
            fields = parsed.Value;
        }
        catch (JsonException)
        {
            return Error(call, "RequestSecureData: invalid JSON arguments.");
        }

        var id = Guid.NewGuid();
        var request = new DysonSecureDataRequest(id, fields, Outcome: null);
        _session.AppendDisplayInfoTurn(
            DysonSecureDataRequest.FormatBubbleMessage(fields),
            secureDataRequest: request);
        return Ok(call, "{\"ok\":true,\"requestId\":\"" + id.ToString("D") + "\"}");
    }

    private static Result<List<DysonSecureDataField>, string> ParseSecureDataFields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return Result<List<DysonSecureDataField>, string>.AsError("RequestSecureData: data must be an array.");
        }

        var count = data.GetArrayLength();
        if (count is < 1 or > DysonSecureDataRequest.MaxFields)
            return Result<List<DysonSecureDataField>, string>.AsError("RequestSecureData: data needs 1 to 8 entries.");

        var fields = new List<DysonSecureDataField>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return Result<List<DysonSecureDataField>, string>.AsError("RequestSecureData: each entry must be an object.");

            var key = item.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString()?.Trim() ?? ""
                : "";
            if (!DysonSessionSecretLimits.IsValidKey(key))
                return Result<List<DysonSecureDataField>, string>.AsError(
                    "RequestSecureData: key must be 1..64 characters of letters, digits, dot, underscore, or hyphen.");
            if (!seen.Add(key))
                return Result<List<DysonSecureDataField>, string>.AsError($"RequestSecureData: duplicate key '{key}'.");

            var description = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? CollapseWhitespace(d.GetString())
                : "";
            if (description.Length is 0 or > DysonSessionSecretLimits.MaxDescriptionLength)
                return Result<List<DysonSecureDataField>, string>.AsError(
                    "RequestSecureData: description must be 1..200 characters.");

            fields.Add(new DysonSecureDataField(key, description));
        }

        return Result<List<DysonSecureDataField>, string>.AsValue(fields);
    }

    private static string CollapseWhitespace(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private async Task<DysonToolCallResult> ListSecureDataKeysAsync(DysonToolCall call, CancellationToken cancellationToken)
    {
        var scope = ResolveSecureDataScope(call, "ListSecureDataKeys");
        if (scope.IsError)
            return Error(call, scope.Error);

        var keys = await scope.Value.Repo.ListKeysAsync(scope.Value.RootId, cancellationToken).ConfigureAwait(false);
        if (keys.IsError)
            return Error(call, $"ListSecureDataKeys: {keys.Error}");

        return Ok(call, JsonSerializer.Serialize(new { keys = keys.Value }));
    }

    private async Task<DysonToolCallResult> WriteSecureDataToFileAsync(DysonToolCall call, CancellationToken cancellationToken)
    {
        var scope = ResolveSecureDataScope(call, "WriteSecureDataToFile");
        if (scope.IsError)
            return Error(call, scope.Error);

        var key = ReadSecureDataKey(call, "WriteSecureDataToFile");
        if (key.IsError)
            return Error(call, key.Error);

        var secret = await scope.Value.Repo.GetAsync(scope.Value.RootId, key.Value, cancellationToken).ConfigureAwait(false);
        if (secret.IsError)
            return Error(call, $"WriteSecureDataToFile: {secret.Error}");

        var written = DysonSecureDataFiles.Write(scope.Value.RootId, key.Value, secret.Value, _session.AppendLog);
        if (written.IsError)
            return Error(call, written.Error);

        return Ok(call, JsonSerializer.Serialize(new { ok = true, path = written.Value }));
    }

    private async Task<DysonToolCallResult> DeleteSecureDataAsync(DysonToolCall call, CancellationToken cancellationToken)
    {
        var scope = ResolveSecureDataScope(call, "DeleteSecureData");
        if (scope.IsError)
            return Error(call, scope.Error);

        var key = ReadSecureDataKey(call, "DeleteSecureData");
        if (key.IsError)
            return Error(call, key.Error);

        var deleted = await scope.Value.Repo.DeleteAsync(scope.Value.RootId, key.Value, cancellationToken).ConfigureAwait(false);
        if (deleted.IsError)
            return Error(call, $"DeleteSecureData: {deleted.Error}");

        DysonSecureDataFiles.DeleteFor(scope.Value.RootId, key.Value, _session.AppendLog);
        return Ok(call, """{"ok":true}""");
    }

    private static Result<string, string> ReadSecureDataKey(DysonToolCall call, string tool)
    {
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var key = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("key", out var k)
                && k.ValueKind == JsonValueKind.String
                    ? k.GetString()?.Trim() ?? ""
                    : "";
            return key.Length == 0
                ? Result<string, string>.AsError($"{tool}: key is required.")
                : Result<string, string>.AsValue(key);
        }
        catch (JsonException)
        {
            return Result<string, string>.AsError($"{tool}: invalid JSON arguments.");
        }
    }

    /// <summary>
    /// Meta Agent or Meta Agent Drone only. The store is the root Meta session: walk
    /// <see cref="DysonAgentSession.Parent"/> to the top and require Meta Agent mode there.
    /// </summary>
    private Result<(IDysonSessionSecretRepository Repo, Guid RootId), string> ResolveSecureDataScope(
        DysonToolCall call,
        string tool)
    {
        if (!string.Equals(_session.Mode, DysonAgentModes.MetaAgent, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_session.Mode, DysonAgentModes.MetaAgentDrone, StringComparison.OrdinalIgnoreCase))
        {
            return Result<(IDysonSessionSecretRepository, Guid), string>.AsError(
                $"{tool} is only available to the Meta Agent or a Meta Agent Drone.");
        }

        var repo = _session.Config.SessionSecrets;
        if (repo is null)
            return Result<(IDysonSessionSecretRepository, Guid), string>.AsError($"{tool}: secure data is not available.");

        var root = _session;
        while (root.Parent is not null)
            root = root.Parent;
        if (!string.Equals(root.Mode, DysonAgentModes.MetaAgent, StringComparison.OrdinalIgnoreCase)
            || root.PersistenceId == Guid.Empty)
        {
            return Result<(IDysonSessionSecretRepository, Guid), string>.AsError(
                $"{tool}: this session is not under a Meta Agent session.");
        }

        return Result<(IDysonSessionSecretRepository, Guid), string>.AsValue((repo, root.PersistenceId));
    }
}
