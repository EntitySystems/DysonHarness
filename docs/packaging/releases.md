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

## Run

1. **Windows (recommended):** download `DysonHarness-{version}-win-x64.msi` and install (default is per-user; no elevation). Launch from the Start Menu shortcut **Dyson Harness**, or run `%LocalAppData%\Programs\DysonHarness\DysonHarness.exe`.
2. **Windows (portable):** download the `win-x64` zip, unzip, and run `DysonHarness.exe` (desktop CEF shell; no separate browser URL needed).
3. **Linux / macOS:** download the zip for your RID, unzip, run `Harness.UI`, and open the agent shell URL printed in the console (default http://localhost:5180) if the browser does not open automatically.

Builds on `master` resolve app mode to **Prod** (`DysonProd` app data) via `GITHUB_REF_NAME` in the resolve-app-mode scripts — see [storage/models.md](../storage/models.md).

## Windows notes

- **MSI:** WiX dual-scope package (`perUserOrMachine`); default is current-user install into `%LocalAppData%\Programs\DysonHarness`. Appears in Apps & Features as **Dyson Harness** under manufacturer **Entity Systems**. Start Menu shortcut **Dyson Harness** uses the app icon. Uninstall removes the install directory and shortcut; app data under `%LocalAppData%\DysonHarness\` is left alone. Zip remains available for portable use.
- **VC++ redistributable:** CefSharp needs the [Visual C++ 2022 x64 redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) on machines that do not already have it (required for both MSI and zip).
- Keep the install/unzip folder intact (`CefSharp.BrowserSubprocess.exe`, `libcef.dll`, `locales\`, etc. must stay next to `DysonHarness.exe`).
- **Single instance:** CEF uses a process singleton on `%LocalAppData%\DysonHarness\`. A second double-click focuses the running window and exits; that is not a packaging failure.
- External http(s) links and popups open in the **OS default browser**; the shell CEF view stays on the local Blazor origin.

Desktop / CefSharp packaging details: [webview.md](webview.md). MSI authoring lives under [`packaging/wix/`](../../packaging/wix/).
