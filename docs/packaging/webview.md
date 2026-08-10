# Windows CefSharp host (desktop shell + agent browser)

Blazor Interactive Server remains the UI stack. On Windows continuous builds, **`DysonHarness.UI.Windows`** hosts that UI inside a CefSharp WPF window (`DysonHarness.exe`). Agent browser windows share the same CEF lifecycle.

Shipped continuous zips (all RIDs) and Windows MSI: [releases.md](releases.md).

## Intent

Ship a **Windows desktop shell** that hosts Dyson UI inside **CefSharp WPF** (not WebView2), talking to in-process `Harness.Engine` and the SQLite app-data store. The same CEF process/lifecycle backs **agent browser windows** opened via MCP (`OpenBrowser`, …).

| Piece | Role |
| ----- | ---- |
| Native host | WPF window chrome + OSR `CefSharp.Wpf.ChromiumWebBrowser` (`DysonHarness.UI.Windows`); title bar / app icon track in-app `dysonTheme` (DWM immersive dark + light/dark `.ico`) |
| Contracts | `IDysonBrowserControl` / `IDysonBrowserWindow` / `IDysonBrowserTab` in `Harness.Abstractions` |
| Agent windows | In-process WPF windows from `DysonCefBrowserControl` (DI singleton on Windows); tabs use **`CefSharp.Wpf.HwndHost.ChromiumWebBrowser`** for windowed GPU present |
| App shell | In-process Kestrel/Blazor on loopback; CEF loads `http://127.0.0.1:*` |
| Engine | In-process `Harness.Engine` (same `DysonAppPaths` / `dyson.db`) |

## How to run (dev)

```bash
dotnet run --project src/Harness/DysonHarness.UI.Windows
```

Browser-based Blazor (no CEF shell) still works:

```bash
dotnet run --project src/Harness/Harness.UI --urls http://localhost:5180
```

## Architecture

1. STA WPF `Application` starts (`DysonHarness.UI.Windows`).
2. `DysonCefStaHost.AttachToExistingApplication()` reuses that dispatcher and calls `Cef.Initialize` once (no second STA/`Application`). When `Harness.UI` runs without the shell, `EnsureStarted` still spawns a background STA thread for agent browsers.
3. `DysonUiWebHost` builds + runs Blazor on loopback (`http://127.0.0.1:0`, HTTPS redirection skipped).
4. Shell `ChromiumWebBrowser` navigates to the listening URL.
5. External http(s) navigations and popups → OS default browser (`ExternalNavigationHandlers`); in-CEF navigation cancelled.
6. On main window close: cancel web host, `Cef.Shutdown`, exit.

## Snip → agent composer

Agent browser chrome has a **Snip** button to the right of the address bar. Agent tabs are **HwndHost** (windowed CEF), so a live WPF rubber-band cannot sit on top of the HWND (airspace). Snip therefore uses a **CDP screenshot-backed overlay**:

1. Click Snip → CDP full-viewport screenshot → show as a WPF `Image` over the content host; **collapse** the HwndHost browser so the overlay is interactive (Esc cancels and restores the browser)
2. Rubber-band select on that image layer
3. On a valid drag: fresh CDP screenshot → map selection (DIP) to pixel bounds → crop to JPEG in WPF
4. `IDysonBrowserControl.SnipCaptured` raises `DysonBrowserSnipPayload` (`ImageBytes`, empty `HtmlRef`, `FileName` = `browser-snip.jpg`); HwndHost is shown again
5. `DysonUiHost` compresses via `DysonUserImageFactory` and `QueuePendingImage` — the thumbnail appears in the composer; the user still types/sends (no auto-send)

`TakeScreenshotAsync` uses DevTools `Page.CaptureScreenshot` (optional `timeoutMs`, default **30s**, linked to the prompt cancellation token so cancel/timeout cannot hang forever). Under HwndHost this capture includes WebGPU/WebGL pixels.

**`HtmlRef` TODO:** `DysonBinaryAttachment.HtmlRef` / payload `HtmlRef` are reserved for a future feature that will resolve HTML elements intersecting the snip rectangle. Today they are always empty/null and are not sent on provider wire image parts.

Boundary: `Harness.WindowsBrowser` only references Abstractions; it never calls `DysonUiHost` directly.

## Constraints

