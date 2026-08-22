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

    public DbSet<DysonSubjectEntity> Subjects => Set<DysonSubjectEntity>();
    public DbSet<DysonModelProviderEntity> ModelProviders => Set<DysonModelProviderEntity>();
    public DbSet<DysonModelSlugEntity> ModelSlugs => Set<DysonModelSlugEntity>();
    public DbSet<DysonModelFavoriteEntity> ModelFavorites => Set<DysonModelFavoriteEntity>();
    public DbSet<DysonWorkDirectoryEntity> WorkDirectories => Set<DysonWorkDirectoryEntity>();
    public DbSet<DysonWorkDirectoryConfigurationEntity> WorkDirectoryConfigurations =>
        Set<DysonWorkDirectoryConfigurationEntity>();
    public DbSet<DysonSessionEntity> Sessions => Set<DysonSessionEntity>();
    public DbSet<DysonTurnEntity> Turns => Set<DysonTurnEntity>();
    public DbSet<DysonSessionLogEntry> SessionLogs => Set<DysonSessionLogEntry>();
    public DbSet<DysonSessionTodoEntity> SessionTodos => Set<DysonSessionTodoEntity>();
    public DbSet<DysonAppSettingEntity> AppSettings => Set<DysonAppSettingEntity>();
    public DbSet<DysonConfiguredShellEntity> ConfiguredShells => Set<DysonConfiguredShellEntity>();
    public DbSet<DysonPluginInstallationEntity> PluginInstallations => Set<DysonPluginInstallationEntity>();
    public DbSet<DysonPluginVariableValueEntity> PluginVariableValues => Set<DysonPluginVariableValueEntity>();
    public DbSet<DysonPluginMcpGrantEntity> PluginMcpGrants => Set<DysonPluginMcpGrantEntity>();
    public DbSet<DysonPluginHookReviewEntity> PluginHookReviews => Set<DysonPluginHookReviewEntity>();
    public DbSet<DysonPluginHookAuditEntity> PluginHookAudits => Set<DysonPluginHookAuditEntity>();
    public DbSet<DysonUsageRequestEntity> UsageRequests => Set<DysonUsageRequestEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (optionsBuilder.IsConfigured)
            return;

        DysonSqliteConfigurator.Configure(optionsBuilder, "dyson-design.db");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DysonSubjectEntity>(e =>
        {
            e.ToTable("subjects");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).IsRequired();
            e.Property(x => x.UserId);
        });

        modelBuilder.Entity<DysonModelProviderEntity>(e =>
        {
            e.ToTable("model_providers");
            e.HasKey(x => x.Id);
            e.Property(x => x.SubjectId).IsRequired();
            e.Property(x => x.DisplayName).IsRequired();
            e.Property(x => x.ProviderKind).IsRequired();
            e.HasIndex(x => new { x.SubjectId, x.ManagedSource }).IsUnique();
            e.HasIndex(x => x.SubjectId);
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
            e.Property(x => x.ReasoningModes)
                .HasConversion(new StringListJsonValueConverter(), StringListJsonValueConverter.Comparer)
                .HasColumnType("TEXT");
            e.HasIndex(x => new { x.ProviderId, x.Slug }).IsUnique();
            e.HasIndex(x => x.IsDefault);
        });

        modelBuilder.Entity<DysonModelFavoriteEntity>(e =>
        {
            e.ToTable("model_favorites");
            e.HasKey(x => x.Id);
            e.Property(x => x.SubjectId).IsRequired();
            e.HasIndex(x => new { x.SubjectId, x.ModelSlugId }).IsUnique();
            e.HasIndex(x => x.SubjectId);
            e.HasOne(x => x.ModelSlug)
                .WithMany()
                .HasForeignKey(x => x.ModelSlugId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DysonWorkDirectoryEntity>(e =>
        {
            e.ToTable("work_directories");
            e.HasKey(x => x.Id);
            e.Property(x => x.SubjectId).IsRequired();
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.AbsolutePath).IsRequired();
            e.Property(x => x.GitOrigin);
            e.Property(x => x.GitProvider);
            e.HasIndex(x => new { x.SubjectId, x.AbsolutePath }).IsUnique();
            e.HasIndex(x => x.LastOpenedUtc);
            e.HasIndex(x => x.SubjectId);
        });

        modelBuilder.Entity<DysonWorkDirectoryConfigurationEntity>(e =>
        {
            e.ToTable("work_directory_configurations");
            e.HasKey(x => x.WorkDirectoryId);
            e.Property(x => x.SubjectId).IsRequired();
            e.Property(x => x.ConfigJson).IsRequired();
            e.HasIndex(x => x.SubjectId);
            e.HasOne(x => x.WorkDirectory)
                .WithOne()
                .HasForeignKey<DysonWorkDirectoryConfigurationEntity>(x => x.WorkDirectoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DysonPluginInstallationEntity>(e =>
        {
            e.ToTable("plugin_installations");
            e.HasKey(x => x.Id);
            e.Property(x => x.SubjectId).IsRequired();
            e.Property(x => x.NormalizedPluginId).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.DisplayName).IsRequired();
            e.Property(x => x.SourceKind).IsRequired();
            e.Property(x => x.SourceLocation).IsRequired();
            e.Property(x => x.PackageFormat).IsRequired();
            e.Property(x => x.InstallScope).IsRequired();
            e.Property(x => x.Status).IsRequired();
            e.Property(x => x.PackageRoot).IsRequired();
            e.Property(x => x.ComponentInventoryJson).IsRequired().HasColumnType("TEXT");
            e.Property(x => x.ConfigurationSchemaJson).HasColumnType("TEXT");
            e.Property(x => x.DiagnosticsJson).IsRequired().HasColumnType("TEXT");
            e.HasIndex(x => x.SubjectId);
            e.HasIndex(x => x.WorkDirectoryId);
            e.HasIndex(x => new { x.SubjectId, x.PackageRoot }).IsUnique();
            e.HasIndex(x => new { x.SubjectId, x.NormalizedPluginId })
                .IsUnique()
                .HasFilter("\"InstallScope\" = 'Global'");
            e.HasIndex(x => new { x.SubjectId, x.NormalizedPluginId, x.WorkDirectoryId })
                .IsUnique()
                .HasFilter("\"InstallScope\" = 'Project'");
            e.HasOne(x => x.WorkDirectory)
                .WithMany()
                .HasForeignKey(x => x.WorkDirectoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DysonPluginVariableValueEntity>(e =>
        {
            e.ToTable("plugin_variable_values");
            e.HasKey(x => x.Id);
            e.Property(x => x.SubjectId).IsRequired();
            e.Property(x => x.VariableName).IsRequired();
            e.Property(x => x.ProtectedValue).IsRequired().HasColumnType("BLOB");
            e.HasIndex(x => x.SubjectId);
            e.HasIndex(x => new { x.SubjectId, x.InstallationId, x.VariableName }).IsUnique();
            e.HasOne(x => x.Installation)
                .WithMany()
                .HasForeignKey(x => x.InstallationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DysonPluginMcpGrantEntity>(e =>
        {
            e.ToTable("plugin_mcp_grants");
            e.HasKey(x => x.Id);
            e.Property(x => x.SubjectId).IsRequired();
            e.Property(x => x.ServerId).IsRequired();
            e.Property(x => x.PackageChecksum).IsRequired();
            e.HasIndex(x => x.SubjectId);
            e.HasIndex(x => new { x.SubjectId, x.InstallationId, x.ServerId }).IsUnique();
            e.HasOne(x => x.Installation)
                .WithMany()
                .HasForeignKey(x => x.InstallationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DysonPluginHookReviewEntity>(e =>
        {
            e.ToTable("plugin_hook_reviews");
            e.HasKey(x => x.Id);
            e.Property(x => x.SubjectId).IsRequired();
            e.Property(x => x.HookComponentId).IsRequired();
            e.Property(x => x.EventName).IsRequired();
            e.Property(x => x.PermissionsJson).IsRequired().HasColumnType("TEXT");
            e.Property(x => x.FailureMode).IsRequired();
            e.HasIndex(x => x.SubjectId);
            e.HasIndex(x => new { x.SubjectId, x.InstallationId, x.HookComponentId, x.EventName }).IsUnique();
            e.HasOne(x => x.Installation)
                .WithMany()
                .HasForeignKey(x => x.InstallationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DysonPluginHookAuditEntity>(e =>
        {
            e.ToTable("plugin_hook_audits");
            e.HasKey(x => x.Id);
            e.Property(x => x.SubjectId).IsRequired();
            e.Property(x => x.HookComponentId).IsRequired();
            e.Property(x => x.EventName).IsRequired();
            e.Property(x => x.Outcome).IsRequired();
            e.Property(x => x.DetailCode).IsRequired();
            e.HasIndex(x => x.SubjectId);
            e.HasIndex(x => new { x.SubjectId, x.InstallationId, x.OccurredUtc });
            e.HasOne(x => x.Installation)
                .WithMany()
                .HasForeignKey(x => x.InstallationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DysonSessionEntity>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.SubjectId).IsRequired();
            e.Property(x => x.AgentMode).IsRequired();
            e.Property(x => x.SystemPromptSnapshot).IsRequired();
            e.Property(x => x.McpAccessMode).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => x.LastActivityUtc);
            e.HasIndex(x => x.ParentSessionId);
            e.HasIndex(x => x.WorkDirectoryId);
            e.HasIndex(x => x.SubjectId);
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

        modelBuilder.Entity<DysonSessionTodoEntity>(e =>
        {
            e.ToTable("session_todos");
            e.HasKey(x => x.Id);
            e.Property(x => x.TaskCode).IsRequired();
            e.Property(x => x.DisplayName).IsRequired();
            e.Property(x => x.CommentsJson).IsRequired();
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => new { x.SessionId, x.TaskCode }).IsUnique();
            e.HasIndex(x => new { x.SessionId, x.Sequence });
            e.HasOne(x => x.Session)
                .WithMany(s => s.Todos)
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DysonAppSettingEntity>(e =>
        {
            e.ToTable("app_settings");
            e.HasKey(x => new { x.SubjectId, x.Key });
            e.Property(x => x.SubjectId).IsRequired();
            e.Property(x => x.Key).IsRequired();
            e.Property(x => x.Value).IsRequired();
        });

        modelBuilder.Entity<DysonConfiguredShellEntity>(e =>
        {
            e.ToTable("configured_shells");
            e.HasKey(x => x.Id);
            e.Property(x => x.SubjectId).IsRequired();
            e.Property(x => x.Name).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.ExecutablePath).IsRequired();
            e.HasIndex(x => new { x.SubjectId, x.Name }).IsUnique();
            e.HasIndex(x => x.SortOrder);
            e.HasIndex(x => x.SubjectId);
        });

        modelBuilder.Entity<DysonUsageRequestEntity>(e =>
        {
            e.ToTable("usage_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.SubjectId).IsRequired();
            e.Property(x => x.WorkDirectoryName).IsRequired();
            e.Property(x => x.ModelSlug).IsRequired();
            e.Property(x => x.ModelDisplayAlias).IsRequired();
            e.Property(x => x.ReasoningEffort).IsRequired();
            e.HasIndex(x => new { x.SubjectId, x.OccurredUtc });
            e.HasIndex(x => new { x.SubjectId, x.WorkDirectoryName });
            e.HasIndex(x => new { x.SubjectId, x.RootSessionId });
        });
    }

    /// <summary>Opens a context for <paramref name="databasePath"/> and applies migrations.</summary>
    public static DysonDbContext Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var optionsBuilder = new DbContextOptionsBuilder<DysonDbContext>();
        DysonSqliteConfigurator.Configure(optionsBuilder, databasePath);
        var ctx = new DysonDbContext(optionsBuilder.Options);
        ctx.Database.Migrate();
        return ctx;
    }

    /// <summary>Applies pending migrations (call after DI constructs a context).</summary>
    public void EnsureMigrated() => Database.Migrate();
}
