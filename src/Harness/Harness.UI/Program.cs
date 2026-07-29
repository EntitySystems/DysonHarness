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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
