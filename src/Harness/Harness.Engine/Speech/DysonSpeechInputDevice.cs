namespace DysonHarness;

/// <summary>One capture device. <see cref="Id"/> is what <see cref="DysonAppSettingKeys.SpeechInputDeviceId"/> stores.</summary>
public sealed record DysonAudioInputDevice(string Id, string Name);

/// <summary>Reads and writes the selected microphone. No device table: one app-setting string.</summary>
public static class DysonSpeechInputDevice
{
    /// <summary>
    /// The saved device when it is still listed; otherwise the first device when nothing is saved.
    /// A saved id that is not listed is an error (not swapped for another microphone).
    /// </summary>
    public static Result<DysonAudioInputDevice, string> Resolve(
        IReadOnlyList<DysonAudioInputDevice> devices,
        string? savedId)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count == 0)
            return Result<DysonAudioInputDevice, string>.AsError("No microphone is connected.");

        if (string.IsNullOrWhiteSpace(savedId))
            return Result<DysonAudioInputDevice, string>.AsValue(devices[0]);

        var id = savedId.Trim();
        var match = devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
        return match is null
            ? Result<DysonAudioInputDevice, string>.AsError($"Microphone '{id}' is not connected.")
            : Result<DysonAudioInputDevice, string>.AsValue(match);
    }

    /// <summary>Persists <paramref name="deviceId"/> when it is in <paramref name="devices"/>.</summary>
    public static async Task<VoidResult<string>> SaveAsync(
        IDysonSubjectSettingsRepository settings,
        IReadOnlyList<DysonAudioInputDevice> devices,
        string? deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(devices);
        if (string.IsNullOrWhiteSpace(deviceId)
            || devices.All(d => !string.Equals(d.Id, deviceId.Trim(), StringComparison.Ordinal)))
            return new VoidResult<string>("That microphone is not connected.");

        return await settings.SetSettingAsync(
            DysonAppSettingKeys.SpeechInputDeviceId, deviceId.Trim(), cancellationToken).ConfigureAwait(false);
    }
}
