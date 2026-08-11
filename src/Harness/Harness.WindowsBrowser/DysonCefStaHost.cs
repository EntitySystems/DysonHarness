using System.Windows;
using System.Windows.Threading;
using CefSharp;
using CefSharp.Handler;
using CefSharp.Wpf;
using CefSharp.Enums;

namespace DysonHarness;

/// <summary>
/// Lazy STA WPF + CefSharp host. Blazor Server runs MTA; all CEF/UI work marshals here.
/// When a WPF <see cref="Application"/> already owns the STA (Windows CEF shell), reuse it.
/// </summary>
public static class DysonCefStaHost
{
    private static readonly object Gate = new();
    private static Thread? _thread;
    private static Dispatcher? _uiDispatcher;
    private static Application? _app;
    private static TaskCompletionSource? _ready;
    private static Exception? _initError;
    private static bool _shutdownBecauseAlreadyRunning;

    /// <summary>
    /// True when <c>Cef.Initialize</c> returned false because another process already holds
    /// this app's <c>RootCachePath</c> (Chromium process singleton). The existing process was notified.
    /// </summary>
    public static bool ShutdownBecauseAlreadyRunning => _shutdownBecauseAlreadyRunning;

    public static Dispatcher UiDispatcher
    {
        get
        {
            EnsureStarted();
            if (_shutdownBecauseAlreadyRunning)
                throw new InvalidOperationException("CEF is already running in another DysonHarness process.");
            if (_initError is not null)
                throw new InvalidOperationException("CEF STA host failed to start.", _initError);
            return _uiDispatcher ?? throw new InvalidOperationException("CEF STA dispatcher not ready.");
        }
    }

