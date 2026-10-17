namespace DysonHarness;

/// <summary>What a speech model row can do. Stays on the row; it is not a third engine interface.</summary>
[Flags]
public enum DysonSpeechCapability
{
    None = 0,

    /// <summary>Speech to text (<c>IDysonSpeechToText</c>).</summary>
    Input = 1,

    /// <summary>Text to speech (<c>IDysonTextToSpeech</c>).</summary>
    Output = 2,
}

/// <summary>
/// Subject-owned built-in speech model row (<c>speech_models</c>). Seeded by
/// <c>EnsureBuiltInAsync</c>; <see cref="IsInstalled"/> flips when the shared speech installer commits.
/// No absolute paths are stored; files live under <c>{DysonAppPaths.GetRoot}/speech/{variant}</c>.
/// </summary>
public sealed class DysonSpeechModelEntity
{
    public Guid Id { get; set; }

    /// <summary>Owning subject.</summary>
    public string SubjectId { get; set; } = "";

    /// <summary>Stable id (<c>phonon-2</c>, <c>pocket-tts</c>); unique per subject.</summary>
    public string RuntimeId { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public DysonSpeechCapability Capability { get; set; }

    /// <summary>Sidecar kind (<c>phonon</c>, <c>pocket</c>).</summary>
    public string RuntimeKind { get; set; } = "";

    /// <summary>True when the model can take a voice prompt (Pocket TTS). Shows Train TTS model.</summary>
    public bool SupportsVoicePrompt { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool IsInstalled { get; set; }

    /// <summary>Install variant (<c>dev</c> / <c>test</c> / <c>stable</c> / <c>preview</c>) when installed.</summary>
    public string? InstalledVariant { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Subject-owned speech voice (<c>speech_voices</c>). Phase 1 only holds catalog voices
/// (<c>alba</c>); the file is <c>{installRoot}/pocket/voices/{VoiceId}.safetensors</c>, never a stored path.
/// </summary>
public sealed class DysonSpeechVoiceEntity
{
    public Guid Id { get; set; }

    /// <summary>Voice id (<c>alba</c>); unique per subject and model.</summary>
    public string VoiceId { get; set; } = "";

    /// <summary>Owning subject.</summary>
    public string SubjectId { get; set; } = "";

    /// <summary><see cref="DysonSpeechModelEntity.RuntimeId"/> this voice belongs to.</summary>
    public string ModelRuntimeId { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>Original upload file name; null for catalog voices.</summary>
    public string? SourceFileName { get; set; }

    /// <summary>Install variant the voice file lives in.</summary>
    public string Variant { get; set; } = "";

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }
}
