using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace DysonHarness;

/// <summary>
/// Lazy STA WPF host plus the one shared WebView2 environment. Blazor Server runs MTA; all WebView2/UI work
/// marshals here. When a WPF <see cref="Application"/> already owns the STA (Windows shell), reuse it.
/// </summary>
public static class DysonWindowsStaHost
{
    /// <summary>Evergreen WebView2 Runtime download (bootstrapper page).</summary>
    public const string RuntimeDownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private static readonly object Gate = new();
    private static Thread? _thread;
    private static Dispatcher? _uiDispatcher;
    private static TaskCompletionSource? _ready;
    private static Exception? _initError;
    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>Test-only user data folder. Set before the first environment is created.</summary>
    internal static string? UserDataFolderOverride { get; set; }

    /// <summary>
    /// WebView2 user data folder, scoped by app mode (<c>{DysonAppPaths root}\webview2</c>) so Dev and Prod builds
    /// never share a folder. Shell and agent tabs share it and are split by profile.
    /// </summary>
    public static string UserDataFolder =>
        UserDataFolderOverride ?? Path.Combine(DysonAppPaths.GetRoot(DysonBuildInfo.Current), "webview2");

    public static Dispatcher UiDispatcher
    {
        get
        {
            EnsureStarted();
            return _uiDispatcher ?? throw new InvalidOperationException("STA dispatcher not ready.");
        }
    }

    /// <summary>Installed Evergreen WebView2 Runtime version, or an error when the runtime is missing.</summary>
    public static Result<string, string> RuntimeVersion()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return string.IsNullOrWhiteSpace(version)
                ? Result<string, string>.AsError("Microsoft Edge WebView2 Runtime is not installed.")
                : Result<string, string>.AsValue(version);
        }
        catch (Exception ex)
        {
            // WebView2RuntimeNotFoundException, or a missing WebView2Loader.dll.
            return Result<string, string>.AsError(
                "Microsoft Edge WebView2 Runtime is not installed: " + ex.Message,
                exception: ex);
        }
    }

    /// <summary>
    /// The process-wide WebView2 environment. Must be called on the STA dispatcher thread; created once.
    /// </summary>
    public static async Task<Result<CoreWebView2Environment, string>> GetEnvironmentAsync()
    {
        Task<CoreWebView2Environment> task;
        lock (Gate)
            task = _environment ??= CreateEnvironmentAsync();

        try
        {
            return Result<CoreWebView2Environment, string>.AsValue(await task.ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            return Result<CoreWebView2Environment, string>.AsError(
                $"WebView2 environment failed (UserDataFolder={UserDataFolder}): {ex.Message}",
                exception: ex);
        }
    }

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var folder = UserDataFolder;
        Directory.CreateDirectory(folder);
        // Edge ships WebGPU on by default; ignore-gpu-blocklist keeps WebGPU/WebGL on blocklisted drivers,
        // matching the old CEF switches. Every user of this folder must pass identical options.
        var options = new CoreWebView2EnvironmentOptions(additionalBrowserArguments: "--ignore-gpu-blocklist");
        return await CoreWebView2Environment.CreateAsync(null, folder, options).ConfigureAwait(true);
    }

    /// <summary>
    /// Bind to an existing STA WPF <see cref="Application"/> (shell owns the message loop).
    /// No-op if already initialized.
    /// </summary>
    public static void AttachToExistingApplication()
    {
        lock (Gate)
        {
            if (_ready is null)
            {
                var app = Application.Current
                    ?? throw new InvalidOperationException("Application.Current is null; cannot attach STA host.");
                var dispatcher = app.Dispatcher;
                if (!dispatcher.CheckAccess()
                    || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                {
                    throw new InvalidOperationException(
                        "AttachToExistingApplication must run on the STA UI thread that owns Application.Current.");
                }

                _uiDispatcher = dispatcher;
                _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ready.TrySetResult();
            }
        }
    }

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_ready is null)
            {
                _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var current = Application.Current;
                if (current?.Dispatcher is { } dispatcher
                    && dispatcher.CheckAccess()
                    && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                {
                    _uiDispatcher = dispatcher;
                    _ready.TrySetResult();
                }
                else
                {
                    _thread = new Thread(StaThreadMain)
                    {
                        IsBackground = true,
                        Name = "DysonWindowsStaHost",
                    };
                    _thread.SetApartmentState(ApartmentState.STA);
                    _thread.Start();
                }
            }
        }

        _ready.Task.GetAwaiter().GetResult();
        if (_initError is not null)
            throw new InvalidOperationException("STA host failed to start.", _initError);
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
            _uiDispatcher = Dispatcher.CurrentDispatcher;
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            _ready?.TrySetResult();
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _initError = ex;
            _ready?.TrySetResult();
        }
    }
}
