namespace Harness.UI;

/// <summary>
/// Compile-time preview gate. True unless this is a Release build with
/// <c>-p:DysonReleaseChannel=stable</c>. Debug always has it, so local dev can run demos.
/// This file stays compiled on stable so tests can assert the flag is false.
/// </summary>
public static class DysonPreviewFeatures
{
    public static bool DemoModeEnabled { get; } =
#if DYSON_PREVIEW
        true;
#else
        false;
#endif
}
