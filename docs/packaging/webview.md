# Desktop webview host (Windows WebView2 + macOS WKWebView)

Blazor Interactive Server remains the UI stack. On Windows continuous builds, **`DysonHarness.UI.Windows`** hosts that UI inside a WebView2 WPF window (`DysonHarness.exe`). On macOS continuous builds, **`DysonHarness.UI.macOS`** hosts it inside a system WKWebView (`Dyson Harness.app`; preview builds are `Dyson Harness (Preview).app`). Agent browser windows are separate from the shell on both platforms. The macOS section is below; the rest of this page is the Windows host.

Shipped continuous zips (all RIDs) and Windows MSI: [releases.md](releases.md).

## Intent

Ship a **Windows desktop shell** that hosts Dyson UI inside the system **Microsoft Edge WebView2** runtime, talking to in-process `Harness.Engine` and the SQLite app-data store. The same WebView2 environment backs **agent browser windows** opened via MCP (`OpenBrowser`, …). The app ships no Chromium of its own: the Evergreen WebView2 Runtime (preinstalled on Windows 11 and current Windows 10) is a deployment dependency.

| Piece | Role |
| ----- | ---- |
| Native host | WPF window chrome + HWND-hosted `Microsoft.Web.WebView2.Wpf.WebView2` (`DysonHarness.UI.Windows`); title bar / app icon track in-app `dysonTheme` (DWM immersive dark + light/dark `.ico`) |
| Contracts | `IDysonBrowserControl` / `IDysonBrowserWindow` / `IDysonBrowserTab` in `Harness.Abstractions` |
| Agent windows | In-process WPF windows from `DysonWebView2BrowserControl` (DI singleton on Windows); each tab is a WPF `WebView2` on profile `agent` |
| App shell | In-process Kestrel/Blazor on loopback; the shell WebView2 (profile `shell`) loads `http://127.0.0.1:*` |
| Engine | In-process `Harness.Engine` (same `DysonAppPaths` / `dyson.db`) |

## How to run (dev)

```bash
dotnet run --project src/Harness/DysonHarness.UI.Windows
```

Browser-based Blazor (no desktop shell) still works:

```bash
dotnet run --project src/Harness/Harness.UI --launch-profile http
```

That debug host listens on **http://localhost:5181**. The desktop shell does not: `App.xaml.cs` passes `http://127.0.0.1:0`, so the installed `DysonHarness.exe` (MSI) gets an ephemeral loopback port. Never stop that process to run the debug host.

## Architecture

1. STA WPF `Application` starts (`DysonHarness.UI.Windows`).
2. `DysonSingleInstance.Claim("DysonHarness-{DysonDev|DysonTest|DysonProd}")` — a named `EventWaitHandle`. A second launch signals it and exits with code 0; the first instance activates its window. One shell per user and app mode.
3. `DysonWindowsStaHost.RuntimeVersion()` checks the WebView2 Runtime. When missing, a dialog offers the Evergreen download page (`RuntimeDownloadUrl`) and the app exits with code 1.
4. `DysonWindowsStaHost.AttachToExistingApplication()` reuses that dispatcher (no second STA/`Application`). When `Harness.UI` runs without the shell, `EnsureStarted` spawns a background STA thread for agent browsers. A background task deletes the legacy CefSharp cache (`%LocalAppData%\DysonHarness\cef-cache`) once no older CefSharp build holds that folder's `lockfile`; nothing else in that folder is touched.
5. `DysonUiWebHost` builds + runs Blazor on loopback (`http://127.0.0.1:0`, HTTPS redirection skipped).
6. The shell `WebView2` navigates to the listening URL.
7. External http(s) navigations and popups → OS default browser (`DysonWebView2Interop.ShouldOpenExternally` in `NavigationStarting` / `NewWindowRequested`); the in-shell navigation is cancelled.
8. On main window close: cancel the web host, release the single-instance handle, exit.

