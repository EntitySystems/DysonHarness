namespace DysonHarness;

/// <summary>
/// Subject-owned <c>speech_models</c> and <c>speech_voices</c>.
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
    /// Inserts a voice row when missing. <c>sourceFileName</c> is the uploaded clip's name for an enrolled voice;
    /// null for a catalog voice.
    /// </summary>
    Task<VoidResult<string>> EnsureCatalogVoiceAsync(
        string modelRuntimeId,
        string voiceId,
        string displayName,
        string variant,
        CancellationToken cancellationToken = default,
        string? sourceFileName = null);

    Task<Result<IReadOnlyList<DysonSpeechVoiceEntity>, string>> ListVoicesAsync(
        string modelRuntimeId,
        CancellationToken cancellationToken = default);
}
