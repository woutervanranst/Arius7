# Arius.Api + Arius.Web vertical-slice restructure — design

- **Date:** 2026-07-11
- **Status:** approved (brainstormed with Wouter Van Ranst)
- **Scope:** `src/Arius.Api/`, `src/Arius.Web/`, their test projects (`Arius.Api.Tests`, `Arius.Api.Integration.Tests`, `Arius.Api.Testing`, Web specs/e2e), `Arius.Architecture.Tests`, and affected docs.

## 1. Goal & constraints

`Arius.Api` and `Arius.Web` were written quickly ("vibecoded") and are structured by technical layer. Restructure both to `Arius.Core`'s structural grammar — `Features/<Slice>/` + `Shared/<Mechanic>/`, thin features coordinating shared mechanics — in two phases so feature parity is provable at every step.

**Phase 1 — rewrite production code; tests are the regression net.**

- Test files may only receive mechanical edits (`using`/namespace/import-path lines), plus edits explicitly tied to an enumerated cleanup (below). Any other test edit required to stay green means contract drift — stop and surface it.
- The wire contract (REST routes, DTO shapes, SignalR hub route `/hubs/arius`, method and message names) and the app SQLite schema stay identical, **except** individually enumerated "obvious vibecode debris" cleanups. The implementation plan names each cleanup explicitly before execution — including the exact test edits it entails; anything not on the list is out of scope.
- Naming aligns with `Arius.Core` wherever the concept matches: same folder grammar, per-operation contracts colocated with their handler, slice-named services, per-slice registration extensions.

**Phase 2 — restructure tests + quality pass; production code is frozen.**

- Zero production edits. If an improved test exposes a real bug, surface it; any fix is a separate, explicitly flagged commit.
- ADR-0011's 90% production line coverage floor holds throughout both phases.

**Both phases:** boundaries are mechanically enforced (`Arius.Architecture.Tests` for the Api, an ESLint boundary rule for the Web), per the repo's standing ethos (ADR-0010/0013: enforced, not just documented).

## 2. Arius.Api target structure

Approach: **Core's grammar without a mediator, REPR endpoints inside resource slices.** Core uses Mediator because multiple hosts dispatch into it; the Api *is* a host — HTTP/SignalR already is its dispatch layer, so a second in-process mediator would be ceremony without a consumer. Instead, each resource slice contains one self-contained REPR endpoint class per operation (owning its Request/Response types), giving Core's per-operation grain where it matters without ~30 top-level folders for CRUD-weight operations.

```text
Arius.Api/
├── Features/
│   ├── Accounts/       ListAccounts.cs · GetAccount.cs · CreateAccount.cs · UpdateAccount.cs
│   │                   DeleteAccount.cs · StreamContainers.cs (hub partial) · AccountsSlice.cs
│   ├── Repositories/   ListRepositories.cs · GetRepository.cs · CreateRepository.cs
│   │                   UpdateRepository.cs · DeleteRepository.cs · RepositoriesSlice.cs
│   ├── Browse/         ListSnapshots.cs · StreamEntries.cs (hub partial) · BrowseSlice.cs
│   ├── Statistics/     GetStatistics.cs · StatisticsSlice.cs
│   ├── Search/         SearchAll.cs (hub partial) · SearchSlice.cs
│   ├── Filesystem/     ListFilesystem.cs · FilesystemSlice.cs
│   ├── Jobs/           StartArchive.cs · StartRestore.cs · AttachToJob.cs · DetachFromJob.cs
│   │                   CancelJob.cs · ApproveRestore.cs · DeclineRestore.cs · SetAutoResume.cs
│   │                   ResumeRestore.cs · ListJobs.cs · GetJob.cs · GetJobWarnings.cs
│   │                   JobsHub.cs (partial root) · JobRunner.cs · JobSink.cs
│   │                   ArchiveForwarders.cs · RestoreForwarders.cs · JobStateRegistry.cs
│   │                   JobViewResolver.cs · JobFormat.cs · JobSnapshot.cs · PersistedJobState.cs
│   │                   RestoreApprovalRegistry.cs · StaleApprovalSweepService.cs
│   │                   RehydrationPollingService.cs · RehydrationSchedule.cs · JobsSlice.cs
│   └── Schedules/      ListSchedules.cs · CreateSchedule.cs · DeleteSchedule.cs
│                       SchedulerService.cs · SchedulesSlice.cs
├── Shared/
│   ├── AppData/        AppDatabase.cs (whole) · Records.cs · JobStatuses.cs · SecretProtector.cs
│   ├── Composition/    RepositoryProviderRegistry.cs · IRepositoryCoreComposer.cs
│   │                   AzureRepositoryCoreComposer.cs
│   └── Extensions/     PeriodicTimerExtensions.cs
└── Program.cs · AriusApiHost.cs · AssemblyMarker.cs
```

Exact file names for REPR operations are indicative; the implementation plan fixes them against the real route/method inventory.

### Load-bearing decisions

