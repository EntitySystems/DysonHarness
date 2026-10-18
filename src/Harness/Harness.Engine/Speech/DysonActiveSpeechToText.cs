using System.Runtime.CompilerServices;

namespace DysonHarness;

/// <summary>
/// The one <see cref="IDysonSpeechToText"/> registration: routes to the active speech-to-text model
/// (<see cref="DysonAppSettingKeys.SpeechInputModelId"/>) among Phonon 2, Phonon 2 GGUF, and
/// Nemotron 3.5 streaming. An active model that is not installed is an error; it never falls back
/// to another model. Live PCM is Nemotron only, and Nemotron is Windows only.
/// </summary>
public sealed class DysonActiveSpeechToText(
    IDysonSpeechModelRepository models,
    IDysonSubjectSettingsRepository settings,
    string installRoot) : IDysonSpeechToText, IDysonLiveSpeechToText
{
    /// <summary><see cref="RuntimeId"/> of the router itself; the routed engine is <see cref="ResolveActiveAsync"/>.</summary>
    public const string ActiveRuntimeId = "active";

    private readonly IDysonSpeechModelRepository _models = models ?? throw new ArgumentNullException(nameof(models));
    private readonly IDysonSubjectSettingsRepository _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly string _installRoot = installRoot ?? throw new ArgumentNullException(nameof(installRoot));

    public string RuntimeId => ActiveRuntimeId;

    /// <summary>
    /// The active STT <c>RuntimeId</c>. A set preference must be an enabled, installed Input row. A missing
    /// preference ⇒ the single installed Input row; none or several is an error pointing at Settings → Voice.
    /// </summary>
    public async Task<Result<string, string>> ResolveActiveAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _models.ListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.IsError)
            return Result<string, string>.AsError(rows.Error);

        var setting = await _settings.GetSettingAsync(DysonAppSettingKeys.SpeechInputModelId, cancellationToken)
            .ConfigureAwait(false);
        if (setting.IsError)
            return Result<string, string>.AsError(setting.Error);

        if (!string.IsNullOrWhiteSpace(setting.Value))
        {
            var id = setting.Value.Trim();
            var row = rows.Value.FirstOrDefault(m => string.Equals(m.RuntimeId, id, StringComparison.OrdinalIgnoreCase));
            var usable = Usable(row);
            return usable.IsError
                ? Result<string, string>.AsError(usable.Error.Replace("{0}", row?.DisplayName ?? id, StringComparison.Ordinal))
                : Result<string, string>.AsValue(row!.RuntimeId);
        }

        var selected = DysonSpeechModelSelection.Select(rows.Value, DysonSpeechCapability.Input, null);
        if (selected is not null)
            return Result<string, string>.AsValue(selected.RuntimeId);

        return Result<string, string>.AsError(
            rows.Value.Any(m => Usable(m).IsSuccess)
                ? "Choose the active speech-to-text model in Settings → Voice."
                : "No speech-to-text model is installed. Open Settings → Voice and install one.");
    }

    /// <summary>Writes <see cref="DysonAppSettingKeys.SpeechInputModelId"/>. Only an installed, enabled Input row.</summary>
    public async Task<VoidResult<string>> SetActiveAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        var rows = await _models.ListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.IsError)
            return new VoidResult<string>(rows.Error);

        var row = rows.Value.FirstOrDefault(m =>
            string.Equals(m.RuntimeId, runtimeId?.Trim(), StringComparison.OrdinalIgnoreCase));
        var usable = Usable(row);
        if (usable.IsError)
            return new VoidResult<string>(usable.Error.Replace("{0}", row?.DisplayName ?? runtimeId, StringComparison.Ordinal));

        return await _settings.SetSettingAsync(DysonAppSettingKeys.SpeechInputModelId, row!.RuntimeId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<bool, string>> UsesLivePcmAsync(CancellationToken cancellationToken)
    {
        var active = await ResolveActiveAsync(cancellationToken).ConfigureAwait(false);
        return active.IsError
            ? Result<bool, string>.AsError(active.Error)
            : Result<bool, string>.AsValue(active.Value == DysonGgufSpeechModels.NemotronId);
    }

    public async IAsyncEnumerable<Result<DysonSpeechChunk, string>> TranscribeAsync(
        Stream audio,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var active = await ResolveActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active.IsError)
        {
            yield return Result<DysonSpeechChunk, string>.AsError(active.Error);
            yield break;
        }

        if (active.Value == DysonGgufSpeechModels.NemotronId && !OperatingSystem.IsWindows())
        {
            yield return Result<DysonSpeechChunk, string>.AsError("Nemotron 3.5 is Windows only.");
            yield break;
        }

        await Task.Run(() => StopOthers(active.Value), CancellationToken.None).ConfigureAwait(false);
        var engine = Engine(active.Value);
        if (engine is null)
        {
            yield return Result<DysonSpeechChunk, string>.AsError($"No speech-to-text engine for '{active.Value}'.");
            yield break;
        }

        await foreach (var chunk in engine.TranscribeAsync(audio, cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    public async IAsyncEnumerable<Result<DysonSpeechChunk, string>> TranscribeLiveAsync(
        IAsyncEnumerable<Result<byte[], string>> pcm16kMono,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var active = await ResolveActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active.IsError)
        {
            yield return Result<DysonSpeechChunk, string>.AsError(active.Error);
            yield break;
        }

        if (active.Value != DysonGgufSpeechModels.NemotronId)
        {
            yield return Result<DysonSpeechChunk, string>.AsError("Live transcription is only available for Nemotron 3.5.");
            yield break;
        }

        if (!OperatingSystem.IsWindows())
        {
            yield return Result<DysonSpeechChunk, string>.AsError("Nemotron 3.5 is Windows only.");
            yield break;
        }

        await Task.Run(() => StopOthers(active.Value), CancellationToken.None).ConfigureAwait(false);
        await foreach (var chunk in DysonNemotronSpeech
                           .TranscribeLiveAsync(_installRoot, pcm16kMono, cancellationToken)
                           .ConfigureAwait(false))
            yield return chunk;
    }

    private IDysonSpeechToText? Engine(string runtimeId) => runtimeId switch
    {
        DysonPhononSpeech.Id => new DysonPhononSpeech(_installRoot),
        DysonGgufSpeechModels.NemotronId => new DysonNemotronSpeech(_installRoot),
        DysonGgufSpeechModels.PhononGgufId => new DysonCrispSpeech(_models, _settings, _installRoot, runtimeId),
        _ => null,
    };

    private static void StopOthers(string runtimeId)
    {
        if (!string.Equals(runtimeId, DysonPhononSpeech.Id, StringComparison.Ordinal))
            DysonPhononSpeech.StopSidecar();
        if (string.Equals(runtimeId, DysonGgufSpeechModels.PhononGgufId, StringComparison.Ordinal))
            DysonCrispSpeech.StopSidecarUnless(runtimeId);
        else
            DysonCrispSpeech.StopSidecar();
        if (!string.Equals(runtimeId, DysonGgufSpeechModels.NemotronId, StringComparison.Ordinal))
            DysonNemotronSpeech.Stop();
    }

    /// <summary>Error text uses <c>{0}</c> for the model name.</summary>
    private static VoidResult<string> Usable(DysonSpeechModelEntity? row) => row switch
    {
        null => new VoidResult<string>("Speech-to-text model '{0}' is unknown. Choose another in Settings → Voice."),
        { Capability: var c } when !c.HasFlag(DysonSpeechCapability.Input) =>
            new VoidResult<string>("{0} is not a speech-to-text model."),
        { IsEnabled: false } => new VoidResult<string>("{0} is disabled. Choose another speech-to-text model in Settings → Voice."),
        { IsInstalled: false } => new VoidResult<string>(
            "{0} is not installed. Open Settings → Voice and install it, or choose another active speech-to-text model."),
        _ => VoidResult<string>.Success,
    };
}
