#if DYSON_PREVIEW
using System.Net;
using System.Text;

namespace Harness.UI.Demo;

/// <summary>The ER diagram the demo's visualization drone "renders": the ledger plan's tables as inline SVG.</summary>
internal static class AesterSchemaVisualization
{
    private const int Width = 230;
    private const int Header = 34;
    private const int Row = 24;

    public static DysonDemoVisualization Create() => new("Aester ledger schema", BuildHtml(), Css, JavaScript: "");

    private static string BuildHtml()
    {
        var svg = new StringBuilder();
        svg.Append("<svg class=\"erd\" viewBox=\"0 0 900 440\" role=\"img\" aria-label=\"Aester ledger entity-relationship diagram\">");

        // Relations first so the entity boxes sit on top of the line ends.
        Relation(svg, "M260 86 C300 86 295 264 335 264", (266, 80), (320, 258));
        Relation(svg, "M640 86 C600 86 605 240 565 240", (628, 80), (571, 234));
        Relation(svg, "M145 202 L145 300", (151, 218), (151, 294));
        svg.Append("<path class=\"rel rel--shard\" d=\"M640 335 C600 335 605 264 565 264\"/>");

        Entity(svg, 30, 40, "account",
        [
            ("id", "uuid", "PK"),
            ("code", "text", null),
            ("kind", "text", null),
            ("currency", "char(3)", null),
            ("opened_at", "timestamptz", null),
        ]);
        Entity(svg, 640, 40, "journal_entry",
        [
            ("id", "uuid", "PK"),
            ("posted_at", "timestamptz", null),
            ("memo", "text", null),
            ("posted_by", "text", null),
        ]);
        Entity(svg, 335, 170, "journal_line",
        [
            ("id", "uuid", "PK"),
            ("entry_id", "uuid", "FK"),
            ("account_id", "uuid", "FK · SK"),
            ("debit", "numeric(18,2)", null),
            ("credit", "numeric(18,2)", null),
        ]);
        Entity(svg, 30, 300, "balance_snapshot",
        [
            ("account_id", "uuid", "FK"),
            ("as_of", "date", null),
            ("amount", "numeric(18,2)", null),
        ]);

        svg.Append("<text class=\"note\" x=\"450\" y=\"358\" text-anchor=\"middle\">Σ debit = Σ credit for every entry · lines immutable once posted</text>");

        svg.Append("<rect class=\"shard\" x=\"640\" y=\"280\" width=\"230\" height=\"110\" rx=\"10\"/>");
        svg.Append("<text class=\"shard__title\" x=\"656\" y=\"306\">Shard key (deferred)</text>");
        svg.Append("<text class=\"shard__body\" x=\"656\" y=\"330\">account_id keeps one account's</text>");
        svg.Append("<text class=\"shard__body\" x=\"656\" y=\"348\">whole history on one shard.</text>");
        svg.Append("<text class=\"shard__body shard__decision\" x=\"656\" y=\"374\">Decision: don't shard yet</text>");
        svg.Append("</svg>");

        return
            "<main class=\"page\">" +
            "<header><h1>Aester ledger schema</h1>" +
            "<p>PostgreSQL · double-entry journal · single database, sharding deferred</p></header>" +
            svg +
            "<footer><span><b>PK</b> primary key</span><span><b>FK</b> foreign key</span>" +
            "<span><b class=\"sk\">SK</b> candidate shard key</span><span><b>1 — N</b> one to many</span></footer>" +
            "</main>";
    }

