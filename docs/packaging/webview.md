# Windows CefSharp host (desktop shell + agent browser)

Blazor Interactive Server remains the UI stack. On Windows continuous builds, **`DysonHarness.UI.Windows`** hosts that UI inside a CefSharp WPF window (`DysonHarness.exe`). Agent browser windows share the same CEF lifecycle.

Shipped continuous zips (all RIDs): [releases.md](releases.md).

## Intent

Ship a **Windows desktop shell** that hosts Dyson UI inside **CefSharp WPF** (not WebView2), talking to in-process `Harness.Engine` and the SQLite app-data store. The same CEF process/lifecycle backs **agent browser windows** opened via MCP (`OpenBrowser`, …).

| Piece | Role |
| ----- | ---- |
| Native host | WPF window chrome + `ChromiumWebBrowser` (`DysonHarness.UI.Windows`); title bar / app icon track in-app `dysonTheme` (DWM immersive dark + light/dark `.ico`) |
| Contracts | `IDysonBrowserControl` / `IDysonBrowserWindow` / `IDysonBrowserTab` in `Harness.Abstractions` |
| Agent windows | In-process WPF windows from `DysonCefBrowserControl` (DI singleton on Windows) |
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

Agent browser chrome has a **Snip** button to the right of the address bar. Clicking it enters rubber-band mode over the page content (Esc cancels). On a valid drag:

1. The active tab takes a full-viewport DevTools screenshot (`TakeScreenshotAsync`; optional `timeoutMs`, default **30s**, linked to the prompt cancellation token so cancel/timeout cannot hang forever)
2. The selection (DIP) is mapped to pixel bounds and cropped to JPEG in WPF
3. `IDysonBrowserControl.SnipCaptured` raises `DysonBrowserSnipPayload` (`ImageBytes`, empty `HtmlRef`, `FileName` = `browser-snip.jpg`)
4. `DysonUiHost` compresses via `DysonUserImageFactory` and `QueuePendingImage` — the thumbnail appears in the composer; the user still types/sends (no auto-send)

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
- **`ClearBrowserCache` MCP:** clears that shared HTTP cache via CDP (`Network.ClearBrowserCache`) once, then hard-reloads every tab in every open **agent** browser window. Cookies/site storage are untouched. The shell WebView is not hard-reloaded, but its HTTP cache is cleared because it shares the same CEF profile.
- If `Cef.Initialize` fails for other reasons, the exception includes `ResultCode`, cache paths, and `BrowserSubprocessPath` (also check `%LocalAppData%\DysonHarness\cef-debug.log` and the VC++ 2022 x64 redistributable)

## Non-goals

- Custom title-bar UI beyond standard Windows chrome
- macOS/Linux desktop shells
- Installers (MSI etc.)
- Full CDP parity (rich network waterfall, DOM highlight, cookie store UI)
- Committing Cef redistributables beyond NuGet restore

## Projects

| Project | TFM | Role |
| ------- | --- | ---- |
| `Harness.Abstractions` | `net10.0` | Result types + browser contracts + `DysonNullBrowserControl` |
| `Harness.WindowsBrowser` | `net10.0-windows` + WPF + `win-x64` | `DysonCefBrowserControl` + chrome + `DysonCefStaHost` |
| `Harness.Engine` | `net10.0` | MCP catalog + executor → `IDysonBrowserControl` |
| `Harness.UI` | `net10.0-windows` + `win-x64` on Windows | Blazor host (`DysonUiWebHost`); conditional WindowsBrowser ref + DI singleton |
| `DysonHarness.UI.Windows` | `net10.0-windows` + WPF + `win-x64` | CEF shell exe (`AssemblyName=DysonHarness`) |
