using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DysonHarness;

public static class DysonLocalDbServiceCollectionExtensions
{
    /// <summary>
    /// Registers SQLite LocalDb: context factory, accessor, and repository implementations.
    /// Caller must also register <see cref="IDysonSubjectContext"/> and <see cref="IDysonAccessEvaluator"/>.
    /// </summary>
    public static IServiceCollection AddDysonLocalDb(
        this IServiceCollection services,
        string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        services.AddDbContextFactory<DysonDbContext>(options =>
            DysonSqliteConfigurator.Configure(options, databasePath));

        services.AddSingleton(sp =>
            new DysonDbAccessor(
                sp.GetRequiredService<IDbContextFactory<DysonDbContext>>(),
                databasePath));

        services.AddScoped<IDysonSessionRepository, DysonSessionRepository>();
        services.AddScoped<IDysonWorkDirectoryRepository, DysonWorkDirectoryRepository>();
        services.AddScoped<IDysonWorkDirectoryConfigurationRepository, DysonWorkDirectoryConfigurationRepository>();
        services.AddScoped<IDysonModelRepository, DysonModelRepository>();
        services.AddScoped<IDysonConfiguredShellRepository, DysonConfiguredShellRepository>();
        services.AddScoped<IDysonSubjectSettingsRepository, DysonSubjectSettingsRepository>();

        return services;
    }
}
