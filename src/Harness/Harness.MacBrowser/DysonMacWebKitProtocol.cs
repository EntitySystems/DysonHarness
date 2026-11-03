using System.Globalization;
using System.Text.Json;

namespace DysonHarness;

internal static class DysonMacWebKitProtocol
{
    public static string ShowShell(string url) => Req("showShell", ("url", url));

    public static string Stop() => Req("stop");

    public static string OpenWindow(string? url, int width, int height) =>
        Req("openWindow", ("url", url), ("width", width), ("height", height));

    public static string ListWindows() => Req("listWindows");

    public static string GetWindow(string windowId) => Req("getWindow", ("windowId", windowId));

    public static string CloseWindow(string windowId) => Req("closeWindow", ("windowId", windowId));

    public static string ResizeWindow(string windowId, int width, int height) =>
        Req("resizeWindow", ("windowId", windowId), ("width", width), ("height", height));

    public static string BringToFront(string windowId) => Req("bringToFront", ("windowId", windowId));

    public static string ListTabs(string windowId) => Req("listTabs", ("windowId", windowId));

    public static string NewTab(string windowId, string? url) =>
        Req("newTab", ("windowId", windowId), ("url", url));

    public static string CloseTab(string windowId, string tabId) =>
        Req("closeTab", ("windowId", windowId), ("tabId", tabId));

    public static string ActivateTab(string windowId, string tabId) =>
        Req("activateTab", ("windowId", windowId), ("tabId", tabId));

    public static string Navigate(string windowId, string tabId, string url) =>
        Req("navigate", ("windowId", windowId), ("tabId", tabId), ("url", url));

    public static string Reload(string windowId, string tabId, bool ignoreCache) =>
        Req("reload", ("windowId", windowId), ("tabId", tabId), ("ignoreCache", ignoreCache));

    public static string GoBack(string windowId, string tabId) =>
        Req("goBack", ("windowId", windowId), ("tabId", tabId));

    public static string GoForward(string windowId, string tabId) =>
        Req("goForward", ("windowId", windowId), ("tabId", tabId));

    public static string GetUrl(string windowId, string tabId) =>
        Req("getUrl", ("windowId", windowId), ("tabId", tabId));

    public static string GetTitle(string windowId, string tabId) =>
        Req("getTitle", ("windowId", windowId), ("tabId", tabId));

    public static string Eval(string windowId, string tabId, string code, int timeoutMs) =>
        Req("eval", ("windowId", windowId), ("tabId", tabId), ("code", code), ("timeoutMs", timeoutMs));

    public static string Screenshot(string windowId, string tabId, int timeoutMs) =>
        Req("screenshot", ("windowId", windowId), ("tabId", tabId), ("timeoutMs", timeoutMs));

    public static string ReadConsole(string windowId, string tabId) =>
        Req("readConsole", ("windowId", windowId), ("tabId", tabId));

    public static string ReadNetwork(string windowId, string tabId) =>
        Req("readNetwork", ("windowId", windowId), ("tabId", tabId));

    public static string ClearConsole(string windowId, string tabId) =>
        Req("clearConsole", ("windowId", windowId), ("tabId", tabId));

    public static string ClearNetwork(string windowId, string tabId) =>
        Req("clearNetwork", ("windowId", windowId), ("tabId", tabId));

    public static string ClearCache() => Req("clearCache");

    public static string WaitNav(string windowId, string tabId, int timeoutMs) =>
        Req("waitNav", ("windowId", windowId), ("tabId", tabId), ("timeoutMs", timeoutMs));

