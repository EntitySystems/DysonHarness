namespace DysonHarness;

/// <summary>How the validator checks one property value.</summary>
public enum DysonA2uiPropKind
{
    DynamicString,
    DynamicBoolean,
    DynamicStringList,
    DynamicAny,

    /// <summary>Object whose values are Dynamic values (action.event.context).</summary>
    DynamicMap,

    /// <summary><c>{"@path"}</c> or a boolean kit function call (<c>@checks</c> condition).</summary>
    Condition,

    /// <summary>UAX #31 identifier string (component ids, event names).</summary>
    Identifier,
    ChildList,
    Action,
    Event,
    Checks,
    Options,
    Accessibility,
    Enum,
    Number,
    NonNegativeInt,

    /// <summary>Relative or absolute JSON pointer string.</summary>
    Pointer,
    LiteralString,

    /// <summary>Non-blank literal string (ActionButton.label).</summary>
    Label,

    /// <summary>Literal built-in conversation func key (ActionButton.func).</summary>
    BuiltInFunc,

    /// <summary>Literal non-empty Guid string (QuestionSlot.questionId).</summary>
    QuestionId,

    /// <summary>Deferred boolean flag: only <c>false</c> is accepted (ChoicePicker.filterable).</summary>
    DeferredFlag,

    /// <summary>Number literal or <c>{"@path"}</c>.</summary>
    DynamicNumber,

    /// <summary>Literal integer in [Min, Max].</summary>
    BoundedInt,

    /// <summary>Number in [Min, Max] (literals are range-checked) or <c>{"@path"}</c>.</summary>
    BoundedNumber,

    /// <summary>Array of at most MaxItems objects checked against Item (any literal object when Item is null), or <c>{"@path"}</c>.</summary>
    ObjectList,

    /// <summary>Like <see cref="ObjectList"/> but an item may also be a plain string.</summary>
    StringOrObjectList,

    /// <summary>A component id that is not a layout child (checked as an identifier; <c>DysonA2uiSurfaces.Children</c> yields it by component name).</summary>
    IdRef,

    /// <summary>Short scalar literal (string up to 64 chars, number, bool); accepted and never rendered.</summary>
    Cosmetic,

    /// <summary>Literal http(s) URL or built-in func key; normalised in place to <c>open_url:...</c>.</summary>
    LinkTarget,

    /// <summary>Literal http(s) URL up to 2048 chars; validated, never fetched or rendered.</summary>
    MediaUrl,

    /// <summary>Chart data: literal array of objects (caps checked) or <c>{"@path"}</c>. Values[0]=="pie" applies the slice cap.</summary>
    ChartData,

    /// <summary>Always fails with the message in <c>Values[0]</c>.</summary>
    Rejected,

    /// <summary>Literal JSON integer &gt;= 1 (GitHub ids exceed int).</summary>
    PositiveLong,

    /// <summary>Literal array of 1..MaxItems distinct positive integers.</summary>
    PositiveLongList,

    /// <summary>Table data-mode rows: array of arrays of string/number/bool/{componentId}, or <c>{"@path"}</c>.</summary>
    CellMatrix,

    /// <summary>Literal ISO 8601 date or date-time string (DateTimeInput.min/max).</summary>
    IsoDateTime,
}

public sealed record DysonA2uiPropSpec(
    DysonA2uiPropKind Kind,
    bool Required = false,
    IReadOnlyList<string>? Values = null,
    double? Min = null,
    double? Max = null,
    int? MaxItems = null,
    IReadOnlyDictionary<string, DysonA2uiPropSpec>? Item = null);

/// <summary><see cref="DysonCatalog"/> components resolve only under <see cref="DysonA2uiProtocol.DysonCatalogId"/>.</summary>
public sealed record DysonA2uiComponentSpec(bool DysonCatalog, IReadOnlyDictionary<string, DysonA2uiPropSpec> Props);

public sealed record DysonA2uiFunctionSpec(bool ReturnsString, IReadOnlyDictionary<string, DysonA2uiPropSpec> Args);

/// <summary>
/// The Dyson kit: a restyled subset of the A2UI v1.0 basic catalog plus the Dyson-catalog
/// components QuestionSlot and ActionButton. Every component also takes <c>@accessibility</c> and
/// <c>weight</c> (weight only on direct Row/Column children; checked by the tree check).
/// Deviation: <c>Text.variant</c> also allows <c>h5</c> (question title); the basic catalog lists only caption/body.
/// </summary>
public static class DysonA2uiCatalog
{
    private static readonly string[] Justify = ["center", "end", "spaceAround", "spaceBetween", "spaceEvenly", "start", "stretch"];
    private static readonly string[] Align = ["start", "center", "end", "stretch"];

