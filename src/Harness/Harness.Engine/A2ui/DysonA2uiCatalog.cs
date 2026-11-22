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
}

public sealed record DysonA2uiPropSpec(DysonA2uiPropKind Kind, bool Required = false, IReadOnlyList<string>? Values = null);

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

    public static IReadOnlyDictionary<string, DysonA2uiComponentSpec> Components { get; } =
        new Dictionary<string, DysonA2uiComponentSpec>(StringComparer.Ordinal)
        {
            ["Text"] = Kit(false,
                ("text", new(DysonA2uiPropKind.DynamicString, Required: true)),
                ("variant", new(DysonA2uiPropKind.Enum, Values: ["caption", "body", "h5"]))),
            ["Row"] = Kit(false,
                ("children", new(DysonA2uiPropKind.ChildList, Required: true)),
                ("justify", new(DysonA2uiPropKind.Enum, Values: Justify)),
                ("align", new(DysonA2uiPropKind.Enum, Values: Align))),
            ["Column"] = Kit(false,
                ("children", new(DysonA2uiPropKind.ChildList, Required: true)),
                ("justify", new(DysonA2uiPropKind.Enum, Values: Justify)),
                ("align", new(DysonA2uiPropKind.Enum, Values: Align))),
            ["List"] = Kit(false,
                ("children", new(DysonA2uiPropKind.ChildList, Required: true)),
                ("direction", new(DysonA2uiPropKind.Enum, Values: ["vertical", "horizontal"])),
                ("align", new(DysonA2uiPropKind.Enum, Values: Align))),
            ["Card"] = Kit(false,
                ("child", new(DysonA2uiPropKind.Identifier, Required: true))),
            ["Divider"] = Kit(false,
                ("axis", new(DysonA2uiPropKind.Enum, Values: ["horizontal", "vertical"]))),
            ["Button"] = Kit(true,
                ("child", new(DysonA2uiPropKind.Identifier, Required: true)),
                ("action", new(DysonA2uiPropKind.Action, Required: true)),
                ("variant", new(DysonA2uiPropKind.Enum, Values: ["default", "primary", "borderless"]))),
            ["TextField"] = Kit(true,
                ("label", new(DysonA2uiPropKind.DynamicString, Required: true)),
                ("value", new(DysonA2uiPropKind.DynamicString, Required: true)),
                ("placeholder", new(DysonA2uiPropKind.DynamicString)),
                ("variant", new(DysonA2uiPropKind.Enum, Values: ["longText", "number", "shortText", "obscured"]))),
            ["CheckBox"] = Kit(true,
                ("label", new(DysonA2uiPropKind.DynamicString, Required: true)),
                ("value", new(DysonA2uiPropKind.DynamicBoolean, Required: true))),
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
        };

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
        catalogId is DysonA2uiProtocol.BasicCatalogId or DysonA2uiProtocol.DysonCatalogId;

    private static DysonA2uiComponentSpec Kit(bool checkable, params (string Name, DysonA2uiPropSpec Spec)[] props)
    {
        var all = props.ToList();
        all.Add(("@accessibility", new(DysonA2uiPropKind.Accessibility)));
        all.Add(("weight", new(DysonA2uiPropKind.Number)));
        if (checkable)
            all.Add(("@checks", new(DysonA2uiPropKind.Checks)));
        return new DysonA2uiComponentSpec(false, Props([.. all]));
    }

    private static DysonA2uiComponentSpec Dyson(params (string Name, DysonA2uiPropSpec Spec)[] props) =>
        new(true, Props([.. props, ("@accessibility", new(DysonA2uiPropKind.Accessibility)), ("weight", new(DysonA2uiPropKind.Number))]));

    private static IReadOnlyDictionary<string, DysonA2uiPropSpec> Props(params (string Name, DysonA2uiPropSpec Spec)[] props) =>
        props.ToDictionary(p => p.Name, p => p.Spec, StringComparer.Ordinal);
}
