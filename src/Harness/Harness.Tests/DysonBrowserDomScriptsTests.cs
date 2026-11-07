using DysonHarness;

namespace Harness.Tests;

public class DysonBrowserDomScriptsTests
{
    [Fact]
    public void Click_selector_uses_query_selector_and_ok()
    {
        var script = DysonBrowserDomScripts.Click(new DysonBrowserClickRequest
        {
            Selector = "#go",
            Button = "left",
        });

        Assert.False(script.IsError);
        Assert.Contains("document.querySelector(\"#go\")", script.Value, StringComparison.Ordinal);
        Assert.Contains("selector not found", script.Value, StringComparison.Ordinal);
        Assert.Contains("return 'ok'", script.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Click_dispatches_one_click_and_never_calls_el_click()
    {
        // el.click() after dispatchEvent fired every handler twice (two tabs, double navigation).
        foreach (var request in new[]
                 {
                     new DysonBrowserClickRequest { Selector = "#go" },
                     new DysonBrowserClickRequest { X = 10, Y = 20 },
                 })
        {
            var script = DysonBrowserDomScripts.Click(request);
            Assert.False(script.IsError);
            Assert.DoesNotContain("el.click()", script.Value, StringComparison.Ordinal);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(script.Value, "dispatchEvent"));
        }
    }

    [Fact]
    public void Click_requires_a_target()
    {
        var script = DysonBrowserDomScripts.Click(new DysonBrowserClickRequest());
        Assert.True(script.IsError);
        Assert.Equal("Click requires selector or x/y coordinates.", script.Error);
    }

    [Fact]
    public void Type_clear_first_empties_the_value()
    {
        var script = DysonBrowserDomScripts.Type(new DysonBrowserTypeRequest
        {
            Selector = "input",
            Text = "hi",
            ClearFirst = true,
        });

        Assert.False(script.IsError);
        Assert.Contains("el.value = ''", script.Value, StringComparison.Ordinal);
        Assert.Contains("\"hi\"", script.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Hover_requires_a_selector()
    {
        var script = DysonBrowserDomScripts.Hover(" ");
        Assert.True(script.IsError);
        Assert.Equal("selector is required", script.Error);
    }
}
