#if WINDOWS
using System.Globalization;
using System.Text.Json;
using DysonHarness;
using ImageMagick;

namespace Harness.Tests;

/// <summary>
/// Windows CEF integration gate: HwndHost agent browser must obtain WebGPU + WebGL and present non-black pixels.
/// Requires a fresh Cef.Initialize (kill Harness.UI / DysonHarness / CefSharp.BrowserSubprocess first).
/// </summary>
public class DysonCefGpuPresentTests
{
    private static readonly string ProbeHtml = """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><title>dyson-gpu-probe</title></head>
        <body style="margin:0;background:#000">
        <canvas id="c" width="256" height="256"></canvas>
        <script>
        window.__dysonGpuProbe = {
          boot: true, ready: false, webgpu: false, webgl: false, present: false, error: null, stage: 'start'
        };
        (async () => {
          const out = window.__dysonGpuProbe;
          try {
            out.stage = 'webgl';
            const canvas = document.getElementById('c');
            const gl = canvas.getContext('webgl2') || canvas.getContext('webgl');
            out.webgl = !!gl;
            if (gl) {
              gl.viewport(0, 0, canvas.width, canvas.height);
              gl.clearColor(1, 0, 0, 1);
              gl.clear(gl.COLOR_BUFFER_BIT);
              out.present = true;
            }
            out.stage = 'webgpu';
            if (navigator.gpu) {
              const adapterPromise = navigator.gpu.requestAdapter();
              const timeoutPromise = new Promise((resolve) => setTimeout(() => resolve(null), 15000));
              const adapter = await Promise.race([adapterPromise, timeoutPromise]);
              if (adapter) {
                const device = await Promise.race([
                  adapter.requestDevice(),
                  new Promise((resolve) => setTimeout(() => resolve(null), 15000))
                ]);
                out.webgpu = !!device;
                if (!device) out.error = (out.error || '') + 'requestDevice timed out;';
              } else {
                out.error = (out.error || '') + 'requestAdapter null/timeout;';
              }
            } else {
              out.error = (out.error || '') + 'navigator.gpu missing;';
            }
          } catch (e) {
            out.error = String(e && e.message ? e.message : e);
          }
          out.stage = 'done';
          out.ready = true;
        })();
        </script>
        </body></html>
        """;