- **Single hub, split via `partial class`.** SignalR does not multiplex hubs over one connection, and Microsoft's guidance is thin hubs delegating to services. `/hubs/arius` and all method/message names are frozen; `JobsHub` becomes `partial`, its root in `Features/Jobs/`, with each foreign method (`StreamContainers`, `StreamEntries`, `SearchAll`) as a thin partial file in its owning slice. Every hub method is a 1–3-line delegation into that slice's services, so no partial file touches a foreign slice.
- **`AppDatabase` stays a shared concept.** It, `Records.cs`, `JobStatuses.cs`, and `SecretProtector.cs` remain whole in `Shared/AppData/` — mirroring Core's "thin features + rich `Shared/`" reality. No per-slice store split, no schema change.
- **`Contracts/` dissolves.** Every DTO moves to the REPR operation file (request/response types) or, where shared across a slice's operations, a slice-local `Models.cs` — mirroring `Features/RestoreCommand/Models.cs` in Core.
- **Schedules is its own slice** (cron rows + `SchedulerService`); it enqueues into Jobs. Because features must not reference other features' internals, the enqueue surface becomes a small interface in `Shared/` (e.g., `Shared/Composition/IJobDispatcher`) implemented by `JobRunner` — the one deliberate cross-slice seam. Executor decides the exact shape; folding Schedules into Jobs is the fallback if the interface turns out artificial.
- **Dependency rules** (enforced by `Arius.Architecture.Tests`):
  - `Features/X` may reference `Shared/` and itself.
  - `Shared/` never references `Features/`.
  - No feature references another feature's internals.
- **Per-slice wiring:** each slice exposes an `<X>Slice.cs` with its `Add`/`Map` extensions; `Program.cs` becomes a composition root calling one line per slice.

## 3. Arius.Web target structure

Approach: **slice-ify state & API** — features keep their layout; per-feature state and API surface move into their slices; `core/` shrinks to genuine plumbing.

```text
src/app/
├── core/                     ← only cross-cutting plumbing
│   ├── realtime.service.ts       (single SignalR connection + reconnect)
│   ├── ktui/ · metronic-init · notification.service
├── features/
│   ├── overview/
│   ├── repos/
│   ├── repo/                 repo-detail · snapshot-bar · files/ · statistics/ · properties/
│   │                         + snapshot.store.ts (moved from core/state) + repo.api.ts
│   ├── jobs/                 jobs · job-detail · jobs.api.ts · models.ts
│   ├── pill/                 job-pill.component + job-pill.store (moved)
│   ├── search/               global-search-overlay + search.store (moved)
│   ├── drawer/               account/archive-restore/properties drawers + drawer.store (moved)
│   ├── wizards/              add/ · create/ (+ their api calls)
│   └── settings/
├── shared/                   format.ts · job-format.ts · cost-calculator · folder-picker
│                             layered-bar · state-legend · state-ring · models.ts (cross-feature DTOs)
```

### Load-bearing decisions

- **`ApiService` dissolves into per-feature `<feature>.api.ts` services**, each owning only its endpoints. `RealtimeService` stays in `core/` — one WebSocket connection is cross-cutting plumbing (same reasoning as the single hub server-side); feature stores subscribe through it.
- **`api-models.ts` dissolves by ownership rule:** a DTO used by one feature colocates in that feature's `models.ts`; a DTO used by 2+ features (job DTOs feed jobs, pill, and drawer) goes to `shared/models.ts` — the codebase's existing precedent (`job-format.ts` already lives there). No feature-to-feature imports.
- **Import direction rule, mirroring the Api's:** `features/` may import `core/` and `shared/`; never another feature. `core/` and `shared/` never import `features/`. Enforced with an ESLint boundary rule (`no-restricted-imports` or eslint-plugin-boundaries).
- **Web feature names stay UI-capability names** (overview, repo, jobs, pill…) — they already roughly track the Api slices; 1:1 renames would be churn without benefit.

## 4. Phase 1 execution strategy

- **Branch/PR shape:** one branch + PR per phase, off `master`. If `jobs-progress` is unmerged when this starts, it lands first — the Jobs slice is exactly where it overlaps.
- **Order: `Shared/` first, then slices smallest→largest.** Move `AppData`, `Composition`, `Extensions` into `Shared/` first (pure moves + namespace updates), then convert slices one at a time: Filesystem → Accounts → Repositories → Browse → Statistics → Search → Schedules → **Jobs last** (largest; every earlier slice establishes conventions before the riskiest one).
- **Each slice conversion:** move files, split its DTOs out of `Contracts/`, rewrite grouped endpoints as REPR operation files, extract its hub partial, add `<X>Slice.cs` wiring — then build + `Arius.Api.Tests` + `Arius.Api.Integration.Tests` green before the next slice starts. `Program.cs` shrinks as slices convert.
- **Web conversion follows the same slice-at-a-time rhythm:** per-feature `*.api.ts` + `models.ts` splits, store moves, `ng build` + `ng test` green after each.
- **Architecture tests + ESLint boundary rule land at the end of Phase 1**, once the structure exists to assert against.
- **Phase 1 exit gate:** all .NET test projects, `ng test`, and the hermetic Playwright e2e green, with no test edits beyond mechanical ones and those declared by enumerated cleanups — the feature-parity proof. Belt-and-braces: dump the route table + hub method inventory before/after and diff.

