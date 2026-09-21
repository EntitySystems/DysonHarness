using Harness.UI.Components.Chat;

namespace Harness.Tests;

/// <summary>
/// Effort menu contents shared by the main composer and the Meta Agent composer.
/// </summary>
public class ComposerEffortOptionsTests
{
    [Fact]
    public void Registered_modes_are_listed_in_order()
    {
        var options = ComposerEffortOptions.Build(["low", "medium", "high"], current: null).ToList();

        Assert.Equal(["low", "medium", "high"], options);
    }

    [Fact]
    public void Current_effort_is_kept_when_the_slug_does_not_register_it()
    {
        // A session can hold an effort the slug no longer lists; hiding it would strand the user.
        var options = ComposerEffortOptions.Build(["low", "high"], current: "xhigh").ToList();

        Assert.Equal(["low", "high", "xhigh"], options);
    }

    [Fact]
    public void Current_effort_is_not_duplicated_when_already_registered()
    {
        var options = ComposerEffortOptions.Build(["low", "high"], current: "  high  ").ToList();

        Assert.Equal(["low", "high"], options);
    }

    [Fact]
    public void No_modes_and_no_current_effort_yields_an_empty_menu()
    {
        Assert.Empty(ComposerEffortOptions.Build(null, current: null));
        Assert.Empty(ComposerEffortOptions.Build([], current: "   "));
    }
}
