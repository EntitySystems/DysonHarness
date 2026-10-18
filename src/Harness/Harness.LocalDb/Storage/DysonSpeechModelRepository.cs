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
        string displayName,
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

                db.SpeechVoices.Add(new DysonSpeechVoiceEntity
                {
                    Id = Guid.NewGuid(),
                    SubjectId = subjectId,
                    ModelRuntimeId = modelRuntimeId,
                    VoiceId = voiceId,
                    DisplayName = displayName,
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

    private static async Task<VoidResult<string>> EnsureBuiltInCoreAsync(
        DysonDbContext db,
        string subjectId,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await db.SpeechModels
                .Where(m => m.SubjectId == subjectId)
                .Select(m => m.RuntimeId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var now = DateTime.UtcNow;
            if (!existing.Contains(PhononRuntimeId))
                db.SpeechModels.Add(NewRow(subjectId, PhononRuntimeId, "Phonon 2", DysonSpeechCapability.Input, "phonon", false, now));
            if (!existing.Contains(PocketRuntimeId))
                db.SpeechModels.Add(NewRow(subjectId, PocketRuntimeId, "Pocket TTS", DysonSpeechCapability.Output, "pocket", true, now));
            if (!existing.Contains(QwenRuntimeId))
                db.SpeechModels.Add(NewRow(subjectId, QwenRuntimeId, "Qwen3-TTS 0.6B Base", DysonSpeechCapability.Output, "qwen", true, now));
            // English only is a product limit, recorded in the name the Voice page renders (no language column).
            if (!existing.Contains(ChatterboxRuntimeId))
                db.SpeechModels.Add(NewRow(subjectId, ChatterboxRuntimeId, "Chatterbox Turbo (English only)", DysonSpeechCapability.Output, "chatterbox", true, now));

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
