# Storage contracts (Wave 0 freeze)

Functional persistence APIs for LocalDb and external providers. Implementations scope rows via `IDysonSubjectContext`.

## Visibility

| Area | List / get | Write |
| ---- | ---------- | ----- |
| Sessions, workdirs, favorites, shells, subject settings | Current subject only. Cross-subject get-by-id → error. | Current subject only. |
| Model providers / slugs | `SubjectId == current` **or** `SubjectId == shared`. | Subject-owned: row must belong to current. Shared: requires `IDysonAccessEvaluator.Can(ManageSharedProviders)`. |

Sentinel `DysonSubjects.Shared` (`"shared"`) is not a real subject row and must never be a cookie subject. `DysonSubjects.Local` (`"local"`) is the desktop fixed subject (distinct from workspace FS `local_fs`).

## Subject settings

`IDysonSubjectSettingsRepository` replaces the public role of app-settings store: `EnsureSubjectAsync`, `GetSettingAsync`, `SetSettingAsync` (null/whitespace value deletes).

## RBAC groundwork

`DysonRole` / `DysonPermission` / `IDysonAccessEvaluator` are stubs. `DysonPermissiveAccessEvaluator` allows all permissions (local default + cloud interim).
