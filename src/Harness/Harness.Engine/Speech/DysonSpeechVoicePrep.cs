namespace DysonHarness;

/// <summary>
/// Train TTS model entry (Pocket only). Phase 1 installs the public no-cloning weights, which cannot
/// encode a voice from audio, so this always returns <see cref="VoiceCloningUnsupported"/>. It writes no
/// voice row, does not read or keep the clip, and never downloads the gated repo.
/// </summary>
public static class DysonSpeechVoicePrep
{
    public const string VoiceCloningUnsupported = "VOICE_CLONING_UNSUPPORTED";

    public static Task<Result<DysonSpeechVoiceEntity, string>> PrepareAsync(
        Stream clip,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(Path.GetExtension(fileName ?? ""), ".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Result<DysonSpeechVoiceEntity, string>.AsError(
                "Train TTS model takes one .mp3 file."));
        }

        return Task.FromResult(Result<DysonSpeechVoiceEntity, string>.AsError(
            VoiceCloningUnsupported
            + ": the installed Pocket TTS weights (kyutai/pocket-tts-without-voice-cloning) cannot make a voice "
            + "from audio. Nothing was saved. Synthesis keeps using the catalog voice alba."));
    }
}
