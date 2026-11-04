# Desktop webview host (Windows CefSharp + macOS WKWebView)

Blazor Interactive Server remains the UI stack. On Windows continuous builds, **`DysonHarness.UI.Windows`** hosts that UI inside a CefSharp WPF window (`DysonHarness.exe`). On macOS continuous builds, **`DysonHarness.UI.macOS`** hosts it inside a system WKWebView (`DysonHarness.app`). Agent browser windows are separate from the shell on both platforms. The macOS section is below; the rest of this page is the Windows host.

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
dotnet run --project src/Harness/Harness.UI --launch-profile http
```

That debug host listens on **http://localhost:5181**. The desktop shell does not: `App.xaml.cs` passes `http://127.0.0.1:0`, so the installed `DysonHarness.exe` (MSI) gets an ephemeral loopback port. Never stop that process to run the debug host.

## Architecture

1. STA WPF `Application` starts (`DysonHarness.UI.Windows`).
2. `DysonCefStaHost.AttachToExistingApplication()` reuses that dispatcher and calls `Cef.Initialize` once (no second STA/`Application`). When `Harness.UI` runs without the shell, `EnsureStarted` still spawns a background STA thread for agent browsers.
3. `DysonUiWebHost` builds + runs Blazor on loopback (`http://127.0.0.1:0`, HTTPS redirection skipped).
4. Shell `ChromiumWebBrowser` navigates to the listening URL.
5. External http(s) navigations and popups → OS default browser (`ExternalNavigationHandlers`); in-CEF navigation cancelled.
6. On main window close: cancel web host, `Cef.Shutdown`, exit.

## Agent browser permissions

Agent tabs (`DysonCefBrowserTab`) set `PermissionHandler` to `DysonAgentBrowserPermissionHandler`. There is no prompt and no per-site choice, so debug automation is not blocked:

- `OnShowPermissionPrompt` calls `Continue(PermissionRequestResult.Accept)` and returns true for every `PermissionRequestType` CefSharp can raise (camera, microphone, geolocation, notifications, clipboard, MIDI, storage, protected media, and the rest of that flags enum).
- `OnRequestMediaAccessPermission` calls `Continue` with the requested `MediaAccessPermissionType` bits (device audio/video and desktop audio/video capture) and returns true. CEF requires that set to match the request for `getUserMedia`, so the grant is exactly what the page asked for — which is every capture bit on the request.
- `OnDismissPermissionPrompt` does nothing. The prompt was already accepted; a later dismiss (navigation or window close) is not turned into a deny.

The desktop shell (`MainWindow` + `ExternalNavigationHandlers`) does not install this handler. It sets `DysonShellMicPermission` instead (composer push-to-talk): a media request that is exactly `AudioCapture` is granted; video, desktop capture, and combined requests are cancelled; only a mic-only permission prompt is accepted, every other prompt is denied. Shell and agent windows share one `Cef.Initialize` (command-line switches included) but not handler instances. No `enable-media-stream` or `use-fake-ui-for-media-stream` switch is added — those would grant media for the shell as well, and `enable-media-stream` skips the handler. The handler alone covers agent windows.

Pages opened in the agent browser can use the camera, microphone, geolocation, and similar capabilities without asking.

The shell OSR view’s right-click menu is Chromium’s default WPF menu plus a top **Reload page** item. **F5** and **Ctrl+F5** call `ChromiumWebBrowser.Reload` from `MainWindow.PreviewKeyDown` while the browser has focus (`Ctrl+F5` passes ignore-cache). Both are browser-process reloads, not page script. The agent-window toolbar reload is unchanged. Reload is a full navigation to the loopback URL: the document and SignalR circuit are discarded (in-flight engine work keeps running; reload does not stop it), the new circuit restores the work directory from `localStorage` key `dyson-workdir`, and the previously focused session is not selected — click it again to resume. A wedged renderer may block the context menu (the renderer requests it) but not F5, which the WPF window sees before the key is sent to the renderer. If the CEF browser UI thread is wedged, neither works; restart the app.

## Snip → agent composer

Agent browser chrome has a **Snip** button to the right of the address bar. Agent tabs are **HwndHost** (windowed CEF), so a live WPF rubber-band cannot sit on top of the HWND (airspace). Snip therefore uses a **CDP screenshot-backed overlay**:

