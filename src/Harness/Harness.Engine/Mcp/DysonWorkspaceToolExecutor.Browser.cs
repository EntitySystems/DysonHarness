using System.Globalization;
using System.Text.Json;

namespace DysonHarness;

public sealed partial class DysonWorkspaceToolExecutor
{
    private async Task<DysonToolCallResult> ExecuteBrowserToolAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var control = _session.Config.BrowserControl;
        if (control is null)
            return Error(call, "browser control unavailable");

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Error(call, $"{call.ToolName}: invalid JSON arguments.");
        }

        return call.ToolName switch
        {
            "OpenBrowser" => await OpenBrowserAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "ListBrowserWindows" => await ListBrowserWindowsAsync(call, control, cancellationToken).ConfigureAwait(false),
            "CloseBrowser" => await CloseBrowserAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "ResizeBrowser" => await ResizeBrowserAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "ListBrowserTabs" => await ListBrowserTabsAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "NewBrowserTab" => await NewBrowserTabAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "CloseBrowserTab" => await CloseBrowserTabAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "ActivateBrowserTab" => await ActivateBrowserTabAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "BrowserNavigate" => await BrowserNavigateAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "BrowserGoBack" => await BrowserGoBackAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "BrowserGoForward" => await BrowserGoForwardAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "BrowserReload" => await BrowserReloadAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "ClearBrowserCache" => await ClearBrowserCacheAsync(call, control, cancellationToken).ConfigureAwait(false),
            "BrowserClick" => await BrowserClickAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "BrowserType" => await BrowserTypeAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "BrowserFill" => await BrowserFillAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "BrowserHover" => await BrowserHoverAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "BrowserPressKey" => await BrowserPressKeyAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "BrowserWaitForSelector" => await BrowserWaitForSelectorAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "BrowserWaitForNavigation" => await BrowserWaitForNavigationAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "BrowserExecuteJavaScript" => await BrowserExecuteJavaScriptAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "BrowserGetHtml" => await BrowserGetHtmlAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "BrowserTakeScreenshot" => await BrowserTakeScreenshotAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "BrowserReadConsoleLog" => await BrowserReadConsoleLogAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            "BrowserReadNetworkLog" => await BrowserReadNetworkLogAsync(call, control, root, cancellationToken).ConfigureAwait(false),
            _ => Stub(call),
        };
    }

    private static async Task<DysonToolCallResult> OpenBrowserAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var url = GetOptionalString(root, "url");
        var width = GetInt(root, "width");
        var height = GetInt(root, "height");
        var opened = await control.OpenBrowserAsync(url, width, height, cancellationToken).ConfigureAwait(false);
        if (opened.IsError)
            return Error(call, opened.Error);

        var window = opened.Value;
        var tabs = await window.ListTabsAsync(cancellationToken).ConfigureAwait(false);
        var tabId = tabs.IsSuccess && tabs.Value.Count > 0 ? tabs.Value[0].Id : null;
        return Ok(call, JsonSerializer.Serialize(new { windowId = window.Id, tabId }));
    }

    private static async Task<DysonToolCallResult> ListBrowserWindowsAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        CancellationToken cancellationToken)
    {
        var listed = await control.ListWindowsAsync(cancellationToken).ConfigureAwait(false);
        if (listed.IsError)
            return Error(call, listed.Error);

        var payload = listed.Value.Select(w => new { windowId = w.Id }).ToArray();
        return Ok(call, JsonSerializer.Serialize(payload));
    }

    private static async Task<DysonToolCallResult> CloseBrowserAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var window = await ResolveWindowAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (window.IsError)
            return Error(call, window.Error);
        var closed = await window.Value.CloseAsync(cancellationToken).ConfigureAwait(false);
        return closed.IsError ? Error(call, closed.Error) : Ok(call, "closed");
    }

    private static async Task<DysonToolCallResult> ResizeBrowserAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var window = await ResolveWindowAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (window.IsError)
            return Error(call, window.Error);
        var width = GetInt(root, "width");
        var height = GetInt(root, "height");
        if (width is null || height is null)
            return Error(call, "width and height are required");
        var resized = await window.Value.ResizeAsync(width.Value, height.Value, cancellationToken).ConfigureAwait(false);
        return resized.IsError ? Error(call, resized.Error) : Ok(call, "resized");
    }

    private static async Task<DysonToolCallResult> ListBrowserTabsAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var window = await ResolveWindowAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (window.IsError)
            return Error(call, window.Error);
        var tabs = await window.Value.ListTabsAsync(cancellationToken).ConfigureAwait(false);
        if (tabs.IsError)
            return Error(call, tabs.Error);

        var rows = new List<object>();
        foreach (var tab in tabs.Value)
        {
            var url = await tab.GetUrlAsync(cancellationToken).ConfigureAwait(false);
            var title = await tab.GetTitleAsync(cancellationToken).ConfigureAwait(false);
            rows.Add(new
            {
                tabId = tab.Id,
                windowId = tab.WindowId,
                url = url.IsSuccess ? url.Value : "",
                title = title.IsSuccess ? title.Value : "",
            });
        }

        return Ok(call, JsonSerializer.Serialize(rows));
    }

    private static async Task<DysonToolCallResult> NewBrowserTabAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var window = await ResolveWindowAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (window.IsError)
            return Error(call, window.Error);
        var created = await window.Value.NewTabAsync(GetOptionalString(root, "url"), cancellationToken).ConfigureAwait(false);
        if (created.IsError)
            return Error(call, created.Error);
        return Ok(call, JsonSerializer.Serialize(new { tabId = created.Value.Id, windowId = created.Value.WindowId }));
    }

    private static async Task<DysonToolCallResult> CloseBrowserTabAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var window = await ResolveWindowAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (window.IsError)
            return Error(call, window.Error);
        var tabId = GetOptionalString(root, "tabId");
        if (string.IsNullOrWhiteSpace(tabId))
            return Error(call, "tabId is required");
        var closed = await window.Value.CloseTabAsync(tabId, cancellationToken).ConfigureAwait(false);
        return closed.IsError ? Error(call, closed.Error) : Ok(call, "closed");
    }

    private static async Task<DysonToolCallResult> ActivateBrowserTabAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var window = await ResolveWindowAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (window.IsError)
            return Error(call, window.Error);
        var tabId = GetOptionalString(root, "tabId");
        if (string.IsNullOrWhiteSpace(tabId))
            return Error(call, "tabId is required");
        var activated = await window.Value.ActivateTabAsync(tabId, cancellationToken).ConfigureAwait(false);
        return activated.IsError ? Error(call, activated.Error) : Ok(call, "activated");
    }

    private static async Task<DysonToolCallResult> BrowserNavigateAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var url = GetOptionalString(root, "url");
        if (string.IsNullOrWhiteSpace(url))
            return Error(call, "url is required");
        var nav = await tab.Value.NavigateAsync(url, cancellationToken).ConfigureAwait(false);
        return nav.IsError ? Error(call, nav.Error) : Ok(call, "navigated");
    }

    private static async Task<DysonToolCallResult> BrowserGoBackAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var result = await tab.Value.GoBackAsync(cancellationToken).ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, "ok");
    }

    private static async Task<DysonToolCallResult> BrowserGoForwardAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var result = await tab.Value.GoForwardAsync(cancellationToken).ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, "ok");
    }

    private static async Task<DysonToolCallResult> BrowserReloadAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var result = await tab.Value.ReloadAsync(cancellationToken).ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, "ok");
    }

    private static async Task<DysonToolCallResult> ClearBrowserCacheAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        CancellationToken cancellationToken)
    {
        var result = await control.ClearBrowserCacheAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsError)
            return Error(call, result.Error);

        return Ok(call, JsonSerializer.Serialize(new
        {
            windows = result.Value.Windows,
            tabsReloaded = result.Value.TabsReloaded,
        }));
    }

    private static async Task<DysonToolCallResult> BrowserClickAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var request = new DysonBrowserClickRequest
        {
            Selector = GetOptionalString(root, "selector"),
            X = GetDouble(root, "x"),
            Y = GetDouble(root, "y"),
            Button = GetOptionalString(root, "button") ?? "left",
            CtrlKey = GetBool(root, "ctrlKey"),
            ShiftKey = GetBool(root, "shiftKey"),
            AltKey = GetBool(root, "altKey"),
            MetaKey = GetBool(root, "metaKey"),
            TimeoutMs = GetInt(root, "timeoutMs"),
        };
        var result = await tab.Value.ClickAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, "ok");
    }

    private static async Task<DysonToolCallResult> BrowserTypeAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var text = GetOptionalString(root, "text");
        if (text is null)
            return Error(call, "text is required");
        var request = new DysonBrowserTypeRequest
        {
            Selector = GetOptionalString(root, "selector"),
            Text = text,
            ClearFirst = GetBool(root, "clearFirst"),
            DelayMs = GetInt(root, "delayMs"),
            TimeoutMs = GetInt(root, "timeoutMs"),
        };
        var result = await tab.Value.TypeAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, "ok");
    }

    private static async Task<DysonToolCallResult> BrowserFillAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var selector = GetOptionalString(root, "selector");
        if (string.IsNullOrWhiteSpace(selector))
            return Error(call, "selector is required");
        if (!root.TryGetProperty("value", out var valueProp) || valueProp.ValueKind == JsonValueKind.Null)
            return Error(call, "selector and value are required");
        var value = valueProp.ValueKind == JsonValueKind.String ? (valueProp.GetString() ?? "") : valueProp.ToString();
        var result = await tab.Value.FillAsync(selector, value, cancellationToken).ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, "ok");
    }

    private static async Task<DysonToolCallResult> BrowserHoverAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var selector = GetOptionalString(root, "selector");
        if (string.IsNullOrWhiteSpace(selector))
            return Error(call, "selector is required");
        var result = await tab.Value.HoverAsync(selector, cancellationToken).ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, "ok");
    }

    private static async Task<DysonToolCallResult> BrowserPressKeyAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var key = GetOptionalString(root, "key");
        if (string.IsNullOrWhiteSpace(key))
            return Error(call, "key is required");
        var request = new DysonBrowserKeyRequest
        {
            Key = key,
            Selector = GetOptionalString(root, "selector"),
            CtrlKey = GetBool(root, "ctrlKey"),
            ShiftKey = GetBool(root, "shiftKey"),
            AltKey = GetBool(root, "altKey"),
            MetaKey = GetBool(root, "metaKey"),
            TimeoutMs = GetInt(root, "timeoutMs"),
        };
        var result = await tab.Value.PressKeyAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, "ok");
    }

    private static async Task<DysonToolCallResult> BrowserWaitForSelectorAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var selector = GetOptionalString(root, "selector");
        if (string.IsNullOrWhiteSpace(selector))
            return Error(call, "selector is required");
        var result = await tab.Value.WaitForSelectorAsync(selector, GetInt(root, "timeoutMs"), cancellationToken)
            .ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, "ok");
    }

    private static async Task<DysonToolCallResult> BrowserWaitForNavigationAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var result = await tab.Value.WaitForNavigationAsync(GetInt(root, "timeoutMs"), cancellationToken)
            .ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, "ok");
    }

    private static async Task<DysonToolCallResult> BrowserExecuteJavaScriptAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var code = GetOptionalString(root, "code");
        if (string.IsNullOrWhiteSpace(code))
            return Error(call, "code is required");
        var result = await tab.Value.ExecuteJavaScriptAsync(code, cancellationToken).ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, result.Value);
    }

    private static async Task<DysonToolCallResult> BrowserGetHtmlAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var result = await tab.Value.GetHtmlAsync(cancellationToken).ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, result.Value);
    }

    private static async Task<DysonToolCallResult> BrowserTakeScreenshotAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var result = await tab.Value
            .TakeScreenshotAsync(GetInt(root, "timeoutMs"), cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError)
            return Error(call, result.Error);

        var compressed = DysonImageCompress.ToJpegMaxEdge(result.Value);
        var windowId = GetOptionalString(root, "windowId");
        var tabId = GetOptionalString(root, "tabId");
        var attachment = new DysonBinaryAttachment
        {
            FileName = "screenshot.jpg",
            Extension = ".jpg",
            MimeType = compressed.MimeType,
            Base64Data = Convert.ToBase64String(compressed.Bytes),
        };
        var ack = JsonSerializer.Serialize(new
        {
            mimeType = compressed.MimeType,
            byteLength = compressed.Bytes.Length,
            width = compressed.Width,
            height = compressed.Height,
            windowId,
            tabId,
        });
        return Ok(call, ack, attachment);
    }

    private static async Task<DysonToolCallResult> BrowserReadConsoleLogAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var result = await tab.Value.ReadConsoleLogAsync(cancellationToken).ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, JsonSerializer.Serialize(result.Value));
    }

    private static async Task<DysonToolCallResult> BrowserReadNetworkLogAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tab = await ResolveTabAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (tab.IsError)
            return Error(call, tab.Error);
        var result = await tab.Value.ReadNetworkLogAsync(cancellationToken).ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, JsonSerializer.Serialize(result.Value));
    }

    private static async Task<Result<IDysonBrowserWindow, string>> ResolveWindowAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        _ = call;
        var windowId = GetOptionalString(root, "windowId");
        if (string.IsNullOrWhiteSpace(windowId))
            return Result<IDysonBrowserWindow, string>.AsError("windowId is required");
        return await control.GetWindowAsync(windowId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<IDysonBrowserTab, string>> ResolveTabAsync(
        DysonToolCall call,
        IDysonBrowserControl control,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var window = await ResolveWindowAsync(call, control, root, cancellationToken).ConfigureAwait(false);
        if (window.IsError)
            return Result<IDysonBrowserTab, string>.AsError(window.Error);

        var tabId = GetOptionalString(root, "tabId");
        if (string.IsNullOrWhiteSpace(tabId))
            return Result<IDysonBrowserTab, string>.AsError("tabId is required");

        var tabs = await window.Value.ListTabsAsync(cancellationToken).ConfigureAwait(false);
        if (tabs.IsError)
            return Result<IDysonBrowserTab, string>.AsError(tabs.Error);

        var tab = tabs.Value.FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.Ordinal));
        if (tab is null)
            return Result<IDysonBrowserTab, string>.AsError($"Tab not found: {tabId}");
        return Result<IDysonBrowserTab, string>.AsValue(tab);
    }

    private static double? GetDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var d))
            return d;
        if (prop.ValueKind == JsonValueKind.String
            && double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }
}
