using DysonHarness;
using Harness.UI.Components;
using Harness.UI.Demo;
using Harness.UI.Theme;

var shellCheck = DysonWindowsShell.SelfCheckArgMap();
if (shellCheck.IsError)
    throw new InvalidOperationException(shellCheck.Error);

var searchCheck = SearchSelfCheck.RunSsrfChecks();
if (searchCheck.IsError)
    throw new InvalidOperationException(searchCheck.Error);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient();
builder.Services.AddScoped(_ =>
{
    var db = new DysonDbContext();
    db.EnsureMigrated();
    return db;
});
builder.Services.AddScoped<DysonModelStore>();
builder.Services.AddScoped<DysonSessionStore>();
builder.Services.AddScoped<DysonWorkDirectoryStore>();
builder.Services.AddScoped(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return factory.CreateClient();
});
builder.Services.AddScoped<DysonUiHost>();
builder.Services.AddScoped<ThemeService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
