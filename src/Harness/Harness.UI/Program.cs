using DysonHarness;
using Harness.UI.Components;
using Harness.UI.Demo;
using Harness.UI.Files;
using Harness.UI.Theme;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Without launchSettings (ASPNETCORE_ENVIRONMENT=Production), MapStaticAssets
// looks under wwwroot and throws FileNotFoundException for scoped CSS / blazor.web.js.
if (!builder.Environment.IsDevelopment())
    builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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

builder.Services.AddScoped<DysonToolPolicyStore>();
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
builder.Services.AddScoped<DysonUiHost>();
builder.Services.AddSingleton<DysonFilePreviewStore>();
builder.Services.AddScoped<ThemeService>();
builder.Services.AddSingleton<DysonFileTreeService>();
builder.Services.AddSingleton<DysonGitChangesService>();

if (hostingMode == DysonHostingMode.Cloud)
    builder.Services.AddScoped<CircuitHandler, DysonCloudSubjectCircuitHandler>();

var app = builder.Build();

{
    await using var db = await app.Services
        .GetRequiredService<IDbContextFactory<DysonDbContext>>()
        .CreateDbContextAsync()
        .ConfigureAwait(false);
    db.EnsureMigrated();
}

app.Lifetime.ApplicationStopping.Register(() =>
{
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

app.Run();
