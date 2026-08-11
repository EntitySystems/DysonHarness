#if WINDOWS
using System.Globalization;
using System.Text.Json;
using DysonHarness;
using ImageMagick;

namespace Harness.Tests;

/// <summary>
/// Windows CEF integration gate: HwndHost agent browser must obtain WebGPU + WebGL and present non-black pixels.
/// Requires a fresh Cef.Initialize (kill Harness.UI / DysonHarness / CefSharp.BrowserSubprocess first).
/// WebGPU path must create a canvas context, configure, and present — adapter/device alone is insufficient
/// (SharedImageBackingFactory / WebGPUSwapBufferProvider can still fail after requestDevice).
/// </summary>
public class DysonCefGpuPresentTests
{
    private static readonly string ProbeHtml = """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><title>dyson-gpu-probe</title></head>
        <body style="margin:0;background:#111">
        <canvas id="gl" width="128" height="128" style="position:fixed;left:0;top:0;width:128px;height:128px"></canvas>
        <canvas id="gpu" width="256" height="256" style="position:fixed;left:128px;top:0;width:256px;height:256px"></canvas>
        <script>
        window.__dysonGpuProbe = {
          boot: true, ready: false, webgpu: false, webgpuPresent: false, webgl: false, present: false,
          deviceLost: null, error: null, stage: 'start', frames: 0
        };
        (async () => {
          const out = window.__dysonGpuProbe;
          try {
            out.stage = 'webgl';
            const glCanvas = document.getElementById('gl');
            const gl = glCanvas.getContext('webgl2') || glCanvas.getContext('webgl');
            out.webgl = !!gl;
            if (gl) {
              gl.viewport(0, 0, glCanvas.width, glCanvas.height);
              gl.clearColor(1, 0, 0, 1);
              gl.clear(gl.COLOR_BUFFER_BIT);
              out.present = true;
            }

            out.stage = 'webgpu-adapter';
            if (!navigator.gpu) {
              out.error = (out.error || '') + 'navigator.gpu missing;';
            } else {
              const adapter = await Promise.race([
                navigator.gpu.requestAdapter(),
                new Promise((resolve) => setTimeout(() => resolve(null), 15000))
              ]);
              if (!adapter) {
                out.error = (out.error || '') + 'requestAdapter null/timeout;';
              } else {
                out.stage = 'webgpu-device';
                const device = await Promise.race([
                  adapter.requestDevice(),
                  new Promise((resolve) => setTimeout(() => resolve(null), 15000))
                ]);
                if (!device) {
                  out.error = (out.error || '') + 'requestDevice timed out;';
                } else {
                  out.webgpu = true;
                  device.lost.then((info) => {
                    out.deviceLost = String(info && info.message ? info.message : info);
                  });
                  out.stage = 'webgpu-canvas';
                  const gpuCanvas = document.getElementById('gpu');
                  const ctx = gpuCanvas.getContext('webgpu');
                  if (!ctx) {
                    out.error = (out.error || '') + 'getContext(webgpu) returned null;';
                  } else {
                    const format = navigator.gpu.getPreferredCanvasFormat();
                    ctx.configure({ device, format, alphaMode: 'opaque' });
                    out.stage = 'webgpu-present';
                    const presentOnce = () => {
                      const encoder = device.createCommandEncoder();
                      const pass = encoder.beginRenderPass({
                        colorAttachments: [{
                          view: ctx.getCurrentTexture().createView(),
                          clearValue: { r: 0, g: 1, b: 0, a: 1 },
                          loadOp: 'clear',
                          storeOp: 'store'
                        }]
                      });
                      pass.end();
                      device.queue.submit([encoder.finish()]);
                      out.frames++;
                    };
                    // Continuous presents so the compositor has a painted swapchain frame.
                    for (let i = 0; i < 8; i++) {
                      presentOnce();
                      await new Promise((r) => requestAnimationFrame(r));
                    }
                    await device.queue.onSubmittedWorkDone();
                    if (out.deviceLost) {
                      out.error = (out.error || '') + 'device lost: ' + out.deviceLost + ';';
                    } else {
                      out.webgpuPresent = true;
                      out.present = true;
                    }
                  }
                }
              }
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
            var webgpuPresent = root.TryGetProperty("webgpuPresent", out var wgp) && wgp.ValueKind == JsonValueKind.True;
            var webgl = root.TryGetProperty("webgl", out var wl) && wl.ValueKind == JsonValueKind.True;
            var present = root.TryGetProperty("present", out var pr) && pr.ValueKind == JsonValueKind.True;
            var error = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String
                ? err.GetString()
                : null;
            var stage = root.TryGetProperty("stage", out var st) && st.ValueKind == JsonValueKind.String
                ? st.GetString()
                : null;
            var deviceLost = root.TryGetProperty("deviceLost", out var dl) && dl.ValueKind == JsonValueKind.String
                ? dl.GetString()
                : null;

            if (!webgpu)
            {
                throw new InvalidOperationException(
                    "WebGPU adapter/device unavailable. Probe=" + probeJson.Value
                    + (string.IsNullOrEmpty(error) ? "" : " error=" + error)
                    + (string.IsNullOrEmpty(stage) ? "" : " stage=" + stage)
                    + " Check chrome://gpu and %LocalAppData%\\DysonHarness\\cef-debug.log after a fresh CEF restart.");
            }

            if (!webgpuPresent)
            {
                throw new InvalidOperationException(
                    "WebGPU canvas configure/present failed (swapchain / SharedImage). Probe=" + probeJson.Value
                    + (string.IsNullOrEmpty(error) ? "" : " error=" + error)
                    + (string.IsNullOrEmpty(deviceLost) ? "" : " deviceLost=" + deviceLost)
                    + (string.IsNullOrEmpty(stage) ? "" : " stage=" + stage)
                    + " Check cef-debug.log for WebGPUSwapBufferProvider / SharedImageBackingFactory.");
            }

            if (!string.IsNullOrEmpty(deviceLost))
            {
                throw new InvalidOperationException(
                    "WebGPU device lost after present. Probe=" + probeJson.Value
                    + " deviceLost=" + deviceLost);
            }

            if (!webgl)
            {
                throw new InvalidOperationException(
                    "WebGL context unavailable. Probe=" + probeJson.Value
                    + (string.IsNullOrEmpty(error) ? "" : " error=" + error));
            }

            if (!present)
                throw new InvalidOperationException("GPU clear did not run. Probe=" + probeJson.Value);

            // Let the compositor paint WebGPU/WebGL frames before CDP capture.
            await Task.Delay(1500);

            var shot = await tab.TakeScreenshotAsync(timeoutMs: 30_000);
            if (shot.IsError)
                throw new InvalidOperationException("TakeScreenshot failed: " + shot.Error);

            // Probe layout: WebGL red at x=0..128, WebGPU green at x=128..384.
            // Require saturated green in the WebGPU rect so WebGL alone cannot pass.
            AssertPngHasWebGpuGreen(shot.Value);
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

    /// <summary>
    /// Probe HTML places WebGL red at left and WebGPU green at right (x≥128 CSS px).
    /// Require saturated green in that rect so a WebGL-only present cannot pass the gate.
    /// </summary>
    private static void AssertPngHasWebGpuGreen(byte[] png)
    {
        using var image = new MagickImage(png);
        if (image.Width < 200 || image.Height < 8)
            throw new InvalidOperationException($"Screenshot too small for WebGPU rect: {image.Width}x{image.Height}");

        // Device pixels: canvas is 256 CSS px starting at x=128; allow DPI scale.
        var scale = image.Width / 640.0;
        var x0 = (int)(128 * scale);
        var x1 = Math.Min((int)image.Width, (int)(384 * scale));
        var stepX = Math.Max(1, (x1 - x0) / 8);
        var stepY = Math.Max(1, (int)image.Height / 8);
        var samples = 0;
        var green = 0;
        double sumG = 0;

        using var pixels = image.GetPixels();
        for (var y = stepY / 2; y < image.Height; y += stepY)
        {
            for (var x = x0 + stepX / 2; x < x1; x += stepX)
            {
                var color = pixels.GetPixel(x, y).ToColor()
                    ?? throw new InvalidOperationException("Pixel ToColor returned null.");
                var r = (double)color.R / MagickColors.White.R;
                var g = (double)color.G / MagickColors.White.G;
                var b = (double)color.B / MagickColors.White.B;
                sumG += g;
                samples++;
                if (g > 0.7 && g > r * 2 && g > b * 2)
                    green++;
            }
        }

        if (samples == 0)
            throw new InvalidOperationException("No WebGPU-rect samples.");

        var meanG = sumG / samples;
        if (green < 3 || meanG < 0.5)
        {
            throw new InvalidOperationException(
                "CDP screenshot missing WebGPU green clear (compositor/swapchain present failed). "
                + $"samples={samples.ToString(CultureInfo.InvariantCulture)} "
                + $"green={green.ToString(CultureInfo.InvariantCulture)} "
                + $"meanG={meanG.ToString("F3", CultureInfo.InvariantCulture)} "
                + $"rect=[{x0},{x1}) size={image.Width}x{image.Height}");
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
