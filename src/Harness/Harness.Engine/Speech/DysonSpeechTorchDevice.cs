namespace DysonHarness;

/// <summary>
/// <see cref="DysonAppSettingKeys.SpeechTorchDevice"/>: only <c>rocm</c> selects the AMD ROCm torch directory.
/// Missing, blank, or any other value is the CPU torch. <c>cuda</c> is refused by <see cref="SaveAsync"/> and is not stored.
/// </summary>
public static class DysonSpeechTorchDevice
{
    public const string CpuValue = "cpu";
    public const string RocmValue = "rocm";

    /// <summary>
    /// qwen-tts 0.1.1 documents <c>device_map="cuda:0"</c>. Chatterbox <c>from_local</c> calls <c>.to(device)</c>.
    /// AMD's Windows ROCm wheel reports the GPU through <c>torch.cuda</c> (device 0).
    /// </summary>
    public const string RocmBridgeDevice = "cuda:0";

    public static bool IsRocm(string? value) =>
        string.Equals(value?.Trim(), RocmValue, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>cuda</c> and <c>cuda:0</c> cannot be stored. Anything else that is not <c>rocm</c> is CPU.</summary>
    public static bool IsRejectedCuda(string? value)
    {
        var text = value?.Trim();
        return text is not null && text.StartsWith("cuda", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The directory the setting names. Does not check that the tree exists.</summary>
    public static string DirectoryFor(string installRoot, string? setting) =>
        IsRocm(setting)
            ? DysonSpeechPaths.GetRocmTorchDir(installRoot)
            : DysonSpeechPaths.GetTorchDir(installRoot);

    /// <summary>
    /// ROCm directory when the setting is <c>rocm</c> and <c>torch-rocm/torch</c> exists; otherwise the CPU torch directory.
    /// </summary>
    public static async Task<Result<string, string>> InstalledDirectoryAsync(
        IDysonSubjectSettingsRepository settings,
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var stored = await settings.GetSettingAsync(DysonAppSettingKeys.SpeechTorchDevice, cancellationToken)
            .ConfigureAwait(false);
        if (stored.IsError)
            return Result<string, string>.AsError(stored.Error);
        if (!IsRocm(stored.Value))
            return Result<string, string>.AsValue(DysonSpeechPaths.GetTorchDir(installRoot));

        var rocm = DysonSpeechPaths.GetRocmTorchDir(installRoot);
        var ready = await Task.Run(() => Directory.Exists(Path.Combine(rocm, "torch")), cancellationToken)
            .ConfigureAwait(false);
        return Result<string, string>.AsValue(ready ? rocm : DysonSpeechPaths.GetTorchDir(installRoot));
    }

    /// <summary><see cref="RocmBridgeDevice"/> when <paramref name="torchDir"/> is the ROCm tree; otherwise null (CPU bridges).</summary>
    public static string? BridgeDevice(string installRoot, string torchDir) =>
        string.Equals(torchDir, DysonSpeechPaths.GetRocmTorchDir(installRoot), StringComparison.OrdinalIgnoreCase)
            ? RocmBridgeDevice
            : null;

    public static async Task<VoidResult<string>> SaveAsync(
        IDysonSubjectSettingsRepository settings,
        string? value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (IsRejectedCuda(value))
            return new VoidResult<string>("CUDA PyTorch is not available.");

        var next = IsRocm(value) ? RocmValue : CpuValue;
        var stored = await settings.GetSettingAsync(DysonAppSettingKeys.SpeechTorchDevice, cancellationToken)
            .ConfigureAwait(false);
        if (stored.IsError)
            return new VoidResult<string>(stored.Error);

        // A cancel drains the bridge and leaves the process up. A ROCm/CPU change kills it so the next speak starts
        // on the new torch. The same device does not stop, so a repeat save leaves the model warm.
        if (IsRocm(stored.Value) != IsRocm(next))
        {
            DysonQwenSpeech.StopBridge();
            DysonChatterboxSpeech.StopBridge();
            DysonPocketSpeech.StopBridge();
        }
        else if (string.Equals(stored.Value?.Trim(), next, StringComparison.OrdinalIgnoreCase))
            return VoidResult<string>.Success;

        return await settings.SetSettingAsync(DysonAppSettingKeys.SpeechTorchDevice, next, cancellationToken)
            .ConfigureAwait(false);
    }
}
