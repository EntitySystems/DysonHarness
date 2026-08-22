# Storage contracts (Wave 0 freeze)

Functional persistence APIs for LocalDb and external providers. Implementations scope rows via `IDysonSubjectContext`.

## Visibility

| Area | List / get | Write |
| ---- | ---------- | ----- |
| Sessions, workdirs, favorites, shells, subject settings, usage requests | Current subject only. Cross-subject get-by-id → error. | Current subject only. |
| Plugin installations | Current subject only. `ListAsync(null)` returns globals; `ListAsync(workDirectoryId)` returns globals plus that owned project's records. | Current subject only. Project records require an owned work directory and a package root under its `.dyson/plugins`; global records require a null work-directory id. |
| Plugin protected values / hook security | Current subject's owning installation only. Ordinary variable APIs expose presence/redacted metadata; hook reviews default deny; audit rows are bounded metadata only. | Ciphertext only for values. Review upsert/revoke is keyed by installation + hook + supported event. Hook audit repository exposes append/list only. |
| Model providers / slugs | `SubjectId == current` **or** `SubjectId == shared`. | Subject-owned: row must belong to current. Shared: requires `IDysonAccessEvaluator.Can(ManageSharedProviders)`. |

Sentinel `DysonSubjects.Shared` (`"shared"`) is not a real subject row and must never be a cookie subject. `DysonSubjects.Local` (`"local"`) is the desktop fixed subject (distinct from workspace FS `local_fs`).

## Subject settings

`IDysonSubjectSettingsRepository` replaces the public role of app-settings store: `EnsureSubjectAsync`, `GetSettingAsync`, `SetSettingAsync` (null/whitespace value deletes).

## Usage analytics

`IDysonUsageAnalyticsRepository` appends one `usage_requests` row per successful OpenAI-compatible Completions/Responses round (`AppendAsync`, `ListAsync`, `ListByRootSessionAsync`). Current subject only; no FKs.

## RBAC groundwork

`DysonRole` / `DysonPermission` / `IDysonAccessEvaluator` are stubs. `DysonPermissiveAccessEvaluator` allows all permissions (local default + cloud interim).
