using System.Text.Encodings.Web;
using System.Text.Json;

namespace DysonHarness;

/// <summary>A2UI v1.0 constants and resource bounds for the Dyson renderer (RenderA2ui).</summary>
public static class DysonA2uiProtocol
{
    public const string Version = "1.0";

    public const string BasicCatalogId = "https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json";

    /// <summary>The kit plus QuestionSlot and ActionButton. An identifier only; never fetched.</summary>
    public const string DysonCatalogId = "a2ui://dyson/catalogs/v1/catalog.json";

    /// <summary>Host-derived surfaces only. Agent surfaceIds with this prefix are rejected.</summary>
    public const string ReservedSurfacePrefix = "dyson_";

    /// <summary>Derived question cards use <c>dyson_question_{questionId:N}</c>.</summary>
    public const string QuestionSurfacePrefix = "dyson_question_";

    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string IntegrityError = "INTEGRITY_ERROR";

    public const int MaxArgumentBytes = 65536;
    public const int MaxMessagesPerCall = 20;
    public const int MaxComponentsPerSurface = 200;
    public const int MaxTreeDepth = 16;
    public const int MaxStringLength = 4000;
    public const int MaxDataModelBytes = 32768;

    /// <summary>Nesting cap for the data model; well under System.Text.Json's default 64 so it always serializes and reloads.</summary>
    public const int MaxDataModelDepth = 32;
    public const int MaxLiveSurfacesPerSession = 20;
    public const int MaxIdentifierLength = 64;
    public const int MaxChecks = 8;
    public const int MaxOptions = 50;

    /// <summary>Compact wire JSON for the model; not HTML, so relaxed escaping keeps it readable.</summary>
    internal static readonly JsonSerializerOptions WireOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
