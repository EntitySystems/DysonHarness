namespace DysonHarness;

/// <summary>
/// Model providers, slugs, and subject-owned favorites.
/// <para>
/// List/resolve providers and slugs: <c>SubjectId == current OR SubjectId == shared</c>.
/// Create/update/delete of shared providers requires
/// <see cref="IDysonAccessEvaluator.Can"/>(<see cref="DysonPermission.ManageSharedProviders"/>).
/// Subject-owned mutations require the row’s SubjectId == current.
/// Favorites are always subject-owned (even when pointing at a shared provider’s slug).
/// </para>
/// </summary>
public interface IDysonModelRepository
{
    Task<Result<IReadOnlyList<DysonModelProviderEntity>, string>> ListProvidersAsync(
        CancellationToken cancellationToken = default);

    Task<Result<DysonModelProviderEntity, string>> GetProviderAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <param name="shared">
    /// When true, store under <see cref="DysonSubjects.Shared"/> (requires ManageSharedProviders);
    /// otherwise under the current subject.
    /// </param>
    Task<Result<Guid, string>> CreateProviderAsync(
        DysonModelProviderEntity provider,
        bool shared = false,
        CancellationToken cancellationToken = default);

    /// <param name="shared">
    /// When true, treat as a shared-provider write (ManageSharedProviders);
    /// when false, subject-owned write for the current subject.
    /// </param>
    Task<VoidResult<string>> UpdateProviderAsync(
        DysonModelProviderEntity provider,
        bool shared = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert-or-update a managed provider by <paramref name="managedSource"/> (id-stable),
    /// merging slugs by name (preserves slug Id and IsEnabled). Empty <paramref name="slugs"/> clears the catalog.
    /// </summary>
    /// <param name="shared">
    /// When true, managed provider is shared; otherwise subject-owned.
    /// </param>
    Task<Result<Guid, string>> UpsertManagedProviderAsync(
        string managedSource,
        string displayName,
        string baseUrl,
        string apiKey,
        string openAiApiMode,
        IReadOnlyList<ManagedSlugSpec> slugs,
        bool shared = false,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> DeleteProviderAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Result<Guid, string>> AddSlugAsync(
        Guid providerId,
        string slug,
        string displayAlias,
        bool isDefault = false,
        string? defaultReasoningEffort = null,
        IEnumerable<string>? reasoningModes = null,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> UpdateSlugAsync(
        DysonModelSlugEntity slug,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> RemoveSlugAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Result<DysonModelSlugEntity?, string>> GetDefaultSlugAsync(
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> SetDefaultSlugAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enable/disable a managed provider slug. Manual providers are rejected.
    /// </summary>
    Task<VoidResult<string>> SetSlugEnabledAsync(
        Guid id,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Set default reasoning effort for a managed provider slug. Manual providers are rejected.
    /// Blank/whitespace <paramref name="effort"/> clears to null (omit).
    /// </summary>
    Task<VoidResult<string>> SetSlugDefaultReasoningEffortAsync(
        Guid id,
        string? effort,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Set default max target context tokens for a managed provider slug.
    /// Manual providers are rejected. Null clears → inherit harness 100K.
    /// Values are clamped to 0…1_000_000.
    /// </summary>
    Task<VoidResult<string>> SetSlugDefaultMaxTargetContextTokensAsync(
        Guid id,
        int? maxTargetContextTokens,
        CancellationToken cancellationToken = default);

    Task<Result<DysonModelSlugEntity, string>> GetSlugAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Case-insensitive exact match on <see cref="DysonModelSlugEntity.Slug"/>, then
    /// <see cref="DysonModelSlugEntity.DisplayAlias"/> (picker label fields).
    /// Visible slugs only (current subject + shared).
    /// </summary>
    Task<Result<DysonModelSlugEntity, string>> FindSlugByNameAsync(
        string name,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<Guid>, string>> ListFavoriteSlugIdsAsync(
        CancellationToken cancellationToken = default);

    Task<Result<bool, string>> IsFavoriteAsync(
        Guid modelSlugId,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> AddFavoriteAsync(
        Guid modelSlugId,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> RemoveFavoriteAsync(
        Guid modelSlugId,
        CancellationToken cancellationToken = default);

    Task<Result<int, string>> RepairMisTaggedProvidersAsync(
        CancellationToken cancellationToken = default);
}
