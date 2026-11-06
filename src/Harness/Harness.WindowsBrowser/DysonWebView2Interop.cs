using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace DysonHarness;

/// <summary>
/// Pure WebView2 helpers (no live CoreWebView2): script result unwrap, CDP payload parsing, permission and
/// external-link decisions. Shared by the agent tab and the desktop shell.
/// </summary>
internal static class DysonWebView2Interop
{
    internal const string ShellProfile = "shell";
    internal const string AgentProfile = "agent";

    /// <summary>
    /// Shim for <c>theme.js</c>: <c>window.dysonShell.notifyTheme(t)</c> posts a web message the shell handles.
    /// </summary>
    internal const string ShellBridgeScript = """
        window.dysonShell = {
          notifyTheme: function (t) {
            try { window.chrome.webview.postMessage({ type: 'theme', theme: String(t || '') }); } catch (e) {}
          }
        };
        """;

    /// <summary>
    /// <c>ExecuteScript</c> returns JSON. A JSON string becomes the plain string, null/undefined become "",
    /// anything else stays raw JSON (numbers, booleans, objects).
    /// </summary>
    internal static string UnwrapScriptResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "";

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => doc.RootElement.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => json,
            };
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>CDP <c>Page.captureScreenshot</c> result <c>{"data":"base64"}</c> → PNG bytes.</summary>
    internal static Result<byte[], string> ParseScreenshotData(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.String
                || data.GetString() is not { Length: > 0 } b64)
            {
                return Result<byte[], string>.AsError("Screenshot capture returned empty.");
            }

            return Result<byte[], string>.AsValue(Convert.FromBase64String(b64));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return Result<byte[], string>.AsError("Screenshot payload invalid: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// CDP <c>Runtime.consoleAPICalled</c> / <c>Runtime.exceptionThrown</c> params → a console entry.
    /// Levels match the CefSharp names (Info, Warning, Error, Verbose). Lines are 1-based.
    /// </summary>
    internal static DysonBrowserConsoleEntry? ParseConsoleEvent(string eventName, string? json, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (string.Equals(eventName, "Runtime.exceptionThrown", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("exceptionDetails", out var details) || details.ValueKind != JsonValueKind.Object)
                    return null;
                var message = details.TryGetProperty("exception", out var ex)
                              && ex.ValueKind == JsonValueKind.Object
                              && ex.TryGetProperty("description", out var desc)
                              && desc.ValueKind == JsonValueKind.String
                    ? desc.GetString()
                    : String(details, "text");
                return new DysonBrowserConsoleEntry
                {
                    Level = "Error",
                    Message = message ?? "",
                    Source = String(details, "url"),
                    Line = Line(details),
                    Timestamp = timestamp,
                };
            }

            var args = root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                ? string.Join(" ", a.EnumerateArray().Select(RenderRemoteObject))
                : "";
            JsonElement frame = default;
            var hasFrame = root.TryGetProperty("stackTrace", out var stack)
                           && stack.ValueKind == JsonValueKind.Object
                           && stack.TryGetProperty("callFrames", out var frames)
                           && frames.ValueKind == JsonValueKind.Array
                           && frames.GetArrayLength() > 0
                           && (frame = frames[0]).ValueKind == JsonValueKind.Object;
            return new DysonBrowserConsoleEntry
            {
                Level = ConsoleLevel(String(root, "type")),
                Message = args,
                Source = hasFrame ? String(frame, "url") : null,
                Line = hasFrame ? Line(frame) : null,
                Timestamp = timestamp,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Agent tabs grant everything. The shell grants the microphone (push-to-talk) only.</summary>
    internal static CoreWebView2PermissionState DecidePermission(CoreWebView2PermissionKind kind, bool isShell) =>
        !isShell || kind == CoreWebView2PermissionKind.Microphone
            ? CoreWebView2PermissionState.Allow
            : CoreWebView2PermissionState.Deny;

    /// <summary>
    /// Shell only: http(s) navigations off the app origin open in the OS browser. The app origin and
    /// browser-internal schemes (about:, data:, edge:, devtools:) stay in the shell.
    /// </summary>
    internal static bool ShouldOpenExternally(Uri uri, Uri appOrigin) =>
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !IsSameOrigin(uri, appOrigin);

    internal static bool IsSameOrigin(Uri uri, Uri appOrigin) =>
        string.Equals(uri.Scheme, appOrigin.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, appOrigin.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == appOrigin.Port;

    /// <summary>Shell web message <c>{"type":"theme","theme":"light"}</c> → "light"; anything else → null.</summary>
    internal static string? ParseThemeMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && String(root, "type") == "theme"
                ? String(root, "theme")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ConsoleLevel(string? type) => type switch
    {
        "warning" => "Warning",
        "error" or "assert" => "Error",
        "debug" => "Verbose",
        _ => "Info",
    };

    private static string RenderRemoteObject(JsonElement arg)
    {
        if (arg.ValueKind != JsonValueKind.Object)
            return arg.GetRawText();
        if (arg.TryGetProperty("value", out var value))
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
        return String(arg, "unserializableValue") ?? String(arg, "description") ?? String(arg, "type") ?? "";
    }

    private static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Line(JsonElement obj) =>
        obj.TryGetProperty("lineNumber", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() + 1 : null;
}
