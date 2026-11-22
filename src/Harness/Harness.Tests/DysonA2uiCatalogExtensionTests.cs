using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>Plan 364 catalog extension: add-1 components, rejections, aliases, link/media policy, tree rules, PipelineRun.</summary>
public class DysonA2uiCatalogExtensionTests
{
    private const string Dyson = DysonA2uiProtocol.DysonCatalogId;
    private const string P0 = "/messages/0/createSurface/components/0";

    private static string Create(string components, string extra = "") => DysonA2uiValidatorTests.Create(components, extra, "s1");

    private static Result<IReadOnlyList<DysonA2uiMessage>, DysonA2uiError> ParseOne(string component, string extra = "") =>
        DysonA2uiValidator.Parse(Create("{\"id\":\"root\"," + component + "}", extra));

    private static string CatalogExtra(string id) => ",\"catalogId\":\"" + id + "\"";

    private static Result<DysonA2uiSurface?, DysonA2uiError> Apply(string components, Guid? questionId = null, string extra = "")
    {
        var parsed = DysonA2uiValidator.Parse(Create(components, extra));
        Assert.True(parsed.IsSuccess, parsed.IsError ? parsed.Error.ToWireJson() : null);
        return DysonA2uiSurfaces.Apply(null, questionId, parsed.Value);
    }

    private static void AssertFails(Result<IReadOnlyList<DysonA2uiMessage>, DysonA2uiError> result, string path)
    {
        Assert.True(result.IsError);
        Assert.Equal(DysonA2uiProtocol.ValidationFailed, result.Error.Code);
        Assert.Equal(path, result.Error.Path);
    }

    private static void AssertTreeFails(Result<DysonA2uiSurface?, DysonA2uiError> result, string pathEnd)
    {
        Assert.True(result.IsError);
        Assert.Equal(DysonA2uiProtocol.ValidationFailed, result.Error.Code);
        Assert.EndsWith(pathEnd, result.Error.Path);
    }