    public static IReadOnlyDictionary<string, DysonA2uiPropSpec> AccessibilityProps { get; } = Props(
        ("label", new(DysonA2uiPropKind.DynamicString)),
        ("description", new(DysonA2uiPropKind.DynamicString)),
        ("live", new(DysonA2uiPropKind.Enum, Values: ["off", "polite", "assertive"])),
        ("hidden", new(DysonA2uiPropKind.DynamicBoolean)));

    public static IReadOnlyDictionary<string, DysonA2uiPropSpec> ActionProps { get; } = Props(
        ("event", new(DysonA2uiPropKind.Event, Required: true)));

    public static IReadOnlyDictionary<string, DysonA2uiPropSpec> EventProps { get; } = Props(
        ("name", new(DysonA2uiPropKind.Identifier, Required: true)),
        ("context", new(DysonA2uiPropKind.DynamicMap)),
        ("userMessage", new(DysonA2uiPropKind.DynamicString)));

    public static IReadOnlyDictionary<string, DysonA2uiPropSpec> CheckProps { get; } = Props(
        ("condition", new(DysonA2uiPropKind.Condition, Required: true)),
        ("message", new(DysonA2uiPropKind.LiteralString)));

    public static IReadOnlyDictionary<string, DysonA2uiPropSpec> OptionProps { get; } = Props(
        ("label", new(DysonA2uiPropKind.DynamicString, Required: true)),
        ("value", new(DysonA2uiPropKind.LiteralString, Required: true)));

    public static IReadOnlyDictionary<string, DysonA2uiPropSpec> TemplateProps { get; } = Props(
        ("componentId", new(DysonA2uiPropKind.Identifier, Required: true)),
        ("path", new(DysonA2uiPropKind.Pointer, Required: true)));

    // Shared prop specs. Declared before Components: static initialisers run in textual order.
    private static readonly string[] Tones = ["default", "positive", "negative", "warning", "info", "accent", "secondary", "success", "danger", "discovery"];
    private static readonly string[] BehaviourNames = ["onVisible", "onClick", "onAction", "onOverlayClick", "onFollowUp", "functionCall", "actionOverlay", "lightbox", "hoverZoom", "streaming"];

    /// <summary>Upstream style props: accepted when scalar, never rendered, never listed in accepted-props errors.</summary>
    private static readonly string[] CosmeticNames =
    [
        "background", "border", "borderColor", "radius", "shadow", "clip", "flush", "width", "height", "minWidth", "maxWidth",
        "minHeight", "maxHeight", "minSize", "maxSize", "aspectRatio", "color", "fontWeight", "textAlign", "size", "role",
        "inline", "scrollable", "disableAutoSpacing", "pill", "block", "strokeWeight", "currency",
    ];

    private static readonly DysonA2uiPropSpec Cosmetic = new(DysonA2uiPropKind.Cosmetic);
    private static readonly DysonA2uiPropSpec Txt = new(DysonA2uiPropKind.DynamicString);
    private static readonly DysonA2uiPropSpec TxtReq = new(DysonA2uiPropKind.DynamicString, Required: true);
    private static readonly DysonA2uiPropSpec Any = new(DysonA2uiPropKind.DynamicAny);
    private static readonly DysonA2uiPropSpec AnyReq = new(DysonA2uiPropKind.DynamicAny, Required: true);
    private static readonly DysonA2uiPropSpec Flag = new(DysonA2uiPropKind.DynamicBoolean);
    private static readonly DysonA2uiPropSpec Num = new(DysonA2uiPropKind.DynamicNumber);
    private static readonly DysonA2uiPropSpec NumReq = new(DysonA2uiPropKind.DynamicNumber, Required: true);
    private static readonly DysonA2uiPropSpec Link = new(DysonA2uiPropKind.LinkTarget);
    private static readonly DysonA2uiPropSpec LinkReq = new(DysonA2uiPropKind.LinkTarget, Required: true);
    private static readonly DysonA2uiPropSpec Media = new(DysonA2uiPropKind.MediaUrl);
    private static readonly DysonA2uiPropSpec MediaReq = new(DysonA2uiPropKind.MediaUrl, Required: true);
    private static readonly DysonA2uiPropSpec Ref = new(DysonA2uiPropKind.IdRef);
    private static readonly DysonA2uiPropSpec RefReq = new(DysonA2uiPropKind.IdRef, Required: true);
    private static readonly DysonA2uiPropSpec Lit = new(DysonA2uiPropKind.LiteralString);
    private static readonly DysonA2uiPropSpec LitReq = new(DysonA2uiPropKind.LiteralString, Required: true);
    private static readonly DysonA2uiPropSpec Kids = new(DysonA2uiPropKind.ChildList);
    private static readonly DysonA2uiPropSpec KidsReq = new(DysonA2uiPropKind.ChildList, Required: true);
    private static readonly DysonA2uiPropSpec Gap = Int(0, 12);
    private static readonly DysonA2uiPropSpec ToneSpec = new(DysonA2uiPropKind.Enum, Values: Tones);

