using Harness.UI.Components.Shell;

namespace Harness.Tests;

public sealed class ErrorToastLifetimeTests
{
    [Fact]
    public void Duration_matches_the_banner_countdown()
    {
        // CSS animation: error-banner-expire 20s. A drift here leaves the bar empty
        // while the toast is still up, or clears the message before the bar finishes.
        Assert.Equal(TimeSpan.FromSeconds(20), ErrorToastLifetime.Duration);
    }
}
