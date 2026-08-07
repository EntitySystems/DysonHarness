using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// Per-work-directory JSON configuration (current subject only).
/// </summary>
public interface IDysonWorkDirectoryConfigurationRepository
{
    /// <summary>
    /// Returns the stored config, or a default <c>{ "mcpActive": true }</c> document when
    /// no row exists (does not materialize the row).
    /// </summary>
    Task<Result<JsonNode, string>> GetAsync(
        Guid workDirectoryId,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces the config document for the work directory.</summary>
    Task<VoidResult<string>> UpsertAsync(
        Guid workDirectoryId,
        JsonNode config,
        CancellationToken cancellationToken = default);
}