**One environment, two profiles.** `DysonWindowsStaHost.GetEnvironmentAsync` creates one `CoreWebView2Environment` per process on the STA thread, with user data folder `{DysonAppPaths root}\webview2` (for example `%LocalAppData%\DysonProd\webview2`) and `AdditionalBrowserArguments = "--ignore-gpu-blocklist"`. The shell uses profile `shell`; agent tabs use profile `agent`, so cookies, storage and HTTP cache are separate. Every process that opens the same folder must pass identical arguments — do not run a Dev debug shell and the `:5181` debug host with different `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`.

**Theme bridge.** `theme.js` still calls `window.dysonShell.notifyTheme(t)`. The shell injects `DysonWebView2Interop.ShellBridgeScript` with `AddScriptToExecuteOnDocumentCreatedAsync`; that shim posts `{type:'theme'}` through `chrome.webview.postMessage`, and `MainWindow` applies the chrome theme from `WebMessageReceived` (app origin only). After each successful app-origin navigation the shell also runs a sync script that reports the current theme.

## Agent browser permissions

Agent tabs handle `PermissionRequested` with `DysonWebView2Interop.DecidePermission(kind, isShell: false)`, which allows every kind (camera, microphone, geolocation, notifications, clipboard, MIDI, and the rest). There is no prompt and no per-site choice, so debug automation is not blocked. Pages opened in the agent browser can use the camera, microphone, geolocation, and similar capabilities without asking.

The desktop shell uses `DecidePermission(kind, isShell: true)`: only `Microphone` is allowed (composer push-to-talk); everything else is denied.

The shell's right-click menu is the default WebView2 menu with a top **Reload page** item and a separator (`ContextMenuRequested`). **F5** and **Ctrl+F5** are handled in `MainWindow.PreviewKeyDown` — the WPF `WebView2` control re-raises browser accelerator keys as WPF key events. F5 calls `CoreWebView2.Reload`; Ctrl+F5 sends CDP `Page.reload {"ignoreCache":true}`. The agent-window toolbar reload is unchanged. Reload is a full navigation to the loopback URL: the document and SignalR circuit are discarded (in-flight engine work keeps running; reload does not stop it), the new circuit restores the work directory from `localStorage` key `dyson-workdir`, and the previously focused session is not selected — click it again to resume.

Agent-tab popups (`NewWindowRequested`) open as a new tab in the same agent window.

## Snip → agent composer

Agent browser chrome has a **Snip** button to the right of the address bar. Agent tabs are HWND-hosted WebView2 controls, so a live WPF rubber-band cannot sit on top of the HWND (airspace). Snip therefore uses a **CDP screenshot-backed overlay**:

1. Click Snip → capture the page URL and a best-effort JS probe (`scrollY`, `scrollHeight`, `innerHeight`) → CDP full-viewport screenshot → show as a WPF `Image` over the content host; **collapse** the browser so the overlay is interactive (Esc cancels and restores the browser)
2. Rubber-band select on that image layer
3. On a valid drag: crop the **overlay** `BitmapSource` (map DIP selection to that bitmap’s pixels). Do **not** take a second CDP screenshot — the collapsed browser lays out at 0×0
4. `IDysonBrowserControl.SnipCaptured` raises `DysonBrowserSnipPayload` (`ImageBytes`, empty `HtmlRef`, `FileName` = `browser-snip.jpg`, plus `Url`, raw scroll metrics, and `PercentDown` for the rubber-band top); the browser is shown again
5. `DysonUiHost` compresses via `DysonUserImageFactory` and `QueuePendingImage` — the thumbnail appears in the composer. It also enqueues a consume-once prompt line (`DysonBrowserSnipCrop.FormatPromptLine`) that `Composer` appends: `Snip: {url} · {n}% down the page` (drops URL or percent when missing; omits the line if both are missing). The user still types/sends (no auto-send)

`TakeScreenshotAsync` uses CDP `Page.captureScreenshot` (optional `timeoutMs`, default **60s**, linked to the prompt cancellation token so cancel/timeout cannot hang forever). JS evaluation (`ExecuteScriptWithResultAsync`; a JSON string result is unwrapped to the plain string, other values stay JSON) uses the same linked-token race; a timed-out script may keep running in the renderer. The capture includes WebGPU/WebGL pixels. Console lines come from CDP `Runtime.consoleAPICalled` / `Runtime.exceptionThrown`. The network log is main-frame `NavigationCompleted` only (URL + HTTP status).

