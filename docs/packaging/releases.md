# Continuous releases

Every push to `master` publishes self-contained **Harness.UI** zips as a GitHub **pre-release** tagged with that run’s CalVer (`YYYY.M.run_number`).

Pull requests to `master` run tests only (see [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml)); they do not publish artifacts.

> **Note:** This repo has [immutable releases](https://docs.github.com/en/repositories/releasing-projects-on-github/immutable-releases) enabled, so a fixed rolling tag like `continuous` cannot be reused after the first publish. Each build gets a unique CalVer tag instead.

## Download

- Releases: https://github.com/EntitySystems/DysonHarness/releases
- Newest continuous build (example): https://github.com/EntitySystems/DysonHarness/releases/tag/2026.7.7
- Asset names: `DysonHarness-{version}-{rid}.zip`

| RID | Runner / OS | Notes |
| --- | ----------- | ----- |
| `win-x64` | Windows | Includes CefSharp browser build (`net10.0-windows`) |
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

1. Download the zip for your RID and unzip.
2. Run `Harness.UI.exe` (Windows) or `Harness.UI` (Linux / macOS).
3. Open the agent shell URL printed in the console (default http://localhost:5180) if the browser does not open automatically.

Builds on `master` resolve app mode to **Prod** (`DysonProd` app data) via `GITHUB_REF_NAME` in the resolve-app-mode scripts — see [storage/models.md](../storage/models.md).

## Windows notes

- **VC++ redistributable:** CefSharp needs the [Visual C++ 2022 x64 redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) on machines that do not already have it.
- First launch may unpack CEF natives next to the exe; keep the unzipped folder intact.

Desktop / CefSharp packaging details: [webview.md](webview.md).
