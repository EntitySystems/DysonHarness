using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DysonHarness;

/// <summary>
/// DI helpers for hosting mode, subject context, and the permissive access evaluator.
/// Wave 2b should bind <see cref="DysonHostingOptions"/> from config then call these;
/// cookie middleware is not registered here.
/// </summary>
public static class DysonHostingServiceCollectionExtensions
{
    /// <summary>
    /// Registers singleton <see cref="IDysonAccessEvaluator"/> → <see cref="DysonPermissiveAccessEvaluator"/>
    /// (local default and cloud interim).
    /// </summary>
    public static IServiceCollection AddDysonPermissiveAccess(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDysonAccessEvaluator, DysonPermissiveAccessEvaluator>();
        return services;
    }

    /// <summary>
    /// Local hosting: fixed <see cref="DysonSubjects.Local"/> subject (singleton) + permissive access.
    /// No subject cookie.
    /// </summary>
    public static IServiceCollection AddDysonLocalHosting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDysonSubjectContext>(_ => DysonFixedLocalSubjectContext.Instance);
        services.AddDysonPermissiveAccess();
        return services;
    }

    /// <summary>
    /// Cloud hosting: scoped <see cref="DysonScopedSubjectContext"/> (also as <see cref="IDysonSubjectContext"/>)
    /// + permissive access. Caller must set the subject (cookie middleware) before use.
    /// Does not register cookie middleware.
    /// </summary>
    public static IServiceCollection AddDysonCloudHosting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<DysonScopedSubjectContext>();
        services.TryAddScoped<IDysonSubjectContext>(sp => sp.GetRequiredService<DysonScopedSubjectContext>());
        services.AddDysonPermissiveAccess();
        return services;
    }

    /// <summary>
    /// Registers subject context + permissive access for <paramref name="mode"/>.
    /// Bind options separately, e.g.
    /// <c>services.Configure&lt;DysonHostingOptions&gt;(config.GetSection(DysonHostingOptions.SectionName))</c>
    /// then pass <c>options.Mode</c> (or read Mode from config before calling).
    /// </summary>
    public static IServiceCollection AddDysonHosting(this IServiceCollection services, DysonHostingMode mode)
    {
        ArgumentNullException.ThrowIfNull(services);
        return mode == DysonHostingMode.Cloud
            ? services.AddDysonCloudHosting()
            : services.AddDysonLocalHosting();
    }

    /// <summary>
    /// Registers subject context + permissive access using <paramref name="configure"/> to choose
    /// <see cref="DysonHostingOptions.Mode"/> (defaults to Local when configure is null).
    /// </summary>
    public static IServiceCollection AddDysonHosting(
        this IServiceCollection services,
        Action<DysonHostingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new DysonHostingOptions();
        configure?.Invoke(options);
        return services.AddDysonHosting(options.Mode);
    }
}
