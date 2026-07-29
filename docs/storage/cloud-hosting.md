# Cloud hosting support (storage + subjects)

Product goal: run DysonHarness as a multi-subject (eventually multi-user) host while keeping the same functional persistence APIs. This page is the architecture note; schema details live in [models.md](models.md), [sessions.md](sessions.md), and [work-directories.md](work-directories.md). Contracts are frozen in [`Harness.Abstractions/Storage`](../../src/Harness/Harness.Abstractions/Storage/) ([`STORAGE_CONTRACTS.md`](../../src/Harness/Harness.Abstractions/Storage/STORAGE_CONTRACTS.md)).

**Status:** contracts and LocalDb/subject wiring land in phased waves. User accounts, login, and real role assignment are **documented intent only** — not implemented yet.

## Subject model

Persistence is scoped by a **subject id** (`IDysonSubjectContext.SubjectId`), not by workspace filesystem identity.

| Constant | Value | Meaning |
| -------- | ----- | ------- |
| `DysonSubjects.Local` | `"local"` | Desktop / local-host fixed subject. Default when hosting mode is Local. |
| `DysonSubjects.Shared` | `"shared"` | Sentinel for **shared model providers** only. Not a real `subjects` row; never a cookie value. |
| `DysonWorkspaceSubjects.LocalFs` | `"local_fs"` | Workspace FS subject for `IDysonWorkspaceFileSystem` — **distinct** from persistence `"local"`. |

Cloud mode may mint other subject ids (Guid strings). Always-subject-owned data: sessions, work directories, favorites, app settings (via subject settings), configured shells. Model providers are either subject-owned or shared (see below).

### `subjects` table

| Property | Notes |
| -------- | ----- |
| `Id` | string PK (e.g. `"local"` or cloud Guid string) |
| `CreatedUtc` | `DateTime` UTC |
| `UserId` | string? — **reserved for future user binding**; unused today |

`IDysonSubjectSettingsRepository.EnsureSubjectAsync` upserts the current context’s row and must never ensure `"shared"`.

## Shared model providers

Each `model_providers` row has `SubjectId` = owning subject **or** `DysonSubjects.Shared`.

- **List / resolve** providers and slugs: `SubjectId == current` **or** `SubjectId == shared`.
- **Default create** remains subject-owned (current subject).
- **Create / update / delete** of shared providers requires `IDysonAccessEvaluator.Can(DysonPermission.ManageSharedProviders)`.
- Subject-owned mutations require the row’s `SubjectId == current`.
- Child `model_slugs` stay parent-scoped (no redundant `SubjectId`).
- Favorites stay subject-owned even when they point at a shared provider’s slug.
- Session `ModelSlugId` may reference a slug under a shared provider; resume/resolve must accept shared visibility.
- Shared and per-subject providers may coexist (Guid PKs). List/display is a union; uniqueness rules apply within scope.

## Repository boundary

Callers depend on functional interfaces in `Harness.Abstractions` (not generic CRUD, not concrete EF stores):

| Interface | Owns |
| --------- | ---- |
| `IDysonSessionRepository` | Sessions, turns, logs, todos |
| `IDysonWorkDirectoryRepository` | Work directory registrations |
| `IDysonModelRepository` | Providers, slugs, favorites (`shared` flag on create/update/managed upsert) |
| `IDysonConfiguredShellRepository` | Configured shells |
| `IDysonSubjectSettingsRepository` | `EnsureSubjectAsync` + subject-scoped KV settings |

Visibility: subject-owned tables filter/write the current `IDysonSubjectContext.SubjectId` only; cross-subject get-by-id → hard error. Model providers follow the shared rules above. Full note: [`STORAGE_CONTRACTS.md`](../../src/Harness/Harness.Abstractions/Storage/STORAGE_CONTRACTS.md).

POCOs / request DTOs live in Abstred (EF-free). Engine and UI inject interfaces; they do not own EF types.

## LocalDb vs external providers

| Project | Role |
| ------- | ---- |
| `Harness.Abstractions` | Contracts: `IDyson*Repository`, `IDysonSubjectContext`, `DysonSubjects`, RBAC stubs, persistence DTOs |
| `Harness.LocalDb` | Shipped SQLite implementation: `DysonDbContext`, Fluent config, migrations, `DysonDbAccessor`, `DysonSqliteConfigurator`, EF repository impls, `AddDysonLocalDb(...)` |
| Consuming repos | Other DB backends implement the same interfaces; not shipped here |

This repo ships **LocalDb (EF SQLite)** only. Non-SQLite providers live outside this repository.

## Forever `dyson-subject` cookie

Config key `DysonHosting:Mode` = `Local` | `Cloud` (default **Local**).

| Mode | Subject | Cookie |
| ---- | ------- | ------ |
| **Local** | Fixed `DysonSubjects.Local` (`"local"`) | No cookie |
| **Cloud** | From forever HttpOnly cookie `dyson-subject` (Secure when HTTPS, SameSite=Lax, far-future expiry) | Mint Guid string on first visit when missing/invalid → `EnsureSubjectAsync` → set cookie |

`"shared"` is never minted as a cookie subject. Access evaluator remains permissive until roles bind to users (see RBAC).

## User-binding intent (not implemented)

Later:

- Each subject binds to a user (`subjects.UserId`).
- Authenticated user’s bound subject **must** match the `dyson-subject` cookie.
- No user tables, login, OIDC, or auth↔subject enforcement in code yet.

## RBAC groundwork (intent + stubs)

| Role (`DysonRole`) | Meaning |
| ------------------ | ------- |
| `Member` | Own subject data; use shared providers; manage own favorites / settings / sessions / workdirs / shells |
| `Admin` | Everything Member can do, plus manage **shared** model providers (and later deploy-wide settings) |

| Permission (`DysonPermission`) | Meaning |
| ------------------------------ | ------- |
| `ManageOwnSubjectData` | Mutate data owned by the current subject (own-data checks may stay implicit via subject filter; exists for host overrides) |
| `ManageSharedProviders` | Create / update / delete shared model providers |

Ship stubs now:

- `IDysonAccessEvaluator` — `Can(DysonPermission)` (+ foreshadowed `Roles`)
- `DysonPermissiveAccessEvaluator` — all permissions `true` (Local default + Cloud interim)
- Shared-provider write paths go through `Can(ManageSharedProviders)` so a future non-permissive evaluator is a drop-in

**Not in scope yet:** role assignment UI, admin UI, claims-based evaluator, auth integration.

`IDysonSubjectContext` carries `SubjectId` only today; future work may add roles / user id on the same context or a sibling `IDysonPrincipal`.

## Related

- Workspace FS cloud note (separate from persistence subjects): [work-directories.md — Cloud hosting / custom implementations](work-directories.md#cloud-hosting--custom-implementations)
- Engine API / persistence-facing types: [api-surface.md](../engine/api-surface.md)