1. Click Snip → capture page URL (`CurrentAddress`) and a best-effort JS probe (`scrollY`, `scrollHeight`, `innerHeight`) → CDP full-viewport screenshot → show as a WPF `Image` over the content host; **collapse** the HwndHost browser so the overlay is interactive (Esc cancels and restores the browser)
2. Rubber-band select on that image layer
3. On a valid drag: crop the **overlay** `BitmapSource` (map DIP selection to that bitmap’s pixels). Do **not** take a second CDP screenshot — collapsed HwndHost lays out at 0×0 and `Page.CaptureScreenshot` returns a tiny/empty frame
4. `IDysonBrowserControl.SnipCaptured` raises `DysonBrowserSnipPayload` (`ImageBytes`, empty `HtmlRef`, `FileName` = `browser-snip.jpg`, plus `Url`, raw scroll metrics, and `PercentDown` for the rubber-band top); HwndHost is shown again
5. `DysonUiHost` compresses via `DysonUserImageFactory` and `QueuePendingImage` — the thumbnail appears in the composer. It also enqueues a consume-once prompt line (`DysonBrowserSnipCrop.FormatPromptLine`) that `Composer` appends: `Snip: {url} · {n}% down the page` (drops URL or percent when missing; omits the line if both are missing). The user still types/sends (no auto-send)

`TakeScreenshotAsync` uses DevTools `Page.CaptureScreenshot` (optional `timeoutMs`, default **60s**, linked to the prompt cancellation token so cancel/timeout cannot hang forever). JS evaluation uses the same linked-token race. Residual: CefSharp `EvaluateScriptAsync` may remain orphaned after timeout (cannot abort the renderer). Under HwndHost this capture includes WebGPU/WebGL pixels.

**`HtmlRef` TODO:** `DysonBinaryAttachment.HtmlRef` / payload `HtmlRef` are reserved for a future feature that will resolve HTML elements intersecting the snip rectangle. Today they are always empty/null and are not sent on provider wire image parts.

Boundary: `Harness.WindowsBrowser` only references Abstractions; it never calls `DysonUiHost` directly.

## Constraints