    private static DysonA2uiPropSpec Int(int min, int max, bool required = false) =>
        new(DysonA2uiPropKind.BoundedInt, required, null, min, max);

    private static DysonA2uiPropSpec Enm(params string[] values) => new(DysonA2uiPropKind.Enum, Values: values);

    private static DysonA2uiPropSpec Strings(int max, bool required = false) =>
        new(DysonA2uiPropKind.DynamicStringList, required, MaxItems: max);

    private static DysonA2uiPropSpec Objects(IReadOnlyDictionary<string, DysonA2uiPropSpec>? item, int max, bool required = false) =>
        new(DysonA2uiPropKind.ObjectList, required, MaxItems: max, Item: item);

    private static DysonA2uiPropSpec StringsOrObjects(IReadOnlyDictionary<string, DysonA2uiPropSpec> item, int max, bool required = false) =>
        new(DysonA2uiPropKind.StringOrObjectList, required, MaxItems: max, Item: item);

    private static DysonA2uiPropSpec Rej(string reason) => new(DysonA2uiPropKind.Rejected, Values: [reason]);

    /// <summary>Components refused by name, with the reason shown to the agent.</summary>
    public static IReadOnlyDictionary<string, string> RejectedComponents { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Svg"] = "Svg is rejected: model-supplied SVG can carry script, foreignObject and external hrefs. Use Icon or Chart.",
        ["HtmlView"] = "HtmlView is rejected: raw HTML is not allowed in a surface. Use Markdown, Text or Table.",
        ["FollowUp"] = "FollowUp is not supported yet: use PostUserQuestion choices.",
        ["Treemap"] = "Treemap is rejected (needs a layout algorithm). Use a bar Chart or a Table.",
        ["SankeyChart"] = "SankeyChart is rejected (needs a layout algorithm). Use a bar Chart or a Table.",
        ["ScatterChart"] = "ScatterChart is not supported yet. Use a line Chart or a Table.",
        ["Heatmap"] = "Heatmap is not supported yet. Use a Table.",
        ["RadarChart"] = "RadarChart is not supported yet. Use a bar Chart.",
        ["CandlestickChart"] = "CandlestickChart is not supported yet. Use a line Chart or a Table.",
    };

    /// <summary>Catalog ids that map onto the Dyson catalog (a pasted CopilotKit playground surface).</summary>
    public static IReadOnlyList<string> CatalogAliases { get; } = [DysonA2uiProtocol.PlaygroundCatalogId];

    public static bool IsDysonCatalogId(string? catalogId) =>
        catalogId == DysonA2uiProtocol.DysonCatalogId || (catalogId is not null && CatalogAliases.Contains(catalogId));

    public const string UnknownComponentGroups =
        "layout (Row, Column, Box, Grid, Tabs, Section, Accordion...), text (Title, Heading, Markdown, Code, Badge, Link...), "
        + "inputs (TextField, Select, Slider, DateTimeInput...), data (Table, DataTable, Stat, Timeline, Progress...), charts (Chart, BarChart...), "
        + "media (Image, Icon, Video...), feedback (Popover, Modal, Tooltip...), composites (LinkCard, EntityCard...); Dyson catalog: ActionButton, QuestionSlot, LocalVideo, Model3D, PipelineRun, SubagentTasks, TreeDiagram";

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> StatItem = Props(
        ("label", TxtReq), ("value", AnyReq), ("delta", Txt), ("trend", Enm("up", "down", "flat")),
        ("tone", ToneSpec), ("caption", Txt), ("icon", Txt));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> KeyValueItem = Props(
        ("label", TxtReq), ("value", AnyReq), ("hint", Txt));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> TimelineItem = Props(
        ("title", TxtReq), ("time", Txt), ("description", Txt), ("status", Txt), ("icon", Txt));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> PersonItem = Props(
        ("name", TxtReq), ("image", Media));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> AgendaEvent = Props(
        ("time", Txt), ("title", TxtReq), ("description", Txt), ("location", Txt));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> AgendaDay = Props(
        ("date", TxtReq), ("label", Txt), ("events", Objects(AgendaEvent, 50)));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> TabItem = Props(
        ("title", TxtReq), ("child", RefReq));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> AccordionItem = Props(
        ("title", TxtReq), ("text", Txt), ("child", Ref));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> SelectOption = Props(
        ("value", TxtReq), ("label", Txt), ("description", Txt));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> SliderMark = Props(
        ("value", NumReq), ("label", Txt));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> TableColumn = Props(
        ("label", Txt), ("align", Enm("start", "center", "end", "left", "right")), ("componentId", Ref));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> DataColumn = Props(
        ("key", LitReq), ("label", TxtReq), ("format", Enm("number", "currency", "percent", "compact", "date")),
        ("align", Enm("start", "center", "end", "left", "right")));

