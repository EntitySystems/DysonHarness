# Continuous releases

Continuous publishing has two tracks:

| Git branch | Channel (`version.json`) | GitHub release |
| ---------- | ------------------------ | -------------- |
| `master` | `stable` | Full release (not pre-release) |
| `release-preview` | `preview` | Pre-release (`--prerelease`) |

Every push to either branch publishes self-contained zips (all RIDs) and a Windows **MSI** (`win-x64`) tagged with that run’s CalVer (`YYYY.M.run_number`). Pushes to other branches (for example `dev-bleeding-edge`) do not run continuous release.

| RID | Publish project | Entrypoint |
| --- | --------------- | ---------- |
| `win-x64` | `DysonHarness.UI.Windows` | `DysonHarness.exe` (CefSharp WPF shell hosting Blazor in-process) |
| `linux-x64` / `osx-*` | `Harness.UI` | `Harness.UI` |

Pull requests to `master` / `release-preview` and pushes to other branches run tests only (see [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml)); they do not publish artifacts. Manual **workflow_dispatch** of continuous release is limited to those two refs.

> **Note:** This repo has [immutable releases](https://docs.github.com/en/repositories/releasing-projects-on-github/immutable-releases) enabled, so a fixed rolling tag like `continuous` cannot be reused after the first publish. Each build gets a unique CalVer tag instead. Stable and preview share the same CalVer scheme (no separate numbering per track).

## Retention

Each **successful** continuous publish runs retention for its own channel after the release and its assets are published and verified:

- **Stable (`master`):** retain the four newest non-prerelease GitHub Releases.
- **Preview (`release-preview`):** retain the four newest prerelease GitHub Releases.

The channels are retained independently. For each older selected release, cleanup uses `gh release delete --cleanup-tag`: it deletes the GitHub Release, its downloadable assets, and the corresponding CalVer tag. It does **not** delete the tagged commit, GitHub Actions workflow runs, or GitHub Actions artifacts.

## Download

- Releases: https://github.com/EntitySystems/DysonHarness/releases
- Prefer the latest **stable** (non-prerelease) MSI for production installs; preview builds are marked as GitHub pre-releases
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
- Channel stamped as `-p:DysonReleaseChannel=stable|preview` (from the publishing branch)

### `version.json`

`DysonHarness.UI.Windows` writes `version.json` next to `DysonHarness.exe` at build time (MSBuild target `GenerateVersionJson` → [`scripts/write-version-json.ps1`](../../scripts/write-version-json.ps1) / [`.sh`](../../scripts/write-version-json.sh), mirroring the `GenerateAppMode` pattern). It rides along in the publish folder → zip → MSI harvest. CI passes `-p:DysonReleaseChannel=…`; local builds leave the property empty and the script derives channel from `GITHUB_REF_NAME` / the current git branch (`master`/`main` → `stable`, `release-preview` → `preview`, else `preview`).

```json
{
  "version": "2026.8.142",
  "informationalVersion": "2026.8.142+abc1234",
  "channel": "preview",
  "rid": "win-x64",
  "repo": "EntitySystems/DysonHarness"
}
```

Unstamped local builds fall back to `1.0.0`, which disables the in-app updater (and omits the sidebar channel badge).

## In-app updater (Windows)

The Windows shell checks for a newer build on the **same channel** once per process, shortly after the UI is ready.

1. `DysonAppVersionInfo` reads `version.json` from `AppContext.BaseDirectory` (assembly `InformationalVersion` as fallback). The updater only runs on Windows and only when the CalVer year is ≥ 2026 — dev builds (`1.0.0`) never check. Missing/unknown `channel` defaults to `preview`.
2. `DysonGitHubReleaseClient` calls `GET /repos/{repo}/releases?per_page=15` and picks the highest-CalVer non-draft release whose `prerelease` flag matches the local channel (`preview` → prerelease only; `stable` → non-prerelease only) and that carries a `*-win-x64.msi` asset. (`/latest` is not used — it only surfaces non-prereleases, so preview builds would never see updates.)
3. If that tag is strictly newer, `UpdateAvailableModal` prompts with local vs remote version. **Not now** / Escape / backdrop persist the tag in `app_settings` under `ui_update_skipped_version`, so the prompt only returns for a newer release.
4. **Update** streams the MSI to `%TEMP%` with a byte progress bar (modal locks — no dismiss), then runs `cmd /c ping -n 4 127.0.0.1 >nul & msiexec /i "<msi>"` and calls `Environment.Exit(0)`. The short delay plus process exit releases CEF/WPF file locks before the WiX major upgrade replaces `%LocalAppData%\Programs\DysonHarness`. (`ping` rather than `timeout`: a `WinExe` host has no console and `timeout` aborts without one.) The updater only hands off to msiexec; it does not start the new exe. After a successful install, the MSI launches `DysonHarness.exe`.

Download or hand-off failures unlock the modal with the message and a **Close** button — the install is never forced. Types live in [`Harness.UI/Services`](../../src/Harness/Harness.UI/Services); parsing Facts are in `DysonAppUpdateTests`.

### Manual check

**Settings → System** provides a user-invoked **Check for updates** action. It queries the effective repository and channel, then shows the newest matching GitHub Release with a link to that release page. The lookup also works on development and non-Windows hosts, so those builds can inspect the applicable release even though they cannot install it in-app.

In-app MSI installation remains available only when a newer release is found for a stamped Windows build. In that case the existing update prompt supplies the normal download and install flow; the manual check does not introduce a separate installer experience.

Stable and preview share the same MSI ProductCode / major-upgrade path — operators on one track update within that track; side-by-side stable+preview installs are not supported.

## Website download contract

Patch instructions for [dysonharness.com](https://dysonharness.com) (Website repo — **not** edited in this workspace):

1. **Stable only on the marketing download card.** Prefer `GET https://api.github.com/repos/EntitySystems/DysonHarness/releases/latest` (GitHub’s latest **non-prerelease**), or when listing releases filter `prerelease === false`, `draft === false`, asset `DysonHarness-*-win-x64.msi`, then sort by CalVer or `published_at`.
2. **Do not** use “newest including pre-release” logic in `site.js` for the primary installer card.
3. Label: e.g. “Stable release” (drop “Continuous build — pre-release” on that card).
4. Optional: separate “Preview builds” link to `https://github.com/EntitySystems/DysonHarness/releases` (GitHub’s pre-release UI) — not required for the primary CTA.
5. Cache key: bump `dh-windows-msi-v1` → `dh-windows-msi-stable-v1` so old cached prerelease URLs are not reused.

## Run

1. **Windows (recommended):** download `DysonHarness-{version}-win-x64.msi` and install (default is per-user; no elevation). A successful install also starts the app automatically. You can launch again from the Start Menu shortcut **Dyson Harness**, or run `%LocalAppData%\Programs\DysonHarness\DysonHarness.exe`.
2. **Windows (portable):** download the `win-x64` zip, unzip, and run `DysonHarness.exe` (desktop CEF shell; no separate browser URL needed).
3. **Linux / macOS:** download the zip for your RID, unzip, run `Harness.UI`, and open the agent shell URL printed in the console (default http://localhost:5180) if the browser does not open automatically.

Builds on `master` and `release-preview` resolve app mode to **Prod** (`DysonProd` app data) via `GITHUB_REF_NAME` in the resolve-app-mode scripts — channel is independent of Dev/Test/Prod. See [storage/models.md](../storage/models.md).

## Windows notes

- **MSI:** WiX dual-scope package (`perUserOrMachine`); default is current-user install into `%LocalAppData%\Programs\DysonHarness`. Appears in Apps & Features as **Dyson Harness** under manufacturer **Entity Systems**. Start Menu shortcut **Dyson Harness** uses the app icon. A successful first install or major upgrade starts `DysonHarness.exe` when msiexec finishes; uninstall, repair, and modify do not. Uninstall removes the install directory and shortcut; app data under `%LocalAppData%\DysonHarness\` is left alone. Zip remains available for portable use. Asset filename keeps CalVer (`DysonHarness-{version}-win-x64.msi`); MSI `ProductVersion` maps `YYYY.M.N` → `(YYYY%100).M.N` because Windows Installer requires major &lt; 256.
- **VC++ redistributable:** CefSharp needs the [Visual C++ 2022 x64 redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) on machines that do not already have it (required for both MSI and zip).
- Keep the install/unzip folder intact (`CefSharp.BrowserSubprocess.exe`, `libcef.dll`, `locales\`, etc. must stay next to `DysonHarness.exe`).
- **Single instance:** CEF uses a process singleton on `%LocalAppData%\DysonHarness\`. A second double-click focuses the running window and exits; that is not a packaging failure.
- External http(s) links and popups open in the **OS default browser**; the shell CEF view stays on the local Blazor origin.

Desktop / CefSharp packaging details: [webview.md](webview.md). MSI authoring lives under [`packaging/wix/`](../../packaging/wix/).