- Reuse app mode + platform paths from [docs/storage/models.md](../storage/models.md) — one `dyson.db` per mode folder
- Do not fork session/resume semantics; use `IDysonSessionRepository.GetFullSessionAsync` / restore
- Keep providers ephemeral; model profiles remain SQLite rows
- CefSharp NuGet restore supplies CEF binaries; Visual C++ 2022 redistributable is a deployment dependency
- **Executable RID:** Windows host projects default to `RuntimeIdentifier=win-x64` and `PlatformTarget=x64` so CefSharp copies natives (`libcef.dll`, `CefSharp.BrowserSubprocess.exe`, …) next to the exe. A library RID on `Harness.WindowsBrowser` alone is not enough — MSBuild only lays out architecture-specific CEF redistributables for the **host** project. The ARM64 release overrides the host RID with `-r win-arm64` (cross-publish from an x64 runner). CefSharp 149 ships `chromiumembeddedframework.runtime.win-arm64`, so the same publish layout applies. Local `dotnet run` stays x64.
- **Direct CefSharp package on the exe:** the Windows shell (and `Harness.UI` on Windows) need a `PackageReference` to `CefSharp.Wpf.NETCore` so CefSharp `buildTransitive` targets run (notably `locales\*.pak`). A `ProjectReference` to `Harness.WindowsBrowser` alone does not import those targets into the executable.
- Reuse `DysonNativeFolderPicker` for work-directory registration (host-process OS dialog; interactive desktop required — not for headless/remote Blazor hosts)
- STA: shell owns the WPF message loop; Blazor Server stays MTA and marshals agent CEF work via the STA dispatcher
- CEF `RootCachePath` is `%LocalAppData%\DysonHarness\` with `CachePath` = `...\cef-cache` and `cef-debug.log` beside it. Chromium allows **one process** per root: a second launch activates the existing window and exits (`Cef.GetExitCode` = `NormalExitProcessNotified`). Do not run `Harness.UI` agent CEF and the desktop shell against the same cache at once.
- **WebGPU + ANGLE D3D11 + GPU compositing:** `DysonCefStaHost.InitializeCef` sets Chromium switches `enable-unsafe-webgpu`, `ignore-gpu-blocklist`, and `use-angle=d3d11` before `Cef.Initialize` (shared by the desktop shell and agent browser windows). It also **removes** `disable-gpu-compositing`, which `CefSharp.Wpf.CefSettings` adds by default for OSR resize ([CefSharp#4953](https://github.com/cefsharp/CefSharp/issues/4953)). With that flag left on, HwndHost agent tabs still get a WebGPU adapter/device (and DOM HUD) but canvas layers stay black / FPS stalls — GPU compositing is required to present WebGPU swapchain textures into the windowed HWND. Do **not** force `use-angle=d3d12`: ANGLE D3D12 leaves no `SharedImageBackingFactory` for `WebgpuSwapChainTexture` / `WebGPUSwapBufferProvider`, so canvas `getContext('webgpu')` configure/present fails and the device is destroyed after adapter init. Explicit ANGLE **D3D11** registers the D3D SharedImage factory needed for swapchain present. `enable-gpu-rasterization` is intentionally omitted (earlier crash correlate). CefSharp **149** NuGet already ships `dxil.dll` / `dxcompiler.dll` (required for Dawn WebGPU on win-x64; see [CefSharp discussion #4662](https://github.com/cefsharp/CefSharp/discussions/4662) / [CEF #3646](https://github.com/chromiumembedded/cef/issues/3646)). `DysonCefStaHost` fails fast if either DLL is missing beside the exe. **#4662's failure mode was "No WebGPU adapters found"** (missing DXC) — not a black canvas after `WebGPURenderer ready`. Host exes also embed `Harness.WindowsBrowser/app.manifest` (Windows 10+ compat + DPI) as required by the CefSharp NuGet Readme / #4662 for GPU detection. For Zafirn-style pages, also remember the character-select `#arena-overlay` is nearly opaque until Join succeeds — that can look like a "black WebGPU canvas" even when the swapchain is presenting. **Agent tabs** (games/WebGPU pages) use `CefSharp.Wpf.HwndHost.ChromiumWebBrowser` with `ActivateBrowserOnCreation` and a layout nudge when the HWND would otherwise be 0×0 — this path is shared by **both** `Harness.UI` (debug web host on `:5181`; published linux/osx default `:5000`) and `DysonHarness.UI.Windows` (ephemeral `127.0.0.1:0`) when opening agent windows. The **desktop shell** Blazor view stays on OSR `CefSharp.Wpf.ChromiumWebBrowser` with `BrowserSettings.WindowlessFrameRate = 60` and **cannot** host external game origins in-process (those open in agent windows / OS browser); enabling GPU compositing for agents may reintroduce OSR resize glitches on that shell view. Flags only apply on a **fresh** `Cef.Initialize` — kill `Harness.UI` / `DysonHarness` / `CefSharp.BrowserSubprocess` before restart. Verify: in an **agent** tab open `chrome://gpu` (WebGPU hardware; ANGLE backend D3D11; Compositing hardware) and/or canvas WebGPU configure+present; shell also allows `chrome://` (see `ExternalNavigationHandlers`). Automated gate: `dotnet test src/Harness/Harness.Tests --filter DysonCefGpuPresentTests` (`WebGpu_WebGl_And_NonBlack_Present` — WebGPU **canvas** configure+present, WebGL clear, non-black CDP present; adapter-only is insufficient). Needs a D3D-capable GPU and the VC++ 2022 x64 redistributable; if swapchain/SharedImage fails, check `%LocalAppData%\DysonHarness\cef-debug.log` for `WebGPUSwapBufferProvider`. Snip over agent tabs uses a CDP screenshot overlay because HWND airspace blocks live WPF rubber-bands (see **Snip → agent composer**).
- **`ClearBrowserCache` MCP:** clears that shared HTTP cache via CDP (`Network.ClearBrowserCache`) once, then hard-reloads every tab in every open **agent** browser window. Cookies/site storage are untouched. The shell WebView is not hard-reloaded, but its HTTP cache is cleared because it shares the same CEF profile.
- If `Cef.Initialize` fails for other reasons, the exception includes `ResultCode`, cache paths, and `BrowserSubprocessPath` (also check `%LocalAppData%\DysonHarness\cef-debug.log` and the VC++ 2022 x64 redistributable)

## Non-goals

- Custom title-bar UI beyond standard Windows chrome
- Linux desktop shell
- Full CDP parity (rich network waterfall, DOM highlight, cookie store UI)
- Committing Cef redistributables beyond NuGet restore

## Projects

