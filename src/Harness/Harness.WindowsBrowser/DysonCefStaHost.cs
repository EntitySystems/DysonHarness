using System.Windows;
using System.Windows.Threading;
using CefSharp;
using CefSharp.Wpf;

namespace DysonHarness;

/// <summary>
/// Lazy STA WPF + CefSharp host. Blazor Server runs MTA; all CEF/UI work marshals here.
/// </summary>
internal static class DysonCefStaHost
{
    private static readonly object Gate = new();
    private static Thread? _thread;
    private static Dispatcher? _uiDispatcher;
    private static Application? _app;
    private static TaskCompletionSource? _ready;
    private static Exception? _initError;

    public static Dispatcher UiDispatcher
    {
        get
        {
            EnsureStarted();
            if (_initError is not null)
                throw new InvalidOperationException("CEF STA host failed to start.", _initError);
            return _uiDispatcher ?? throw new InvalidOperationException("CEF STA dispatcher not ready.");
        }
    }

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_ready is null)
            {
                _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _thread = new Thread(StaThreadMain)
                {
                    IsBackground = true,
                    Name = "DysonCefStaHost",
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }

        _ready.Task.GetAwaiter().GetResult();
        if (_initError is not null)
            throw new InvalidOperationException("CEF STA host failed to start.", _initError);
    }

    public static Task<T> InvokeAsync<T>(Func<T> func)
    {
        var dispatcher = UiDispatcher;
        if (dispatcher.CheckAccess())
            return Task.FromResult(func());

        return dispatcher.InvokeAsync(func).Task;
    }

    public static Task InvokeAsync(Action action)
    {
        var dispatcher = UiDispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    public static async Task<T> InvokeAsync<T>(Func<Task<T>> func)
    {
        var dispatcher = UiDispatcher;
        if (dispatcher.CheckAccess())
            return await func().ConfigureAwait(true);

        var op = dispatcher.InvokeAsync(func);
        var inner = await op.Task.ConfigureAwait(false);
        return await inner.ConfigureAwait(false);
    }

    public static async Task InvokeAsync(Func<Task> func)
    {
        var dispatcher = UiDispatcher;
        if (dispatcher.CheckAccess())
        {
            await func().ConfigureAwait(true);
            return;
        }

        var op = dispatcher.InvokeAsync(func);
        var inner = await op.Task.ConfigureAwait(false);
        await inner.ConfigureAwait(false);
    }

    private static void StaThreadMain()
    {
        try
        {
            _uiDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _app = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var cefRoot = System.IO.Path.Combine(localAppData, "DysonHarness");
            var cachePath = System.IO.Path.Combine(cefRoot, "cef-cache");
            System.IO.Directory.CreateDirectory(cachePath);

            var settings = new CefSettings
            {
                CachePath = cachePath,
                LogFile = System.IO.Path.Combine(cefRoot, "cef-debug.log"),
            };

            var subprocess = System.IO.Path.Combine(AppContext.BaseDirectory, "CefSharp.BrowserSubprocess.exe");
            if (System.IO.File.Exists(subprocess))
                settings.BrowserSubprocessPath = subprocess;

            if (!Cef.IsInitialized.GetValueOrDefault())
            {
                if (!Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null))
                    throw new InvalidOperationException("Cef.Initialize returned false.");
            }

            _ready?.TrySetResult();
            System.Windows.Threading.Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _initError = ex;
            _ready?.TrySetResult();
        }
    }
}
