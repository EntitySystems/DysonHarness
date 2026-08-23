using ApexCharts;
using DysonHarness;
using Harness.UI.Components;
using Harness.UI.Demo;
using Harness.UI.Files;
using Harness.UI.Logging;
using Harness.UI.Services;
using Harness.UI.Theme;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.EntityFrameworkCore;

namespace Harness.UI;

/// <summary>Shared Blazor Interactive Server host used by CLI <c>Harness.UI</c> and the Windows CEF shell.</summary>
public static class DysonUiWebHost
{
    public static WebApplication Create(string[] args, DysonUiWebHostOptions? options = null)
    {
        options ??= new DysonUiWebHostOptions();

        var uiAssemblyDir = Path.GetDirectoryName(typeof(DysonUiWebHost).Assembly.Location)
            ?? AppContext.BaseDirectory;
        var contentRoot = options.ContentRoot ?? uiAssemblyDir;
        var webRoot = options.WebRoot ?? Path.Combine(contentRoot, "wwwroot");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRoot,
            WebRootPath = webRoot,
        });

        if (!string.IsNullOrWhiteSpace(options.Urls))
            builder.WebHost.UseUrls(options.Urls);

        // Without launchSettings (ASPNETCORE_ENVIRONMENT=Production), MapStaticAssets
        // looks under wwwroot and throws FileNotFoundException for scoped CSS / blazor.web.js.
        if (!builder.Environment.IsDevelopment())
            builder.WebHost.UseStaticWebAssets();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.AddApexCharts();

        builder.Services.AddHttpClient();
        builder.Services.AddHttpClient(SkillsHubSkillExplorerProvider.ProviderId, client =>
        {
            client.BaseAddress = new Uri("https://skillshub.wtf/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DysonHarness/1.0");
        });
        builder.Services.AddHttpClient(SkillsShSkillExplorerProvider.ProviderId, client =>
        {
            client.BaseAddress = new Uri("https://skills.sh/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DysonHarness/1.0");
        });
        builder.Services.AddHttpClient(ClawHubSkillExplorerProvider.ProviderId, client =>
        {
            client.BaseAddress = new Uri("https://clawhub.ai/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DysonHarness/1.0");
        });
        builder.Services.AddHttpClient(SkillsDirectorySkillExplorerProvider.ProviderId, client =>
        {
            client.BaseAddress = new Uri("https://www.skillsdirectory.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DysonHarness/1.0");
        });
        // Registration order = SkillSearchModal tab order (GetServices preserves it).
        builder.Services.AddSingleton<IDysonSkillExplorerProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new SkillsHubSkillExplorerProvider(
                factory.CreateClient(SkillsHubSkillExplorerProvider.ProviderId));
        });
        builder.Services.AddSingleton<IDysonSkillExplorerProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new SkillsShSkillExplorerProvider(
                factory.CreateClient(SkillsShSkillExplorerProvider.ProviderId));
        });
        builder.Services.AddSingleton<IDysonSkillExplorerProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new ClawHubSkillExplorerProvider(
                factory.CreateClient(ClawHubSkillExplorerProvider.ProviderId));
        });
        builder.Services.AddSingleton<IDysonSkillExplorerProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new SkillsDirectorySkillExplorerProvider(
                factory.CreateClient(SkillsDirectorySkillExplorerProvider.ProviderId));
        });
        builder.Services.AddSingleton<IDysonSkillExplorer>(sp =>
            new DysonSkillExplorer(sp.GetServices<IDysonSkillExplorerProvider>()));
        builder.Services.AddHttpContextAccessor();

        DysonAppPaths.EnsureRoot(DysonBuildInfo.Current);
        var databasePath = DysonAppPaths.GetDatabasePath(DysonBuildInfo.Current);

        builder.Services.Configure<DysonHostingOptions>(
            builder.Configuration.GetSection(DysonHostingOptions.SectionName));

        var hostingMode = builder.Configuration
            .GetSection(DysonHostingOptions.SectionName)
            .Get<DysonHostingOptions>()?.Mode
            ?? DysonHostingMode.Local;

        builder.Services.AddDysonHosting(hostingMode);
        builder.Services.AddDysonLocalDb(databasePath);
        builder.Services.AddScoped<DysonWorkDirectoryService>();

        builder.Services.AddScoped<DysonToolPolicyStore>();
        builder.Services.AddScoped<DysonPluginCatalogService>();
        builder.Services.AddSingleton<DysonPluginContributionResolver>();
        builder.Services.AddSingleton<DysonPluginPackageLimits>();
        builder.Services.AddSingleton<IDysonPluginPackageParser, DysonPluginPackageParser>();
        builder.Services.AddScoped<IDysonPluginPackageService, DysonPluginPackageService>();
        builder.Services.AddScoped<DysonPluginLifecycleService>();
        builder.Services.AddScoped(sp => DysonPluginVariableProtector.ForMode(DysonBuildInfo.Current));
        builder.Services.AddScoped<DysonPluginVariableService>();
        builder.Services.AddScoped<DysonPluginHookSecurityService>();
        builder.Services.AddSingleton<DysonPluginMcpResolver>();
        builder.Services.AddScoped<DysonPluginMcpGrantService>();
        builder.Services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new DysonCliProxyHost(factory.CreateClient("cliproxy"));
        });
        builder.Services.AddHttpClient("cliproxy");
        builder.Services.AddScoped(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return factory.CreateClient();
        });
        builder.Services.AddScoped<ManagedInferenceProviderCatalog>();
