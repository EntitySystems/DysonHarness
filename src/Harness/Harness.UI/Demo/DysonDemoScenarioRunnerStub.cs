using Microsoft.JSInterop;

#if !DYSON_PREVIEW
namespace Harness.UI.Demo;

/// <summary>
/// Stable builds compile the demo scenarios out. This stub keeps the DI registration and the
/// Meta page inject compiling. <see cref="IsRunning"/> is always false, so the real-provider gate stays on.
/// It lives outside <c>Demo/Scenarios</c> because the stable Release build removes that folder from Compile.
/// </summary>
public sealed class DysonDemoScenarioRunner : IDisposable
{
    public DysonDemoScenarioRunner(IJSRuntime js)
    {
        ArgumentNullException.ThrowIfNull(js);
    }

    public bool IsRunning => false;

    public void Dispose()
    {
    }
}
#endif