**`HtmlRef` TODO:** `DysonBinaryAttachment.HtmlRef` / payload `HtmlRef` are reserved for a future feature that will resolve HTML elements intersecting the snip rectangle. Today they are always empty/null and are not sent on provider wire image parts.

Boundary: `Harness.WindowsBrowser` only references Abstractions/Engine; it never calls `DysonUiHost` directly.

## Constraints

- Reuse app mode + platform paths from [docs/storage/models.md](../storage/models.md) — one `dyson.db` per mode folder
- Do not fork session/resume semantics; use `IDysonSessionRepository.GetFullSessionAsync` / restore
- Keep providers ephemeral; model profiles remain SQLite rows
- The `Microsoft.Web.WebView2` NuGet package supplies the managed wrappers and `WebView2Loader.dll`; the browser itself is the system Evergreen WebView2 Runtime. No VC++ redistributable is needed for the browser.
- **Executable RID:** Windows host projects default to `RuntimeIdentifier=win-x64` and `PlatformTarget=x64` so the RID-specific `WebView2Loader.dll` is copied next to the exe. The ARM64 release overrides the host RID with `-r win-arm64` (cross-publish from an x64 runner); CI asserts `WebView2Loader.dll` and `Microsoft.Web.WebView2.Core.dll` in that publish. Local `dotnet run` stays x64.
- Reuse `DysonNativeFolderPicker` for work-directory registration (host-process OS dialog; interactive desktop required — not for headless/remote Blazor hosts)
- STA: shell owns the WPF message loop; Blazor Server stays MTA and marshals agent WebView2 work via the STA dispatcher
- **WebGPU / WebGL:** Edge ships WebGPU on by default; `--ignore-gpu-blocklist` keeps it on blocklisted drivers. Host exes embed `Harness.WindowsBrowser/app.manifest` (Windows 10+ compat + DPI). Verify in an **agent** tab with `edge://gpu`. Automated gate: `dotnet test src/Harness/Harness.Tests --filter DysonWindowsGpuPresentTests` — `WebGpu_WebGl_And_NonBlack_Present` (WebGPU canvas configure+present, WebGL clear, green pixels in the CDP screenshot) and `Agent_tools_round_trip` (every `IDysonBrowserWindow` / `IDysonBrowserTab` call the Browser* MCP tools use). Both use a temp user data folder (`DysonWindowsStaHost.UserDataFolderOverride`), so they run while an installed `DysonHarness.exe` is open.
- **`ClearBrowserCache` MCP:** `ClearBrowsingDataAsync(DiskCache | CacheStorage)` once on the agent profile, then CDP `Page.reload {"ignoreCache":true}` on every tab in every open **agent** window. Cookies/site storage are untouched. The shell profile is not cleared or reloaded.
- If the environment fails to start, the error names the user data folder; a missing runtime is caught at startup (see Architecture step 3).

## Non-goals

- Custom title-bar UI beyond standard Windows chrome
- Linux desktop shell
- Full CDP parity (rich network waterfall, DOM highlight, cookie store UI)
- Bundling a fixed-version WebView2 runtime

## Projects

| Project | TFM | Role |
| ------- | --- | ---- |
| `Harness.Abstractions` | `net10.0` | Result types + browser contracts + `DysonNullBrowserControl` |
| `Harness.WindowsBrowser` | `net10.0-windows` + WPF + `win-x64` (library default; the host RID wins at publish) | `DysonWebView2BrowserControl` + chrome + `DysonWindowsStaHost` + `DysonSingleInstance` |
| `Harness.Engine` | `net10.0` | MCP catalog + executor → `IDysonBrowserControl` |
| `Harness.UI` | `net10.0-windows` + `win-x64` on Windows | Blazor host (`DysonUiWebHost`); conditional WindowsBrowser ref + DI singleton |
| `DysonHarness.UI.Windows` | `net10.0-windows` + WPF + `win-x64` (default) / `win-arm64` (`-r`) | WebView2 shell exe (`AssemblyName=DysonHarness`) |
| `Harness.MacBrowser` | `net10.0` | `DysonMacBrowserControl` — JSON C ABI over `libDysonWebKit.dylib` |
| `DysonHarness.UI.macOS` | `net10.0` + `osx-arm64` / `osx-x64` | WKWebView shell exe (`AssemblyName=DysonHarness`), bundled as `Dyson Harness.app` (preview: `Dyson Harness (Preview).app`) |