    private static void Entity(StringBuilder svg, int x, int y, string name, (string Field, string Type, string? Tag)[] fields)
    {
        var height = Header + (fields.Length * Row) + 8;
        svg.Append($"<g class=\"entity\"><rect class=\"entity__box\" x=\"{x}\" y=\"{y}\" width=\"{Width}\" height=\"{height}\" rx=\"10\"/>");
        svg.Append($"<rect class=\"entity__head\" x=\"{x}\" y=\"{y}\" width=\"{Width}\" height=\"{Header}\" rx=\"10\"/>");
        svg.Append($"<rect class=\"entity__head\" x=\"{x}\" y=\"{y + Header - 10}\" width=\"{Width}\" height=\"10\"/>");
        svg.Append($"<text class=\"entity__name\" x=\"{x + 14}\" y=\"{y + 22}\">{WebUtility.HtmlEncode(name)}</text>");
        for (var i = 0; i < fields.Length; i++)
        {
            var (field, type, tag) = fields[i];
            var baseline = y + Header + (i * Row) + 17;
            var keyClass = tag is null ? "" : tag.Contains("SK", StringComparison.Ordinal) ? " field--shard" : " field--key";
            svg.Append($"<text class=\"field{keyClass}\" x=\"{x + 14}\" y=\"{baseline}\">{WebUtility.HtmlEncode(field)}</text>");
            if (tag is not null)
                svg.Append($"<text class=\"tag{keyClass}\" x=\"{x + 112}\" y=\"{baseline}\" text-anchor=\"middle\">{WebUtility.HtmlEncode(tag)}</text>");
            svg.Append($"<text class=\"type\" x=\"{x + Width - 14}\" y=\"{baseline}\" text-anchor=\"end\">{WebUtility.HtmlEncode(type)}</text>");
        }

        svg.Append("</g>");
    }

    private static void Relation(StringBuilder svg, string path, (int X, int Y) one, (int X, int Y) many)
    {
        svg.Append($"<path class=\"rel\" d=\"{path}\"/>");
        svg.Append($"<text class=\"card\" x=\"{one.X}\" y=\"{one.Y}\">1</text>");
        svg.Append($"<text class=\"card\" x=\"{many.X}\" y=\"{many.Y}\">N</text>");
    }

    private const string Css = """
        :root { color-scheme: dark; }
        html, body { margin: 0; background: #16161a; color: #e8e6e3; font: 13px/1.4 system-ui, "Segoe UI", sans-serif; }
        .page { padding: 16px 18px 12px; }
        header h1 { margin: 0; font-size: 16px; font-weight: 600; color: #e09b58; }
        header p { margin: 2px 0 10px; color: #9a968f; font-size: 12px; }
        .erd { width: 100%; height: auto; display: block; }
        .entity__box { fill: #212127; stroke: #3a3a44; stroke-width: 1; }
        .entity__head { fill: #2b2620; }
        .entity__name { fill: #e09b58; font: 600 13px ui-monospace, Consolas, monospace; }
        .field { fill: #e8e6e3; font: 12px ui-monospace, Consolas, monospace; }
        .type { fill: #8d8981; font: 11px ui-monospace, Consolas, monospace; }
        .tag { fill: #b9b4ab; font: 600 10px system-ui, sans-serif; letter-spacing: .04em; }
        .field--key { fill: #f0c08e; }
        .field--shard { fill: #e09b58; font-weight: 700; }
        .rel { fill: none; stroke: #6c6a74; stroke-width: 1.5; }
        .rel--shard { stroke: #e09b58; stroke-dasharray: 5 4; }
        .card { fill: #b9b4ab; font: 600 11px system-ui, sans-serif; }
        .note { fill: #9a968f; font: italic 11px system-ui, sans-serif; }
        .shard { fill: rgba(224, 155, 88, .08); stroke: #e09b58; stroke-dasharray: 5 4; }
        .shard__title { fill: #e09b58; font: 600 12px system-ui, sans-serif; }
        .shard__body { fill: #cfcac2; font: 11.5px system-ui, sans-serif; }
        .shard__decision { fill: #e8e6e3; font-weight: 600; }
        footer { display: flex; flex-wrap: wrap; gap: 14px; margin-top: 8px; color: #9a968f; font-size: 11px; }
        footer b { color: #e8e6e3; }
        footer b.sk { color: #e09b58; }
        """;
}
#endif
