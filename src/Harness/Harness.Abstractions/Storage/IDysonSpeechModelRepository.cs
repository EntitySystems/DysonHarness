namespace DysonHarness;

/// <summary>
/// Subject-owned <c>speech_models</c>, <c>speech_voices</c>, and <c>speech_wake_names</c>.
/// Visibility: current subject only.
/// </summary>
public interface IDysonSpeechModelRepository
{
    /// <summary>
    /// Inserts <c>phonon-2</c> (Input), and <c>pocket-tts</c>, <c>qwen3-tts-0.6b-base</c>, <c>chatterbox-turbo</c>
    /// (Output, voice prompt) when missing.
    /// </summary>
    Task<VoidResult<string>> EnsureBuiltInAsync(CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DysonSpeechModelEntity>, string>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> MarkInstalledAsync(
        string runtimeId,
        string variant,
        CancellationToken cancellationToken = default);

    /// <summary>Clears <c>IsInstalled</c> and deletes this subject's voices for the model.</summary>
    Task<VoidResult<string>> MarkUninstalledAsync(
        string runtimeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a voice row when missing. <paramref name="displayName"/> null names an enrolled voice
    /// <c>Voice #N</c> (N is 1 + this subject's rows with <c>SourceFileName</c> set, across every model).
    /// <paramref name="sourceFileName"/> is the uploaded clip's name for an enrolled voice; null for a catalog voice.
    /// </summary>
    Task<VoidResult<string>> EnsureCatalogVoiceAsync(
        string modelRuntimeId,
        string voiceId,
        string? displayName,
        string variant,
        CancellationToken cancellationToken = default,
        string? sourceFileName = null);

    Task<Result<IReadOnlyList<DysonSpeechVoiceEntity>, string>> ListVoicesAsync(
        string modelRuntimeId,
        CancellationToken cancellationToken = default);

    /// <summary>The 8 stored wake texts, in order. An empty list when this subject has no row.</summary>
    Task<Result<IReadOnlyList<string>, string>> GetWakeNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts this subject's wake-name row. Requires exactly 8 trimmed, non-blank texts of at most 200 characters.
    /// </summary>
    Task<VoidResult<string>> SaveWakeNamesAsync(
        IReadOnlyList<string> variants,
        CancellationToken cancellationToken = default);

    /// <summary>Renames any voice, including a catalog voice. The name is trimmed and must be 1–64 characters.</summary>
    Task<VoidResult<string>> RenameVoiceAsync(
        string modelRuntimeId,
        string voiceId,
        string displayName,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an enrolled voice row. Catalog voices (<c>SourceFileName</c> null) are refused. Files are not touched.</summary>
    Task<VoidResult<string>> DeleteVoiceAsync(
        string modelRuntimeId,
        string voiceId,
        CancellationToken cancellationToken = default);
}
