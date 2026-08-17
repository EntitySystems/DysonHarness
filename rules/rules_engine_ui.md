# Engine / UI boundary

All Dyson functional code that is not directly tied to a UI layer lives in `Harness.Engine`. Shared contracts and Result types live in `Harness.Abstractions`.

UI assemblies (`Harness.UI`, `DysonHarness.UI.Windows`, `Harness.WindowsBrowser`) only hook onto the engine: bind events, render chrome, adapt platform I/O.

Write engine code as if it may run embedded on a server, headless, or behind another UI. No Blazor circuit, no `DysonUiHost`, no “the focused chat” assumption.

## Allowed in UI

- Razor / CSS / theme
- Circuit lifetime
- Composer chrome
- CefSharp / file pickers
- Thin adapters that call engine APIs

## Not allowed in UI

- Session loop
- Tool execution
- Provider calls
- Persistence policy
- Multi-session ownership / cancel / drain / ask-wait
- Any helper another host would have to copy

Engine and Abstractions must never reference UI assemblies.

Functional tests target engine types in `Harness.Tests`. Host tests only when the hook itself is the subject.

Existing host debt is not a license to add more. When you touch a host method that is actually functional, move that piece then — no drive-by rewrite of `DysonUiHost`.
