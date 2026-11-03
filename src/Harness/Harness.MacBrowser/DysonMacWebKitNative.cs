using System.Reflection;
using System.Runtime.InteropServices;

namespace DysonHarness;

internal static class DysonMacWebKitNative
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SnipCallback(IntPtr json);

    private static readonly SnipCallback SnipThunk = OnSnip;
    private static Action<string>? _snip;

    static DysonMacWebKitNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(DysonMacWebKitNative).Assembly, Resolve);
    }

    public static void Init(Action<string> onSnip)
    {
        _snip = onSnip;
        if (dyson_webkit_init(SnipThunk) != 0)
            throw new InvalidOperationException("dyson_webkit_init failed.");
    }

    public static void Run() => dyson_webkit_run();

    public static void ActivatePeer() => dyson_webkit_activate_peer();

    public static void Alert(string message) => dyson_webkit_alert(message);

    public static string Invoke(string request)
    {
        var ptr = dyson_webkit_invoke(request);
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? "{\"ok\":false,\"error\":\"empty response\"}";
        }
        finally
        {
            if (ptr != IntPtr.Zero)
                dyson_webkit_free(ptr);
        }
    }

    private static void OnSnip(IntPtr json)
    {
        var text = Marshal.PtrToStringUTF8(json);
        if (text is null)
            return;
        var copy = text;
        var handler = _snip;
        ThreadPool.QueueUserWorkItem(_ => handler?.Invoke(copy));
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "libDysonWebKit")
            return IntPtr.Zero;

        var path = Path.Combine(AppContext.BaseDirectory, "libDysonWebKit.dylib");
        return NativeLibrary.Load(path);
    }

    [DllImport("libDysonWebKit", CallingConvention = CallingConvention.Cdecl)]
    private static extern int dyson_webkit_init(SnipCallback callback);

    [DllImport("libDysonWebKit", CallingConvention = CallingConvention.Cdecl)]
    private static extern void dyson_webkit_run();

    [DllImport("libDysonWebKit", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dyson_webkit_invoke([MarshalAs(UnmanagedType.LPUTF8Str)] string request);

    [DllImport("libDysonWebKit", CallingConvention = CallingConvention.Cdecl)]
    private static extern void dyson_webkit_free(IntPtr pointer);

    [DllImport("libDysonWebKit", CallingConvention = CallingConvention.Cdecl)]
    private static extern void dyson_webkit_activate_peer();

    [DllImport("libDysonWebKit", CallingConvention = CallingConvention.Cdecl)]
    private static extern void dyson_webkit_alert([MarshalAs(UnmanagedType.LPUTF8Str)] string message);
}