#if WINDOWS
        builder.Services.AddSingleton<IDysonBrowserControl, DysonCefBrowserControl>();
#endif
        builder.Services.AddSingleton<IDysonSessionRuntimeScopeFactory, DysonUiSessionRuntimeScopeFactory>();
        builder.Services.AddSingleton<DysonSessionRuntimeRegistry>();
        builder.Services.AddScoped<DysonUiRuntimeAttachment>();
        builder.Services.AddScoped<IDysonAgentSessionRuntimeFactory, DysonUiAgentSessionRuntimeFactory>();
        builder.Services.AddScoped<DysonUiAgentSessionRuntimeConfigBuilder>();
        builder.Services.AddScoped<DysonSessionRuntime>();
        builder.Services.AddScoped<DysonUiHost>();
        builder.Services.AddSingleton<DysonFilePreviewStore>();
        builder.Services.AddScoped<ThemeService>();
        builder.Services.AddScoped<ConfirmDialogService>();
        builder.Services.AddSingleton<DysonFileTreeService>();
        builder.Services.AddSingleton<DysonGitChangesService>();
        builder.Services.AddHttpClient(DysonGitHubReleaseClient.HttpClientName, client =>
        {
            // Same client streams the MSI, so the default 100s timeout is far too short.
            client.Timeout = TimeSpan.FromMinutes(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DysonHarness/1.0");
        });
        builder.Services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new DysonAppUpdateService(factory.CreateClient(DysonGitHubReleaseClient.HttpClientName));
        });
        builder.Services.AddHttpClient("embedded-runtimes", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DysonHarness/1.0");
        });
        builder.Services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new DysonEmbeddedRuntimeInstaller(factory.CreateClient("embedded-runtimes"));
        });

        if (hostingMode == DysonHostingMode.Cloud)
            builder.Services.AddScoped<CircuitHandler, DysonCloudSubjectCircuitHandler>();

        builder.Logging.AddProvider(
            new DysonFileLoggerProvider(DysonAppPaths.GetLogFilePath(DysonBuildInfo.Current)));

        builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(o =>
        {
            o.DetailedErrors = hostingMode != DysonHostingMode.Cloud;
        });

        var app = builder.Build();
        RegisterProcessExceptionHooksOnce(app.Services.GetRequiredService<ILoggerFactory>());

        {
            using var db = app.Services
                .GetRequiredService<IDbContextFactory<DysonDbContext>>()
                .CreateDbContext();
            db.EnsureMigrated();
        }

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                var registry = app.Services.GetService<DysonSessionRuntimeRegistry>();
                registry?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // ignore shutdown races
            }

            try
            {
                var host = app.Services.GetService<DysonCliProxyHost>();
                host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // ignore shutdown races
            }
        });

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        if (!options.SkipHttpsRedirection)
            app.UseHttpsRedirection();

        if (hostingMode == DysonHostingMode.Cloud)
            app.UseMiddleware<DysonSubjectCookieMiddleware>();

        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapGet(DysonFilePreviewStore.RoutePrefix + "/{id}", (string id, DysonFilePreviewStore store) =>
        {
            // ponytail: unguessable GUID tokens only; no subject binding — fine for local, tighten if cloud shares previews.
            if (!store.TryGet(id, out var entry))
                return Results.NotFound();

            return Results.File(entry.Bytes, entry.ContentType, enableRangeProcessing: true);
        });
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return app;
    }

    private static int _processExceptionHooksRegistered;

    /// <summary>
    /// Subscribe process-wide exception events once so a second <see cref="Create"/>
    /// (tests / UI restart) does not double-log.
    /// </summary>
    private static void RegisterProcessExceptionHooksOnce(ILoggerFactory loggerFactory)
    {
        if (Interlocked.Exchange(ref _processExceptionHooksRegistered, 1) != 0)
            return;

        var logger = loggerFactory.CreateLogger("DysonHarness.Process");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                    logger.LogCritical(ex, "Unhandled exception.");
                else
                    logger.LogCritical("Unhandled exception: {ExceptionObject}", e.ExceptionObject);
            }
            catch
            {
                // process hooks must not throw
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                logger.LogError(e.Exception, "Unobserved task exception.");
            }
            catch
            {
                // process hooks must not throw
            }
        };
    }

    /// <summary>Migrate DB then run until shutdown (CLI entrypoint).</summary>
    public static async Task RunAsync(string[] args, DysonUiWebHostOptions? options = null)
    {
        var app = Create(args, options);
        await app.RunAsync().ConfigureAwait(false);
    }
}
