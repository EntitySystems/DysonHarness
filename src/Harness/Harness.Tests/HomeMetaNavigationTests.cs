namespace Harness.Tests;

public class HomeMetaNavigationTests
{
    [Fact]
    public void Redirects_only_when_the_user_selects_a_work_directory()
    {
        Assert.True(Harness.UI.Components.Pages.HomeMetaNavigation.ShouldRedirectToMeta(userSelectedWorkDirectory: true));
        Assert.False(Harness.UI.Components.Pages.HomeMetaNavigation.ShouldRedirectToMeta(userSelectedWorkDirectory: false));
    }
}
