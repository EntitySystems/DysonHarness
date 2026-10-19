using System.Runtime.CompilerServices;

namespace DysonHarness;

/// <summary>
/// The one <see cref="IDysonTextToSpeech"/> registration: routes to the active text-to-speech model
/// (<see cref="DysonAppSettingKeys.SpeechOutputModelId"/>) among Pocket TTS, Qwen3-TTS 0.6B Base,
/// Chatterbox Turbo (English only), and the CrispASR GGUF builds of those three
/// (<see cref="DysonCrispSpeech"/>). An active model that is not installed is an error; it never falls back
/// to another model. Switching the active model uninstalls nothing.
/// </summary>
public sealed class DysonActiveTextToSpeech(
    IDysonSpeechModelRepository models,
    IDysonSubjectSettingsRepository settings,
    string installRoot) : IDysonTextToSpeech
{
    /// <summary><see cref="RuntimeId"/> of the router itself; the routed engine is <see cref="ResolveActiveAsync"/>.</summary>
    public const string ActiveRuntimeId = "active";

    /// <summary>Speech install tree. The accept-sentence WAV and sidecar live here.</summary>
    public string InstallRoot { get; } = installRoot ?? throw new ArgumentNullException(nameof(installRoot));

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
            var id when DysonCrispSpeech.IsTextToSpeech(id)
                => new DysonCrispSpeech(_models, _settings, installRoot, id),
            _ => _pocket,
        };

        // Qwen and Chatterbox each hold several GB of RAM: only the active engine keeps a bridge.
        await Task.Run(() => StopBridgesExcept(engine.RuntimeId), CancellationToken.None).ConfigureAwait(false);
        await foreach (var chunk in engine.SynthesizeAsync(text, voiceId, cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    /// <summary>
    /// Train TTS model for the active model: Qwen3-TTS (.mp3 or .wav; speaker embedding; the clip is kept as
    /// <c>{voiceId}.source.mp3</c> or <c>.source.wav</c>) and
    /// Chatterbox Turbo (.mp3) enroll a voice prompt (not a fine-tune). A non-blank
    /// <paramref name="transcript"/> is stored as <c>{voiceId}.txt</c> beside that <c>.pt</c>.
    /// CrispASR GGUF models enroll a 16-bit PCM .wav (<paramref name="transcript"/> is required for Qwen3-TTS GGUF,
    /// which is stored as 24 kHz mono). A non-blank transcript is stored as <c>{voiceId}.txt</c> for those three.
    /// Pocket TTS returns <see cref="DysonSpeechVoicePrep.VoiceCloningUnsupported"/> and stores nothing.
    /// </summary>
    public async Task<Result<DysonSpeechVoiceEntity, string>> PrepareVoiceAsync(
        Stream clip,
        string fileName,
        CancellationToken cancellationToken = default,
        string? transcript = null)
    {
        var active = await ResolveActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active.IsError)
            return Result<DysonSpeechVoiceEntity, string>.AsError(active.Error);

        transcript = string.IsNullOrWhiteSpace(transcript) ? null : transcript.Trim();
        return active.Value switch
        {
            DysonQwenSpeech.Id => await _qwen.EnrollVoiceAsync(clip, fileName, cancellationToken, transcript)
                .ConfigureAwait(false),
            DysonChatterboxSpeech.Id => await _chatterbox.EnrollVoiceAsync(clip, fileName, cancellationToken, transcript)
                .ConfigureAwait(false),
            var id when DysonGgufSpeechModels.IsCrisp(id) => await new DysonCrispSpeech(_models, _settings, installRoot, id)
                .EnrollVoiceAsync(clip, fileName, transcript, cancellationToken).ConfigureAwait(false),
            _ => await DysonSpeechVoicePrep.PrepareAsync(clip, fileName, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>Selects <paramref name="voiceId"/> on the active model (<see cref="DysonAppSettingKeys.SpeechOutputVoiceId"/>).</summary>
    public async Task<VoidResult<string>> SetVoiceAsync(string voiceId, CancellationToken cancellationToken = default)
    {
        var active = await ResolveActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active.IsError)
            return new VoidResult<string>(active.Error);

        var voices = await _models.ListVoicesAsync(active.Value, cancellationToken).ConfigureAwait(false);
        if (voices.IsError)
            return new VoidResult<string>(voices.Error);

        var id = (voiceId ?? "").Trim();
        var row = voices.Value.FirstOrDefault(v =>
            string.Equals(v.VoiceId, id, StringComparison.OrdinalIgnoreCase));
        return row is null
            ? new VoidResult<string>($"Unknown voice '{id}'.")
            : await _settings.SetSettingAsync(DysonAppSettingKeys.SpeechOutputVoiceId, row.VoiceId, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes an enrolled voice's files, then its row. Catalog voices are refused.
    /// Clears <see cref="DysonAppSettingKeys.SpeechOutputVoiceId"/> when it names this voice.
    /// </summary>
    public async Task<VoidResult<string>> DeleteVoiceAsync(
        string modelRuntimeId,
        string voiceId,
        CancellationToken cancellationToken = default)
    {
        var runtimeId = (modelRuntimeId ?? "").Trim();
        var id = (voiceId ?? "").Trim();
        var voices = await _models.ListVoicesAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (voices.IsError)
            return new VoidResult<string>(voices.Error);

        var row = voices.Value.FirstOrDefault(v => string.Equals(v.VoiceId, id, StringComparison.Ordinal));
        if (row is null)
            return new VoidResult<string>($"Voice '{id}' was not found.");
        if (row.SourceFileName is null)
            return new VoidResult<string>("Built-in voices cannot be deleted.");

        var dir = VoicesDir(runtimeId);
        if (dir is null)
            return new VoidResult<string>($"No voice files for '{runtimeId}'.");

        var deleted = await Task.Run(() => DeleteVoiceFiles(dir, row.VoiceId), cancellationToken).ConfigureAwait(false);
        if (deleted.IsError)
            return deleted;

        var removed = await _models.DeleteVoiceAsync(runtimeId, row.VoiceId, cancellationToken).ConfigureAwait(false);
        if (removed.IsError)
            return removed;

        var setting = await _settings.GetSettingAsync(DysonAppSettingKeys.SpeechOutputVoiceId, cancellationToken)
            .ConfigureAwait(false);
        if (setting.IsError)
            return new VoidResult<string>(setting.Error);
        if (!string.Equals(setting.Value?.Trim(), row.VoiceId, StringComparison.Ordinal))
            return VoidResult<string>.Success;

        return await _settings.SetSettingAsync(DysonAppSettingKeys.SpeechOutputVoiceId, "", cancellationToken)
            .ConfigureAwait(false);
    }

    private string? VoicesDir(string runtimeId) => runtimeId switch
    {
        DysonQwenSpeech.Id => DysonSpeechPaths.GetQwenVoicesDir(installRoot),
        DysonChatterboxSpeech.Id => DysonSpeechPaths.GetChatterboxVoicesDir(installRoot),
        var id when DysonCrispSpeech.IsTextToSpeech(id) => DysonGgufSpeechModels.GetVoicesDir(installRoot, id),
        _ => null,
    };

    /// <summary><c>voiceId.*</c> matches <c>voiceId.pt</c> and <c>voiceId.source.mp3</c>, not <c>voiceId-2.pt</c>.</summary>
    private static VoidResult<string> DeleteVoiceFiles(string dir, string voiceId)
    {
        try
        {
            if (!Directory.Exists(dir))
                return VoidResult<string>.Success;
            foreach (var file in Directory.GetFiles(dir, voiceId + ".*"))
                File.Delete(file);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new VoidResult<string>($"Could not delete voice files: {ex.Message}");
        }
    }

    private static void StopBridgesExcept(string runtimeId)
    {
        if (runtimeId != DysonPocketSpeech.Id)
            DysonPocketSpeech.StopBridge();
        if (runtimeId != DysonQwenSpeech.Id)
            DysonQwenSpeech.StopBridge();
        if (runtimeId != DysonChatterboxSpeech.Id)
            DysonChatterboxSpeech.StopBridge();
        if (DysonGgufSpeechModels.IsCrisp(runtimeId))
            DysonCrispSpeech.StopSidecarUnless(runtimeId);
        else
            DysonCrispSpeech.StopSidecarIfServing(DysonCrispSpeech.IsTextToSpeech);
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
