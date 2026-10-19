using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

/// <summary>Seed + install flags for <see cref="DysonSpeechModelEntity"/> and catalog <see cref="DysonSpeechVoiceEntity"/> rows.</summary>
public sealed class DysonSpeechModelRepository(
    DysonDbAccessor accessor,
    IDysonSubjectContext subjectContext) : IDysonSpeechModelRepository
{
    public const string PhononRuntimeId = "phonon-2";
    public const string PocketRuntimeId = "pocket-tts";
    public const string QwenRuntimeId = "qwen3-tts-0.6b-base";
    public const string ChatterboxRuntimeId = "chatterbox-turbo";

    // Ids and display names match DysonGgufSpeechModels. LocalDb does not reference Harness.Engine.
    public const string NemotronRuntimeId = "nemotron-3.5-asr-streaming-0.6b";
    public const string PhononGgufRuntimeId = "phonon-2-gguf";
    public const string PhononGgufQ8RuntimeId = "phonon-2-gguf-q8";
    public const string PocketGgufRuntimeId = "pocket-tts-gguf";
    public const string PocketGgufQ8RuntimeId = "pocket-tts-gguf-q8";
    public const string QwenGgufRuntimeId = "qwen3-tts-0.6b-base-gguf";
    public const string QwenGgufQ4RuntimeId = "qwen3-tts-0.6b-base-gguf-q4";
    public const string ChatterboxGgufRuntimeId = "chatterbox-turbo-gguf";
    public const string ChatterboxGgufQ8RuntimeId = "chatterbox-turbo-gguf-q8";

    private const string PhononDisplayName = "Phonon 2 · BPS packed (Python)";
    private const string PocketDisplayName = "Pocket TTS · safetensors, no voice cloning (Python)";
    private const string QwenDisplayName = "Qwen3-TTS 0.6B Base · safetensors (Python)";
    private const string ChatterboxDisplayName = "Chatterbox Turbo (English only) · safetensors (Python)";
    private const string NemotronDisplayName = "Nemotron 3.5 ASR 0.6B · GGUF Q5_K_M, streaming (560 MB)";
    private const string PhononGgufDisplayName = "Phonon 2 · GGUF Q4_K (402 MB)";
    private const string PhononGgufQ8DisplayName = "Phonon 2 · GGUF Q8_0 (674 MB)";
    private const string PocketGgufDisplayName = "Pocket TTS · GGUF Q4_K, voice cloning (73 MB)";
    private const string PocketGgufQ8DisplayName = "Pocket TTS · GGUF Q8_0, voice cloning (124 MB)";
    private const string QwenGgufDisplayName = "Qwen3-TTS 0.6B Base · GGUF Q8_0 + F16 codec (1.34 GB)";
    private const string QwenGgufQ4DisplayName = "Qwen3-TTS 0.6B Base · GGUF Q4_K + F16 codec (0.89 GB), lower quality";
    private const string ChatterboxGgufDisplayName = "Chatterbox Turbo (English only) · GGUF Q4_K (735 MB)";
    private const string ChatterboxGgufQ8DisplayName = "Chatterbox Turbo (English only) · GGUF Q8_0 (1.03 GB)";

    private static readonly (string RuntimeId, string DisplayName, DysonSpeechCapability Capability, string RuntimeKind, bool SupportsVoicePrompt)[] BuiltIns =
    [
        (PhononRuntimeId, PhononDisplayName, DysonSpeechCapability.Input, "phonon", false),
        (PocketRuntimeId, PocketDisplayName, DysonSpeechCapability.Output, "pocket", true),
        (QwenRuntimeId, QwenDisplayName, DysonSpeechCapability.Output, "qwen", true),
        (ChatterboxRuntimeId, ChatterboxDisplayName, DysonSpeechCapability.Output, "chatterbox", true),
        (NemotronRuntimeId, NemotronDisplayName, DysonSpeechCapability.Input, "gguf", false),
        (PhononGgufRuntimeId, PhononGgufDisplayName, DysonSpeechCapability.Input, "gguf", false),
        (PhononGgufQ8RuntimeId, PhononGgufQ8DisplayName, DysonSpeechCapability.Input, "gguf", false),
        (PocketGgufRuntimeId, PocketGgufDisplayName, DysonSpeechCapability.Output, "gguf", true),
        (PocketGgufQ8RuntimeId, PocketGgufQ8DisplayName, DysonSpeechCapability.Output, "gguf", true),
        (QwenGgufRuntimeId, QwenGgufDisplayName, DysonSpeechCapability.Output, "gguf", true),
        (QwenGgufQ4RuntimeId, QwenGgufQ4DisplayName, DysonSpeechCapability.Output, "gguf", true),
        (ChatterboxGgufRuntimeId, ChatterboxGgufDisplayName, DysonSpeechCapability.Output, "gguf", true),
        (ChatterboxGgufQ8RuntimeId, ChatterboxGgufQ8DisplayName, DysonSpeechCapability.Output, "gguf", true),
    ];

    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly IDysonSubjectContext _subjectContext =
        subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));

    public Task<VoidResult<string>> EnsureBuiltInAsync(CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync((db, ct) => EnsureBuiltInCoreAsync(db, subjectId, ct), cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonSpeechModelEntity>, string>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync<Result<IReadOnlyList<DysonSpeechModelEntity>, string>>(async (db, ct) =>
        {
            try
            {
                var rows = await db.SpeechModels.AsNoTracking()
                    .Where(m => m.SubjectId == subjectId)
                    .OrderBy(m => m.RuntimeId)
                    .ToListAsync(ct).ConfigureAwait(false);
                return Result<IReadOnlyList<DysonSpeechModelEntity>, string>.AsValue(rows);
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<IReadOnlyList<DysonSpeechModelEntity>, string>.AsError(
                    $"Failed to list speech models: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> MarkInstalledAsync(
        string runtimeId,
        string variant,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => SetInstalledCoreAsync(db, subjectId, runtimeId, variant, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> MarkUninstalledAsync(
        string runtimeId,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => SetInstalledCoreAsync(db, subjectId, runtimeId, variant: null, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> EnsureCatalogVoiceAsync(
        string modelRuntimeId,
        string voiceId,
        string? displayName,
        string variant,
        CancellationToken cancellationToken = default,
        string? sourceFileName = null)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync<VoidResult<string>>(async (db, ct) =>
        {
            try
            {
                var exists = await db.SpeechVoices.AnyAsync(
                        v => v.SubjectId == subjectId && v.ModelRuntimeId == modelRuntimeId && v.VoiceId == voiceId,
                        ct)
                    .ConfigureAwait(false);
                if (exists)
                    return VoidResult<string>.Success;

                var name = displayName;
                if (name is null)
                {
                    var enrolled = await db.SpeechVoices.CountAsync(
                            v => v.SubjectId == subjectId && v.SourceFileName != null,
                            ct)
                        .ConfigureAwait(false);
                    name = $"Voice #{enrolled + 1}";
                }

                db.SpeechVoices.Add(new DysonSpeechVoiceEntity
                {
                    Id = Guid.NewGuid(),
                    SubjectId = subjectId,
                    ModelRuntimeId = modelRuntimeId,
                    VoiceId = voiceId,
                    DisplayName = name,
                    SourceFileName = sourceFileName,
                    Variant = variant,
                    CreatedUtc = DateTime.UtcNow,
                });
                await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return new VoidResult<string>($"Failed to add speech voice '{voiceId}': {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonSpeechVoiceEntity>, string>> ListVoicesAsync(
        string modelRuntimeId,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync<Result<IReadOnlyList<DysonSpeechVoiceEntity>, string>>(async (db, ct) =>
        {
            try
            {
                var rows = await db.SpeechVoices.AsNoTracking()
                    .Where(v => v.SubjectId == subjectId && v.ModelRuntimeId == modelRuntimeId)
                    .OrderBy(v => v.VoiceId)
                    .ToListAsync(ct).ConfigureAwait(false);
                return Result<IReadOnlyList<DysonSpeechVoiceEntity>, string>.AsValue(rows);
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<IReadOnlyList<DysonSpeechVoiceEntity>, string>.AsError(
                    $"Failed to list speech voices: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<string>, string>> GetWakeNamesAsync(CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync<Result<IReadOnlyList<string>, string>>(async (db, ct) =>
        {
            try
            {
                var row = await db.SpeechWakeNames.AsNoTracking()
                    .FirstOrDefaultAsync(w => w.SubjectId == subjectId, ct)
                    .ConfigureAwait(false);
                if (row is null)
                    return Result<IReadOnlyList<string>, string>.AsValue([]);

                return Result<IReadOnlyList<string>, string>.AsValue(
                [
                    row.Variant1, row.Variant2, row.Variant3, row.Variant4,
                    row.Variant5, row.Variant6, row.Variant7, row.Variant8,
                ]);
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<IReadOnlyList<string>, string>.AsError($"Failed to read wake names: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> SaveWakeNamesAsync(
        IReadOnlyList<string> variants,
        CancellationToken cancellationToken = default)
    {
        if (variants is null || variants.Count != 8)
            return Task.FromResult(new VoidResult<string>("Wake name needs 8 variants."));

        var texts = new string[8];
        for (var i = 0; i < 8; i++)
        {
            var raw = variants[i];
            var text = string.IsNullOrWhiteSpace(raw) ? "" : raw.Trim();
            if (text.Length == 0)
                return Task.FromResult(new VoidResult<string>($"Variant {i + 1} is empty."));
            if (text.Length > 200)
                return Task.FromResult(new VoidResult<string>($"Variant {i + 1} is over 200 characters."));
            texts[i] = text;
        }

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync<VoidResult<string>>(async (db, ct) =>
        {
            try
            {
                var row = await db.SpeechWakeNames
                    .FirstOrDefaultAsync(w => w.SubjectId == subjectId, ct)
                    .ConfigureAwait(false);
                var now = DateTime.UtcNow;
                if (row is null)
                {
                    row = new DysonSpeechWakeNameEntity
                    {
                        Id = Guid.NewGuid(),
                        SubjectId = subjectId,
                        CreatedUtc = now,
                    };
                    db.SpeechWakeNames.Add(row);
                }

                row.Variant1 = texts[0];
                row.Variant2 = texts[1];
                row.Variant3 = texts[2];
                row.Variant4 = texts[3];
                row.Variant5 = texts[4];
                row.Variant6 = texts[5];
                row.Variant7 = texts[6];
                row.Variant8 = texts[7];
                row.UpdatedUtc = now;
                await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return new VoidResult<string>($"Failed to save wake names: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> RenameVoiceAsync(
        string modelRuntimeId,
        string voiceId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var name = (displayName ?? "").Trim();
        if (name.Length == 0)
            return Task.FromResult(new VoidResult<string>("Voice name is empty."));
        if (name.Length > 64)
            return Task.FromResult(new VoidResult<string>("Voice name is over 64 characters."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync<VoidResult<string>>(async (db, ct) =>
        {
            try
            {
                var row = await db.SpeechVoices
                    .FirstOrDefaultAsync(
                        v => v.SubjectId == subjectId && v.ModelRuntimeId == modelRuntimeId && v.VoiceId == voiceId,
                        ct)
                    .ConfigureAwait(false);
                if (row is null)
                    return new VoidResult<string>($"Voice '{voiceId}' was not found.");

                row.DisplayName = name;
                await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return new VoidResult<string>($"Failed to rename voice '{voiceId}': {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> DeleteVoiceAsync(
        string modelRuntimeId,
        string voiceId,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync<VoidResult<string>>(async (db, ct) =>
        {
            try
            {
                var row = await db.SpeechVoices
                    .FirstOrDefaultAsync(
                        v => v.SubjectId == subjectId && v.ModelRuntimeId == modelRuntimeId && v.VoiceId == voiceId,
                        ct)
                    .ConfigureAwait(false);
                if (row is null)
                    return new VoidResult<string>($"Voice '{voiceId}' was not found.");
                if (row.SourceFileName is null)
                    return new VoidResult<string>("Built-in voices cannot be deleted.");

                db.SpeechVoices.Remove(row);
                await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return new VoidResult<string>($"Failed to delete voice '{voiceId}': {ex.Message}");
            }
        }, cancellationToken);
    }

    private static async Task<VoidResult<string>> EnsureBuiltInCoreAsync(
        DysonDbContext db,
        string subjectId,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await db.SpeechModels
                .Where(m => m.SubjectId == subjectId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var now = DateTime.UtcNow;
            foreach (var spec in BuiltIns)
            {
                var row = existing.FirstOrDefault(m => m.RuntimeId == spec.RuntimeId);
                if (row is null)
                {
                    db.SpeechModels.Add(NewRow(
                        subjectId, spec.RuntimeId, spec.DisplayName, spec.Capability, spec.RuntimeKind, spec.SupportsVoicePrompt, now));
                }
                else if (!string.Equals(row.DisplayName, spec.DisplayName, StringComparison.Ordinal))
                {
                    row.DisplayName = spec.DisplayName;
                    row.UpdatedUtc = now;
                }
            }

            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return new VoidResult<string>($"Failed to seed speech models: {ex.Message}");
        }
    }

    private static DysonSpeechModelEntity NewRow(
        string subjectId,
        string runtimeId,
        string displayName,
        DysonSpeechCapability capability,
        string runtimeKind,
        bool supportsVoicePrompt,
        DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        SubjectId = subjectId,
        RuntimeId = runtimeId,
        DisplayName = displayName,
        Capability = capability,
        RuntimeKind = runtimeKind,
        SupportsVoicePrompt = supportsVoicePrompt,
        IsEnabled = true,
        CreatedUtc = now,
        UpdatedUtc = now,
    };

    private static async Task<VoidResult<string>> SetInstalledCoreAsync(
        DysonDbContext db,
        string subjectId,
        string runtimeId,
        string? variant,
        CancellationToken cancellationToken)
    {
        try
        {
            var row = await db.SpeechModels
                .FirstOrDefaultAsync(m => m.SubjectId == subjectId && m.RuntimeId == runtimeId, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
                return new VoidResult<string>($"Speech model '{runtimeId}' not found.");

            row.IsInstalled = variant is not null;
            row.InstalledVariant = variant;
            row.UpdatedUtc = DateTime.UtcNow;

            if (variant is null)
            {
                var voices = await db.SpeechVoices
                    .Where(v => v.SubjectId == subjectId && v.ModelRuntimeId == runtimeId)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                db.SpeechVoices.RemoveRange(voices);
            }

            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return new VoidResult<string>($"Failed to update speech model '{runtimeId}': {ex.Message}");
        }
    }
}
