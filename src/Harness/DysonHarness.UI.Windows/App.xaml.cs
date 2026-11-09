using System.Diagnostics;
using System.Windows;
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
    private DysonSingleInstance? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // One shell per user and app mode (Dev / Test / Prod): a second launch activates the first and exits.
            var claim = DysonSingleInstance.Claim(
                "DysonHarness-" + DysonAppPaths.GetModeFolderName(DysonBuildInfo.Current),
                () => Dispatcher.BeginInvoke(ActivateExistingWindow));
            if (claim.IsError)
                throw new InvalidOperationException(claim.Error);
            _singleInstance = claim.Value;
            if (!_singleInstance.IsFirst)
            {
                Shutdown(0);
                return;
            }

            var runtime = DysonWindowsStaHost.RuntimeVersion();
            if (runtime.IsError)
            {
                ShowRuntimeMissing(runtime.Error);
                Shutdown(1);
                return;
            }

            DysonWindowsStaHost.AttachToExistingApplication();
            // Pre-WebView2 builds left a Chromium cache here; drop it once nothing holds it.
            _ = Task.Run(DysonWindowsStaHost.TryDeleteLegacyCefCache);

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
            _singleInstance?.Dispose();
        }

        base.OnExit(e);
    }

    private static void ShowRuntimeMissing(string detail)
    {
        var answer = MessageBox.Show(
            "Dyson Harness needs the Microsoft Edge WebView2 Runtime." + Environment.NewLine + Environment.NewLine
            + detail + Environment.NewLine + Environment.NewLine
            + "Open the download page now?" + Environment.NewLine + DysonWindowsStaHost.RuntimeDownloadUrl,
            "DysonHarness",
            MessageBoxButton.YesNo,
            MessageBoxImage.Error);
        if (answer != MessageBoxResult.Yes)
            return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = DysonWindowsStaHost.RuntimeDownloadUrl, UseShellExecute = true });
        }
        catch
        {
            // the URL is in the dialog text
        }
    }

    /// <summary>Second launch: focus the existing shell (or any visible agent window).</summary>
    private void ActivateExistingWindow()
    {
        Window? target = MainWindow;
        if (target is null || !target.IsVisible)
            target = Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible);
        if (target is null)
            return;

        if (target.WindowState == WindowState.Minimized)
            target.WindowState = WindowState.Normal;
        target.Activate();
        target.Topmost = true;
        target.Topmost = false;
        _ = target.Focus();
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