    public static IReadOnlyDictionary<string, DysonA2uiPropSpec> CellProps { get; } = Props(("componentId", new(DysonA2uiPropKind.Identifier, Required: true)));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> ComparisonRow = Props(("feature", TxtReq), ("values", AnyReq));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> ChartSeriesItem = Props(
        ("key", LitReq), ("label", Txt), ("color", Cosmetic));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> MapMarker = Props(
        ("label", TxtReq), ("lat", new(DysonA2uiPropKind.BoundedNumber, true, null, -90, 90)),
        ("lng", new(DysonA2uiPropKind.BoundedNumber, true, null, -180, 180)), ("category", Txt), ("description", Txt));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> GalleryImage = Props(
        ("url", Media), ("caption", Txt), ("alt", Txt), ("title", Txt), ("description", Txt));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> EntityAction = Props(
        ("label", TxtReq), ("href", LinkReq));

    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> TreeTag = Props(
        ("label", LitReq), ("color", Enm("red", "green", "accent")));

    /// <summary>TreeDiagram node: <c>children</c> points back at this dictionary, so one spec validates a tree of any depth (capped by the tree rule).</summary>
    private static readonly IReadOnlyDictionary<string, DysonA2uiPropSpec> TreeNode = BuildTreeNode();

    private static Dictionary<string, DysonA2uiPropSpec> BuildTreeNode()
    {
        var node = new Dictionary<string, DysonA2uiPropSpec>(StringComparer.Ordinal)
        {
            ["id"] = new(DysonA2uiPropKind.Identifier, Required: true),
            ["title"] = LitReq,
            ["text"] = Lit,
            ["tags"] = Objects(TreeTag, DysonA2uiTreeDiagram.MaxTags),
        };
        node["children"] = Objects(node, DysonA2uiTreeDiagram.MaxNodes);
        return node;
    }

    public static IReadOnlyDictionary<string, DysonA2uiComponentSpec> Components { get; } = BuildComponents();

    private static Dictionary<string, DysonA2uiComponentSpec> BuildComponents()
    {
        var d = new Dictionary<string, DysonA2uiComponentSpec>(StringComparer.Ordinal)
        {
            ["Text"] = Kit(false,
                ("text", new(DysonA2uiPropKind.DynamicString, Required: true)),
                ("variant", new(DysonA2uiPropKind.Enum, Values: ["caption", "body", "h1", "h2", "h3", "h4", "h5"]))),
            ["Row"] = Kit(false,
                ("children", new(DysonA2uiPropKind.ChildList, Required: true)),
                ("justify", new(DysonA2uiPropKind.Enum, Values: Justify)),
                ("align", new(DysonA2uiPropKind.Enum, Values: Align)),
                ("gap", Gap), ("padding", Gap)),
            ["Column"] = Kit(false,
                ("children", new(DysonA2uiPropKind.ChildList, Required: true)),
                ("justify", new(DysonA2uiPropKind.Enum, Values: Justify)),
                ("align", new(DysonA2uiPropKind.Enum, Values: Align)),
                ("gap", Gap), ("padding", Gap)),
            ["List"] = Kit(false,
                ("children", new(DysonA2uiPropKind.ChildList, Required: true)),
                ("direction", new(DysonA2uiPropKind.Enum, Values: ["vertical", "horizontal"])),
                ("align", new(DysonA2uiPropKind.Enum, Values: Align)),
                ("gap", Gap), ("padding", Gap)),
            // Card: exactly one of child / children (checked by the validator at the component pointer).
            ["Card"] = Kit(false,
                ("child", new(DysonA2uiPropKind.Identifier)),
                ("children", Kids), ("gap", Gap), ("padding", Gap)),
            ["Divider"] = Kit(false,
                ("axis", new(DysonA2uiPropKind.Enum, Values: ["horizontal", "vertical"]))),
            ["Button"] = Kit(true,
                ("child", new(DysonA2uiPropKind.Identifier, Required: true)),
                ("action", new(DysonA2uiPropKind.Action, Required: true)),
                ("href", Rej("Button.href is not supported: use Link or ActionButton.")),
                ("variant", new(DysonA2uiPropKind.Enum, Values: ["default", "primary", "borderless"]))),
            ["TextField"] = Kit(true,
                ("label", new(DysonA2uiPropKind.DynamicString, Required: true)),
                ("value", new(DysonA2uiPropKind.DynamicString, Required: true)),
                ("placeholder", new(DysonA2uiPropKind.DynamicString)),
                ("variant", new(DysonA2uiPropKind.Enum, Values: ["longText", "number", "shortText", "obscured"]))),
            ["CheckBox"] = Kit(true,
                ("label", new(DysonA2uiPropKind.DynamicString, Required: true)),
                ("value", new(DysonA2uiPropKind.DynamicBoolean, Required: true))),
            [DysonA2uiLocalVideo.ComponentName] = Dyson(
                ("path", new(DysonA2uiPropKind.Label, Required: true)),
                ("title", new(DysonA2uiPropKind.DynamicString))),
            [DysonA2uiModel3D.ComponentName] = Dyson(
                ("path", new(DysonA2uiPropKind.Label, Required: true)),
                ("title", new(DysonA2uiPropKind.DynamicString)),
                ("height", Int(160, 800))),
            ["ChoicePicker"] = Kit(true,
                ("options", new(DysonA2uiPropKind.Options, Required: true)),
                ("value", new(DysonA2uiPropKind.DynamicStringList, Required: true)),
                ("label", new(DysonA2uiPropKind.DynamicString)),
                ("variant", new(DysonA2uiPropKind.Enum, Values: ["multipleSelection", "mutuallyExclusive"])),
                ("displayStyle", new(DysonA2uiPropKind.Enum, Values: ["checkbox", "chips"])),
                ("filterable", new(DysonA2uiPropKind.DeferredFlag))),
            ["QuestionSlot"] = Dyson(
                ("questionId", new(DysonA2uiPropKind.QuestionId, Required: true))),
            ["ActionButton"] = Dyson(
                ("label", new(DysonA2uiPropKind.Label, Required: true)),
                ("func", new(DysonA2uiPropKind.BuiltInFunc, Required: true))),
            // PipelineRun: exactly one of runId / jobIds (checked by the validator at the component pointer).
            ["PipelineRun"] = Dyson(
                ("runId", new(DysonA2uiPropKind.PositiveLong)),
                ("jobIds", new(DysonA2uiPropKind.PositiveLongList, MaxItems: 30))),
            [DysonA2uiSubagentTasks.ComponentName] = Dyson(
                ("agentId", new(DysonA2uiPropKind.BoundedInt, Required: true, Min: 1, Max: int.MaxValue))),
            [DysonA2uiTreeDiagram.ComponentName] = Dyson(
                ("nodes", Objects(TreeNode, DysonA2uiTreeDiagram.MaxNodes, true)),
                ("flow", Enm("vertical", "horizontal")),
                ("title", Txt)),
        };

        AddLayout(d);
        AddText(d);
        AddInputs(d);
        AddData(d);
        AddMedia(d);
        AddFeedback(d);
        return d;
    }

