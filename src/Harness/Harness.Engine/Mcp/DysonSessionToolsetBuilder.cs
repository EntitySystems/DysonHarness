namespace DysonHarness;

/// <summary>
/// Builds the per-session MCP catalog: default tools → structural gates → policy denylist.
/// </summary>
public static class DysonSessionToolsetBuilder
{
    /// <summary>
    /// Full catalog for a live session: CreateDefault, shell/Plan and image-provider gates,
    /// inter-agent depth, root vs child completion-tool omit (children drop CompleteTask; roots drop
    /// SubmitSubagentReport), then mode denylist.
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
        else
            OmitSubmitSubagentReport(pipeline);

        // Dynamic sources merge after the general structural gates. This dedicated provider gate
        // deliberately runs after the merge, so neither a custom nor plugin catalog can expose
        // GenerateImage when the session did not configure an image-generation provider.
        config.CustomMcpHost?.ApplyToPipeline(pipeline);
        config.PluginMcpHost?.ApplyToPipeline(pipeline);
        ApplyImageGenerationProviderGate(pipeline, config);
        ApplyDisabledTools(pipeline, ResolveDisabledTools(config, agentMode, modelSlugId));
        return pipeline;
    }

    /// <summary>
    /// Ctor-time catalog before Parent/depth is known: CreateDefault + shell and image-provider gates + denylist.
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
        ApplyImageGenerationProviderGate(pipeline, config);
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
    /// Removes <c>GenerateImage</c> unless this session has a resolved image-generation provider.
    /// This runs after dynamic MCP sources so an unavailable provider cannot be bypassed by a
    /// colliding external tool registration.
    /// </summary>
    private static void ApplyImageGenerationProviderGate(
        DysonMcpPipeline pipeline,
        DysonAgentSessionConfig config)
    {
        if (config.ImageGenerationProvider is null)
            pipeline.Tools.Remove("GenerateImage");
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
        // Reapply dynamic sources first; the provider gate and mode policy remain authoritative.
        config.CustomMcpHost?.ApplyToPipeline(pipeline);
        config.PluginMcpHost?.ApplyToPipeline(pipeline);
        ApplyImageGenerationProviderGate(pipeline, config);
        ApplyDisabledTools(pipeline, ResolveDisabledTools(config, agentMode, modelSlugId));
    }

    /// <summary>
    /// Full catalog tools for Settings checklists (browser included; platform shells). Includes
    /// <c>GenerateImage</c> so policies can deny it, although live catalogs still require an
    /// image-generation provider.
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

    public static void OmitSubmitSubagentReport(DysonMcpPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        pipeline.Tools.Remove("SubmitSubagentReport");
    }
}