    public static DysonMacWebKitResponse Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
        return new DysonMacWebKitResponse
        {
            Ok = ok,
            Error = StringProp(root, "error"),
            WindowId = StringProp(root, "windowId"),
            TabId = StringProp(root, "tabId"),
            Value = StringProp(root, "value"),
            Png = BytesProp(root, "pngBase64"),
            Windows = WindowsProp(root),
            Tabs = TabsProp(root),
            Console = ConsoleProp(root),
            Network = NetworkProp(root),
            WindowsCleared = IntProp(root, "windows"),
            TabsReloaded = IntProp(root, "tabsReloaded"),
        };
    }

    private static string Req(string op, params (string Key, object? Value)[] fields)
    {
        var payload = new Dictionary<string, object?>(fields.Length + 1) { ["op"] = op };
        foreach (var (key, value) in fields)
            payload[key] = value;
        return JsonSerializer.Serialize(payload);
    }

    private static string? StringProp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        return el.GetString();
    }

    private static int IntProp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return 0;
        return el.TryGetInt32(out var n) ? n : 0;
    }

    private static byte[]? BytesProp(JsonElement root, string name)
    {
        var text = StringProp(root, name);
        if (string.IsNullOrEmpty(text))
            return null;
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IReadOnlyList<DysonMacWindowRow> WindowsProp(JsonElement root)
    {
        if (!root.TryGetProperty("windows", out var el) || el.ValueKind != JsonValueKind.Array)
            return [];

        var rows = new List<DysonMacWindowRow>();
        foreach (var item in el.EnumerateArray())
        {
            var id = StringProp(item, "id");
            if (string.IsNullOrEmpty(id))
                continue;
            rows.Add(new DysonMacWindowRow { Id = id });
        }

        return rows;
    }

    private static IReadOnlyList<DysonMacTabRow> TabsProp(JsonElement root)
    {
        if (!root.TryGetProperty("tabs", out var el) || el.ValueKind != JsonValueKind.Array)
            return [];

        var rows = new List<DysonMacTabRow>();
        foreach (var item in el.EnumerateArray())
        {
            var id = StringProp(item, "id");
            if (string.IsNullOrEmpty(id))
                continue;
            rows.Add(new DysonMacTabRow
            {
                Id = id,
                Url = StringProp(item, "url") ?? "",
                Title = StringProp(item, "title") ?? "",
            });
        }

        return rows;
    }

    private static IReadOnlyList<DysonBrowserConsoleEntry> ConsoleProp(JsonElement root)
    {
        if (!root.TryGetProperty("console", out var el) || el.ValueKind != JsonValueKind.Array)
            return [];

        var rows = new List<DysonBrowserConsoleEntry>();
        foreach (var item in el.EnumerateArray())
        {
            rows.Add(new DysonBrowserConsoleEntry
            {
                Level = StringProp(item, "level") ?? "log",
                Message = StringProp(item, "message") ?? "",
                Source = StringProp(item, "source"),
                Line = item.TryGetProperty("line", out var line) && line.TryGetInt32(out var n) ? n : null,
                Timestamp = Stamp(StringProp(item, "timestamp")),
            });
        }

        return rows;
    }

    private static IReadOnlyList<DysonBrowserNetworkEntry> NetworkProp(JsonElement root)
    {
        if (!root.TryGetProperty("network", out var el) || el.ValueKind != JsonValueKind.Array)
            return [];

        var rows = new List<DysonBrowserNetworkEntry>();
        foreach (var item in el.EnumerateArray())
        {
            int? status = item.TryGetProperty("status", out var statusEl) && statusEl.TryGetInt32(out var code)
                ? code
                : null;
            rows.Add(new DysonBrowserNetworkEntry
            {
                Url = StringProp(item, "url") ?? "",
                Method = StringProp(item, "method") ?? "GET",
                Status = status,
                MimeType = StringProp(item, "mimeType"),
                Timestamp = Stamp(StringProp(item, "timestamp")),
            });
        }

        return rows;
    }

    private static DateTimeOffset Stamp(string? text)
    {
        if (text is not null
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return parsed;
        return DateTimeOffset.UtcNow;
    }
}

internal sealed class DysonMacWindowRow
{
    public required string Id { get; init; }
}

internal sealed class DysonMacTabRow
{
    public required string Id { get; init; }
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
}

internal sealed class DysonMacWebKitResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? WindowId { get; init; }
    public string? TabId { get; init; }
    public string? Value { get; init; }
    public byte[]? Png { get; init; }
    public IReadOnlyList<DysonMacWindowRow> Windows { get; init; } = [];
    public IReadOnlyList<DysonMacTabRow> Tabs { get; init; } = [];
    public IReadOnlyList<DysonBrowserConsoleEntry> Console { get; init; } = [];
    public IReadOnlyList<DysonBrowserNetworkEntry> Network { get; init; } = [];
    public int WindowsCleared { get; init; }
    public int TabsReloaded { get; init; }
}
