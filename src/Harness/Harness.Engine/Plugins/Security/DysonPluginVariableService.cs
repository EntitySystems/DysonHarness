using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DysonHarness;

public sealed record DysonPluginVariableInfo
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Description { get; init; }
    public bool IsRequired { get; init; }
    public bool IsSecret { get; init; }
    public IReadOnlyList<string> Uses { get; init; } = [];
    public bool HasValue { get; init; }
    public string DisplayValue => HasValue ? "[SET]" : "[NOT SET]";
}

public sealed partial class DysonPluginVariableService(
    IDysonPluginInstallationRepository installations,
    IDysonPluginVariableValueRepository values,
    IDysonSubjectContext subjectContext,
    DysonPluginVariableProtector protector)
{
    private readonly IDysonPluginInstallationRepository _installations = installations ?? throw new ArgumentNullException(nameof(installations));
    private readonly IDysonPluginVariableValueRepository _values = values ?? throw new ArgumentNullException(nameof(values));
    private readonly IDysonSubjectContext _subjectContext = subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));
    private readonly DysonPluginVariableProtector _protector = protector ?? throw new ArgumentNullException(nameof(protector));

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex VariableNameRegex();

    public async Task<Result<IReadOnlyList<DysonPluginVariableInfo>, string>> ListAsync(Guid installationId, CancellationToken cancellationToken = default)
    {
        var declarations = await LoadDeclarationsAsync(installationId, cancellationToken).ConfigureAwait(false);
        if (declarations.IsError) return Result<IReadOnlyList<DysonPluginVariableInfo>, string>.AsError(declarations.Error);
        var names = await _values.ListNamesAsync(installationId, cancellationToken).ConfigureAwait(false);
        if (names.IsError) return Result<IReadOnlyList<DysonPluginVariableInfo>, string>.AsError(names.Error);
        return Result<IReadOnlyList<DysonPluginVariableInfo>, string>.AsValue(declarations.Value.Values
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x with { HasValue = names.Value.Contains(x.Name) }).ToArray());
    }

    public async Task<VoidResult<string>> SetAsync(Guid installationId, string variableName, string value, CancellationToken cancellationToken = default)
    {
        if (value is null) return VoidResult<string>.AsError("Plugin variable value is required.");
        var declaration = await FindDeclarationAsync(installationId, variableName, cancellationToken).ConfigureAwait(false);
        if (declaration.IsError) return VoidResult<string>.AsError(declaration.Error);
        var valid = ValidateValue(declaration.Value.Type, value);
        if (valid.IsError) return valid;
        var protectedValue = _protector.Protect(_subjectContext.SubjectId, installationId, declaration.Value.Name, value);
        if (protectedValue.IsError) return VoidResult<string>.AsError(protectedValue.Error);
        return await _values.UpsertAsync(installationId, declaration.Value.Name, protectedValue.Value, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<bool, string>> HasAsync(Guid installationId, string variableName, CancellationToken cancellationToken = default)
    {
        var declaration = await FindDeclarationAsync(installationId, variableName, cancellationToken).ConfigureAwait(false);
        if (declaration.IsError) return Result<bool, string>.AsError(declaration.Error);
        var stored = await _values.GetAsync(installationId, declaration.Value.Name, cancellationToken).ConfigureAwait(false);
        return stored.IsError ? Result<bool, string>.AsError(stored.Error) : Result<bool, string>.AsValue(stored.Value is not null);
    }

    public async Task<VoidResult<string>> DeleteAsync(Guid installationId, string variableName, CancellationToken cancellationToken = default)
    {
        var declaration = await FindDeclarationAsync(installationId, variableName, cancellationToken).ConfigureAwait(false);
        if (declaration.IsError) return VoidResult<string>.AsError(declaration.Error);
        return await _values.DeleteAsync(installationId, declaration.Value.Name, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<DysonPluginSecretValue, string>> ResolveAsync(Guid installationId, string variableName, CancellationToken cancellationToken = default)
    {
        var declaration = await FindDeclarationAsync(installationId, variableName, cancellationToken).ConfigureAwait(false);
        if (declaration.IsError) return Result<DysonPluginSecretValue, string>.AsError(declaration.Error);
        var stored = await _values.GetAsync(installationId, declaration.Value.Name, cancellationToken).ConfigureAwait(false);
        if (stored.IsError) return Result<DysonPluginSecretValue, string>.AsError(stored.Error);
        if (stored.Value is null) return Result<DysonPluginSecretValue, string>.AsError("Plugin variable value is not configured.");
        return _protector.Unprotect(_subjectContext.SubjectId, installationId, declaration.Value.Name, stored.Value.ProtectedValue);
    }

    private async Task<Result<DysonPluginVariableInfo, string>> FindDeclarationAsync(Guid installationId, string variableName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(variableName) || !VariableNameRegex().IsMatch(variableName.Trim()))
            return Result<DysonPluginVariableInfo, string>.AsError("Plugin variable name is invalid.");
        var declarations = await LoadDeclarationsAsync(installationId, cancellationToken).ConfigureAwait(false);
        if (declarations.IsError) return Result<DysonPluginVariableInfo, string>.AsError(declarations.Error);
        return declarations.Value.TryGetValue(variableName.Trim(), out var declaration)
            ? Result<DysonPluginVariableInfo, string>.AsValue(declaration)
            : Result<DysonPluginVariableInfo, string>.AsError("Plugin variable is not declared by this installation.");
    }

    private async Task<Result<IReadOnlyDictionary<string, DysonPluginVariableInfo>, string>> LoadDeclarationsAsync(Guid installationId, CancellationToken cancellationToken)
    {
        var installation = await _installations.GetAsync(installationId, cancellationToken).ConfigureAwait(false);
        if (installation.IsError) return Result<IReadOnlyDictionary<string, DysonPluginVariableInfo>, string>.AsError(installation.Error);
        if (string.IsNullOrWhiteSpace(installation.Value.ConfigurationSchemaJson))
            return Result<IReadOnlyDictionary<string, DysonPluginVariableInfo>, string>.AsValue(new Dictionary<string, DysonPluginVariableInfo>());
        try
        {
            using var document = JsonDocument.Parse(installation.Value.ConfigurationSchemaJson);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                return ParseArrayDeclarations(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Result<IReadOnlyDictionary<string, DysonPluginVariableInfo>, string>.AsError("Plugin variable declaration schema is invalid.");
            var root = document.RootElement;
            var declarationsRoot = root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object ? properties : root;
            var required = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
                foreach (var item in requiredElement.EnumerateArray()) if (item.ValueKind == JsonValueKind.String) required.Add(item.GetString()!);
            var result = new Dictionary<string, DysonPluginVariableInfo>(StringComparer.Ordinal);
            foreach (var property in declarationsRoot.EnumerateObject())
            {
                if (!VariableNameRegex().IsMatch(property.Name) || property.Name is "required" or "type" or "additionalProperties") continue;
                var type = "string";
                string? description = null;
                var secret = false;
                var uses = new List<string>();
                var isRequired = required.Contains(property.Name);
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    if (property.Value.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
                        type = typeElement.GetString()!.Trim().ToLowerInvariant();
                    if (property.Value.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String)
                        description = descriptionElement.GetString()?.Trim();
                    if (property.Value.TryGetProperty("secret", out var secretElement) && secretElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        secret = secretElement.GetBoolean();
                    if (property.Value.TryGetProperty("required", out var itemRequired) && itemRequired.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        isRequired |= itemRequired.GetBoolean();
                    if (property.Value.TryGetProperty("uses", out var usesElement))
                    {
                        if (usesElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(usesElement.GetString()))
                            uses.Add(usesElement.GetString()!.Trim());
                        else if (usesElement.ValueKind == JsonValueKind.Array)
                            uses.AddRange(usesElement.EnumerateArray()
                                .Where(item => item.ValueKind == JsonValueKind.String)
                                .Select(item => item.GetString()!.Trim())
                                .Where(item => item.Length > 0));
                    }
                }
                result[property.Name] = new DysonPluginVariableInfo
                {
                    Name = property.Name,
                    Type = type,
                    Description = description,
                    IsRequired = isRequired,
                    IsSecret = secret,
                    Uses = uses.Distinct(StringComparer.Ordinal).OrderBy(use => use, StringComparer.Ordinal).ToArray(),
                    HasValue = false,
                };
            }
            return Result<IReadOnlyDictionary<string, DysonPluginVariableInfo>, string>.AsValue(result);
        }
        catch (JsonException)
        {
            return Result<IReadOnlyDictionary<string, DysonPluginVariableInfo>, string>.AsError("Plugin variable declaration schema is invalid.");
        }
    }

    private static Result<IReadOnlyDictionary<string, DysonPluginVariableInfo>, string> ParseArrayDeclarations(JsonElement root)
    {
        var result = new Dictionary<string, DysonPluginVariableInfo>(StringComparer.Ordinal);
        foreach (var item in root.EnumerateArray())
        {
            var name = item.ValueKind == JsonValueKind.String ? item.GetString() :
                item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(name) || !VariableNameRegex().IsMatch(name)) continue;
            var type = "string";
            string? description = null;
            var secret = false;
            var required = false;
            var uses = new List<string>();
            if (item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
                    type = typeElement.GetString()!.Trim().ToLowerInvariant();
                if (item.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String)
                    description = descriptionElement.GetString()?.Trim();
                if (item.TryGetProperty("secret", out var secretElement) && secretElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    secret = secretElement.GetBoolean();
                if (item.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    required = requiredElement.GetBoolean();
                if (item.TryGetProperty("uses", out var usesElement))
                {
                    if (usesElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(usesElement.GetString()))
                        uses.Add(usesElement.GetString()!.Trim());
                    else if (usesElement.ValueKind == JsonValueKind.Array)
                        uses.AddRange(usesElement.EnumerateArray()
                            .Where(use => use.ValueKind == JsonValueKind.String)
                            .Select(use => use.GetString()!.Trim())
                            .Where(use => use.Length > 0));
                }
            }
            result[name] = new DysonPluginVariableInfo
            {
                Name = name,
                Type = type,
                Description = description,
                IsRequired = required,
                IsSecret = secret,
                Uses = uses.Distinct(StringComparer.Ordinal).OrderBy(use => use, StringComparer.Ordinal).ToArray(),
                HasValue = false,
            };
        }
        return Result<IReadOnlyDictionary<string, DysonPluginVariableInfo>, string>.AsValue(result);
    }

    private static VoidResult<string> ValidateValue(string type, string value)
    {
        var valid = type switch
        {
            "string" => true,
            "integer" => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "number" => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
            "boolean" => bool.TryParse(value, out _),
            "object" => IsJsonKind(value, JsonValueKind.Object),
            "array" => IsJsonKind(value, JsonValueKind.Array),
            _ => false,
        };
        return valid ? VoidResult<string>.Success : VoidResult<string>.AsError("Plugin variable value does not satisfy its declared type.");
    }

    private static bool IsJsonKind(string value, JsonValueKind kind)
    {
        try { using var document = JsonDocument.Parse(value); return document.RootElement.ValueKind == kind; }
        catch (JsonException) { return false; }
    }
}