    private static void AddLayout(Dictionary<string, DysonA2uiComponentSpec> d)
    {
        d["Box"] = Kit(false, ("children", KidsReq), ("direction", Enm("col", "column", "row")), ("gap", Gap), ("padding", Gap),
            ("align", Enm(Align)), ("justify", Enm(Justify)), ("wrap", Flag));
        d["Grid"] = Kit(false, ("children", KidsReq), ("columns", Int(1, DysonA2uiProtocol.MaxGridColumns)),
            ("minChildWidth", Int(40, 600)), ("gap", Gap), ("padding", Gap));
        d["Flow"] = Kit(false, ("children", KidsReq), ("minChildWidth", Int(40, 600)), ("gap", Gap), ("padding", Gap));
        foreach (var item in new[] { "GridItem", "FlowItem" })
        {
            d[item] = Kit(false, ("child", new(DysonA2uiPropKind.Identifier)), ("children", Kids),
                ("span", Int(1, 12)), ("rowSpan", Int(1, 12)));
        }

        d["Carousel"] = Kit(false, ("children", KidsReq), ("visibleItems", Int(1, 6)), ("activeIndex", Num), ("gap", Gap));
        d["CarouselItem"] = Kit(false, ("child", new(DysonA2uiPropKind.Identifier)), ("children", Kids));
        d["CarouselMediaItem"] = Kit(false, ("url", Media), ("caption", Txt), ("alt", Txt), ("title", Txt), ("description", Txt));
        d["Spacer"] = Kit(false, ("size", Int(1, 12)));
        d["Tabs"] = Kit(false, ("tabs", Objects(TabItem, DysonA2uiProtocol.MaxTabs, true) with { Min = 1 }), ("selectedIndex", Num));
        d["Section"] = Kit(false, ("title", TxtReq), ("eyebrow", Txt), ("description", Txt), ("children", KidsReq), ("gap", Gap));
        d["Accordion"] = Kit(false, ("items", Objects(AccordionItem, 50, true) with { Min = 1 }), ("multiple", Flag));
    }