    /// <summary>
    /// Bind to an existing STA WPF <see cref="Application"/> (shell owns the message loop).
    /// No-op if already initialized.
    /// </summary>
    public static void AttachToExistingApplication()
    {
        lock (Gate)
        {
            if (_ready is not null)
            {
                // Already started or starting.
            }
            else
            {
                var app = Application.Current
                    ?? throw new InvalidOperationException("Application.Current is null; cannot attach CEF STA host.");
                var dispatcher = app.Dispatcher
                    ?? throw new InvalidOperationException("Application dispatcher is null.");
                if (!dispatcher.CheckAccess()
                    || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                {
                    throw new InvalidOperationException(
                        "AttachToExistingApplication must run on the STA UI thread that owns Application.Current.");
                }

                _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    BindExistingApplication(app, dispatcher);
                    _ready.TrySetResult();
                }
                catch (Exception ex)
                {
                    _initError = ex;
                    _ready.TrySetResult();
                }
            }
        }

        _ready!.Task.GetAwaiter().GetResult();
        if (_shutdownBecauseAlreadyRunning)
            return;
        if (_initError is not null)
            throw new InvalidOperationException("CEF STA host failed to start.", _initError);
    }

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_ready is null)
            {
                var current = Application.Current;
                if (current?.Dispatcher is { } dispatcher
                    && dispatcher.CheckAccess()
                    && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                {
                    _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    try
                    {
                        BindExistingApplication(current, dispatcher);
                        _ready.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        _initError = ex;
                        _ready.TrySetResult();
                    }
                }
                else
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
        }

        _ready.Task.GetAwaiter().GetResult();
        if (_shutdownBecauseAlreadyRunning)
            throw new InvalidOperationException("CEF is already running in another DysonHarness process.");
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

    private static void BindExistingApplication(Application app, Dispatcher dispatcher)
    {
        _app = app;
        _uiDispatcher = dispatcher;
        InitializeCef();
    }

    private static void InitializeCef()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // RootCachePath must be unique per app; Chromium allows only one process per root.
        var cefRoot = System.IO.Path.Combine(localAppData, "DysonHarness");
        var cachePath = System.IO.Path.Combine(cefRoot, "cef-cache");
        System.IO.Directory.CreateDirectory(cachePath);

        var settings = new CefSettings
        {
            RootCachePath = cefRoot,
            CachePath = cachePath,
            LogFile = System.IO.Path.Combine(cefRoot, "cef-debug.log"),
        };

        var subprocess = System.IO.Path.Combine(AppContext.BaseDirectory, "CefSharp.BrowserSubprocess.exe");
        if (!System.IO.File.Exists(subprocess))
        {
            throw new InvalidOperationException(
                $"Missing CefSharp.BrowserSubprocess.exe next to the executable (BaseDirectory={AppContext.BaseDirectory}). " +
                "Publish/copy CEF natives beside DysonHarness.exe.");
        }

        settings.BrowserSubprocessPath = subprocess;

        // CefSharp discussion #4662 / CEF #3646: Dawn WebGPU on win-x64 needs DXC next to the exe.
        // Without these, navigator.gpu finds no adapters. CefSharp 149 NuGet ships them; fail fast
        // if a custom publish drops them.
        foreach (var dawnDll in new[] { "dxil.dll", "dxcompiler.dll" })
        {
            var dawnPath = System.IO.Path.Combine(AppContext.BaseDirectory, dawnDll);
            if (!System.IO.File.Exists(dawnPath))
            {
                throw new InvalidOperationException(
                    $"Missing {dawnDll} next to the executable (BaseDirectory={AppContext.BaseDirectory}). " +
                    "Required for WebGPU (CefSharp discussion #4662 / CEF #3646).");
            }
        }

        // Prefer GPU / WebGPU for in-page graphics (Chromium 149+; needs dxil/dxcompiler for Dawn).
        // Explicit ANGLE D3D11 registers the D3D SharedImageBackingFactory required for
        // WebGPUSwapBufferProvider canvas present; use-angle=d3d12 leaves no factory for
        // WebgpuSwapChainTexture and destroys the device after adapter init.
        // enable-gpu-rasterization is omitted (correlated with Renderer11 crashes on some GPUs).
        //
        // CefSharp.Wpf.CefSettings ctor adds disable-gpu-compositing for OSR resize (#4953).
        // That forces software compositing process-wide and leaves HwndHost WebGPU canvases
        // black (adapter/device/HUD still work; canvas layers never present). Agent tabs need
        // GPU compositing; accept possible OSR shell resize glitches on the Blazor view.
        settings.CefCommandLineArgs.Remove("disable-gpu-compositing");
        settings.CefCommandLineArgs.Add("enable-unsafe-webgpu");
        settings.CefCommandLineArgs.Add("ignore-gpu-blocklist");
        settings.CefCommandLineArgs.Add("use-angle", "d3d11");

        if (!Cef.IsInitialized.GetValueOrDefault())
        {
            if (!Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: new DysonCefBrowserProcessHandler()))
            {
                var code = Cef.GetExitCode();
                if (code == ResultCode.NormalExitProcessNotified)
                {
                    // Another process holds RootCachePath; that process received OnAlreadyRunningAppRelaunch.
                    _shutdownBecauseAlreadyRunning = true;
                    return;
                }

                throw new InvalidOperationException(
                    $"Cef.Initialize returned false (ResultCode.{code} / {(int)code}). " +
                    $"RootCachePath={cefRoot}; CachePath={cachePath}; BrowserSubprocessPath={subprocess}; " +
                    $"BaseDirectory={AppContext.BaseDirectory}. " +
                    "Check cef-debug.log under %LocalAppData%\\DysonHarness\\, ensure VC++ 2022 x64 redistributable is installed, " +
                    "and that no other process is using this CEF cache.");
            }
        }
    }

    private static void StaThreadMain()
    {
        try
        {
            _uiDispatcher = Dispatcher.CurrentDispatcher;
            _app = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };

            InitializeCef();

            _ready?.TrySetResult();
            if (_shutdownBecauseAlreadyRunning)
                return;

            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _initError = ex;
            _ready?.TrySetResult();
        }
    }

    /// <summary>
    /// Focus existing shell/agent windows when a second process relaunches with the same RootCachePath.
    /// Returning true suppresses CEF's default "New Tab - Chromium" window.
    /// </summary>
    private sealed class DysonCefBrowserProcessHandler : BrowserProcessHandler
    {
        protected override bool OnAlreadyRunningAppRelaunch(
            IReadOnlyDictionary<string, string> commandLine,
            string currentDirectory)
        {
            var dispatcher = _uiDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher is null)
                return true;

            dispatcher.BeginInvoke(static () =>
            {
                var app = Application.Current;
                if (app is null)
                    return;

                Window? target = app.MainWindow;
                if (target is null || !target.IsVisible)
                {
                    foreach (Window window in app.Windows)
                    {
                        if (window.IsVisible)
                        {
                            target = window;
                            break;
                        }
                    }
                }

                if (target is null)
                    return;

                if (target.WindowState == WindowState.Minimized)
                    target.WindowState = WindowState.Normal;

                target.Activate();
                target.Topmost = true;
                target.Topmost = false;
                _ = target.Focus();
            });

            return true;
        }
    }
}
