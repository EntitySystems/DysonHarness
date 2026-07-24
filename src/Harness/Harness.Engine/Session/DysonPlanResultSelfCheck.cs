namespace DysonHarness;

/// <summary>
/// ponytail: assert-only PlanResult kind + PlanRelativePath restore + SubmitPlan path shape.
/// Run: <c>DysonPlanResultSelfCheck.Run()</c> (also from UI <c>Program</c> startup).
/// </summary>
public static class DysonPlanResultSelfCheck
{
    public static void Run()
    {
        AssertKindValue();
        AssertCreateTurnFields();
        AssertPersistenceRoundTrip();
        AssertSubmitPlanCatalog();
    }

    private static void AssertKindValue()
    {
        if ((int)DysonAgentTurnKind.PlanResult != 6)
            throw new InvalidOperationException("DysonAgentTurnKind.PlanResult must be 6.");
    }

    private static void AssertCreateTurnFields()
    {
        var turn = DysonPlanResultFlow.CreateTurn(".dyson/plans/demo-abcdef0123.md", "Demo Plan");
        if (turn.Kind != DysonAgentTurnKind.PlanResult
            || turn.PlanRelativePath != ".dyson/plans/demo-abcdef0123.md"
            || turn.AgentTitle != "Demo Plan"
            || turn.CompletedUtc is null
            || string.IsNullOrWhiteSpace(turn.Instruction)
            || !turn.Instruction.Contains(".dyson/plans/demo-abcdef0123.md", StringComparison.Ordinal)
            || !turn.Instruction.Contains("Do not call SubmitPlan again", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PlanResult CreateTurn fields mismatch.");
        }
    }

    private static void AssertPersistenceRoundTrip()
    {
        var live = DysonPlanResultFlow.CreateTurn(".dyson/plans/round-aabbccddee.md", "Round Trip");
        var entity = DysonTurnPersistence.ToEntity(live, Guid.NewGuid(), sequence: 3);
        if (entity.Kind != DysonAgentTurnKind.PlanResult
            || entity.PlanRelativePath != live.PlanRelativePath
            || entity.Instruction != live.Instruction
            || entity.AgentTitle != live.AgentTitle)
        {
            throw new InvalidOperationException("PlanResult ToEntity lost fields.");
        }

        var restored = new DysonAgentTurn
        {
            Id = entity.Id,
            Kind = entity.Kind,
            Instruction = entity.Instruction,
            AgentTitle = entity.AgentTitle,
            PlanRelativePath = entity.PlanRelativePath,
            AssistantText = entity.AssistantText,
            StartedUtc = entity.CreatedUtc,
            CompletedUtc = entity.CompletedUtc,
        };

        if (restored.Kind != DysonAgentTurnKind.PlanResult
            || restored.PlanRelativePath != ".dyson/plans/round-aabbccddee.md")
        {
            throw new InvalidOperationException("PlanResult restore field mismatch.");
        }
    }

    private static void AssertSubmitPlanCatalog()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        if (!pipeline.Tools.TryGetValue("SubmitPlan", out var tool)
            || string.IsNullOrWhiteSpace(tool.Description)
            || !tool.InputSchemaJson.Contains("\"title\"", StringComparison.Ordinal)
            || !tool.InputSchemaJson.Contains("\"markdown\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SubmitPlan MCP catalog entry missing or incomplete.");
        }
    }
}