## 5. Phase 2 execution strategy

- `Arius.Api.Tests` reorganizes to mirror `Features/<Slice>/` + `Shared/`, adopting `Arius.Core.Tests` naming conventions (TUnit idioms per repo standard).
- Integration tests keep their **scenario grain** (cross-slice by nature) but group per capability (lifecycle, approvals, reattach, representation…).
- **Quality pass:** delete dead/duplicate tests, rename unclear ones, fill seam gaps so every REPR operation has at least contract-level coverage (unit or integration). Coverage gate: ≥90% production line coverage (ADR-0011).
- **Web:** spec files move with their source files in Phase 1 (they are colocated); Phase 2 splits/renames specs to match new module boundaries and applies the same quality pass. Playwright e2e specs stay untouched.

## 6. Verification, docs, deliverables

- **Gates at every step:** `dotnet build`, all .NET test projects, `ng build`, `ng test`, hermetic Playwright e2e. Full (real-Azure) e2e once per phase before PR.
- **Docs sync at the end of each phase** (`update-docs` flow): `docs/design/hosts/web.md` structure references; `docs/guide/development.md` if it mentions layout.
- **One new ADR:** *adopt vertical-slice structure for host projects* — recording REPR-inside-resource-slices, the single-partial-hub decision, the `Shared`/`Features` dependency rules, and their mechanical enforcement.
- **Deliverables:** Phase 1 PR, Phase 2 PR, the ADR, updated docs — each phase independently shippable.

## Plan-time corrections (2026-07-11, during implementation planning)

Deep source analysis while writing the implementation plan corrected two Section 2/3 mechanisms. The goals are unchanged; the mechanisms are.

1. **Hub: slice-service delegation instead of `partial class` files.** All parts of a partial class share one namespace, so a partial file in `Features/Accounts/` would still have to declare `namespace Arius.Api.Features.Jobs` — breaking folder=namespace and slice attribution for the architecture tests. Instead: `JobsHub` stays one class in `Features/Jobs/` (its name is test-referenced; the route + method names are wire-frozen), and each foreign method becomes a 1-line delegation to a service owned by its slice — `Accounts.ContainerNameService` (StreamContainers), `Browse.EntryStreamer` (StreamEntries), `Search.RepositorySearcher` (SearchAll). These services are `public` only because they are injected into the public hub's constructor (the ADR-0010 "public because infrastructure requires it" precedent). The architecture test whitelists exactly these hub→slice references as the documented single-hub delivery seam. `EntryDto`/`StateFlagsDto`/`EntryMapping` are consumed by both Browse and Search and move to `Shared/Entries/`.
2. **Web: the usage map contradicts per-feature API/store ownership.** Measured imports show `drawer.store` is consumed by drawer+overview+repo, `job-pill.store` by pill+drawer (and injected by `drawer.store` itself), `search.store` by the app shell+search, and most `ApiService` methods are called from 2+ features. Therefore: only `snapshot.store` moves (to `features/repo/` — all three consumers live there); `drawer.store`, `job-pill.store`, `search.store` remain in `core/state` as cross-feature orchestrators. `ApiService` and `api-models.ts` split **per server domain inside `core/api/`** — `accounts`, `repos`, `browse`, `statistics`, `jobs`, `schedules`, `fs`, `health` — mirroring the Api's slice map 1:1 instead of the UI feature map. Import direction becomes `features → shared → core` (core imports nothing above it), enforced by ESLint (`eslint-plugin-boundaries`; no ESLint exists today, so the plan introduces a minimal boundary-only config).

## Considered alternatives

- **Full Core clone with Mediator (rejected):** every Api operation as command/query + handler. Maximum naming identity, but adds a second mediator layer to a host whose only caller is HTTP/SignalR, doubling type count for no consumer. ADR-0010's own rationale (Mediator exists for the host boundary) does not apply inside a host.
- **Pure file moves (rejected):** relocate files under `Features/` without splitting `Contracts/Dtos.cs` or `JobEndpoints.cs`. Cheapest, but slices would stay entangled — Core's folder names without Core's property.
- **Full per-operation top-level folders (rejected):** `Features/CreateAccountCommand/` etc., ~30 folders. Core-identical naming, but Core's per-operation folders earn their keep with heavyweight pipelines; the Api's operations are mostly CRUD-weight.
- **Per-feature SignalR hubs (rejected):** breaks the frozen wire contract and multiplies client connections (SignalR does not multiplex hubs over one connection) for a single-SPA host.
- **Splitting `AppDatabase` into per-slice stores (rejected):** the app DB is a shared mechanic, like Core's `Shared/` services; splitting it adds seams without a driver and risks schema drift.
