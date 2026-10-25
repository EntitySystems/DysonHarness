namespace Harness.UI.Components.Shell;

/// <summary>How long <see cref="ErrorToast"/> stays up. Matches the CSS border countdown.</summary>
public static class ErrorToastLifetime
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(20);
}