- Reuse app mode + platform paths from [docs/storage/models.md](../storage/models.md) — one `dyson.db` per mode folder
- Do not fork session/resume semantics; use `IDysonSessionRepository.GetFullSessionAsync` / restore
- Keep providers ephemeral; model profiles remain SQLite rows
- CefSharp NuGet restore supplies CEF binaries; Visual C++ 2022 redistributable is a deployment dependency
- **Executable RID:** Windows host projects (`DysonHarness.UI.Windows`, and `Harness.UI` when run on Windows) must set `RuntimeIdentifier=win-x64` (and `PlatformTarget=x64`) so CefSharp copies natives (`libcef.dll`, `CefSharp.BrowserSubprocess.exe`, …) next to the exe. A library RID on `Harness.WindowsBrowser` alone is not enough — MSBuild only lays out architecture-specific CEF redistributables for the **host** project.
- **Direct CefSharp package on the exe:** the Windows shell (and `Harness.UI` on Windows) need a `PackageReference` to `CefSharp.Wpf.NETCore` so CefSharp `buildTransitive` targets run (notably `locales\*.pak`). A `ProjectReference` to `Harness.WindowsBrowser` alone does not import those targets into the executable.
- Reuse `DysonNativeFolderPicker` for work-directory registration (host-process OS dialog; interactive desktop required — not for headless/remote Blazor hosts)
- STA: shell owns the WPF message loop; Blazor Server stays MTA and marshals agent CEF work via the STA dispatcher
- CEF `RootCachePath` is `%LocalAppData%\DysonHarness\` with `CachePath` = `...\cef-cache` and `cef-debug.log` beside it. Chromium allows **one process** per root: a second launch activates the existing window and exits (`Cef.GetExitCode` = `NormalExitProcessNotified`). Do not run `Harness.UI` agent CEF and the desktop shell against the same cache at once.
- **WebGPU + ANGLE D3D12:** `DysonCefStaHost.InitializeCef` sets Chromium switches `enable-unsafe-webgpu`, `ignore-gpu-blocklist`, and `use-angle=d3d12` before `Cef.Initialize` (shared by the desktop shell and agent browser windows). ANGLE D3D12 avoids a D3D11 device-removed GPU-process crash path seen with the previous rasterization-only flag set; `enable-gpu-rasterization` is intentionally omitted. CefSharp **149** NuGet already ships `dxil.dll` / `dxcompiler.dll` (required for Dawn/D3D12 WebGPU on win-x64). **Agent tabs** (games/WebGPU pages) use `CefSharp.Wpf.HwndHost.ChromiumWebBrowser` with `ActivateBrowserOnCreation` and a layout nudge when the HWND would otherwise be 0×0 — this path is shared by **both** `Harness.UI` (web host on `:5180`) and `DysonHarness.UI.Windows` when opening agent windows. The **desktop shell** Blazor view stays on OSR `CefSharp.Wpf.ChromiumWebBrowser` with `BrowserSettings.WindowlessFrameRate = 60` and **cannot** host external game origins in-process (those open in agent windows / OS browser). Flags only apply on a **fresh** `Cef.Initialize` — kill `Harness.UI` / `DysonHarness` / `CefSharp.BrowserSubprocess` before restart. Verify: in an **agent** tab open `chrome://gpu` (WebGPU hardware; ANGLE backend D3D12) and/or `navigator.gpu.requestAdapter()`; shell also allows `chrome://` (see `ExternalNavigationHandlers`). Automated gate: `dotnet test src/Harness/Harness.Tests --filter DysonCefGpuPresentTests` (`WebGpu_WebGl_And_NonBlack_Present` — WebGPU adapter, WebGL context, non-black CDP present). Needs a D3D12-capable GPU and the VC++ 2022 x64 redistributable; if the GPU process fails, check `%LocalAppData%\DysonHarness\cef-debug.log`. Snip over agent tabs uses a CDP screenshot overlay because HWND airspace blocks live WPF rubber-bands (see **Snip → agent composer**).
- **`ClearBrowserCache` MCP:** clears that shared HTTP cache via CDP (`Network.ClearBrowserCache`) once, then hard-reloads every tab in every open **agent** browser window. Cookies/site storage are untouched. The shell WebView is not hard-reloaded, but its HTTP cache is cleared because it shares the same CEF profile.
- If `Cef.Initialize` fails for other reasons, the exception includes `ResultCode`, cache paths, and `BrowserSubprocessPath` (also check `%LocalAppData%\DysonHarness\cef-debug.log` and the VC++ 2022 x64 redistributable)

## Non-goals

- Custom title-bar UI beyond standard Windows chrome
- macOS/Linux desktop shells
- Full CDP parity (rich network waterfall, DOM highlight, cookie store UI)
- Committing Cef redistributables beyond NuGet restore

## Projects

| Project | TFM | Role |
| ------- | --- | ---- |
| `Harness.Abstractions` | `net10.0` | Result types + browser contracts + `DysonNullBrowserControl` |
| `Harness.WindowsBrowser` | `net10.0-windows` + WPF + `win-x64` | `DysonCefBrowserControl` + chrome + `DysonCefStaHost`; agent tabs = HwndHost |
| `Harness.Engine` | `net10.0` | MCP catalog + executor → `IDysonBrowserControl` |
| `Harness.UI` | `net10.0-windows` + `win-x64` on Windows | Blazor host (`DysonUiWebHost`); conditional WindowsBrowser ref + DI singleton |
| `DysonHarness.UI.Windows` | `net10.0-windows` + WPF + `win-x64` | CEF shell exe (`AssemblyName=DysonHarness`) |
