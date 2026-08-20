namespace DysonHarness;

/// <summary>Locked size caps for model-facing tool-result Content (UTF-16 chars unless noted).</summary>
public static class DysonToolResultLimits
{
    public const int MaxContentChars = 64 * 1024;
    public const int MaxReadFileChars = 32 * 1024;
    public const int MaxReadFileLineChars = 8 * 1024;
    public const int MaxReadFileTailLines = 512;
    public const int MaxShellStreamBytes = 64 * 1024;
    public const int MaxLongRunningTailChars = 64 * 1024;

    public static string TruncateContent(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length <= MaxContentChars)
            return content;

        return content[..MaxContentChars]
            + $"… truncated at {MaxContentChars} chars (original {content.Length}). Page with ReadFile offset/limit or a smaller shell/tail read.";
    }
}