    private static void AddText(Dictionary<string, DysonA2uiComponentSpec> d)
    {
        d["Title"] = Kit(false, ("text", TxtReq), ("size", Enm("xs", "sm", "md", "lg", "xl", "2xl", "3xl")));
        d["Heading"] = Kit(false, ("text", TxtReq), ("level", Enm("1", "2", "3", "4")), ("eyebrow", Txt), ("subtitle", Txt));
        d["Caption"] = Kit(false, ("text", TxtReq));
        d["Label"] = Kit(false, ("text", TxtReq), ("for", Lit));
        foreach (var name in new[] { "Bold", "Italic", "Underline", "Strikethrough", "ShimmerText" })
            d[name] = Kit(false, ("text", TxtReq));
        d["Badge"] = Kit(false, ("text", TxtReq), ("variant", Enm("soft", "solid", "outline")), ("color", ToneSpec),
            ("size", Enm("sm", "md", "lg")));
        foreach (var name in new[] { "Blockquote", "Quote" })
            d[name] = Kit(false, ("text", TxtReq), ("author", Txt), ("source", Txt), ("attribution", Txt), ("cite", Txt));
        foreach (var name in new[] { "Code", "CodeBlock" })
            d[name] = Kit(false, ("code", Txt), ("text", Txt), ("language", Txt), ("title", Txt));
        d["Kbd"] = Kit(false, ("keys", Strings(8, true)), ("label", Txt));
        d["Markdown"] = Kit(false, ("text", Txt), ("content", Txt));
        d["Math"] = Kit(false, ("text", Txt), ("tex", Txt), ("display", Flag));
        d["Link"] = Kit(false, ("text", TxtReq), ("href", LinkReq));
        d["Tooltip"] = Kit(false, ("child", RefReq), ("text", Txt), ("tip", Txt));
        d["VideoEmbed"] = Kit(false, ("url", LinkReq), ("title", Txt), ("caption", Txt), ("thumbnail", Media));
        foreach (var name in new[] { "ImageGallery", "ImageGrid" })
            d[name] = Kit(false, ("images", Objects(GalleryImage, 50)), ("items", Objects(GalleryImage, 50)), ("columns", Int(1, 6)));
    }

    private static void AddInputs(Dictionary<string, DysonA2uiComponentSpec> d)
    {
        d["Pressable"] = Kit(true, ("child", new(DysonA2uiPropKind.Identifier, Required: true)),
            ("action", new(DysonA2uiPropKind.Action, Required: true)),
            ("href", Rej("Pressable.href is not supported: use Link or ActionButton.")));
        d["Form"] = Kit(false, ("children", KidsReq), ("title", Txt), ("gap", Gap));
        d["TextArea"] = Kit(true, ("label", TxtReq), ("value", TxtReq), ("placeholder", Txt), ("rows", Int(2, 12)));
        d["Select"] = Kit(true, ("label", Txt), ("options", StringsOrObjects(SelectOption, DysonA2uiProtocol.MaxOptions, true) with { Min = 1 }),
            ("value", TxtReq), ("placeholder", Txt));
        d["RadioGroup"] = Kit(true, ("label", Txt), ("options", StringsOrObjects(SelectOption, DysonA2uiProtocol.MaxOptions, true) with { Min = 1 }),
            ("value", TxtReq));
        d["SegmentedControl"] = Kit(true, ("label", Txt), ("options", StringsOrObjects(SelectOption, DysonA2uiProtocol.MaxOptions, true) with { Min = 1 }),
            ("value", TxtReq));
        d["Radio"] = Kit(true, ("label", TxtReq), ("value", TxtReq), ("option", LitReq));
        d["Slider"] = Kit(true, ("label", Txt), ("value", NumReq), ("min", new(DysonA2uiPropKind.BoundedNumber)),
            ("max", new(DysonA2uiPropKind.BoundedNumber)), ("step", new(DysonA2uiPropKind.BoundedNumber)), ("unit", Txt),
            ("marks", Objects(SliderMark, 12)));
        d["DateTimeInput"] = Kit(true, ("label", Txt), ("value", TxtReq), ("enableDate", Flag), ("enableTime", Flag),
            ("min", new(DysonA2uiPropKind.IsoDateTime)), ("max", new(DysonA2uiPropKind.IsoDateTime)));
    }

