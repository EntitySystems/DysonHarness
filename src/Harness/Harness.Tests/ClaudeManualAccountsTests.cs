using DysonHarness;

namespace Harness.Tests;

public class ClaudeManualAccountsTests
{
    [Fact]
    public void CheckCap_rejects_8_and_allows_7()
    {
        var atCap = ClaudeManualAccounts.CheckCap(ClaudeManualAccounts.MaxAccounts);
        Assert.True(atCap.IsError);
        Assert.Equal(
            "Claude accounts are limited to 8. Remove one before adding another.",
            atCap.Error);

        Assert.False(ClaudeManualAccounts.CheckCap(7).IsError);
    }

    [Fact]
    public void BuildPinPlan_leaves_a_single_enabled_account()
    {
        var accounts = new ClaudeAccount[]
        {
            new("b.json", null, null, true, false),
            new("a.json", "a@example.com", null, false, false),
            new("claude:apikey:abc", null, "claude-apikey", false, true),
        };

        var plan = ClaudeManualAccounts.BuildPinPlan(accounts, "b.json");

        Assert.Equal("b.json", plan[0].Name);
        Assert.False(plan[0].Disabled);
        Assert.Single(plan, patch => !patch.Disabled);
        Assert.Equal(2, plan.Count(patch => patch.Disabled));
        Assert.Contains(plan, patch => patch.Name == "claude:apikey:abc" && patch.Disabled);
        Assert.Contains(plan, patch => patch.Name == "a.json" && patch.Disabled);
    }

    [Fact]
    public void ParseAuthFiles_omits_non_claude_and_includes_api_key()
    {
        const string json = """
            {
              "files": [
                {"name":"gem.json","provider":"gemini","type":"gemini","email":"g@x"},
                {"name":"c.json","provider":"claude","type":"claude","email":"a@b.c","disabled":false,"source":"file"},
                {"name":"claude:apikey:abc","id":"claude:apikey:abc","provider":"Claude","type":"claude","label":"claude-apikey","account_type":"api_key","disabled":true,"source":"memory"}
              ]
            }
            """;

        var parsed = ClaudeManualAccounts.ParseAuthFiles(json);

        Assert.False(parsed.IsError);
        Assert.Equal(["c.json", "claude:apikey:abc"], parsed.Value.Select(a => a.Name).ToArray());
        Assert.DoesNotContain(parsed.Value, a => a.Name == "gem.json");
        Assert.False(parsed.Value[0].IsApiKey);
        Assert.True(parsed.Value[1].IsApiKey);
    }

    [Fact]
    public void BuildReconcilePlan_keeps_ordinal_first_enabled_name()
    {
        var accounts = new ClaudeAccount[]
        {
            new("m.json", null, null, false, false),
            new("a.json", null, null, false, true),
            new("z.json", null, null, true, false),
        };

        var plan = ClaudeManualAccounts.BuildReconcilePlan(accounts);

        Assert.Equal([new ClaudeAccountStatusPatch("m.json", true)], plan);
    }

    [Fact]
    public void ParseAuthFiles_reads_display_email_label_and_active()
    {
        const string json = """
            {
              "files": [
                {"name":"blank.json","type":"claude","email":"  ","label":"Ada","disabled":false},
                {"id":"id-only","provider":"claude","email":"a@b.c","disabled":true}
              ]
            }
            """;

        var parsed = ClaudeManualAccounts.ParseAuthFiles(json);

        Assert.False(parsed.IsError);
        var byName = parsed.Value.ToDictionary(a => a.Name, StringComparer.Ordinal);
        Assert.Equal("Ada", byName["blank.json"].Display);
        Assert.True(byName["blank.json"].IsActive);
        Assert.Equal("a@b.c", byName["id-only"].Display);
        Assert.False(byName["id-only"].IsActive);
        Assert.Equal("id-only", byName["id-only"].Name);
    }
}
