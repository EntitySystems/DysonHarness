using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace DysonHarness;

/// <summary>
/// Chatterbox Turbo (<c>ResembleAI/chatterbox-turbo</c>), <b>English only</b>, through <c>chatterbox/bridge.py</c>
/// on the shared CPU torch. 24 kHz PCM; no language parameter. Built-in voice <see cref="CatalogVoiceId"/>
/// (the repo's <c>conds.pt</c>). Train makes a voice prompt from an .mp3 longer than 5 seconds, never a fine-tune.
/// Every output carries Resemble's Perth implicit watermark (kept on purpose).
/// </summary>
public sealed class DysonChatterboxSpeech(
    IDysonSpeechModelRepository models,
    IDysonSubjectSettingsRepository settings,
    string installRoot) : IDysonTextToSpeech
{
    public const string Id = "chatterbox-turbo";
    public const string DisplayName = "Chatterbox Turbo (English only)";
    public const int SampleRate = 24_000;
    public const string CatalogVoiceId = "turbo";
    internal const string BridgeResourceName = "DysonHarness.Speech.chatterbox_bridge.py";

    private static readonly DysonSpeechBridge Bridge = new("Chatterbox Turbo");

    private readonly IDysonSpeechModelRepository _models = models ?? throw new ArgumentNullException(nameof(models));
    private readonly IDysonSubjectSettingsRepository _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly string _installRoot = installRoot ?? throw new ArgumentNullException(nameof(installRoot));

    public string RuntimeId => Id;

    public async IAsyncEnumerable<Result<DysonSpeechAudioChunk, string>> SynthesizeAsync(
        string text,
        string? voiceId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield return Result<DysonSpeechAudioChunk, string>.AsError("Text to speak is empty.");
            yield break;
        }

        var voice = await DysonSpeechBridge.ResolveVoiceIdAsync(
                _models, _settings, Id, "Chatterbox Turbo", voiceId, CatalogVoiceId,
                $"No Chatterbox Turbo voice '{CatalogVoiceId}'. Install {DisplayName} in Settings → Voice to add it.",
                cancellationToken)
            .ConfigureAwait(false);
        if (voice.IsError)
        {
            yield return Result<DysonSpeechAudioChunk, string>.AsError(voice.Error);
            yield break;
        }

        var voicePath = Path.Combine(DysonSpeechPaths.GetChatterboxVoicesDir(_installRoot), voice.Value + ".pt");
        await foreach (var chunk in Bridge.SynthesizeAsync(
                           () => Check(voicePath), StartInfo, new { text, voicePath }, SampleRate, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// Train TTS model: one .mp3 of English speech longer than 5 seconds (up to 15 s used) ⇒ a voice prompt
    /// (speaker embedding + speech tokens), saved and made the default voice. Shorter clips are an error.
    /// </summary>
    public Task<Result<DysonSpeechVoiceEntity, string>> EnrollVoiceAsync(
        Stream clip,
        string fileName,
        CancellationToken cancellationToken = default) =>
        Bridge.EnrollAsync(
            _models, _settings, _installRoot, Id, DysonSpeechPaths.GetChatterboxVoicesDir(_installRoot),
            () => Check(null), StartInfo, clip, fileName, cancellationToken);

    /// <summary>Kills the bridge if this process started one. Safe to call from app shutdown and uninstall.</summary>
    public static void StopBridge() => Bridge.Stop();

    private VoidResult<string> Check(string? voicePath)
    {
        if (!DysonSpeechInstaller.Probe(_installRoot).ChatterboxInstalled)
            return new VoidResult<string>($"{DisplayName} is not installed. Open Settings → Voice and install it.");
        return voicePath is null || File.Exists(voicePath)
            ? VoidResult<string>.Success
            : new VoidResult<string>($"Voice file {Path.GetFileName(voicePath)} is missing. Reinstall {DisplayName} or train the voice again.");
    }

    private ProcessStartInfo StartInfo() => DysonSpeechInstaller.PythonStartInfo(
        _installRoot,
        [DysonSpeechPaths.GetChatterboxSite(_installRoot), DysonSpeechPaths.GetTorchDir(_installRoot)],
        [DysonSpeechPaths.GetChatterboxBridge(_installRoot), "--model", DysonSpeechPaths.GetChatterboxModelDir(_installRoot)]);
}
