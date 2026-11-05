# Continuous releases

Continuous publishing has two tracks:

| Git branch | Channel (`version.json`) | GitHub release |
| ---------- | ------------------------ | -------------- |
| `master` | `stable` | Full release (not pre-release) |
| `release-preview` | `preview` | Pre-release (`--prerelease`) |

Every push to either branch publishes self-contained zips (all RIDs), Windows **MSIs** (`win-x64` and `win-arm64`), and signed macOS **packages** (`osx-arm64`, `osx-x64`) tagged with that run’s CalVer (`YYYY.M.run_number`). Pushes to other branches (for example `dev-bleeding-edge`) do not run continuous release.

| RID | Publish project | Entrypoint |
| --- | --------------- | ---------- |
| `win-x64` | `DysonHarness.UI.Windows` | `DysonHarness.exe` (CefSharp WPF shell hosting Blazor in-process) |
| `win-arm64` | `DysonHarness.UI.Windows` | Same shell, cross-published for Windows ARM64 |
| `linux-x64` | `Harness.UI` | `Harness.UI` |
| `osx-arm64` / `osx-x64` | `DysonHarness.UI.macOS` | `Dyson Harness.app` (preview: `Dyson Harness (Preview).app`). WKWebView shell hosting Blazor in-process; agent browser is a second window |

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
- Every publish output (so every zip, the MSI, and the macOS pkg) also contains `LICENSE` and `THIRD-PARTY-NOTICES.md` (MIT attribution for the CLIProxyAPI-derived native xAI and ChatGPT Codex providers).
- Asset names: `DysonHarness-{version}-{rid}.zip` (all RIDs); `DysonHarness-{version}-win-x64.msi` and `DysonHarness-{version}-win-arm64.msi` (Windows installers); `DysonHarness-{version}-osx-arm64.pkg` and `DysonHarness-{version}-osx-x64.pkg` (macOS installers). The `win-x64` names are unchanged.

| RID | Runner / OS | Notes |
| --- | ----------- | ----- |
| `win-x64` | Windows x64 | CEF shell + agent browser (`net10.0-windows`); zip + MSI. Existing asset names, unchanged |
| `win-arm64` | Windows ARM64, cross-published from the x64 Windows runner | Same shell. Zip `DysonHarness-{version}-win-arm64.zip` plus MSI `DysonHarness-{version}-win-arm64.msi`. CefSharp supplies `chromiumembeddedframework.runtime.win-arm64` |
| `linux-x64` | Linux | Blazor host only (no agent browser) |
| `osx-arm64` | macOS Apple Silicon | WKWebView shell + agent browser window. Prefer `DysonHarness-{version}-osx-arm64.pkg` (installs to `/Applications`). The zip is the same stapled app |
| `osx-x64` | macOS Intel (cross-published from the Apple Silicon runner, including `swiftc -target x86_64-apple-macos14`) | Same `.app` and a matching `.pkg` |

Publish uses `--self-contained true` / `-p:SelfContained=true` (runtime included; no separate .NET install).

## Version (CalVer)

`{year}.{month}.{run_number}` in UTC, month **not** zero-padded (maps to .NET `Major.Minor.Build`).

Example: `2026.7.142`

- Git tag and release title version segment use the same CalVer
- Stamped as `-p:Version=…`
- `InformationalVersion` appends `+{shortSha}` (not in the zip filename)
- Channel stamped as `-p:DysonReleaseChannel=stable|preview` (from the publishing branch)

Preview features: `DYSON_PREVIEW` is defined for every project under `src/Harness` unless the build is not Debug and `-p:DysonReleaseChannel=stable` (`src/Harness/Directory.Build.props`). Debug builds define it even when the channel is stable, so local dev can run Settings → Demo. A Release build with the channel set to stable compiles out the demo page, its CSS, `wwwroot/dyson-demo.js`, `Harness.UI/Demo/Scenarios`, and the `DYSON_PREVIEW` symbol. `DysonPreviewFeatures` stays in the assembly with the flag false. The settings nav checks that flag, so the Demo link is not in the stable UI. The stable publish is the only one that passes that channel on a Release configuration.

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

Unstamped local builds fall back to `1.0.0`, which disables in-app installation (and omits the sidebar channel badge). A manual check can still look up the channel release.

## In-app updater

The shell checks for a newer build on the **same channel** once per process, shortly after the UI is ready. Windows installs from the MSI that matches the **running process** architecture (`-win-arm64.msi` for an arm64 process, `-win-x64.msi` otherwise). macOS installs from the `.pkg` that matches `version.json` `rid` (`osx-arm64` or `osx-x64`).

