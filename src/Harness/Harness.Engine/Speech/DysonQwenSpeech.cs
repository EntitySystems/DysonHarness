using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace DysonHarness;

/// <summary>
/// Qwen3-TTS 0.6B Base (<c>Qwen/Qwen3-TTS-12Hz-0.6B-Base</c>) through <c>qwen/bridge.py</c> on the shared CPU torch.
/// 24 kHz PCM. Base has no built-in speaker: every voice is an enrolled <c>.pt</c> voice prompt (speaker
/// embedding from one .mp3, x-vector only), never a fine-tune.
/// </summary>
public sealed class DysonQwenSpeech(
    IDysonSpeechModelRepository models,
    IDysonSubjectSettingsRepository settings,
    string installRoot) : IDysonTextToSpeech
{
    public const string Id = "qwen3-tts-0.6b-base";
    public const string DisplayName = "Qwen3-TTS 0.6B Base";
    public const int SampleRate = 24_000;
    internal const string BridgeResourceName = "DysonHarness.Speech.qwen_bridge.py";

    internal const string NoVoiceMessage =
        "No Qwen3-TTS voice yet. Use Train TTS model in Settings → Voice with a short .mp3 clip.";

    private static readonly DysonSpeechBridge Bridge = new("Qwen3-TTS");

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
                _models, _settings, Id, "Qwen3-TTS", voiceId, defaultVoiceId: null, NoVoiceMessage, cancellationToken)
            .ConfigureAwait(false);
        if (voice.IsError)
        {
            yield return Result<DysonSpeechAudioChunk, string>.AsError(voice.Error);
            yield break;
        }

        var voicePath = Path.Combine(DysonSpeechPaths.GetQwenVoicesDir(_installRoot), voice.Value + ".pt");
        await foreach (var chunk in Bridge.SynthesizeAsync(
                           () => Check(voicePath), StartInfo, new { text, voicePath }, SampleRate, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <summary>Train TTS model: one .mp3 ⇒ a speaker-embedding voice prompt, saved and made the default voice.</summary>
    public Task<Result<DysonSpeechVoiceEntity, string>> EnrollVoiceAsync(
        Stream clip,
        string fileName,
        CancellationToken cancellationToken = default) =>
        Bridge.EnrollAsync(
            _models, _settings, _installRoot, Id, DysonSpeechPaths.GetQwenVoicesDir(_installRoot),
            () => Check(null), StartInfo, clip, fileName, cancellationToken);

    /// <summary>Kills the bridge if this process started one. Safe to call from app shutdown and uninstall.</summary>
    public static void StopBridge() => Bridge.Stop();

    private VoidResult<string> Check(string? voicePath)
    {
        if (!DysonSpeechInstaller.Probe(_installRoot).QwenInstalled)
            return new VoidResult<string>($"{DisplayName} is not installed. Open Settings → Voice and install it.");
        return voicePath is null || File.Exists(voicePath)
            ? VoidResult<string>.Success
            : new VoidResult<string>($"Voice file {Path.GetFileName(voicePath)} is missing. Train the voice again in Settings → Voice.");
    }

    private ProcessStartInfo StartInfo() => DysonSpeechInstaller.PythonStartInfo(
        _installRoot,
        [DysonSpeechPaths.GetQwenSite(_installRoot), DysonSpeechPaths.GetTorchDir(_installRoot)],
        [DysonSpeechPaths.GetQwenBridge(_installRoot), "--model", DysonSpeechPaths.GetQwenModelDir(_installRoot)]);
}
