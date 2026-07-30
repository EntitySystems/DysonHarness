using System.Windows;
using CefSharp;
using Harness.UI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DysonHarness.UI.Windows;

public partial class App : Application
{
    private CancellationTokenSource? _webHostCts;
    private WebApplication? _webApp;
    private Task? _webHostTask;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            DysonCefStaHost.AttachToExistingApplication();
            if (DysonCefStaHost.ShutdownBecauseAlreadyRunning)
            {
                // Chromium process singleton: existing instance was activated via OnAlreadyRunningAppRelaunch.
                Shutdown(0);
                return;
            }

            _webHostCts = new CancellationTokenSource();
            var options = new DysonUiWebHostOptions
            {
                Urls = "http://127.0.0.1:0",
                SkipHttpsRedirection = true,
            };

            _webApp = DysonUiWebHost.Create(e.Args, options);

            var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _webApp.Lifetime.ApplicationStarted.Register(() =>
            {
                try
                {
                    started.TrySetResult(ResolveListeningUrl(_webApp));
                }
                catch (Exception ex)
                {
                    started.TrySetException(ex);
                }
            });

            _webHostTask = _webApp.RunAsync(_webHostCts.Token);
            var url = await started.Task.ConfigureAwait(true);

            var main = new MainWindow(new Uri(url));
            MainWindow = main;
            main.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to start DysonHarness:{Environment.NewLine}{ex}",
                "DysonHarness",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _webHostCts?.Cancel();
            if (_webHostTask is not null)
            {
                try
                {
                    _webHostTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // shutdown races
                }
            }

            _webApp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // ignore shutdown races
        }
        finally
        {
            if (Cef.IsInitialized.GetValueOrDefault())
                Cef.Shutdown();
        }

        base.OnExit(e);
    }

    private static string ResolveListeningUrl(WebApplication app)
    {
        foreach (var url in app.Urls)
        {
            if (!string.IsNullOrWhiteSpace(url))
                return url;
        }

        var addresses = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;
        var first = addresses?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        if (first is not null)
            return first;

        throw new InvalidOperationException("Kestrel started but no listening address was reported.");
    }
}