1. `DysonAppVersionInfo` reads `version.json` from `AppContext.BaseDirectory` (assembly `InformationalVersion` as fallback), once at host start (`DysonUiWebHost.Create`). In-app install runs only when the CalVer year is ≥ 2026, and only on Windows or on macOS when `rid` is `osx-arm64` or `osx-x64`. Dev builds (`1.0.0`) stay lookup-only. Missing/unknown `channel` defaults to `preview`.
2. `DysonGitHubReleaseClient` calls `GET /repos/{repo}/releases?per_page=15` and picks the highest-CalVer non-draft release whose `prerelease` flag matches the local channel (`preview` → prerelease only; `stable` → non-prerelease only) and that carries this host’s installer asset: on Windows `*-win-arm64.msi` when `RuntimeInformation.ProcessArchitecture` is Arm64, otherwise `*-win-x64.msi`; on macOS `*-{rid}.pkg`. (`/latest` is not used — it only surfaces non-prereleases, so preview builds would never see updates.) Hosts that cannot install in-app still look up that Windows MSI asset so a manual check can show a release. An x64 install running under emulation on an ARM device reports Arm64 for the OS and X64 for the process, so it stays on the x64 MSI. It switches only when the user installs the arm64 package.
3. If that tag is strictly newer, `UpdateAvailableModal` prompts with local vs remote version. **Not now** / Escape / backdrop persist the tag in `app_settings` under `ui_update_skipped_version`, so the prompt only returns for a newer release.
4. **Update** streams the installer to the temp directory with a byte progress bar (modal locks — no dismiss), then exits so the installer can replace the app.
   - **Windows:** `cmd /c ping -n 4 127.0.0.1 >nul & msiexec /i "<msi>"` and `Environment.Exit(0)`. The short delay plus process exit releases CEF/WPF file locks before the WiX major upgrade replaces `%LocalAppData%\Programs\DysonHarness`. (`ping` rather than `timeout`: a `WinExe` host has no console and `timeout` aborts without one.) The updater only hands off to msiexec; it does not start the new exe. After a successful install, the MSI launches `DysonHarness.exe`.
   - **macOS:** a detached `nohup` bash helper waits until this process is gone, then runs `installer -pkg "<pkg>" -target /` through an administrator password dialog (`osascript` `do shell script … with administrator privileges`). When that exits it runs `open` on the bundle for this build's channel: `/Applications/Dyson Harness.app` (stable) or `/Applications/Dyson Harness (Preview).app` (preview). A cancelled or failed install opens that same path so the current app comes back. The pkg has no postinstall launch, which would start the app twice.

Download or hand-off failures unlock the modal with the message and a **Close** button — the install is never forced. Types live in [`Harness.UI/Services`](../../src/Harness/Harness.UI/Services); parsing Facts are in `DysonAppUpdateTests`.

### Manual check

**Settings → System** provides a user-invoked **Check for updates** action. It queries the effective repository and channel, then shows the newest matching GitHub Release with a link to that release page. The lookup also works on development builds and on platforms without an in-app installer, so those builds can inspect the applicable release even though they cannot install it in-app.

In-app installation is available when a newer release is found for a stamped Windows build, or for a stamped macOS build whose `rid` is `osx-arm64` or `osx-x64`. Settings shows **Available on this Windows build** or **Available on this Mac** in those cases. The existing update prompt supplies the download and install flow; the manual check does not introduce a separate installer experience.

Stable and preview share the same MSI ProductCode / major-upgrade path — operators on one track update within that track; side-by-side stable+preview installs are not supported. On macOS the two channels install side by side: stable to `/Applications/Dyson Harness.app`, preview to `/Applications/Dyson Harness (Preview).app`. They keep one bundle id (`com.entitysystems.dysonharness`) and one app-data folder. The first pkg install after this change deletes the old `/Applications/DysonHarness.app` and nothing under `~/Library`.

## Website download contract

