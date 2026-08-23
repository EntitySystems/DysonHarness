namespace Harness.UI.Demo;

/// <summary>
/// Ephemeral generated-image preview information. The id is only valid while the
/// corresponding component owns it and must never be persisted with a turn.
/// </summary>
public sealed record DysonGeneratedImagePreview(string Id, string Url)
{
    public static bool LooksLikePng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8
        && bytes[0] == 0x89
        && bytes[1] == 0x50
        && bytes[2] == 0x4E
        && bytes[3] == 0x47
        && bytes[4] == 0x0D
        && bytes[5] == 0x0A
        && bytes[6] == 0x1A
        && bytes[7] == 0x0A;
}