## macOS WKWebView host

`DysonHarness.UI.macOS` is a `Microsoft.NET.Sdk.Web` exe. It starts `DysonUiWebHost` on `http://127.0.0.1:0` (HTTPS redirection skipped) and loads that URL in a shell `WKWebView`. `DysonUiWebHostOptions.ConfigureServices` registers `DysonMacBrowserControl`. The shell does not reference WebKit; `Harness.MacBrowser` talks to `src/native/macos` through `libDysonWebKit.dylib`.

```bash
dotnet run --project src/Harness/DysonHarness.UI.macOS
```

That builds the dylib with `swiftc -target arm64-apple-macos14` (or `x86_64-apple-macos14` when `-r osx-x64`) and opens the shell. Browser-based Blazor is still `dotnet run --project src/Harness/Harness.UI` on port 5181. Do not stop a running `DysonHarness` to free that port.

A second NSWindow is the agent browser (`OpenBrowser` and the header **Open Browser** action). It implements `IDysonBrowserWindow` / `IDysonBrowserTab`. DOM clicks, typing, hover, and keys use `DysonBrowserDomScripts` (the same strings as the Windows tab). Screenshots are `WKWebView.takeSnapshot` PNGs; the executor still compresses to JPEG. Console lines come from a document-start user script. The network log is main-frame `didFinish` / `didFail` only.

Agent tabs use a persistent `WKWebsiteDataStore(forIdentifier:)` so their cookies and cache stay off the shell's default store. `ClearBrowserCache` removes disk, memory, fetch, and offline-app cache on that store, then reloads agent tabs. Cookies and site storage stay. The shell is not reloaded.

The agent `WKUIDelegate` grants media capture (camera, microphone, camera+microphone), geolocation on macOS 27+, and file-open panels. JavaScript `alert` / `confirm` / `prompt` complete immediately (`confirm` is true) so a dialog cannot hang a tool. `window.open` becomes a tab. The shell window does not grant those prompts: it keeps the loopback origin and sends other `http(s)` to `NSWorkspace`.

The Dock and Finder icon is the white Dyson mark on black (`CFBundleIconFile` `DysonHarness.icns`, built by `scripts/bundle-macos-app.sh` from `DysonHarness.UI.Windows/Assets/dyson.ico`). The host leaves that bundle icon alone. Setting `NSApp.applicationIconImage` would replace the Dock tile with the unmasked square. The shell window title and the startup alert use `CFBundleDisplayName`. The bundle file name matches it, because Finder, Launchpad and Spotlight display the file name: stable is `Dyson Harness.app`, preview is `Dyson Harness (Preview).app`. `CFBundleName` and `CFBundleDisplayName` are **Dyson Harness** or **Dyson Harness (Preview)**. The bundle id stays `com.entitysystems.dysonharness`.

`ponytail:` ceilings: no WebUSB, WebSerial, WebBluetooth, or WebMIDI (no public WKWebView API). Device-orientation permission is unavailable on macOS. Display capture is not a `WKMediaCaptureType`. WebGPU is whatever the OS WebKit ships. The first camera, microphone, or location use still shows the macOS TCC dialog. Info.plist usage strings do not skip it. Minimum system version is macOS 14. Pull-request CI and a local `scripts/bundle-macos-app.sh` ad-hoc sign the bundle. Continuous release sets `CODESIGN_IDENTITY` and signs with Developer ID Application, hardened runtime, and [`packaging/macos/entitlements.plist`](../../packaging/macos/entitlements.plist), then notarizes the app and a `/Applications` `.pkg` (Developer ID Installer). See [releases.md](releases.md).

Single instance: `flock` on `{app support}/{mode}/dyson-mac.lock`. A second launch activates `com.entitysystems.dysonharness` and exits. Snip raises `SnipCaptured`; `DysonUiHost` already queues that into the composer.
