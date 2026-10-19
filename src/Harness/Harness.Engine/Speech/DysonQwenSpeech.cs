using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace DysonHarness;

/// <summary>
/// Qwen3-TTS 0.6B Base (<c>Qwen/Qwen3-TTS-12Hz-0.6B-Base</c>) through <c>qwen/bridge.py</c> on the CPU torch,
/// or on <c>torch-rocm/</c> with <c>device_map="cuda:0"</c> when <c>speech_torch_device</c> is <c>rocm</c>.
/// 24 kHz PCM. Base has no built-in speaker: every voice is an enrolled <c>.pt</c> voice prompt (speaker
/// embedding from one .mp3 or .wav, x-vector only), never a fine-tune. The clip is kept as\n/// <c>{voiceId}.source.mp3</c> or <c>.source.wav</c>.
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
        "No Qwen3-TTS voice yet. Use Train TTS model in Settings → Voice with a short clip.";

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

        var torch = await DysonSpeechTorchDevice.InstalledDirectoryAsync(_settings, _installRoot, cancellationToken)
            .ConfigureAwait(false);
        if (torch.IsError)
        {
            yield return Result<DysonSpeechAudioChunk, string>.AsError(torch.Error);
            yield break;
        }

        var mode = await _settings.GetSettingAsync(DysonAppSettingKeys.SpeechResponseMode, cancellationToken)
            .ConfigureAwait(false);
        if (mode.IsError)
        {
            yield return Result<DysonSpeechAudioChunk, string>.AsError(mode.Error);
            yield break;
        }

        var lowLatency = DysonSpeechBridge.IsLowLatency(mode.Value);
        var voicePath = Path.Combine(DysonSpeechPaths.GetQwenVoicesDir(_installRoot), voice.Value + ".pt");
        await foreach (var chunk in Bridge.SynthesizeAsync(
                           () => Check(voicePath), () => StartInfo(torch.Value), new { text, voicePath, lowLatency }, SampleRate, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// Train TTS model: one .mp3 or .wav ⇒ a speaker-embedding voice prompt, saved and made the default voice.
    /// The clip is deleted. A non-blank <paramref name="transcript"/> is stored as <c>{voiceId}.txt</c> beside the <c>.pt</c>.
    /// </summary>
    public async Task<Result<DysonSpeechVoiceEntity, string>> EnrollVoiceAsync(
        Stream clip,
        string fileName,
        CancellationToken cancellationToken = default,
        string? transcript = null)
    {
        var torch = await DysonSpeechTorchDevice.InstalledDirectoryAsync(_settings, _installRoot, cancellationToken)
            .ConfigureAwait(false);
        if (torch.IsError)
            return Result<DysonSpeechVoiceEntity, string>.AsError(torch.Error);
        return await Bridge.EnrollAsync(
            _models, _settings, _installRoot, Id, DysonSpeechPaths.GetQwenVoicesDir(_installRoot),
            () => Check(null), () => StartInfo(torch.Value), clip, fileName, cancellationToken, acceptWav: true, transcript: transcript)
            .ConfigureAwait(false);
    }

    /// <summary>Kills the bridge if this process started one. Safe to call from app shutdown and uninstall.</summary>
    public static void StopBridge() => Bridge.Stop();

    private VoidResult<string> Check(string? voicePath)
    {
        if (!DysonSpeechInstaller.Probe(_installRoot).QwenInstalled)
            return new VoidResult<string>($"{DisplayName} is not installed. Open Settings → Voice and install it.");
        var scripts = DysonSpeechInstaller.EnsureBridgeScripts(BridgeResourceName, DysonSpeechPaths.GetQwenBridge(_installRoot));
        if (scripts.IsError)
            return new VoidResult<string>(scripts.Error);
        if (scripts.Value)
            StopBridge();
        return voicePath is null || File.Exists(voicePath)
            ? VoidResult<string>.Success
            : new VoidResult<string>($"Voice file {Path.GetFileName(voicePath)} is missing. Train the voice again in Settings → Voice.");
    }

    private ProcessStartInfo StartInfo(string torchDir) => DysonSpeechInstaller.PythonStartInfo(
        _installRoot,
        [DysonSpeechPaths.GetQwenSite(_installRoot), torchDir],
        [DysonSpeechPaths.GetQwenBridge(_installRoot), "--model", DysonSpeechPaths.GetQwenModelDir(_installRoot)],
        DysonSpeechTorchDevice.BridgeDevice(_installRoot, torchDir));
}
