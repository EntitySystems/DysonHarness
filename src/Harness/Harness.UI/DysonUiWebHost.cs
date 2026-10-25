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

        // Debug port. launchSettings.json (http/https profiles) is 5181, but `dotnet run
        // --no-launch-profile` never reads that file and would otherwise fall back to Kestrel
        // :5000. Development with no explicit URL binds 5181. The installed Windows shell does
        // not use a fixed port: App.xaml.cs passes http://127.0.0.1:0 (ephemeral loopback).
        // Published linux/osx Harness.UI has no launchSettings and is Production, so it keeps
        // the Kestrel default (http://localhost:5000) unless the caller passes a URL.
        // An explicit URL still wins: --urls, ASPNETCORE_URLS, or DysonUiWebHostOptions.Urls.
        if (builder.Environment.IsDevelopment()
            && string.IsNullOrWhiteSpace(options.Urls)
            && string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
            builder.WebHost.UseUrls("http://localhost:5181");

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
        // Process lifetime, not the circuit. AdoptLiveSession (4f35c0d) hands this client to
        // DysonSessionRuntime; disposing it with the circuit made the next request throw ObjectDisposedException.
        AddSessionHttpClient(builder.Services);
        // Native xAI/Grok: host-level client identity overrides (config Dyson:XaiGrok), a plain "xai-oauth" client for
        // the OAuth endpoints, and XaiGrokRequestHandler on the DEFAULT client (acts only on dyson-xai:<guid> bearers).
        builder.Services.AddSingleton(
            builder.Configuration.GetSection(XaiGrokClientOptions.SectionName).Get<XaiGrokClientOptions>()
            ?? new XaiGrokClientOptions());
        builder.Services.AddHttpClient("xai-oauth");
        builder.Services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new XaiOAuthClient(() => factory.CreateClient("xai-oauth"));
        });
        builder.Services.AddSingleton<XaiGrokAuthService>();
        builder.Services.AddTransient<XaiGrokRequestHandler>();
        builder.Services.AddHttpClient(Microsoft.Extensions.Options.Options.DefaultName)
            .AddHttpMessageHandler<XaiGrokRequestHandler>();
        builder.Services.AddScoped<ManagedInferenceProviderCatalog>();
#if WINDOWS
        builder.Services.AddSingleton<IDysonBrowserControl, DysonCefBrowserControl>();
        builder.Services.AddSingleton<IDysonAudioPlayback, DysonWindowsAudioPlayback>();
        builder.Services.AddSingleton<IDysonAudioCapture, DysonWindowsAudioCapture>();
#endif
        builder.Services.AddSingleton<IDysonSessionRuntimeScopeFactory, DysonUiSessionRuntimeScopeFactory>();
        builder.Services.AddSingleton<DysonSessionRuntimeRegistry>();
        builder.Services.AddSingleton<DysonMessageBus>();
        builder.Services.AddSingleton<DysonGitRepoChangePublisher>();
        builder.Services.AddSingleton<DysonSessionEventPublisher>();
        builder.Services.AddScoped<DysonUiRuntimeAttachment>();
        builder.Services.AddScoped<IDysonAgentSessionRuntimeFactory, DysonUiAgentSessionRuntimeFactory>();
        builder.Services.AddScoped<DysonUiAgentSessionRuntimeConfigBuilder>();
        // In-memory work-directory owner. One per scope, including the retained subject scope. Not Windows-only. Not a singleton.
        builder.Services.AddScoped<DysonWorkDirectoryFocus>();
        builder.Services.AddScoped<DysonSessionRuntime>();
        builder.Services.AddScoped<DysonUiHost>();
        builder.Services.AddSingleton<DysonFilePreviewStore>();
        builder.Services.AddScoped<ThemeService>();
        builder.Services.AddScoped<ConfirmDialogService>();
        builder.Services.AddScoped<FileStorageConnectService>();
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
        builder.Services.AddSingleton(sp =>
        {
            var version = DysonAppVersionInfo.Local;
            var root = DysonSpeechPaths.GetInstallRoot(
                DysonBuildInfo.Current,
                version.IsStampedRelease ? version.EffectiveChannel : null);
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new DysonSpeechInstaller(factory.CreateClient("embedded-runtimes"), root);
        });
        builder.Services.AddScoped(sp => new DysonActiveSpeechToText(
            sp.GetRequiredService<IDysonSpeechModelRepository>(),
            sp.GetRequiredService<IDysonSubjectSettingsRepository>(),
            sp.GetRequiredService<DysonSpeechInstaller>().InstallRoot));
        builder.Services.AddScoped<IDysonSpeechToText>(sp => sp.GetRequiredService<DysonActiveSpeechToText>());
        builder.Services.AddScoped(sp => new DysonActiveTextToSpeech(
            sp.GetRequiredService<IDysonSpeechModelRepository>(),
            sp.GetRequiredService<IDysonSubjectSettingsRepository>(),
            sp.GetRequiredService<DysonSpeechInstaller>().InstallRoot));
        builder.Services.AddScoped<IDysonTextToSpeech>(sp => sp.GetRequiredService<DysonActiveTextToSpeech>());
#if WINDOWS
        // Circuit Settings and the retained voice service each get a tryout. Both share the singleton playback and capture devices.
        builder.Services.AddScoped<DysonSpeechTryout>();
        // One voice service in the retained subject scope (DysonSessionRuntime.Voice). Circuits must not resolve it.
        // Not a singleton and not a static.
        builder.Services.AddScoped<DysonVoiceMode>();
#endif

        if (hostingMode == DysonHostingMode.Cloud)
            builder.Services.AddScoped<CircuitHandler, DysonCloudSubjectCircuitHandler>();

        builder.Logging.AddProvider(
            new DysonFileLoggerProvider(DysonAppPaths.GetLogFilePath(DysonBuildInfo.Current)));

        builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(o =>
        {
            o.DetailedErrors = hostingMode != DysonHostingMode.Cloud;
            // Framework calls we do not own (hash scroll) must not pin the circuit for a minute.
            o.JSInteropDefaultCallTimeout = DysonCircuitJs.Timeout;
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

            DysonPhononSpeech.StopSidecar();
            DysonCrispSpeech.StopSidecar();
            DysonNemotronSpeech.Stop();
            DysonPocketSpeech.StopBridge();
            DysonQwenSpeech.StopBridge();
            DysonChatterboxSpeech.StopBridge();
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

    /// <summary>
    /// Default named client (<see cref="XaiGrokRequestHandler"/> is registered on that name) as a singleton.
    /// <see cref="IHttpClientFactory"/> owns the handler pipeline, so this registration does not dispose the client.
    /// </summary>
    internal static void AddSessionHttpClient(IServiceCollection services) =>
        services.AddSingleton(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient());

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
