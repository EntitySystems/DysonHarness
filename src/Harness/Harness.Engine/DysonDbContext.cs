using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonDbContext : DbContext
{
    public DysonDbContext()
    {
    }

    public DysonDbContext(DbContextOptions<DysonDbContext> options)
        : base(options)
    {
    }

    public DbSet<DysonModelProviderEntity> ModelProviders => Set<DysonModelProviderEntity>();
    public DbSet<DysonModelSlugEntity> ModelSlugs => Set<DysonModelSlugEntity>();
    public DbSet<DysonModelFavoriteEntity> ModelFavorites => Set<DysonModelFavoriteEntity>();
    public DbSet<DysonWorkDirectoryEntity> WorkDirectories => Set<DysonWorkDirectoryEntity>();
    public DbSet<DysonSessionEntity> Sessions => Set<DysonSessionEntity>();
    public DbSet<DysonTurnEntity> Turns => Set<DysonTurnEntity>();
    public DbSet<DysonSessionLogEntry> SessionLogs => Set<DysonSessionLogEntry>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (optionsBuilder.IsConfigured)
            return;

        DysonAppPaths.EnsureRoot(DysonBuildInfo.Current);
        var path = DysonAppPaths.GetDatabasePath(DysonBuildInfo.Current);
        optionsBuilder.UseSqlite($"Data Source={path}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DysonModelProviderEntity>(e =>
        {
            e.ToTable("model_providers");
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).IsRequired();
            e.Property(x => x.ProviderKind).IsRequired();
            e.HasMany(x => x.Slugs)
                .WithOne(s => s.Provider)
                .HasForeignKey(s => s.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DysonModelSlugEntity>(e =>
        {
            e.ToTable("model_slugs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Slug).IsRequired();
            e.Property(x => x.DisplayAlias).IsRequired();
            e.HasIndex(x => new { x.ProviderId, x.Slug }).IsUnique();
            e.HasIndex(x => x.IsDefault);
        });

        modelBuilder.Entity<DysonModelFavoriteEntity>(e =>
        {
            e.ToTable("model_favorites");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ModelSlugId).IsUnique();
            e.HasOne(x => x.ModelSlug)
                .WithMany()
                .HasForeignKey(x => x.ModelSlugId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DysonWorkDirectoryEntity>(e =>
        {
            e.ToTable("work_directories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.AbsolutePath).IsRequired();
            e.HasIndex(x => x.AbsolutePath).IsUnique();
            e.HasIndex(x => x.LastOpenedUtc);
        });

        modelBuilder.Entity<DysonSessionEntity>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.AgentMode).IsRequired();
            e.Property(x => x.SystemPromptSnapshot).IsRequired();
            e.Property(x => x.McpAccessMode).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => x.LastActivityUtc);
            e.HasIndex(x => x.ParentSessionId);
            e.HasIndex(x => x.WorkDirectoryId);
            e.HasOne(x => x.ParentSession)
                .WithMany()
                .HasForeignKey(x => x.ParentSessionId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ModelSlug)
                .WithMany()
                .HasForeignKey(x => x.ModelSlugId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.WorkDirectory)
                .WithMany(w => w.Sessions)
                .HasForeignKey(x => x.WorkDirectoryId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DysonTurnEntity>(e =>
        {
            e.ToTable("turns");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.ToolStateJson).IsRequired();
            e.HasIndex(x => new { x.SessionId, x.Sequence }).IsUnique();
            e.HasOne(x => x.Session)
                .WithMany(s => s.Turns)
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DysonSessionLogEntry>(e =>
        {
            e.ToTable("session_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).IsRequired();
            e.Property(x => x.PayloadJson).IsRequired();
            e.HasIndex(x => new { x.SessionId, x.Sequence }).IsUnique();
            e.HasIndex(x => x.TurnId);
            e.HasIndex(x => new { x.SessionId, x.Kind });
            e.HasOne(x => x.Session)
                .WithMany(s => s.Logs)
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// Opens a context for the current build mode's <c>dyson.db</c> and applies migrations.
    /// </summary>
    public static DysonDbContext Open()
    {
        DysonAppPaths.EnsureRoot(DysonBuildInfo.Current);
        var ctx = new DysonDbContext();
        ctx.Database.Migrate();
        return ctx;
    }

    /// <summary>Applies pending migrations (call after DI constructs a context).</summary>
    public void EnsureMigrated() => Database.Migrate();
}
