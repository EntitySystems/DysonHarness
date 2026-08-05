# Continuous releases

Every push to `master` publishes self-contained zips (all RIDs) and a Windows **MSI** (`win-x64`) as a GitHub **pre-release** tagged with that run’s CalVer (`YYYY.M.run_number`). Pushes to other branches (for example `dev-bleeding-edge`) do not run continuous release.

| RID | Publish project | Entrypoint |
| --- | --------------- | ---------- |
| `win-x64` | `DysonHarness.UI.Windows` | `DysonHarness.exe` (CefSharp WPF shell hosting Blazor in-process) |
| `linux-x64` / `osx-*` | `Harness.UI` | `Harness.UI` |

Pull requests to `master` and pushes to non-`master` branches run tests only (see [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml)); they do not publish artifacts. Manual **workflow_dispatch** of continuous release is also limited to `master`.

> **Note:** This repo has [immutable releases](https://docs.github.com/en/repositories/releasing-projects-on-github/immutable-releases) enabled, so a fixed rolling tag like `continuous` cannot be reused after the first publish. Each build gets a unique CalVer tag instead.

## Download

- Releases: https://github.com/EntitySystems/DysonHarness/releases
- Newest continuous build (example): https://github.com/EntitySystems/DysonHarness/releases/tag/2026.7.7
- Asset names: `DysonHarness-{version}-{rid}.zip` (all RIDs); `DysonHarness-{version}-win-x64.msi` (Windows installer)

| RID | Runner / OS | Notes |
| --- | ----------- | ----- |
| `win-x64` | Windows | CEF shell + agent browser (`net10.0-windows`); zip + MSI |
| `linux-x64` | Linux | Blazor host only (no agent browser) |
| `osx-arm64` | macOS Apple Silicon | Blazor host only |
| `osx-x64` | macOS Intel (cross-published from Apple Silicon runner) | Blazor host only |

Publish uses `--self-contained true` / `-p:SelfContained=true` (runtime included; no separate .NET install).

## Version (CalVer)

`{year}.{month}.{run_number}` in UTC, month **not** zero-padded (maps to .NET `Major.Minor.Build`).

Example: `2026.7.142`

- Git tag and release title version segment use the same CalVer
- Stamped as `-p:Version=…`
- `InformationalVersion` appends `+{shortSha}` (not in the zip filename)

### `version.json`

`DysonHarness.UI.Windows` writes `version.json` next to `DysonHarness.exe` at build time (MSBuild target `GenerateVersionJson` → [`scripts/write-version-json.ps1`](../../scripts/write-version-json.ps1) / [`.sh`](../../scripts/write-version-json.sh), mirroring the `GenerateAppMode` pattern). It rides along in the publish folder → zip → MSI harvest, so no CI change is needed.

```json
{
  "version": "2026.8.142",
  "informationalVersion": "2026.8.142+abc1234",
  "rid": "win-x64",
  "repo": "EntitySystems/DysonHarness"
}
```

Unstamped local builds fall back to `1.0.0`, which disables the in-app updater.

## In-app updater (Windows)

The Windows shell checks for a newer build once per process, shortly after the UI is ready.

1. `DysonAppVersionInfo` reads `version.json` from `AppContext.BaseDirectory` (assembly `InformationalVersion` as fallback). The updater only runs on Windows and only when the CalVer year is ≥ 2026 — dev builds (`1.0.0`) never check.
2. `DysonGitHubReleaseClient` calls `GET /repos/{repo}/releases?per_page=15` (not `/latest`, because continuous builds are **pre-releases**) and picks the highest-CalVer non-draft release carrying a `*-win-x64.msi` asset.
3. If that tag is strictly newer, `UpdateAvailableModal` prompts with local vs remote version. **Not now** / Escape / backdrop persist the tag in `app_settings` under `ui_update_skipped_version`, so the prompt only returns for a newer release.
4. **Update** streams the MSI to `%TEMP%` with a byte progress bar (modal locks — no dismiss), then runs `cmd /c ping -n 4 127.0.0.1 >nul & msiexec /i "<msi>"` and calls `Environment.Exit(0)`. The short delay plus process exit releases CEF/WPF file locks before the WiX major upgrade replaces `%LocalAppData%\Programs\DysonHarness`. (`ping` rather than `timeout`: a `WinExe` host has no console and `timeout` aborts without one.)

Download or hand-off failures unlock the modal with the message and a **Close** button — the install is never forced. Types live in [`Harness.UI/Services`](../../src/Harness/Harness.UI/Services); parsing Facts are in `DysonAppUpdateTests`.

## Run

1. **Windows (recommended):** download `DysonHarness-{version}-win-x64.msi` and install (default is per-user; no elevation). Launch from the Start Menu shortcut **Dyson Harness**, or run `%LocalAppData%\Programs\DysonHarness\DysonHarness.exe`.
2. **Windows (portable):** download the `win-x64` zip, unzip, and run `DysonHarness.exe` (desktop CEF shell; no separate browser URL needed).
3. **Linux / macOS:** download the zip for your RID, unzip, run `Harness.UI`, and open the agent shell URL printed in the console (default http://localhost:5180) if the browser does not open automatically.

Builds on `master` resolve app mode to **Prod** (`DysonProd` app data) via `GITHUB_REF_NAME` in the resolve-app-mode scripts — see [storage/models.md](../storage/models.md).

## Windows notes

- **MSI:** WiX dual-scope package (`perUserOrMachine`); default is current-user install into `%LocalAppData%\Programs\DysonHarness`. Appears in Apps & Features as **Dyson Harness** under manufacturer **Entity Systems**. Start Menu shortcut **Dyson Harness** uses the app icon. Uninstall removes the install directory and shortcut; app data under `%LocalAppData%\DysonHarness\` is left alone. Zip remains available for portable use. Asset filename keeps CalVer (`DysonHarness-{version}-win-x64.msi`); MSI `ProductVersion` maps `YYYY.M.N` → `(YYYY%100).M.N` because Windows Installer requires major &lt; 256.
- **VC++ redistributable:** CefSharp needs the [Visual C++ 2022 x64 redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) on machines that do not already have it (required for both MSI and zip).
- Keep the install/unzip folder intact (`CefSharp.BrowserSubprocess.exe`, `libcef.dll`, `locales\`, etc. must stay next to `DysonHarness.exe`).
- **Single instance:** CEF uses a process singleton on `%LocalAppData%\DysonHarness\`. A second double-click focuses the running window and exits; that is not a packaging failure.
- External http(s) links and popups open in the **OS default browser**; the shell CEF view stays on the local Blazor origin.

Desktop / CefSharp packaging details: [webview.md](webview.md). MSI authoring lives under [`packaging/wix/`](../../packaging/wix/).