    private static void AddData(Dictionary<string, DysonA2uiComponentSpec> d)
    {
        d["ListItem"] = Kit(false, ("label", TxtReq), ("description", Txt), ("value", Any), ("icon", Txt), ("children", Kids),
            ("action", Rej("ListItem.action is not supported yet: use a Button.")));
        d["Table"] = Kit(false, ("columns", StringsOrObjects(TableColumn, DysonA2uiProtocol.MaxTableColumns)),
            ("rows", new(DysonA2uiPropKind.CellMatrix, MaxItems: DysonA2uiProtocol.MaxTableRows)), ("children", Kids),
            ("cellComponents", Kids), ("caption", Txt), ("emptyLabel", Txt));
        d["TableSection"] = Kit(false, ("children", KidsReq), ("header", Flag));
        d["TableRow"] = Kit(false, ("children", KidsReq), ("label", Txt), ("header", Flag));
        d["TableCell"] = Kit(false, ("text", Txt), ("child", RefReq with { Required = false }), ("colSpan", Int(1, 20)), ("rowSpan", Int(1, 20)));
        d["DataTable"] = Kit(false, ("columns", Objects(DataColumn, DysonA2uiProtocol.MaxTableColumns, true) with { Min = 1 }),
            ("rows", Objects(null, DysonA2uiProtocol.MaxTableRows, true)), ("title", Txt), ("searchable", Flag),
            ("pageSize", Int(1, 100)));
        d["ComparisonTable"] = Kit(false, ("options", Strings(8, true)), ("rows", Objects(ComparisonRow, 50, true)), ("title", Txt),
            ("highlight", Int(0, 7)));
        d["Stat"] = Kit(false, ("label", TxtReq), ("value", AnyReq), ("delta", Txt), ("trend", Enm("up", "down", "flat")),
            ("caption", Txt), ("tone", ToneSpec), ("icon", Txt));
        d["StatGroup"] = Kit(false, ("items", Objects(StatItem, 12, true) with { Min = 1 }), ("title", Txt));
        d["Avatar"] = Kit(false, ("name", TxtReq), ("image", Media), ("subtitle", Txt), ("size", Enm("sm", "md", "lg")),
            ("status", Enm("online", "away", "offline")));
        d["AvatarGroup"] = Kit(false, ("people", Objects(PersonItem, 50, true)), ("max", Int(1, 20)), ("label", Txt));
        d["KeyValueList"] = Kit(false, ("items", Objects(KeyValueItem, DysonA2uiProtocol.MaxListItems, true)), ("title", Txt),
            ("columns", Int(1, 3)));
        d["Timeline"] = Kit(false, ("items", Objects(TimelineItem, DysonA2uiProtocol.MaxListItems, true)), ("title", Txt));
        d["Progress"] = Kit(false, ("value", NumReq), ("label", Txt), ("caption", Txt), ("tone", ToneSpec));
        d["RadialProgress"] = Kit(false, ("value", NumReq), ("label", Txt), ("display", Txt), ("tone", ToneSpec));
        d["Rating"] = Kit(false, ("value", NumReq), ("count", Num), ("label", Txt));
        d["TagList"] = Kit(false, ("tags", Strings(50, true)), ("tone", ToneSpec));
        d["Agenda"] = Kit(false, ("days", Objects(AgendaDay, 31, true)), ("title", Txt));
        d["Map"] = Kit(false, ("markers", Objects(MapMarker, DysonA2uiProtocol.MaxMapMarkers, true)), ("route", Flag), ("selected", Txt),
            ("title", Txt));

        var chartCommon = new (string, DysonA2uiPropSpec)[]
        {
            ("title", Txt), ("subtitle", Txt), ("height", Int(120, 480)), ("format", Enm("number", "currency", "percent", "compact")),
            ("unit", Txt), ("showLegend", Flag),
        };
        var chartData = new DysonA2uiPropSpec(DysonA2uiPropKind.ChartData, Required: true);
        d["Chart"] = Kit(false, [("data", chartData), ("chartType", Enm("bar", "line", "area", "pie", "donut")), ("xKey", Lit),
            ("series", StringsOrObjects(ChartSeriesItem, DysonA2uiProtocol.MaxChartSeries)), ("stacked", Flag),
            ("layout", Enm("vertical", "horizontal")), ("showDots", Flag), ("innerRadius", Cosmetic), .. chartCommon]);
        d["BarChart"] = Kit(false, [("data", chartData), ("stacked", Flag), ("layout", Enm("vertical", "horizontal")), .. chartCommon]);
        d["LineChart"] = Kit(false, [("data", chartData), ("showDots", Flag), .. chartCommon]);
        d["AreaChart"] = Kit(false, [("data", chartData), ("stacked", Flag), .. chartCommon]);
        d["PieChart"] = Kit(false, [("data", chartData), ("innerRadius", Cosmetic), .. chartCommon]);
        d["Sparkline"] = Kit(false, [("data", chartData), .. chartCommon]);
        d["Gauge"] = Kit(false, ("value", NumReq), ("min", Num), ("max", Num), ("label", Txt), ("unit", Txt), ("title", Txt),
            ("subtitle", Txt), ("tone", ToneSpec), ("format", Enm("number", "currency", "percent", "compact")));
    }

    private static void AddMedia(Dictionary<string, DysonA2uiComponentSpec> d)
    {
        // Image: exactly one of url / path (checked by the validator at the component pointer). path is a Dyson extension prop.
        d["Image"] = Kit(false, ("url", Media), ("path", new DysonA2uiPropSpec(DysonA2uiPropKind.Label)), ("height", Int(120, 800)), ("description", Txt), ("fallback", Txt), ("alt", Txt), ("caption", Txt),
            ("variant", Enm("icon", "avatar", "small", "medium", "large", "header")));
        d["Icon"] = Kit(false, ("name", TxtReq), ("label", Txt),
            ("svgPath", Rej("Icon.svgPath is rejected: model-supplied SVG is not allowed. Use a built-in icon name.")));
        d["Favicon"] = Kit(false, ("url", Media), ("domain", Lit));
        foreach (var name in new[] { "Video", "AudioPlayer" })
            d[name] = Kit(false, ("url", LinkReq), ("title", Txt), ("caption", Txt));
        d["LinkCard"] = Kit(false, ("url", LinkReq), ("title", TxtReq), ("description", Txt), ("image", Media), ("favicon", Media),
            ("siteName", Txt));
        d["Citation"] = Kit(false, ("label", Txt), ("index", Num), ("title", Txt), ("snippet", Txt), ("source", Txt), ("url", Link));
        d["EntityCard"] = Kit(false, ("title", TxtReq), ("subtitle", Txt), ("description", Txt), ("meta", Txt), ("badge", Txt),
            ("rating", Num), ("image", Media), ("href", Link), ("actions", Objects(EntityAction, 3)));
    }

