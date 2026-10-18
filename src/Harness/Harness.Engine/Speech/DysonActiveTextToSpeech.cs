using System.Runtime.CompilerServices;

namespace DysonHarness;

/// <summary>
/// The one <see cref="IDysonTextToSpeech"/> registration: routes to the active text-to-speech model
/// (<see cref="DysonAppSettingKeys.SpeechOutputModelId"/>) among Pocket TTS, Qwen3-TTS 0.6B Base, and
/// Chatterbox Turbo (English only). An active model that is not installed is an error; it never falls back
/// to another model. Switching the active model uninstalls nothing.
/// </summary>
public sealed class DysonActiveTextToSpeech(
    IDysonSpeechModelRepository models,
    IDysonSubjectSettingsRepository settings,
    string installRoot) : IDysonTextToSpeech
{
    /// <summary><see cref="RuntimeId"/> of the router itself; the routed engine is <see cref="ResolveActiveAsync"/>.</summary>
    public const string ActiveRuntimeId = "active";

    private readonly IDysonSpeechModelRepository _models = models ?? throw new ArgumentNullException(nameof(models));
    private readonly IDysonSubjectSettingsRepository _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly DysonPocketSpeech _pocket = new(models, settings, installRoot);
    private readonly DysonQwenSpeech _qwen = new(models, settings, installRoot);
    private readonly DysonChatterboxSpeech _chatterbox = new(models, settings, installRoot);

    public string RuntimeId => ActiveRuntimeId;

    /// <summary>
    /// The active TTS <c>RuntimeId</c>. A set preference must be an enabled, installed Output row. A missing
    /// preference ⇒ the single installed Output row; none or several is an error pointing at Settings → Voice.
    /// </summary>
    public async Task<Result<string, string>> ResolveActiveAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _models.ListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.IsError)
            return Result<string, string>.AsError(rows.Error);

        var setting = await _settings.GetSettingAsync(DysonAppSettingKeys.SpeechOutputModelId, cancellationToken)
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

        var selected = DysonSpeechModelSelection.Select(rows.Value, DysonSpeechCapability.Output, null);
        if (selected is not null)
            return Result<string, string>.AsValue(selected.RuntimeId);

        return Result<string, string>.AsError(
            rows.Value.Any(m => Usable(m).IsSuccess)
                ? "Choose the active text-to-speech model in Settings → Voice."
                : "No text-to-speech model is installed. Open Settings → Voice and install one.");
    }

    /// <summary>Writes <see cref="DysonAppSettingKeys.SpeechOutputModelId"/>. Only an installed, enabled Output row.</summary>
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

        return await _settings.SetSettingAsync(DysonAppSettingKeys.SpeechOutputModelId, row!.RuntimeId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> SynthesizeAsync(
        string text,
        string? voiceId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var active = await ResolveActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active.IsError)
        {
            yield return Result<DysonSpeechAudioChunk, string>.AsError(active.Error);
            yield break;
        }

        IDysonTextToSpeech engine = active.Value switch
        {
            DysonQwenSpeech.Id => _qwen,
            DysonChatterboxSpeech.Id => _chatterbox,
            _ => _pocket,
        };

        // Qwen and Chatterbox each hold several GB of RAM: only the active engine keeps a bridge.
        await Task.Run(() => StopBridgesExcept(engine.RuntimeId), CancellationToken.None).ConfigureAwait(false);
        await foreach (var chunk in engine.SynthesizeAsync(text, voiceId, cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    /// <summary>
    /// Train TTS model for the active model: Qwen3-TTS and Chatterbox Turbo enroll a voice prompt (not a fine-tune);
    /// Pocket TTS returns <see cref="DysonSpeechVoicePrep.VoiceCloningUnsupported"/> and stores nothing.
    /// </summary>
    public async Task<Result<DysonSpeechVoiceEntity, string>> PrepareVoiceAsync(
        Stream clip,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var active = await ResolveActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active.IsError)
            return Result<DysonSpeechVoiceEntity, string>.AsError(active.Error);

        return active.Value switch
        {
            DysonQwenSpeech.Id => await _qwen.EnrollVoiceAsync(clip, fileName, cancellationToken).ConfigureAwait(false),
            DysonChatterboxSpeech.Id => await _chatterbox.EnrollVoiceAsync(clip, fileName, cancellationToken).ConfigureAwait(false),
            _ => await DysonSpeechVoicePrep.PrepareAsync(clip, fileName, cancellationToken).ConfigureAwait(false),
        };
    }

    private static void StopBridgesExcept(string runtimeId)
    {
        if (runtimeId != DysonPocketSpeech.Id)
            DysonPocketSpeech.StopBridge();
        if (runtimeId != DysonQwenSpeech.Id)
            DysonQwenSpeech.StopBridge();
        if (runtimeId != DysonChatterboxSpeech.Id)
            DysonChatterboxSpeech.StopBridge();
    }

    /// <summary>Error text uses <c>{0}</c> for the model name.</summary>
    private static VoidResult<string> Usable(DysonSpeechModelEntity? row) => row switch
    {
        null => new VoidResult<string>("Text-to-speech model '{0}' is unknown. Choose another in Settings → Voice."),
        { Capability: var c } when !c.HasFlag(DysonSpeechCapability.Output) =>
            new VoidResult<string>("{0} is not a text-to-speech model."),
        { IsEnabled: false } => new VoidResult<string>("{0} is disabled. Choose another text-to-speech model in Settings → Voice."),
        { IsInstalled: false } => new VoidResult<string>(
            "{0} is not installed. Open Settings → Voice and install it, or choose another active text-to-speech model."),
        _ => VoidResult<string>.Success,
    };
}
