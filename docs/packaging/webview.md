# Windows CefSharp host (future packaging + agent browser)

Blazor Interactive Server remains the **current** UI host. This note records the Windows packaging direction and how agent browser windows share that CEF lifecycle.

## Intent

Ship a **Windows desktop shell** that hosts Dyson UI inside **CefSharp WPF** (not WebView2), talking to in-process `Harness.Engine` and the SQLite app-data store. The same CEF process/lifecycle already backs **agent browser windows** opened via MCP (`OpenBrowser`, …).

| Piece | Role |
| ----- | ---- |
| Native host | WPF window chrome + `ChromiumWebBrowser` (`Harness.WindowsBrowser`) |
| Contracts | `IDysonBrowserControl` / `IDysonBrowserWindow` / `IDysonBrowserTab` in `Harness.Abstractions` |
| Agent windows | In-process WPF windows from `DysonCefBrowserControl` (DI singleton on Windows) |
| Future app shell | Same CEF lifecycle will later host the Blazor UI (out of scope for the current change) |
| Engine | In-process `Harness.Engine` (same `DysonAppPaths` / `dyson.db`) |

## Today (Blazor Server)

- UI: `Harness.UI` Interactive Server
- On Windows, `Program.cs` registers `AddSingleton<IDysonBrowserControl, DysonCefBrowserControl>()`
- `DysonUiHost` passes the singleton into `DysonAgentSessionConfig.BrowserControl`
- MCP browser tools appear only when that property is non-null
- Non-Windows: no project reference to `Harness.WindowsBrowser`; browser tools omitted

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
- Do not fork session/resume semantics; use `DysonSessionStore.GetFullSessionAsync` / restore
- Keep providers ephemeral; model profiles remain SQLite rows
- CefSharp NuGet restore supplies CEF binaries; Visual C++ 2022 redistributable is a deployment dependency
- **Executable RID:** `Harness.UI` (Windows) must set `RuntimeIdentifier=win-x64` (and `PlatformTarget=x64`) so CefSharp copies natives (`libcef.dll`, `CefSharp.BrowserSubprocess.exe`, …) next to `Harness.UI.exe`. A library RID on `Harness.WindowsBrowser` alone is not enough — MSBuild only lays out architecture-specific CEF redistributables for the **host** project.
- **Direct CefSharp package on UI:** `Harness.UI` also needs a Windows-only `PackageReference` to `CefSharp.Wpf.NETCore` so CefSharp `buildTransitive` targets run on the Web host (notably `locales\*.pak`). A `ProjectReference` to `Harness.WindowsBrowser` alone does not import those targets into the executable.
- Reuse `DysonNativeFolderPicker` for work-directory registration (host-process OS dialog; interactive desktop required — not for headless/remote Blazor hosts)
- STA: CEF/WPF runs on a dedicated STA thread; Blazor Server stays MTA and marshals via the STA dispatcher
- CEF cache + `cef-debug.log` live under `%LocalAppData%\DysonHarness\`

## Non-goals (for now)

- Embedding Blazor UI inside the CefSharp shell (packaging follow-up)
- Full CDP parity (rich network waterfall, DOM highlight, cookie store UI)
- macOS/Linux browser implementations
- Committing Cef redistributables beyond NuGet restore

## Projects

| Project | TFM | Role |
| ------- | --- | ---- |
| `Harness.Abstractions` | `net10.0` | Result types + browser contracts + `DysonNullBrowserControl` |
| `Harness.WindowsBrowser` | `net10.0-windows` + WPF + `win-x64` | `DysonCefBrowserControl` + chrome |
| `Harness.Engine` | `net10.0` | MCP catalog + executor → `IDysonBrowserControl` |
| `Harness.UI` | `net10.0-windows` + `win-x64` on Windows | Conditional ref + DI singleton; RID required for CEF native copy |
