using System.Runtime.InteropServices;
using DysonHarness;
using Harness.UI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;

namespace DysonHarness.UI.Mac;

internal static class Program
{
    private static FileStream? _instanceLock;

    public static void Main(string[] args)
    {
        DysonMacBrowserControl? browser = null;
        try
        {
            DysonMacWebKitNative.Init(json => browser?.RaiseSnip(json));
        }
        catch (Exception ex)
        {
            TryAlert(ex.ToString());
            return;
        }

        if (!TryAcquireInstance())
        {
            DysonMacWebKitNative.ActivatePeer();
            return;
        }

        CancellationTokenSource? cts = null;
        WebApplication? app = null;
        Task? hostTask = null;
        try
        {
            cts = new CancellationTokenSource();
            browser = new DysonMacBrowserControl();
            var browserControl = browser;
            app = DysonUiWebHost.Create(args, new DysonUiWebHostOptions
            {
                Urls = "http://127.0.0.1:0",
                SkipHttpsRedirection = true,
                ConfigureServices = services => services.AddSingleton<IDysonBrowserControl>(browserControl),
            });

            var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                try
                {
                    started.TrySetResult(ResolveListeningUrl(app));
                }
                catch (Exception ex)
                {
                    started.TrySetException(ex);
                }
            });

            hostTask = Task.Run(() => app.RunAsync(cts.Token));
            _ = Task.Run(async () =>
            {
                try
                {
                    var url = await started.Task.ConfigureAwait(false);
                    DysonMacWebKitNative.Invoke(DysonMacWebKitProtocol.ShowShell(url));
                }
                catch (Exception ex)
                {
                    DysonMacWebKitNative.Alert(ex.ToString());
                    DysonMacWebKitNative.Invoke(DysonMacWebKitProtocol.Stop());
                }
            });

            DysonMacWebKitNative.Run();
        }
        catch (Exception ex)
        {
            TryAlert(ex.ToString());
        }
        finally
        {
            try
            {
                cts?.Cancel();
                hostTask?.Wait(TimeSpan.FromSeconds(5));
                if (app is not null)
                    app.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // shutdown races
            }

            _instanceLock?.Dispose();
        }
    }

    private static bool TryAcquireInstance()
    {
        var path = Path.Combine(DysonAppPaths.EnsureRoot(DysonBuildInfo.Current), "dyson-mac.lock");
        try
        {
            // FileStream.Lock is Windows-only. flock is the macOS exclusive lock and stays held while the stream lives.
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (stream.Length < 1)
            {
                stream.SetLength(1);
                stream.Flush(flushToDisk: true);
            }

            if (Flock(stream.SafeFileHandle, LockExclusive | LockNonBlocking) != 0)
            {
                stream.Dispose();
                return false;
            }

            _instanceLock = stream;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryAlert(string message)
    {
        try
        {
            DysonMacWebKitNative.Alert(message);
        }
        catch
        {
            Console.Error.WriteLine(message);
        }
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

    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle fd, int operation);
}
