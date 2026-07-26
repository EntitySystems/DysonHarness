using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>
/// Loads/saves <see cref="DysonToolPolicyDocument"/> under
/// <see cref="DysonAppSettingKeys.AgentModeToolPolicy"/>.
/// </summary>
public sealed class DysonToolPolicyStore(DysonAppSettingsStore settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly IReadOnlySet<string> EmptyDisabled =
        new HashSet<string>(StringComparer.Ordinal);

    private readonly DysonAppSettingsStore _settings =
        settings ?? throw new ArgumentNullException(nameof(settings));

    public async Task<Result<DysonToolPolicyDocument, string>> GetDocumentAsync(
        CancellationToken cancellationToken = default)
    {
        var get = await _settings
            .GetAsync(DysonAppSettingKeys.AgentModeToolPolicy, cancellationToken)
            .ConfigureAwait(false);
        if (get.IsError)
            return Result<DysonToolPolicyDocument, string>.AsError(get.Error);

        if (string.IsNullOrWhiteSpace(get.Value))
            return Result<DysonToolPolicyDocument, string>.AsValue(new DysonToolPolicyDocument());

        try
        {
            var doc = JsonSerializer.Deserialize<DysonToolPolicyDocument>(get.Value, JsonOptions)
                ?? new DysonToolPolicyDocument();
            Normalize(doc);
            return Result<DysonToolPolicyDocument, string>.AsValue(doc);
        }
        catch (JsonException ex)
        {
            return Result<DysonToolPolicyDocument, string>.AsError(
                $"Invalid agent mode tool policy JSON: {ex.Message}");
        }
    }

    public async Task<VoidResult<string>> SetDocumentAsync(
        DysonToolPolicyDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        Normalize(document);

        string json;
        try
        {
            json = JsonSerializer.Serialize(document, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new VoidResult<string>($"Failed to serialize tool policy: {ex.Message}");
        }

        return await _settings
            .SetAsync(DysonAppSettingKeys.AgentModeToolPolicy, json, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<IReadOnlySet<string>, string>> GetModeDisabledToolsAsync(
        string mode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return Result<IReadOnlySet<string>, string>.AsError("Agent mode is required.");

        var doc = await GetDocumentAsync(cancellationToken).ConfigureAwait(false);
        if (doc.IsError)
            return Result<IReadOnlySet<string>, string>.AsError(doc.Error);

        return Result<IReadOnlySet<string>, string>.AsValue(
            DysonToolPolicyResolver.Resolve(doc.Value, mode));
    }

    public async Task<VoidResult<string>> SetModeDisabledToolsAsync(
        string mode,
        IEnumerable<string> disabledTools,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return new VoidResult<string>("Agent mode is required.");
        ArgumentNullException.ThrowIfNull(disabledTools);

        var docResult = await GetDocumentAsync(cancellationToken).ConfigureAwait(false);
        if (docResult.IsError)
            return new VoidResult<string>(docResult.Error);

        var doc = docResult.Value;
        var names = disabledTools
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        doc.Modes[mode.Trim()] = new DysonToolPolicyModeEntry { DisabledTools = names };
        return await SetDocumentAsync(doc, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a per-model mode denylist (plumbing for future overlays).
    /// Used by tests / future resolver path; v1 resolve ignores models.
    /// </summary>
    public async Task<Result<IReadOnlySet<string>, string>> GetModelModeDisabledToolsAsync(
        Guid modelSlugId,
        string mode,
        CancellationToken cancellationToken = default)
    {
        if (modelSlugId == Guid.Empty)
            return Result<IReadOnlySet<string>, string>.AsError("Model slug id is required.");
        if (string.IsNullOrWhiteSpace(mode))
            return Result<IReadOnlySet<string>, string>.AsError("Agent mode is required.");

        var doc = await GetDocumentAsync(cancellationToken).ConfigureAwait(false);
        if (doc.IsError)
            return Result<IReadOnlySet<string>, string>.AsError(doc.Error);

        var key = modelSlugId.ToString("D");
        if (!doc.Value.Models.TryGetValue(key, out var model)
            || !model.Modes.TryGetValue(mode.Trim(), out var entry)
            || entry.DisabledTools is not { Count: > 0 })
        {
            return Result<IReadOnlySet<string>, string>.AsValue(EmptyDisabled);
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in entry.DisabledTools)
        {
            if (!string.IsNullOrWhiteSpace(name))
                set.Add(name.Trim());
        }

        return Result<IReadOnlySet<string>, string>.AsValue(set.Count == 0 ? EmptyDisabled : set);
    }

    private static void Normalize(DysonToolPolicyDocument doc)
    {
        doc.Modes = new Dictionary<string, DysonToolPolicyModeEntry>(
            doc.Modes ?? [],
            StringComparer.OrdinalIgnoreCase);
        doc.Models = new Dictionary<string, DysonToolPolicyModelEntry>(
            doc.Models ?? [],
            StringComparer.OrdinalIgnoreCase);

        foreach (var mode in doc.Modes.Values)
            mode.DisabledTools ??= [];

        foreach (var model in doc.Models.Values)
        {
            model.Modes = new Dictionary<string, DysonToolPolicyModeEntry>(
                model.Modes ?? [],
                StringComparer.OrdinalIgnoreCase);
            foreach (var mode in model.Modes.Values)
                mode.DisabledTools ??= [];
        }
    }
}
