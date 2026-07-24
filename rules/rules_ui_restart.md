# UI restart (local dev)

After completing a task that affects the Blazor UI — or when in doubt after any `Harness.UI` / `Harness.Engine` change that needs a running local UI — restart the dev server on **http://localhost:5180** before considering the task done.

## When this applies

- Changes under `src/Harness/Harness.UI/` (components, CSS, `wwwroot`, `Program.cs`, demo host)
- Engine changes that alter session/turn/streaming behavior the UI binds to
- When unsure whether a local UI is needed: restart anyway

Skip for docs-only or unrelated backend edits with no UI impact.

## Steps (repo root)

1. **Stop** whatever is listening on port 5180 (Windows / PowerShell):

```powershell
Get-NetTCPConnection -LocalPort 5180 -ErrorAction SilentlyContinue |
  ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
```

2. **Start** the UI (background; keep running):

```powershell
dotnet run --project src/Harness/Harness.UI --urls http://localhost:5180
```

3. **Confirm** the server is listening — e.g. `Get-NetTCPConnection -LocalPort 5180 -State Listen`, or open http://localhost:5180 and verify a response.

Do not report the task complete until the restart succeeds or you explain why a restart was skipped.