Patch instructions for [dysonharness.com](https://dysonharness.com) (Website repo — **not** edited in this workspace):

1. **Stable only on the marketing download card.** Prefer `GET https://api.github.com/repos/EntitySystems/DysonHarness/releases/latest` (GitHub’s latest **non-prerelease**), or when listing releases filter `prerelease === false`, `draft === false`, asset `DysonHarness-*-win-x64.msi`, then sort by CalVer or `published_at`.
2. **Do not** use “newest including pre-release” logic in `site.js` for the primary installer card.
3. Label: e.g. “Stable release” (drop “Continuous build — pre-release” on that card).
4. Optional: separate “Preview builds” link to `https://github.com/EntitySystems/DysonHarness/releases` (GitHub’s pre-release UI) — not required for the primary CTA.
5. Cache key: bump `dh-windows-msi-v1` → `dh-windows-msi-stable-v1` so old cached prerelease URLs are not reused.

## Run

1. **Windows x64 (recommended):** download `DysonHarness-{version}-win-x64.msi` and install (default is per-user; no elevation). A successful install also starts the app automatically. You can launch again from the Start Menu shortcut **Dyson Harness**, or run `%LocalAppData%\Programs\DysonHarness\DysonHarness.exe`.
2. **Windows ARM64:** download `DysonHarness-{version}-win-arm64.msi`. It is a separate product (its own upgrade code) and installs to `%LocalAppData%\Programs\DysonHarness-arm64`. It does not upgrade or remove an x64 install. An x64 install left running under emulation on an ARM device keeps updating from the x64 MSI.
3. **Windows (portable):** download the `win-x64` or `win-arm64` zip, unzip, and run `DysonHarness.exe` (desktop CEF shell; no separate browser URL needed).
4. **Linux:** download the zip, unzip, run `Harness.UI`, and open the URL printed in the console (Kestrel default http://localhost:5000; there is no `launchSettings.json` in the publish output) if the browser does not open automatically. Local debug (`dotnet run`, Development) uses http://localhost:5181 instead — see [UI](../ui/README.md).
5. **macOS:** download `DysonHarness-{version}-osx-arm64.pkg` (Apple Silicon) or `DysonHarness-{version}-osx-x64.pkg` (Intel) and install. A stable package puts **Dyson Harness** in `/Applications`; a preview package puts **Dyson Harness (Preview)** there. Both ask for an administrator password. The WKWebView shell binds an ephemeral loopback address, same as the Windows desktop shell. The zip is that same app if you do not want the installer. Details: [webview.md](webview.md).

Builds on `master` and `release-preview` resolve app mode to **Prod** (`DysonProd` app data) via `GITHUB_REF_NAME` in the resolve-app-mode scripts — channel is independent of Dev/Test/Prod. See [storage/models.md](../storage/models.md).

Speech (Settings → Voice) is not in the zip, the MSI, or the macOS pkg, and the release workflow downloads nothing for it. Runtimes and weights are downloaded at install time, per model the user checks, from the URLs and hashes in `packaging/speech/manifest.json`, into `{DysonProd}/speech/{stable|preview}`. Python installs fetch CPython, CPU torch, and the Phonon-2 (Fermion BPS packed) / Pocket TTS / Qwen3-TTS 0.6B Base / Chatterbox Turbo (English only) safetensors weights. GGUF installs fetch transcribe.cpp v0.4.0 plus the Nemotron 3.5 streaming Q5 GGUF, and CrispASR v0.8.43 plus the Phonon 2, Pocket TTS, Qwen3-TTS, and Chatterbox Turbo GGUF weights. Whisper tiny and base fetch the ggml file plus the OpenVINO wheels and FP16 IR at install, not in the zip. That folder is app data, so an MSI upgrade keeps it.

## Windows notes

- **MSI:** WiX dual-scope package (`perUserOrMachine`). The x64 MSI is unchanged: default current-user install into `%LocalAppData%\Programs\DysonHarness`, upgrade code `3887F848-1A76-413B-91F0-04CA3CB459AF`, so existing installs still major-upgrade in place. The arm64 MSI (`-arch arm64`) uses upgrade code `7C4E1B92-6D30-4F8A-A5E2-19B0C6D47F31` and installs into `%LocalAppData%\Programs\DysonHarness-arm64`, so the two packages never remove each other. Both appear in Apps & Features as **Dyson Harness** under manufacturer **Entity Systems**. The x64 Start Menu folder and shortcut stay **Dyson Harness** (component GUID `1E5DB2C9-836C-4DF7-9F4C-A919E74DC731`). The arm64 shortcut is **Dyson Harness (ARM64)** with its own component GUID (`A6F03C18-4B27-4E91-8D55-2C7E90B14FA6`) and its own keypath value (`InstalledArm64` under the same registry key), so uninstalling one product does not remove the other's shortcut or its keypath. The x64 keypath stays `Installed`. A successful first install or major upgrade starts `DysonHarness.exe` when msiexec finishes; uninstall, repair, and modify do not. Uninstall removes that package’s install directory and shortcut; app data under `%LocalAppData%\DysonHarness\` is left alone (and is shared by both architectures). Zip remains available for portable use. Asset filenames keep CalVer (`DysonHarness-{version}-win-x64.msi`, `DysonHarness-{version}-win-arm64.msi`); MSI `ProductVersion` maps `YYYY.M.N` → `(YYYY%100).M.N` because Windows Installer requires major &lt; 256. `win-arm64` is cross-published from the x64 Windows runner (`dotnet publish -r win-arm64`); no ARM runner is required.
- **Windows signing:** continuous release signs every unsigned `.exe` and `.dll` in the `win-*` publish folder, then the MSI, with [Azure Artifact Signing](https://learn.microsoft.com/en-us/azure/artifact-signing/overview) (Public Trust), timestamped (`http://timestamp.acs.microsoft.com`). The order is: sign the inner files, zip, build the MSI from that same signed publish folder, sign the MSI. Both the zip and the MSI therefore carry the signed bytes. Earlier builds ran the MSI step before the inner signing and shipped unsigned files inside a signed MSI. After the MSI is signed, the job runs an administrative extract (`msiexec /a`) into `$RUNNER_TEMP` and fails if `scripts/list-unsigned-pe.ps1` finds any `.exe` or `.dll` that is not Valid. Asset names and the release gate (5 zips + 2 MSIs + 2 pkgs) do not change. [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) and a local `dotnet build packaging/wix/DysonHarness.Installer.wixproj` stay unsigned, the same way a local macOS bundle stays ad-hoc signed.
  Credentials are repository secrets and variables, never files in this repo. Secrets: `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_CERTIFICATE_PEM` (raw PEM text: the certificate block plus an unencrypted `BEGIN PRIVATE KEY` block, not base64, not a PFX, no password). `ARTIFACT_SIGNING_ENDPOINT`, `ARTIFACT_SIGNING_ACCOUNT` and `ARTIFACT_SIGNING_PROFILE` may be repository variables (preferred, they are not sensitive) or repository secrets; a variable wins when both are set. The Windows publish rows fail fast when any of those is empty, and that failure blocks the whole continuous release.
- **VC++ redistributable:** CefSharp needs the [Visual C++ 2022 redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) that matches the install (x64 or ARM64) on machines that do not already have it (required for both MSI and zip).
- Keep the install/unzip folder intact (`CefSharp.BrowserSubprocess.exe`, `libcef.dll`, `locales\`, etc. must stay next to `DysonHarness.exe`).
- **Single instance:** CEF uses a process singleton on `%LocalAppData%\DysonHarness\`. A second double-click focuses the running window and exits; that is not a packaging failure.
- External http(s) links and popups open in the **OS default browser**; the shell CEF view stays on the local Blazor origin.

Desktop / CefSharp packaging details: [webview.md](webview.md). MSI authoring lives under [`packaging/wix/`](../../packaging/wix/).

## macOS notes

- Prefer the `.pkg`. It installs into `/Applications` and asks for an administrator password. Finder, Launchpad, Spotlight, the Dock and the menu bar show the bundle's file name: **Dyson Harness** on stable (`/Applications/Dyson Harness.app`) and **Dyson Harness (Preview)** on preview (`/Applications/Dyson Harness (Preview).app`). `CFBundleName` and `CFBundleDisplayName` match that name. The bundle id stays `com.entitysystems.dysonharness` and the executable stays `DysonHarness`. The pkg's postinstall removes a leftover `/Applications/DysonHarness.app` from before this rename; it does not touch app data. The zip contains the same renamed app. Release asset names are unchanged.
- The zip is the same notarized app, for a copy that does not use the installer.
- Continuous release signs the app with **Developer ID Application** (hardened runtime, `packaging/macos/entitlements.plist`), builds the pkg with **Developer ID Installer**, then notarizes and staples both. The credentials are repository secrets, not files in this repo: `APPLE_TEAM_ID`, `APPLE_CERTIFICATE_P12_BASE64`, `APPLE_CERTIFICATE_PASSWORD`, `APPLE_API_KEY_ID`, `APPLE_API_ISSUER_ID`, `APPLE_API_PRIVATE_KEY`.
- Pull-request CI and a local `scripts/bundle-macos-app.sh` stay ad-hoc signed. Gatekeeper still asks for a right-click Open on those builds. The macOS publish job fails if any of the secrets above is empty, and that failure blocks the whole continuous release.
