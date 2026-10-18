using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace DysonHarness;

/// <summary>
/// Pocket TTS (<c>pocket-tts</c>) through <c>pocket/bridge.py</c> (<see cref="DysonSpeechBridge"/>): one JSON line
/// in (<c>text</c>, <c>voicePath</c>), length-prefixed PCM s16le 24 kHz frames out. The voice is always a local
/// catalog <c>.safetensors</c> file, so Pocket's voice-cloning check never runs. <c>HF_HUB_OFFLINE=1</c>.
/// </summary>
public sealed class DysonPocketSpeech(
    IDysonSpeechModelRepository models,
    IDysonSubjectSettingsRepository settings,
    string installRoot) : IDysonTextToSpeech
{
    public const string Id = "pocket-tts";
    public const int SampleRate = 24_000;
    public const string DefaultVoiceId = DysonSpeechInstaller.CatalogVoiceId;

    // ponytail: one process-wide bridge (single install root per app process); upgrade path is a keyed host.
    private static readonly DysonSpeechBridge Bridge = new("Pocket TTS");

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

        var voice = await ResolveVoicePathAsync(_models, _settings, _installRoot, voiceId, cancellationToken)
            .ConfigureAwait(false);
        if (voice.IsError)
        {
            yield return Result<DysonSpeechAudioChunk, string>.AsError(voice.Error);
            yield break;
        }

        var voicePath = voice.Value;
        await foreach (var chunk in Bridge.SynthesizeAsync(
                           () => Check(_installRoot, voicePath), () => StartInfo(_installRoot),
                           new { text, voicePath }, SampleRate, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// Null / blank id ⇒ <see cref="DysonAppSettingKeys.SpeechOutputVoiceId"/> when it is a Pocket voice ⇒
    /// <see cref="DefaultVoiceId"/>. Only catalog rows resolve; the path is derived from the install root, never stored.
    /// </summary>
    internal static async Task<Result<string, string>> ResolveVoicePathAsync(
        IDysonSpeechModelRepository models,
        IDysonSubjectSettingsRepository settings,
        string installRoot,
        string? voiceId,
        CancellationToken cancellationToken)
    {
        var id = await DysonSpeechBridge.ResolveVoiceIdAsync(
                models, settings, Id, "Pocket TTS", voiceId, DefaultVoiceId,
                $"No Pocket TTS voice '{DefaultVoiceId}'. Install Pocket TTS in Settings → Voice to add it.",
                cancellationToken)
            .ConfigureAwait(false);
        return id.IsError
            ? id
            : Result<string, string>.AsValue(Path.Combine(DysonSpeechPaths.GetPocketVoicesDir(installRoot), id.Value + ".safetensors"));
    }

    /// <summary>Kills the bridge if this process started one. Safe to call from app shutdown and uninstall.</summary>
    public static void StopBridge() => Bridge.Stop();

    private static VoidResult<string> Check(string installRoot, string voicePath)
    {
        if (!DysonSpeechInstaller.Probe(installRoot).PocketInstalled)
            return new VoidResult<string>("Pocket TTS is not installed. Open Settings → Voice and install it.");
        return File.Exists(voicePath)
            ? VoidResult<string>.Success
            : new VoidResult<string>($"Voice file {Path.GetFileName(voicePath)} is missing. Reinstall Pocket TTS in Settings → Voice.");
    }

    private static ProcessStartInfo StartInfo(string installRoot) => DysonSpeechInstaller.PythonStartInfo(
        installRoot,
        [DysonSpeechPaths.GetPocketSite(installRoot), DysonSpeechPaths.GetTorchDir(installRoot)],
        [DysonSpeechPaths.GetPocketBridge(installRoot), "--config", DysonSpeechPaths.GetPocketConfig(installRoot)]);
}
