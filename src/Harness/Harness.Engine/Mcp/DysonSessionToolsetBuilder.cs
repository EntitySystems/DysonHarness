namespace DysonHarness;

/// <summary>
/// Builds the per-session MCP catalog: default tools → structural gates → policy denylist.
/// </summary>
public static class DysonSessionToolsetBuilder
{
    /// <summary>
    /// Full catalog for a live session: CreateDefault, shell/Plan gate, inter-agent depth,
    /// optional subagent completion omit, then mode denylist.
    /// </summary>
    public static DysonMcpPipeline Build(
        DysonAgentSessionConfig config,
        string agentMode,
        int interAgentDepth = 0,
        bool omitRootTaskCompletionTools = false,
        Guid? modelSlugId = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var pipeline = DysonMcpPipeline.CreateDefault(
            config.McpAccessMode,
            config.AvailableShells.Select(s => s.Name).ToArray(),
            browserControlAvailable: config.BrowserControl is not null,
            uiTheme: config.UiTheme);

        pipeline.ConfigureShellExecuteForMode(
            string.Equals(agentMode, DysonAgentModes.Plan, StringComparison.OrdinalIgnoreCase));
        pipeline.ConfigureInterAgentTools(interAgentDepth);

        if (omitRootTaskCompletionTools)
            OmitRootTaskCompletionTools(pipeline);

        // Dynamic sources merge after structural gates. User custom MCP wins any collision; managed
        // plugin tools may never replace built-ins or custom tools. Denylist remains authoritative.
        config.CustomMcpHost?.ApplyToPipeline(pipeline);
        config.PluginMcpHost?.ApplyToPipeline(pipeline);
        ApplyDisabledTools(pipeline, ResolveDisabledTools(config, agentMode, modelSlugId));
        return pipeline;
    }

    /// <summary>
    /// Ctor-time catalog before Parent/depth is known: CreateDefault + shell gate + denylist.
    /// Callers apply inter-agent / omit-completion afterward, then
    /// <see cref="ReapplyDisabledTools"/>.
    /// </summary>
    public static DysonMcpPipeline BuildInitial(
        DysonAgentSessionConfig config,
        string agentMode,
        Guid? modelSlugId = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var pipeline = DysonMcpPipeline.CreateDefault(
            config.McpAccessMode,
            config.AvailableShells.Select(s => s.Name).ToArray(),
            browserControlAvailable: config.BrowserControl is not null,
            uiTheme: config.UiTheme);

        pipeline.ConfigureShellExecuteForMode(
            string.Equals(agentMode, DysonAgentModes.Plan, StringComparison.OrdinalIgnoreCase));
        config.CustomMcpHost?.ApplyToPipeline(pipeline);
        config.PluginMcpHost?.ApplyToPipeline(pipeline);
        ApplyDisabledTools(pipeline, ResolveDisabledTools(config, agentMode, modelSlugId));
        return pipeline;
    }

    public static void ApplyDisabledTools(
        DysonMcpPipeline pipeline,
        IReadOnlySet<string>? disabledTools)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (disabledTools is null || disabledTools.Count == 0)
            return;

        foreach (var name in disabledTools)
        {
            if (!string.IsNullOrWhiteSpace(name))
                pipeline.Tools.Remove(name);
        }
    }

    /// <summary>
    /// Re-apply denylist after structural gates that may <c>Ensure*</c> tools back into the catalog.
    /// </summary>
    public static void ReapplyDisabledTools(
        DysonMcpPipeline pipeline,
        DysonAgentSessionConfig config,
        string agentMode,
        Guid? modelSlugId = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(config);
        // Structural gates run before dynamic merge; mode policy is applied last so it can disable
        // built-in, custom MCP, and managed plugin MCP names uniformly.
        config.CustomMcpHost?.ApplyToPipeline(pipeline);
        config.PluginMcpHost?.ApplyToPipeline(pipeline);
        ApplyDisabledTools(pipeline, ResolveDisabledTools(config, agentMode, modelSlugId));
    }

    /// <summary>
    /// Full catalog tools for Settings checklists (browser included; platform shells).
    /// </summary>
    public static IReadOnlyList<DysonMcpTool> AllCatalogTools()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(
            DysonMcpAccessMode.FullAccess,
            DysonShell.DefaultShellNamesForCurrentPlatform(),
            browserControlAvailable: true);
        return pipeline.Tools.Values
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// All catalog tool names for Settings checklists (browser included; platform shells).
    /// </summary>
    public static IReadOnlyList<string> AllCatalogToolNames() =>
        AllCatalogTools().Select(t => t.Name).ToArray();

    public static IReadOnlySet<string> ResolveDisabledTools(
        DysonAgentSessionConfig config,
        string agentMode,
        Guid? modelSlugId = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.ToolPolicy is not null)
            return DysonToolPolicyResolver.Resolve(config.ToolPolicy, agentMode, modelSlugId);

        return config.DisabledTools
            ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);
    }

    public static void OmitRootTaskCompletionTools(DysonMcpPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        pipeline.Tools.Remove("CompleteTask");
        pipeline.Tools.Remove("ConfirmTaskComplete");
        pipeline.Tools.Remove("ContinueWork");
    }
}
