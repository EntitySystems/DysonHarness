# UI restart (local dev)

After completing a task that affects the Blazor UI — or when in doubt after any `Harness.UI` / `Harness.Engine` change that needs a running local UI — restart the **debug** dev server on **http://localhost:5181** before considering the task done.

The installed Windows desktop shell (`DysonHarness.exe`, MSI / `%LocalAppData%\Programs\DysonHarness`) is the live harness. It binds an ephemeral loopback port (`http://127.0.0.1:0` in `DysonHarness.UI.Windows/App.xaml.cs`), not 5181 and not a fixed port. Never stop, restart, or kill that process. Do not stop a listener just because it is not on 5181 — identify `DysonHarness.exe` first.

## When this applies

- Changes under `src/Harness/Harness.UI/` (components, CSS, `wwwroot`, `Program.cs`, demo host)
- Engine changes that alter session/turn/streaming behavior the UI binds to
- When unsure whether a local UI is needed: restart the debug UI anyway

Skip for docs-only or unrelated backend edits with no UI impact.

## Steps (repo root)

1. **Stop** only the debug listener on port **5181** (Windows / PowerShell). Do not stop `DysonHarness.exe`:

```powershell
Get-NetTCPConnection -LocalPort 5181 -ErrorAction SilentlyContinue |
  ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
```

2. **Start** the debug UI (background; keep running). `dotnet run` without `--urls` reads `Properties/launchSettings.json` (`http` profile, `ASPNETCORE_ENVIRONMENT=Development`) and binds **5181**. `DysonUiWebHost` also binds 5181 for Development when no URL was passed (`dotnet run --no-launch-profile`):

```powershell
dotnet run --project src/Harness/Harness.UI --launch-profile http
```

`--urls http://localhost:5181` is equivalent. Do not pass another port.

3. **Confirm** http://localhost:5181 returns 200 — e.g. `Invoke-WebRequest http://localhost:5181 -UseBasicParsing` — or `Get-NetTCPConnection -LocalPort 5181 -State Listen`.

Do not report the task complete until the debug restart succeeds or you explain why a restart was skipped.