    // Minimal valid body (without id/component) for every add-1 component. A new catalog component
    // without a row here fails ComponentTable_covers_the_whole_catalog.
    public static readonly Dictionary<string, string> Minimal = new(StringComparer.Ordinal)
    {
        ["Box"] = "\"children\":[]",
        ["Grid"] = "\"children\":[],\"columns\":3,\"gap\":4",
        ["GridItem"] = "\"span\":2",
        ["Flow"] = "\"children\":[]",
        ["FlowItem"] = "\"span\":2",
        ["Carousel"] = "\"children\":[],\"visibleItems\":2",
        ["CarouselItem"] = "",
        ["CarouselMediaItem"] = "\"url\":\"https://example.com/a.png\",\"caption\":\"c\"",
        ["Spacer"] = "\"size\":2",
        ["Tabs"] = "\"tabs\":[{\"title\":\"A\",\"child\":\"x\"}]",
        ["Section"] = "\"title\":\"S\",\"children\":[]",
        ["Accordion"] = "\"items\":[{\"title\":\"A\",\"text\":\"t\"}]",
        ["Title"] = "\"text\":\"T\"",
        ["Heading"] = "\"text\":\"H\",\"level\":\"2\",\"eyebrow\":\"e\",\"subtitle\":\"s\"",
        ["Caption"] = "\"text\":\"c\"",
        ["Label"] = "\"text\":\"l\"",
        ["Bold"] = "\"text\":\"b\"",
        ["Italic"] = "\"text\":\"i\"",
        ["Underline"] = "\"text\":\"u\"",
        ["Strikethrough"] = "\"text\":\"s\"",
        ["Badge"] = "\"text\":\"b\",\"variant\":\"soft\",\"color\":\"success\"",
        ["Blockquote"] = "\"text\":\"q\",\"author\":\"a\"",
        ["Quote"] = "\"text\":\"q\",\"author\":\"a\"",
        ["Code"] = "\"code\":\"x = 1\",\"language\":\"py\"",
        ["CodeBlock"] = "\"code\":\"x = 1\",\"language\":\"py\",\"title\":\"t\"",
        ["Kbd"] = "\"keys\":[\"Ctrl\",\"K\"]",
        ["Markdown"] = "\"text\":\"**b**\"",
        ["Math"] = "\"text\":\"x^2\"",
        ["ShimmerText"] = "\"text\":\"...\"",
        ["Link"] = "\"text\":\"Docs\",\"href\":\"https://example.com/docs\"",
        ["Tooltip"] = "\"child\":\"x\",\"text\":\"tip\"",
        ["VideoEmbed"] = "\"url\":\"https://example.com/v\",\"title\":\"V\"",
        ["ImageGallery"] = "\"images\":[{\"url\":\"https://example.com/a.png\",\"caption\":\"c\"}]",
        ["ImageGrid"] = "\"images\":[{\"caption\":\"c\"}]",
        ["Pressable"] = "\"child\":\"x\",\"action\":{\"event\":{\"name\":\"go\"}}",
        ["Form"] = "\"children\":[]",
        ["TextArea"] = "\"label\":\"L\",\"value\":{\"@path\":\"/v\"},\"rows\":4",
        ["Select"] = "\"options\":[\"a\",{\"value\":\"b\",\"label\":\"B\"}],\"value\":{\"@path\":\"/v\"},\"placeholder\":\"p\"",
        ["RadioGroup"] = "\"options\":[\"a\",\"b\"],\"value\":{\"@path\":\"/v\"}",
        ["SegmentedControl"] = "\"options\":[\"a\",\"b\"],\"value\":{\"@path\":\"/v\"}",
        ["Radio"] = "\"label\":\"L\",\"value\":{\"@path\":\"/v\"},\"option\":\"a\"",
        ["Slider"] = "\"value\":{\"@path\":\"/v\"},\"min\":0,\"max\":10,\"step\":1,\"unit\":\"x\",\"marks\":[{\"value\":5,\"label\":\"mid\"}]",
        ["DateTimeInput"] = "\"value\":{\"@path\":\"/v\"},\"enableDate\":true,\"min\":\"2026-01-01\",\"max\":\"2026-12-31\"",
        ["ListItem"] = "\"label\":\"L\",\"description\":\"d\",\"value\":\"v\"",
        ["Table"] = "\"columns\":[\"A\",{\"label\":\"B\",\"align\":\"end\"}],\"rows\":[[\"1\",2]],\"caption\":\"c\"",
        ["TableSection"] = "\"children\":[],\"header\":true",
        ["TableRow"] = "\"children\":[]",
        ["TableCell"] = "\"text\":\"t\",\"colSpan\":2",
        ["DataTable"] = "\"columns\":[{\"key\":\"a\",\"label\":\"A\",\"format\":\"number\"}],\"rows\":[{\"a\":1}],\"searchable\":true,\"pageSize\":10",
        ["ComparisonTable"] = "\"options\":[\"A\",\"B\"],\"rows\":[{\"feature\":\"f\",\"values\":[true,\"x\"]}],\"highlight\":0",
        ["Stat"] = "\"label\":\"L\",\"value\":\"1\",\"delta\":\"+1%\",\"trend\":\"up\",\"tone\":\"positive\",\"icon\":\"star\"",
        ["StatGroup"] = "\"items\":[{\"label\":\"L\",\"value\":3}]",
        ["Avatar"] = "\"name\":\"Ada Lovelace\",\"image\":\"https://example.com/a.png\",\"status\":\"online\"",
        ["AvatarGroup"] = "\"people\":[{\"name\":\"Ada\"}],\"max\":3",
        ["KeyValueList"] = "\"items\":[{\"label\":\"k\",\"value\":\"v\"}],\"columns\":2",
        ["Timeline"] = "\"items\":[{\"title\":\"t\",\"status\":\"done\"}]",
        ["Progress"] = "\"value\":50,\"label\":\"p\"",
        ["RadialProgress"] = "\"value\":50,\"display\":\"50\"",
        ["Rating"] = "\"value\":4.5,\"count\":10",
        ["TagList"] = "\"tags\":[\"a\",\"b\"]",
        ["Agenda"] = "\"days\":[{\"date\":\"2026-11-22\",\"events\":[{\"time\":\"10:00\",\"title\":\"t\"}]}]",
        ["Map"] = "\"markers\":[{\"label\":\"A\",\"lat\":1.5,\"lng\":2.5}],\"route\":true",
        ["Chart"] = "\"chartType\":\"bar\",\"xKey\":\"k\",\"series\":[\"v\"],\"data\":[{\"k\":\"a\",\"v\":1}]",
        ["BarChart"] = "\"data\":[{\"x\":\"a\",\"y\":1}],\"stacked\":true",
        ["LineChart"] = "\"data\":[{\"x\":\"a\",\"y\":1}],\"showDots\":true",
        ["AreaChart"] = "\"data\":[{\"x\":\"a\",\"y\":1,\"series\":\"s\"}],\"format\":\"currency\"",
        ["PieChart"] = "\"data\":[{\"label\":\"a\",\"value\":1}]",
        ["Sparkline"] = "\"data\":[{\"x\":\"a\",\"y\":1}]",
        ["Gauge"] = "\"value\":40,\"min\":0,\"max\":100",
        ["Image"] = "\"url\":\"https://example.com/a.png\",\"description\":\"d\",\"variant\":\"small\"",
        ["Icon"] = "\"name\":\"check\"",
        ["Favicon"] = "\"domain\":\"example.com\"",
        ["Video"] = "\"url\":\"https://example.com/v\"",
        ["AudioPlayer"] = "\"url\":\"https://example.com/a\"",
        ["LinkCard"] = "\"url\":\"https://example.com\",\"title\":\"T\",\"image\":\"https://example.com/i.png\"",
        ["Citation"] = "\"label\":\"1\",\"url\":\"open_plan:3\"",
        ["EntityCard"] = "\"title\":\"T\",\"href\":\"https://example.com\",\"actions\":[{\"label\":\"Go\",\"href\":\"open_file:a.md\"}]",
        ["PulseIndicator"] = "\"tone\":\"success\"",
        ["FlashcardFlip"] = "\"front\":\"f\",\"back\":\"b\"",
        ["Popover"] = "\"children\":[]",
        ["PopoverTrigger"] = "",
        ["PopoverContent"] = "",
        ["Modal"] = "\"trigger\":\"x\",\"content\":\"y\",\"title\":\"t\",\"maxWidth\":480",
    };