    [Fact]
    public async Task WebGpu_WebGl_And_NonBlack_Present()
    {
        var control = new DysonCefBrowserControl();
        IDysonBrowserWindow? window = null;
        string? probePath = null;
        try
        {
            probePath = Path.Combine(Path.GetTempPath(), "dyson-gpu-probe-" + Guid.NewGuid().ToString("N") + ".html");
            await File.WriteAllTextAsync(probePath, ProbeHtml);
            var fileUrl = new Uri(probePath).AbsoluteUri;

            var opened = await control.OpenBrowserAsync(
                url: fileUrl,
                width: 640,
                height: 480);
            if (opened.IsError)
                throw new InvalidOperationException("OpenBrowser failed: " + opened.Error);

            window = opened.Value;
            var tabs = await window.ListTabsAsync();
            if (tabs.IsError || tabs.Value.Count == 0)
                throw new InvalidOperationException("Expected an initial tab.");

            var tab = tabs.Value[0];

            // Wait until the HwndHost browser can evaluate JS against our fixture.
            await WaitForBrowserAddressAsync(tab, "dyson-gpu-probe", TimeSpan.FromSeconds(30));
            await WaitForProbeReadyAsync(tab, TimeSpan.FromSeconds(60));

            var probeJson = await tab.ExecuteJavaScriptAsync(
                "JSON.stringify(window.__dysonGpuProbe || {})");
            if (probeJson.IsError)
                throw new InvalidOperationException("Probe JS failed: " + probeJson.Error);

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(probeJson.Value) ? "{}" : probeJson.Value);
            var root = doc.RootElement;
            var webgpu = root.TryGetProperty("webgpu", out var wg) && wg.ValueKind == JsonValueKind.True;
            var webgl = root.TryGetProperty("webgl", out var wl) && wl.ValueKind == JsonValueKind.True;
            var present = root.TryGetProperty("present", out var pr) && pr.ValueKind == JsonValueKind.True;
            var error = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String
                ? err.GetString()
                : null;
            var stage = root.TryGetProperty("stage", out var st) && st.ValueKind == JsonValueKind.String
                ? st.GetString()
                : null;

            if (!webgpu)
            {
                throw new InvalidOperationException(
                    "WebGPU adapter/device unavailable. Probe=" + probeJson.Value
                    + (string.IsNullOrEmpty(error) ? "" : " error=" + error)
                    + (string.IsNullOrEmpty(stage) ? "" : " stage=" + stage)
                    + " Check chrome://gpu and %LocalAppData%\\DysonHarness\\cef-debug.log after a fresh CEF restart.");
            }

            if (!webgl)
            {
                throw new InvalidOperationException(
                    "WebGL context unavailable. Probe=" + probeJson.Value
                    + (string.IsNullOrEmpty(error) ? "" : " error=" + error));
            }

            if (!present)
                throw new InvalidOperationException("WebGL clear did not run. Probe=" + probeJson.Value);

            // Let the compositor paint the red clear before CDP capture.
            await Task.Delay(750);

            var shot = await tab.TakeScreenshotAsync(timeoutMs: 30_000);
            if (shot.IsError)
                throw new InvalidOperationException("TakeScreenshot failed: " + shot.Error);

            AssertPngNotUniformlyBlack(shot.Value);
        }
        finally
        {
            if (window is not null)
            {
                try
                {
                    await window.CloseAsync();
                }
                catch
                {
                    // best-effort so the shared CEF RootCachePath lock is released for later hosts
                }
            }

            if (probePath is not null)
            {
                try
                {
                    File.Delete(probePath);
                }
                catch
                {
                    // ignore temp cleanup
                }
            }
        }
    }

    private static async Task WaitForBrowserAddressAsync(IDysonBrowserTab tab, string titleOrUrlHint, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        string? lastUrl = null;
        string? lastTitle = null;
        while (Environment.TickCount64 < deadline)
        {
            var url = await tab.GetUrlAsync();
            var title = await tab.GetTitleAsync();
            lastUrl = url.IsError ? url.Error : url.Value;
            lastTitle = title.IsError ? title.Error : title.Value;
            if ((!string.IsNullOrEmpty(lastUrl) && lastUrl.Contains(titleOrUrlHint, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(lastTitle) && lastTitle.Contains(titleOrUrlHint, StringComparison.OrdinalIgnoreCase)))
            {
                // Also require probe boot marker so we know JS ran.
                var boot = await tab.ExecuteJavaScriptAsync(
                    "JSON.stringify(window.__dysonGpuProbe && window.__dysonGpuProbe.boot === true)");
                if (!boot.IsError && string.Equals(boot.Value, "true", StringComparison.OrdinalIgnoreCase))
                    return;
            }

            await Task.Delay(150);
        }

        throw new TimeoutException(
            "Timed out waiting for probe page. url=" + (lastUrl ?? "(null)")
            + " title=" + (lastTitle ?? "(null)"));
    }

    private static async Task WaitForProbeReadyAsync(IDysonBrowserTab tab, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        string? last = null;
        while (Environment.TickCount64 < deadline)
        {
            var result = await tab.ExecuteJavaScriptAsync(
                "JSON.stringify(window.__dysonGpuProbe || { ready: false })");
            if (!result.IsError && !string.IsNullOrWhiteSpace(result.Value))
            {
                last = result.Value;
                try
                {
                    using var doc = JsonDocument.Parse(result.Value);
                    if (doc.RootElement.TryGetProperty("ready", out var ready)
                        && ready.ValueKind == JsonValueKind.True)
                    {
                        return;
                    }
                }
                catch (JsonException)
                {
                    // keep polling
                }
            }

            await Task.Delay(150);
        }

        throw new TimeoutException(
            "Timed out waiting for __dysonGpuProbe.ready. Last=" + (last ?? "(null)"));
    }

    private static void AssertPngNotUniformlyBlack(byte[] png)
    {
        using var image = new MagickImage(png);
        if (image.Width < 8 || image.Height < 8)
            throw new InvalidOperationException($"Screenshot too small: {image.Width}x{image.Height}");

        // Sample a grid; require some non-near-black pixels (WebGL clear is saturated red).
        var stepX = Math.Max(1, (int)image.Width / 8);
        var stepY = Math.Max(1, (int)image.Height / 8);
        var samples = 0;
        var bright = 0;
        double sumLuma = 0;
        double sumR = 0;

        using var pixels = image.GetPixels();
        for (var y = stepY / 2; y < image.Height; y += stepY)
        {
            for (var x = stepX / 2; x < image.Width; x += stepX)
            {
                var pixel = pixels.GetPixel(x, y);
                var color = pixel.ToColor()
                    ?? throw new InvalidOperationException("Pixel ToColor returned null.");
                // Magick.NET Quantum uses 0..Quantum.Max; normalize to 0..1.
                var r = (double)color.R / MagickColors.White.R;
                var g = (double)color.G / MagickColors.White.G;
                var b = (double)color.B / MagickColors.White.B;
                var luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                sumLuma += luma;
                sumR += r;
                samples++;
                if (luma > 0.08 || r > 0.2)
                    bright++;
            }
        }

        if (samples == 0)
            throw new InvalidOperationException("No screenshot samples.");

        var meanLuma = sumLuma / samples;
        var meanR = sumR / samples;
        if (bright == 0 || meanLuma < 0.05)
        {
            throw new InvalidOperationException(
                "CDP screenshot is uniformly black / empty (GPU present failed). "
                + $"samples={samples.ToString(CultureInfo.InvariantCulture)} "
                + $"bright={bright.ToString(CultureInfo.InvariantCulture)} "
                + $"meanLuma={meanLuma.ToString("F3", CultureInfo.InvariantCulture)} "
                + $"meanR={meanR.ToString("F3", CultureInfo.InvariantCulture)} "
                + $"size={image.Width}x{image.Height}");
        }
    }
}
#else
namespace Harness.Tests;

/// <summary>CEF GPU present gate is Windows-only.</summary>
public class DysonCefGpuPresentTests
{
    [Fact(Skip = "Windows CEF / HwndHost only")]
    public void WebGpu_WebGl_And_NonBlack_Present()
    {
    }
}
#endif