    private static void AddFeedback(Dictionary<string, DysonA2uiComponentSpec> d)
    {
        d["PulseIndicator"] = Kit(false, ("tone", ToneSpec), ("label", Txt));
        d["FlashcardFlip"] = Kit(false, ("front", TxtReq), ("back", TxtReq), ("hint", Txt));
        d["Popover"] = Kit(false, ("children", KidsReq), ("open", Flag));
        foreach (var name in new[] { "PopoverTrigger", "PopoverContent" })
            d[name] = Kit(false, ("child", new(DysonA2uiPropKind.Identifier)), ("children", Kids));
        d["Modal"] = Kit(false, ("trigger", RefReq), ("content", RefReq), ("title", Txt), ("maxWidth", Int(240, 960)), ("open", Flag));
    }

    /// <summary>formatString supports only <c>${/abs}</c> / <c>${rel}</c> path placeholders.</summary>
    public static IReadOnlyDictionary<string, DysonA2uiFunctionSpec> KitFunctions { get; } =
        new Dictionary<string, DysonA2uiFunctionSpec>(StringComparer.Ordinal)
        {
            ["formatString"] = new(true, Props(("value", new(DysonA2uiPropKind.DynamicString, Required: true)))),
            ["required"] = new(false, Props(("value", new(DysonA2uiPropKind.DynamicAny, Required: true)))),
            ["length"] = new(false, Props(
                ("value", new(DysonA2uiPropKind.DynamicString, Required: true)),
                ("min", new(DysonA2uiPropKind.NonNegativeInt)),
                ("max", new(DysonA2uiPropKind.NonNegativeInt)))),
            ["email"] = new(false, Props(("value", new(DysonA2uiPropKind.DynamicString, Required: true)))),
            ["not"] = new(false, Props(("value", new(DysonA2uiPropKind.DynamicBoolean, Required: true)))),
        };

    /// <summary>The single URL allowlist hook. Empty: no v1 kit property is a URL render target.</summary>
    public static IReadOnlyList<string> AllowedUrlPrefixes { get; } = [];

    public static bool IsSupportedCatalogId(string? catalogId) =>
        catalogId is DysonA2uiProtocol.BasicCatalogId or DysonA2uiProtocol.DysonCatalogId
        || (catalogId is not null && CatalogAliases.Contains(catalogId));

    /// <summary>Accepted prop names for an error message: cosmetic and rejected props are left out.</summary>
    public static string AcceptedProps(IReadOnlyDictionary<string, DysonA2uiPropSpec> props) =>
        string.Join(", ", props.Where(p => p.Value.Kind is not (DysonA2uiPropKind.Cosmetic or DysonA2uiPropKind.Rejected) && p.Key[0] != '@' && p.Key != "weight").Select(p => p.Key));

    private static DysonA2uiComponentSpec Kit(bool checkable, params (string Name, DysonA2uiPropSpec Spec)[] props)
    {
        var all = props.ToList();
        all.Add(("@accessibility", new(DysonA2uiPropKind.Accessibility)));
        all.Add(("weight", new(DysonA2uiPropKind.Number)));
        if (checkable)
            all.Add(("@checks", new(DysonA2uiPropKind.Checks)));
        var known = new HashSet<string>(all.Select(p => p.Name), StringComparer.Ordinal);
        foreach (var name in BehaviourNames)
        {
            if (known.Add(name))
                all.Add((name, Rej($"{name} is not supported: surfaces send events only through Button/Pressable on a question-bound surface.")));
        }

        foreach (var name in CosmeticNames)
        {
            if (known.Add(name))
                all.Add((name, Cosmetic));
        }

        return new DysonA2uiComponentSpec(false, Props([.. all]));
    }

    private static DysonA2uiComponentSpec Dyson(params (string Name, DysonA2uiPropSpec Spec)[] props) =>
        new(true, Props([.. props, ("@accessibility", new(DysonA2uiPropKind.Accessibility)), ("weight", new(DysonA2uiPropKind.Number))]));

    private static IReadOnlyDictionary<string, DysonA2uiPropSpec> Props(params (string Name, DysonA2uiPropSpec Spec)[] props) =>
        props.ToDictionary(p => p.Name, p => p.Spec, StringComparer.Ordinal);
}