| Project | TFM | Role |
| ------- | --- | ---- |
| `Harness.Abstractions` | `net10.0` | Result types + browser contracts + `DysonNullBrowserControl` |
| `Harness.WindowsBrowser` | `net10.0-windows` + WPF + `win-x64` (library default; the host RID wins at publish) | `DysonCefBrowserControl` + chrome + `DysonCefStaHost`; agent tabs = HwndHost |
| `Harness.Engine` | `net10.0` | MCP catalog + executor → `IDysonBrowserControl` |
| `Harness.UI` | `net10.0-windows` + `win-x64` on Windows | Blazor host (`DysonUiWebHost`); conditional WindowsBrowser ref + DI singleton |
| `DysonHarness.UI.Windows` | `net10.0-windows` + WPF + `win-x64` (default) / `win-arm64` (`-r`) | CEF shell exe (`AssemblyName=DysonHarness`) |
| `Harness.MacBrowser` | `net10.0` | `DysonMacBrowserControl` — JSON C ABI over `libDysonWebKit.dylib` |
| `DysonHarness.UI.macOS` | `net10.0` + `osx-arm64` / `osx-x64` | WKWebView shell exe (`AssemblyName=DysonHarness`), bundled as `DysonHarness.app` |

## macOS WKWebView host

`DysonHarness.UI.macOS` is a `Microsoft.NET.Sdk.Web` exe. It starts `DysonUiWebHost` on `http://127.0.0.1:0` (HTTPS redirection skipped) and loads that URL in a shell `WKWebView`. `DysonUiWebHostOptions.ConfigureServices` registers `DysonMacBrowserControl`. The shell does not reference WebKit; `Harness.MacBrowser` talks to `src/native/macos` through `libDysonWebKit.dylib`.

```bash
dotnet run --project src/Harness/DysonHarness.UI.macOS
```

That builds the dylib with `swiftc -target arm64-apple-macos14` (or `x86_64-apple-macos14` when `-r osx-x64`) and opens the shell. Browser-based Blazor is still `dotnet run --project src/Harness/Harness.UI` on port 5181. Do not stop a running `DysonHarness` to free that port.

A second NSWindow is the agent browser (`OpenBrowser` and the header **Open Browser** action). It implements `IDysonBrowserWindow` / `IDysonBrowserTab`. DOM clicks, typing, hover, and keys use `DysonBrowserDomScripts` (the same strings as the Cef tab). Screenshots are `WKWebView.takeSnapshot` PNGs; the executor still compresses to JPEG. Console lines come from a document-start user script. The network log is main-frame `didFinish` / `didFail` only.

Agent tabs use a persistent `WKWebsiteDataStore(forIdentifier:)` so their cookies and cache stay off the shell's default store. `ClearBrowserCache` removes disk, memory, fetch, and offline-app cache on that store, then reloads agent tabs. Cookies and site storage stay. The shell is not reloaded.

The agent `WKUIDelegate` grants media capture (camera, microphone, camera+microphone), geolocation on macOS 27+, and file-open panels. JavaScript `alert` / `confirm` / `prompt` complete immediately (`confirm` is true) so a dialog cannot hang a tool. `window.open` becomes a tab. The shell window does not grant those prompts: it keeps the loopback origin and sends other `http(s)` to `NSWorkspace`.

The Dock and Finder icon is the white Dyson mark on black (`CFBundleIconFile` `DysonHarness.icns`, built by `scripts/bundle-macos-app.sh` from `DysonHarness.UI.Windows/Assets/dyson.ico`). The host leaves that bundle icon alone. Setting `NSApp.applicationIconImage` would replace the Dock tile with the unmasked square. The shell window title and the startup alert use `CFBundleDisplayName`. A preview package sets that, and `CFBundleName`, to **Dyson Harness (Preview)**. Stable stays **Dyson Harness**.

`ponytail:` ceilings: no WebUSB, WebSerial, WebBluetooth, or WebMIDI (no public WKWebView API). Device-orientation permission is unavailable on macOS. Display capture is not a `WKMediaCaptureType`. WebGPU is whatever the OS WebKit ships. The first camera, microphone, or location use still shows the macOS TCC dialog. Info.plist usage strings do not skip it. Minimum system version is macOS 14. Pull-request CI and a local `scripts/bundle-macos-app.sh` ad-hoc sign the bundle. Continuous release sets `CODESIGN_IDENTITY` and signs with Developer ID Application, hardened runtime, and [`packaging/macos/entitlements.plist`](../../packaging/macos/entitlements.plist), then notarizes the app and a `/Applications` `.pkg` (Developer ID Installer). See [releases.md](releases.md).

Single instance: `flock` on `{app support}/{mode}/dyson-mac.lock`. A second launch activates `com.entitysystems.dysonharness` and exits. Snip raises `SnipCaptured`; `DysonUiHost` already queues that into the composer.
