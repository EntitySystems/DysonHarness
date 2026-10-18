namespace DysonHarness;

/// <summary>
/// Shared speech install tree: <c>{DysonAppPaths.GetRoot(mode)}/speech/{variant}</c>. Never under the MSI
/// install directory or beside the executable. Absolute paths are never stored in SQLite.
/// </summary>
public static class DysonSpeechPaths
{
    /// <summary>Test-only override of the <see cref="DysonAppPaths.GetRoot"/> part.</summary>
    public const string RootOverrideVariable = "DYSON_SPEECH_ROOT";

    /// <summary>Dev → <c>dev</c>, Test → <c>test</c>, Prod → stamped channel (<c>stable</c> / <c>preview</c>) or <c>dev</c>.</summary>
    public static string GetVariant(DysonAppMode mode, string? stampedChannel) => mode switch
    {
        DysonAppMode.Test => "test",
        DysonAppMode.Prod when string.Equals(stampedChannel, "stable", StringComparison.OrdinalIgnoreCase) => "stable",
        DysonAppMode.Prod when string.Equals(stampedChannel, "preview", StringComparison.OrdinalIgnoreCase) => "preview",
        _ => "dev",
    };

    /// <param name="stampedChannel">UI passes <c>IsStampedRelease ? EffectiveChannel : null</c>.</param>
    public static string GetInstallRoot(DysonAppMode mode, string? stampedChannel)
    {
        var overrideRoot = Environment.GetEnvironmentVariable(RootOverrideVariable);
        var root = string.IsNullOrWhiteSpace(overrideRoot) ? DysonAppPaths.GetRoot(mode) : overrideRoot;
        return Path.Combine(root, "speech", GetVariant(mode, stampedChannel));
    }

    public static string GetPythonExe(string installRoot) => Path.Combine(installRoot, "python", "python.exe");

    public static string GetTorchDir(string installRoot) => Path.Combine(installRoot, "torch");

    public static string GetPhononSite(string installRoot) => Path.Combine(installRoot, "phonon", "site");

    public static string GetPhononModelDir(string installRoot) =>
        Path.Combine(installRoot, "phonon", "model", "model_phonon2_c4c_int6");

    public static string GetPocketSite(string installRoot) => Path.Combine(installRoot, "pocket", "site");

    public static string GetPocketModelFile(string installRoot) =>
        Path.Combine(installRoot, "pocket", "model", "model.safetensors");

    public static string GetPocketConfig(string installRoot) => Path.Combine(installRoot, "pocket", "model", "english.yaml");

    public static string GetPocketBridge(string installRoot) => Path.Combine(installRoot, "pocket", "bridge.py");

    public static string GetPocketVoicesDir(string installRoot) => Path.Combine(installRoot, "pocket", "voices");

    public static string GetQwenSite(string installRoot) => Path.Combine(installRoot, "qwen", "site");

    /// <summary>The 7 root model files; the speech tokenizer is in <c>speech_tokenizer/</c> below it.</summary>
    public static string GetQwenModelDir(string installRoot) => Path.Combine(installRoot, "qwen", "model");

    public static string GetQwenBridge(string installRoot) => Path.Combine(installRoot, "qwen", "bridge.py");

    public static string GetQwenVoicesDir(string installRoot) => Path.Combine(installRoot, "qwen", "voices");

    public static string GetChatterboxSite(string installRoot) => Path.Combine(installRoot, "chatterbox", "site");

    public static string GetChatterboxModelDir(string installRoot) => Path.Combine(installRoot, "chatterbox", "model");

    public static string GetChatterboxBridge(string installRoot) => Path.Combine(installRoot, "chatterbox", "bridge.py");

    public static string GetChatterboxVoicesDir(string installRoot) => Path.Combine(installRoot, "chatterbox", "voices");

    public static string GetInstallJson(string installRoot) => Path.Combine(installRoot, "install.json");
}
