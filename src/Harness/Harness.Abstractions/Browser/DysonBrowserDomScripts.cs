using System.Globalization;
using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// DOM automation scripts shared by the Windows WebView2 tab and the macOS WKWebView tab.
/// Synthetic JS events, not OS input.
/// </summary>
public static class DysonBrowserDomScripts
{
    public const string OuterHtml = "document.documentElement.outerHTML";

    public static Result<string, string> Click(DysonBrowserClickRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(request.Selector))
        {
            var sel = JsonSerializer.Serialize(request.Selector);
            var button = JsonSerializer.Serialize(request.Button ?? "left");
            var js = $$"""
                (() => {
                  const el = document.querySelector({{sel}});
                  if (!el) return 'selector not found';
                  el.dispatchEvent(new MouseEvent('click', {
                    bubbles: true, cancelable: true, view: window,
                    button: {{button}} === 'right' ? 2 : ({{button}} === 'middle' ? 1 : 0),
                    ctrlKey: {{JsBool(request.CtrlKey)}},
                    shiftKey: {{JsBool(request.ShiftKey)}},
                    altKey: {{JsBool(request.AltKey)}},
                    metaKey: {{JsBool(request.MetaKey)}}
                  }));
                  if (typeof el.click === 'function' && {{button}} === 'left') el.click();
                  return 'ok';
                })()
                """;
            return Result<string, string>.AsValue(js);
        }

        if (request.X is double x && request.Y is double y)
        {
            var xs = x.ToString(CultureInfo.InvariantCulture);
            var ys = y.ToString(CultureInfo.InvariantCulture);
            var js = $$"""
                (() => {
                  const el = document.elementFromPoint({{xs}}, {{ys}});
                  if (!el) return 'no element at coordinates';
                  el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window, clientX: {{xs}}, clientY: {{ys}} }));
                  if (typeof el.click === 'function') el.click();
                  return 'ok';
                })()
                """;
            return Result<string, string>.AsValue(js);
        }

        return Result<string, string>.AsError("Click requires selector or x/y coordinates.");
    }

    public static Result<string, string> Type(DysonBrowserTypeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Text))
            return Result<string, string>.AsError("text is required");

        var sel = request.Selector is null ? "null" : JsonSerializer.Serialize(request.Selector);
        var text = JsonSerializer.Serialize(request.Text);
        var js = $$"""
            (() => {
              const el = {{sel}} ? document.querySelector({{sel}}) : document.activeElement;
              if (!el) return 'target not found';
              el.focus();
              if ({{JsBool(request.ClearFirst)}} && 'value' in el) el.value = '';
              if ('value' in el) {
                el.value = (el.value || '') + {{text}};
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
              } else {
                el.textContent = (el.textContent || '') + {{text}};
              }
              return 'ok';
            })()
            """;
        return Result<string, string>.AsValue(js);
    }

    public static Result<string, string> Hover(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return Result<string, string>.AsError("selector is required");

        var sel = JsonSerializer.Serialize(selector);
        var js = $$"""
            (() => {
              const el = document.querySelector({{sel}});
              if (!el) return 'selector not found';
              el.dispatchEvent(new MouseEvent('mouseover', { bubbles: true, cancelable: true, view: window }));
              el.dispatchEvent(new MouseEvent('mouseenter', { bubbles: true, cancelable: true, view: window }));
              return 'ok';
            })()
            """;
        return Result<string, string>.AsValue(js);
    }

    public static Result<string, string> PressKey(DysonBrowserKeyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Key))
            return Result<string, string>.AsError("key is required");

        var sel = request.Selector is null ? "null" : JsonSerializer.Serialize(request.Selector);
        var key = JsonSerializer.Serialize(request.Key);
        var js = $$"""
            (() => {
              const el = {{sel}} ? document.querySelector({{sel}}) : document.activeElement || document.body;
              if (!el) return 'target not found';
              el.focus();
              const opts = {
                key: {{key}}, bubbles: true, cancelable: true,
                ctrlKey: {{JsBool(request.CtrlKey)}},
                shiftKey: {{JsBool(request.ShiftKey)}},
                altKey: {{JsBool(request.AltKey)}},
                metaKey: {{JsBool(request.MetaKey)}}
              };
              el.dispatchEvent(new KeyboardEvent('keydown', opts));
              el.dispatchEvent(new KeyboardEvent('keypress', opts));
              el.dispatchEvent(new KeyboardEvent('keyup', opts));
              return 'ok';
            })()
            """;
        return Result<string, string>.AsValue(js);
    }

    public static string SelectorProbe(string selectorJson) =>
        $"(document.querySelector({selectorJson}) ? 'ok' : 'missing')";

    public static VoidResult<string> InterpretOk(Result<string, string> eval)
    {
        if (eval.IsError)
            return VoidResult<string>.AsError(eval.Error);
        if (!string.Equals(eval.Value, "ok", StringComparison.Ordinal))
            return VoidResult<string>.AsError(eval.Value);
        return VoidResult<string>.Success;
    }

    private static string JsBool(bool value) => value ? "true" : "false";
}