    public static TheoryData<string> AddedNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Minimal.Keys)
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(AddedNames))]
    public void Every_added_component_validates_from_a_minimal_example(string name)
    {
        var body = Minimal[name];
        var result = DysonA2uiValidator.Parse(Create("{\"id\":\"root\",\"component\":\"" + name + "\"" + (body.Length == 0 ? "" : "," + body) + "}"));
        Assert.True(result.IsSuccess, result.IsError ? result.Error.ToWireJson() : null);
    }

    [Fact]
    public void ComponentTable_covers_the_whole_catalog()
    {
        var dysonOnly = new HashSet<string> { "ActionButton", "QuestionSlot", "PipelineRun", "LocalVideo" };
        var have = new HashSet<string> { "Text", "Row", "Column", "List", "Card", "Divider", "Button", "TextField", "CheckBox", "ChoicePicker" };
        foreach (var name in DysonA2uiCatalog.Components.Keys)
        {
            if (dysonOnly.Contains(name) || have.Contains(name))
                continue;
            Assert.True(Minimal.ContainsKey(name), $"no minimal example for {name}");
            Assert.False(DysonA2uiCatalog.Components[name].DysonCatalog, name);
        }

        Assert.True(DysonA2uiCatalog.Components["PipelineRun"].DysonCatalog);
    }

    [Fact]
    public void Added_components_resolve_under_every_catalog_id_and_alias()
    {
        foreach (var extra in new[] { "", CatalogExtra(DysonA2uiProtocol.BasicCatalogId), CatalogExtra(Dyson), CatalogExtra(DysonA2uiProtocol.PlaygroundCatalogId) })
            Assert.True(ParseOne("\"component\":\"Heading\",\"text\":\"x\"", extra).IsSuccess, extra);
        AssertFails(ParseOne("\"component\":\"Heading\",\"text\":\"x\"", CatalogExtra("https://evil.example/c.json")), "/messages/0/createSurface/catalogId");
        Assert.True(DysonA2uiCatalog.IsSupportedCatalogId(DysonA2uiProtocol.PlaygroundCatalogId));
        Assert.False(DysonA2uiCatalog.IsSupportedCatalogId("https://copilotkit.ai/a2ui/catalogs/playground/v2"));
    }

    [Fact]
    public void Playground_alias_surface_is_a_Dyson_surface()
    {
        var ok = ParseOne("\"component\":\"ActionButton\",\"label\":\"Go\",\"func\":\"open_plan:1\"", CatalogExtra(DysonA2uiProtocol.PlaygroundCatalogId));
        Assert.True(ok.IsSuccess, ok.IsError ? ok.Error.ToWireJson() : null);
        var component = (JsonObject)ok.Value[0].Body["components"]![0]!;
        Assert.Equal(Dyson, (string?)component["@catalogId"]);
    }

    [Theory]
    [InlineData("\"component\":\"Svg\",\"markup\":\"<svg/>\"", "/component")]
    [InlineData("\"component\":\"HtmlView\",\"html\":\"<b/>\"", "/component")]
    [InlineData("\"component\":\"FollowUp\",\"message\":\"m\"", "/component")]
    [InlineData("\"component\":\"Treemap\"", "/component")]
    [InlineData("\"component\":\"SankeyChart\"", "/component")]
    [InlineData("\"component\":\"ScatterChart\"", "/component")]
    [InlineData("\"component\":\"Heatmap\"", "/component")]
    [InlineData("\"component\":\"RadarChart\"", "/component")]
    [InlineData("\"component\":\"CandlestickChart\"", "/component")]
    [InlineData("\"component\":\"Text\",\"text\":\"x\",\"onVisible\":{}", "/onVisible")]
    [InlineData("\"component\":\"Card\",\"child\":\"c\",\"onClick\":{}", "/onClick")]
    [InlineData("\"component\":\"Card\",\"child\":\"c\",\"onAction\":{}", "/onAction")]
    [InlineData("\"component\":\"Modal\",\"trigger\":\"a\",\"content\":\"b\",\"onOverlayClick\":{}", "/onOverlayClick")]
    [InlineData("\"component\":\"Text\",\"text\":\"x\",\"onFollowUp\":{}", "/onFollowUp")]
    [InlineData("\"component\":\"Text\",\"text\":\"x\",\"functionCall\":{}", "/functionCall")]
    [InlineData("\"component\":\"Button\",\"child\":\"c\",\"action\":{\"event\":{\"name\":\"g\"}},\"href\":\"https://x.example\"", "/href")]
    [InlineData("\"component\":\"Pressable\",\"child\":\"c\",\"action\":{\"event\":{\"name\":\"g\"}},\"href\":\"https://x.example\"", "/href")]
    [InlineData("\"component\":\"EntityCard\",\"title\":\"t\",\"actionOverlay\":true", "/actionOverlay")]
    [InlineData("\"component\":\"ImageGallery\",\"lightbox\":true", "/lightbox")]
    [InlineData("\"component\":\"ImageGrid\",\"hoverZoom\":true", "/hoverZoom")]
    [InlineData("\"component\":\"Markdown\",\"text\":\"x\",\"streaming\":true", "/streaming")]
    [InlineData("\"component\":\"Icon\",\"name\":\"check\",\"svgPath\":\"M0 0\"", "/svgPath")]
    [InlineData("\"component\":\"ListItem\",\"label\":\"l\",\"action\":{\"event\":{\"name\":\"g\"}}", "/action")]
    [InlineData("\"component\":\"Image\",\"url\":\"data:image/png;base64,AAAA\"", "/url")]
    [InlineData("\"component\":\"Image\",\"url\":{\"@path\":\"/u\"}", "/url")]
    [InlineData("\"component\":\"Link\",\"text\":\"x\",\"href\":\"javascript:alert(1)\"", "/href")]
    [InlineData("\"component\":\"Link\",\"text\":\"x\",\"href\":\"mailto:a@b.c\"", "/href")]
    [InlineData("\"component\":\"Link\",\"text\":\"x\",\"href\":\"/relative\"", "/href")]
    [InlineData("\"component\":\"Link\",\"text\":\"x\",\"href\":{\"@path\":\"/u\"}", "/href")]
    [InlineData("\"component\":\"EntityCard\",\"title\":\"t\",\"actions\":[{\"label\":\"a\",\"href\":\"javascript:x\"}]", "/actions/0/href")]
    [InlineData("\"component\":\"Text\",\"text\":\"x\",\"bogus\":1", "/bogus")]
    public void Rejections_fail_at_the_exact_pointer(string component, string suffix) =>
        AssertFails(ParseOne(component), P0 + suffix);

    [Fact]
    public void Rejection_messages_give_a_reason_and_unknown_names_list_the_groups()
    {
        Assert.Contains("Icon or Chart", ParseOne("\"component\":\"Svg\"").Error.Message);
        Assert.Contains("Markdown", ParseOne("\"component\":\"HtmlView\"").Error.Message);
        Assert.Contains("PostUserQuestion", ParseOne("\"component\":\"FollowUp\"").Error.Message);
        Assert.Contains("use Link or ActionButton", ParseOne("\"component\":\"Button\",\"child\":\"c\",\"action\":{\"event\":{\"name\":\"g\"}},\"href\":\"x\"").Error.Message);

        var unknown = ParseOne("\"component\":\"Nope\"").Error.Message;
        Assert.Contains("unknown component 'Nope'; see the dyson-a2ui skill", unknown);
        Assert.Contains("layout", unknown);

        var prop = ParseOne("\"component\":\"Text\",\"text\":\"x\",\"bogus\":1").Error.Message;
        Assert.Contains("accepted: text, variant", prop);
        Assert.DoesNotContain("background", prop);
    }

    [Fact]
    public void Cosmetic_props_are_accepted_when_scalar_and_short()
    {
        var ok = ParseOne("\"component\":\"Row\",\"children\":[],\"background\":\"#123456\",\"radius\":8,\"shadow\":true,\"width\":\"50%\",\"aspectRatio\":\"16/9\"");
        Assert.True(ok.IsSuccess, ok.IsError ? ok.Error.ToWireJson() : null);
        AssertFails(ParseOne("\"component\":\"Row\",\"children\":[],\"background\":{\"@path\":\"/c\"}"), P0 + "/background");
        AssertFails(ParseOne($"\"component\":\"Row\",\"children\":[],\"background\":\"{new string('x', DysonA2uiProtocol.MaxCosmeticStringLength + 1)}\""), P0 + "/background");
    }

    [Fact]
    public void Upstream_checks_is_renamed_to_at_checks_in_place()
    {
        var parsed = ParseOne("\"component\":\"TextField\",\"label\":\"L\",\"value\":{\"@path\":\"/v\"},\"checks\":[{\"condition\":{\"@call\":\"required\",\"args\":{\"value\":{\"@path\":\"/v\"}}},\"message\":\"m\"}]");
        Assert.True(parsed.IsSuccess, parsed.IsError ? parsed.Error.ToWireJson() : null);
        var component = (JsonObject)parsed.Value[0].Body["components"]![0]!;
        Assert.False(component.ContainsKey("checks"));
        Assert.NotNull(component["@checks"]);
    }

    [Fact]
    public void Row_Column_List_Card_gain_gap_padding_and_Card_children_with_exactly_one_of_child_children()
    {
        Assert.True(ParseOne("\"component\":\"Row\",\"children\":[],\"gap\":12,\"padding\":0").IsSuccess);
        AssertFails(ParseOne("\"component\":\"Column\",\"children\":[],\"gap\":13"), P0 + "/gap");
        AssertFails(ParseOne("\"component\":\"List\",\"children\":[],\"padding\":-1"), P0 + "/padding");
        Assert.True(ParseOne("\"component\":\"Card\",\"children\":[]").IsSuccess);
        Assert.True(ParseOne("\"component\":\"Card\",\"child\":\"x\"").IsSuccess);
        AssertFails(ParseOne("\"component\":\"Card\""), P0);
        AssertFails(ParseOne("\"component\":\"Card\",\"child\":\"x\",\"children\":[]"), P0);
        foreach (var variant in new[] { "h1", "h2", "h3", "h4", "h5", "caption", "body" })
            Assert.True(ParseOne($"\"component\":\"Text\",\"text\":\"x\",\"variant\":\"{variant}\"").IsSuccess, variant);
    }

    private static string Repeat(string item, int count) => string.Join(",", Enumerable.Repeat(item, count));

    [Fact]
    public void Chart_and_table_caps_fail_at_the_offending_index()
    {
        AssertFails(ParseOne($"\"component\":\"BarChart\",\"data\":[{Repeat("{\"x\":\"a\",\"y\":1}", 501)}]"), P0 + "/data/500");
        Assert.True(ParseOne($"\"component\":\"BarChart\",\"data\":[{Repeat("{\"x\":\"a\",\"y\":1}", 500)}]").IsSuccess);
        AssertFails(ParseOne($"\"component\":\"PieChart\",\"data\":[{Repeat("{\"label\":\"a\",\"value\":1}", 25)}]"), P0 + "/data/24");
        Assert.True(ParseOne($"\"component\":\"PieChart\",\"data\":[{Repeat("{\"label\":\"a\",\"value\":1}", 24)}]").IsSuccess);
        AssertFails(ParseOne($"\"component\":\"Chart\",\"chartType\":\"donut\",\"data\":[{Repeat("{\"label\":\"a\",\"value\":1}", 25)}]"), P0 + "/data/24");
        AssertFails(ParseOne($"\"component\":\"Table\",\"rows\":[{Repeat("[\"a\"]", 201)}]"), P0 + "/rows/200");
        AssertFails(ParseOne($"\"component\":\"Table\",\"columns\":[{Repeat("\"c\"", 21)}]"), P0 + "/columns/20");
        AssertFails(ParseOne($"\"component\":\"Table\",\"rows\":[[{Repeat("\"c\"", 21)}]]"), P0 + "/rows/0/20");
    }

    private static string LinkRow(int buttons, int links, bool pipeline = false)
    {
        var ids = new List<string>();
        var parts = new List<string>();
        for (var i = 0; i < buttons; i++)
        {
            ids.Add($"\"b{i}\"");
            parts.Add($"{{\"id\":\"b{i}\",\"component\":\"ActionButton\",\"label\":\"P\",\"func\":\"open_plan:{i + 1}\"}}");
        }

        for (var i = 0; i < links; i++)
        {
            ids.Add($"\"l{i}\"");
            parts.Add(i % 2 == 0
                ? $"{{\"id\":\"l{i}\",\"component\":\"Link\",\"text\":\"L\",\"href\":\"https://example.com/{i}\"}}"
                : $"{{\"id\":\"l{i}\",\"component\":\"LinkCard\",\"url\":\"https://example.com/{i}\",\"title\":\"T\"}}");
        }

        if (pipeline)
        {
            ids.Add("\"run\"");
            parts.Add("{\"id\":\"run\",\"component\":\"PipelineRun\",\"runId\":7}");
        }

        return $"{{\"id\":\"root\",\"component\":\"Row\",\"children\":[{string.Join(",", ids)}]}}," + string.Join(",", parts);
    }

    [Fact]
    public void Link_cap_counts_ActionButtons_LinkTargets_and_PipelineRuns_together()
    {
        var extra = CatalogExtra(Dyson);
        Assert.True(Apply(LinkRow(4, 4), extra: extra).IsSuccess);
        Assert.True(Apply(LinkRow(5, 4), extra: extra).IsError);
        Assert.True(Apply(LinkRow(7, 0, pipeline: true), extra: extra).IsSuccess);
        Assert.True(Apply(LinkRow(8, 0, pipeline: true), extra: extra).IsError);
    }

    [Fact]
    public void Link_funcs_are_normalised_in_place_and_listed_with_pointers()
    {
        var parsed = ParseOne("\"component\":\"Link\",\"text\":\"x\",\"href\":\"https://example.com/a\"");
        Assert.True(parsed.IsSuccess, parsed.IsError ? parsed.Error.ToWireJson() : null);
        var component = (JsonObject)parsed.Value[0].Body["components"]![0]!;
        Assert.Equal("open_url:https://example.com/a", (string?)component["href"]);
        var pointers = DysonA2uiValidator.LinkFuncPointers(parsed.Value);
        Assert.Equal([(P0 + "/href", "open_url:https://example.com/a")], pointers);
    }

    [Fact]
    public void Dangling_tab_child_and_modal_cycle_fail_the_tree_check()
    {
        AssertTreeFails(Apply("{\"id\":\"root\",\"component\":\"Tabs\",\"tabs\":[{\"title\":\"A\",\"child\":\"t\"},{\"title\":\"B\",\"child\":\"ghost\"}]},{\"id\":\"t\",\"component\":\"Text\",\"text\":\"x\"}"), "/tabs/1/child");
        Assert.True(Apply("{\"id\":\"root\",\"component\":\"Tabs\",\"tabs\":[{\"title\":\"A\",\"child\":\"t\"}]},{\"id\":\"t\",\"component\":\"Text\",\"text\":\"x\"}").IsSuccess);
        var cycle = Apply("{\"id\":\"root\",\"component\":\"Modal\",\"trigger\":\"t\",\"content\":\"m\"},{\"id\":\"t\",\"component\":\"Text\",\"text\":\"x\"},{\"id\":\"m\",\"component\":\"Modal\",\"trigger\":\"t\",\"content\":\"root\"}");
        Assert.True(cycle.IsError);
        Assert.Contains("cycle", cycle.Error.Message);
        AssertTreeFails(Apply("{\"id\":\"root\",\"component\":\"Modal\",\"trigger\":\"t\",\"content\":\"ghost\"},{\"id\":\"t\",\"component\":\"Text\",\"text\":\"x\"}"), "/content");
    }

    [Fact]
    public void Structure_rules_for_Popover_Table_Grid_are_enforced()
    {
        const string text = "{\"id\":\"x\",\"component\":\"Text\",\"text\":\"x\"}";
        Assert.True(Apply("{\"id\":\"root\",\"component\":\"Popover\",\"children\":[\"t\",\"c\"]},{\"id\":\"t\",\"component\":\"PopoverTrigger\",\"child\":\"x\"},{\"id\":\"c\",\"component\":\"PopoverContent\",\"child\":\"x\"}," + text).IsSuccess);
        AssertTreeFails(Apply("{\"id\":\"root\",\"component\":\"Popover\",\"children\":[\"c\",\"t\"]},{\"id\":\"t\",\"component\":\"PopoverTrigger\",\"child\":\"x\"},{\"id\":\"c\",\"component\":\"PopoverContent\",\"child\":\"x\"}," + text), "/children");
        AssertTreeFails(Apply("{\"id\":\"root\",\"component\":\"Row\",\"children\":[\"t\"]},{\"id\":\"t\",\"component\":\"PopoverTrigger\",\"child\":\"x\"}," + text), "/children/0");
        Assert.True(Apply("{\"id\":\"root\",\"component\":\"Table\",\"children\":[\"r\"]},{\"id\":\"r\",\"component\":\"TableRow\",\"children\":[\"c\"]},{\"id\":\"c\",\"component\":\"TableCell\",\"child\":\"x\"}," + text).IsSuccess);
        AssertTreeFails(Apply("{\"id\":\"root\",\"component\":\"Row\",\"children\":[\"r\"]},{\"id\":\"r\",\"component\":\"TableRow\",\"children\":[]}"), "/children/0");
        Assert.True(Apply("{\"id\":\"root\",\"component\":\"Grid\",\"columns\":2,\"children\":[\"g\"]},{\"id\":\"g\",\"component\":\"GridItem\",\"span\":2,\"child\":\"x\"}," + text).IsSuccess);
        AssertTreeFails(Apply("{\"id\":\"root\",\"component\":\"Row\",\"children\":[\"g\"]},{\"id\":\"g\",\"component\":\"GridItem\",\"child\":\"x\"}," + text), "/children/0");
        AssertTreeFails(Apply("{\"id\":\"root\",\"component\":\"GridItem\",\"child\":\"x\"}," + text), "");
    }

    [Fact]
    public void Pressable_needs_a_question_bound_surface_and_resolves_like_a_Button()
    {
        const string components = "{\"id\":\"root\",\"component\":\"Pressable\",\"child\":\"t\",\"action\":{\"event\":{\"name\":\"go\"}}},{\"id\":\"t\",\"component\":\"Text\",\"text\":\"x\"}";
        AssertTreeFails(Apply(components), "/action");
        var bound = Apply(components, Guid.NewGuid());
        Assert.True(bound.IsSuccess, bound.IsError ? bound.Error.ToWireJson() : null);
        var action = DysonA2uiAction.Resolve(bound.Value!, "root", new JsonObject(), DateTime.UtcNow);
        Assert.True(action.IsSuccess);
        Assert.Equal("go", action.Value.Name);
    }

    private const string Dashboard = """
        {"id":"root","component":"Column","children":["head","grid","area","channels_table","caption","channel_pie"]},
        {"id":"head","component":"Heading","text":"Revenue","level":"1","eyebrow":"Q4"},
        {"id":"grid","component":"Grid","columns":3,"gap":4,"children":["s1","s2"]},
        {"id":"s1","component":"Stat","label":"MRR","value":"$12k","delta":"+4%","trend":"up","tone":"positive","icon":"trending-up"},
        {"id":"s2","component":"Stat","label":"Churn","value":"1.2%","trend":"down","tone":"warning"},
        {"id":"area","component":"AreaChart","data":[{"x":"Jan","y":1,"series":"a"},{"x":"Feb","y":2,"series":"a"}],"stacked":true,"format":"number"},
        {"id":"channels_table","component":"Table","columns":["Channel",{"label":"Share","align":"end"}],"rows":[["Web",60],["Ads",40]]},
        {"id":"caption","component":"Caption","text":"Source: billing"},
        {"id":"channel_pie","component":"PieChart","data":[{"label":"Web","value":60},{"label":"Ads","value":40}]}
        """;

    [Fact]
    public void Dashboard_surface_under_the_playground_alias_applies_and_round_trips()
    {
        var applied = Apply(Dashboard, extra: CatalogExtra(DysonA2uiProtocol.PlaygroundCatalogId));
        Assert.True(applied.IsSuccess, applied.IsError ? applied.Error.ToWireJson() : null);

        var json = DysonA2uiSurfaceSerializer.Serialize(applied.Value);
        var back = DysonA2uiSurfaceSerializer.Deserialize(json);
        Assert.NotNull(back);
        Assert.Equal(applied.Value!.Components.Keys, back!.Components.Keys);
        Assert.Equal(json, DysonA2uiSurfaceSerializer.Serialize(back));
    }

    [Theory]
    [InlineData("\"runId\":1,\"jobIds\":[1]", "")]
    [InlineData("", "")]
    [InlineData("\"runId\":\"1\"", "/runId")]
    [InlineData("\"runId\":{\"@path\":\"/r\"}", "/runId")]
    [InlineData("\"runId\":0", "/runId")]
    [InlineData("\"jobIds\":[]", "/jobIds")]
    [InlineData("\"jobIds\":[1,1]", "/jobIds/1")]
    [InlineData("\"jobIds\":[1,0]", "/jobIds/1")]
    [InlineData("\"runId\":1,\"showJobs\":true", "/showJobs")]
    public void PipelineRun_validation_fails_at_the_exact_pointer(string props, string suffix) =>
        AssertFails(ParseOne("\"component\":\"PipelineRun\"" + (props.Length == 0 ? "" : "," + props), CatalogExtra(Dyson)), P0 + suffix);

    [Fact]
    public void PipelineRun_accepts_runId_or_jobIds_only_with_a_Dyson_catalog()
    {
        Assert.True(ParseOne("\"component\":\"PipelineRun\",\"runId\":5", CatalogExtra(Dyson)).IsSuccess);
        Assert.True(ParseOne("\"component\":\"PipelineRun\",\"jobIds\":[1,2,3]", CatalogExtra(DysonA2uiProtocol.PlaygroundCatalogId)).IsSuccess);
        AssertFails(ParseOne("\"component\":\"PipelineRun\",\"jobIds\":[" + string.Join(",", Enumerable.Range(1, 31)) + "]", CatalogExtra(Dyson)), P0 + "/jobIds");
        AssertFails(ParseOne("\"component\":\"PipelineRun\",\"runId\":5"), P0 + "/component");
    }

    [Fact]
    public void Chart_Normalize_pivots_both_shapes_and_counts_dropped_and_truncated()
    {
        var playground = DysonA2uiChart.Normalize(JsonNode.Parse("[{\"x\":\"a\",\"y\":1,\"series\":\"s\"},{\"x\":\"b\",\"y\":2,\"series\":\"s\"}]"));
        Assert.Equal(["a", "b"], playground.Categories);
        var only = Assert.Single(playground.Series);
        Assert.Equal("s", only.Key);
        Assert.Equal(new double?[] { 1, 2 }, only.Values);

        var standard = DysonA2uiChart.Normalize(JsonNode.Parse("[{\"k\":\"a\",\"v\":1,\"w\":2}]"), "k", [("v", "Vee"), ("w", "W")]);
        Assert.Equal(["v", "w"], standard.Series.Select(s => s.Key));
        Assert.Equal("Vee", standard.Series[0].Label);

        var bad = DysonA2uiChart.Normalize(JsonNode.Parse("[{\"x\":\"a\",\"y\":\"NaN\"}]"));
        Assert.Equal(1, bad.Dropped);
        Assert.True(bad.IsEmpty);

        var pie = DysonA2uiChart.Normalize(JsonNode.Parse("[" + string.Join(",", Enumerable.Range(0, 30).Select(i => $"{{\"label\":\"s{i}\",\"value\":1}}")) + "]"), pie: true);
        Assert.Equal(DysonA2uiProtocol.MaxPieSlices, pie.Categories.Count);
        Assert.True(pie.Truncated);
        Assert.True(DysonA2uiChart.Normalize(null).IsEmpty);
    }
}
