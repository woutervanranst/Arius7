# Implement the Arius.Web design handoff

## Context

`openspec/changes/2026-06-16-arius-web-claude-design-handoff/` is a **high-fidelity design handoff** (an HTML prototype `Arius.Web.dc.html` + a detailed `README.md`) for a web management UI for Arius — the deduplicating, encrypted, chunked Azure-Blob archival tool that today ships only as a CLI (`Arius.Cli`) and a WPF desktop app (`Arius.Explorer`). The handoff asks us to recreate the prototype in a **target stack we must build from scratch**:

- **`Arius.Web`** — an Angular app styled with **Metronic v9 (Tailwind + KTUI)**, recreating 8 screens pixel-faithfully.
- **`Arius.Api`** — a new **.NET minimal API over `Arius.Core`** (relays Core's command/query handlers; owns a small app SQLite for accounts/repos/jobs/schedules; runs a cron scheduler).
- **Two small `Arius.Core` additions** — `SnapshotsQuery` and a stats aggregation.

Everything is **greenfield** (no web/API/Angular exists). The app manages multiple repositories (one Azure Blob container each), overlays the local filesystem on the archive with **time-travel across snapshots**, streams live **archive/restore** progress, and supports a mid-stream **restore cost-approval** handshake.

### Decisions made with the user
1. **Scope: full build, all 5 phases** — read-only browser → streaming archive/restore → accounts/repos/jobs CRUD + cron scheduler → Docker. Built and verified phase-by-phase but as one body of work.
2. **Streaming: SignalR throughout** — one hub carries entry streaming, archive/restore/list logs, the jobs feed, and the inline restore cost-approval request→response.
3. **Frontend: Angular**, scaffolded from the **official `keenthemes/metronic-tailwind-html-integration` → `metronic-tailwind-angular` seed** (Angular 20 + Tailwind v4), with the licensed Metronic source dropped in from `~/Downloads/metronic-v9.5.0`.

### Key finding that shaped the plan
Metronic v9.5.0 ships **no Angular component library** — per the official docs it is **Tailwind CSS classes + a JS library** (`core.bundle.js`, `KTComponents.init()` / `KTLayout.init()`). You author your own Angular components using Metronic's CSS classes (copied from the HTML demos / `demo8`) and **re-init the JS on navigation**. So all bespoke controls (state ring, scrubber, tree, drawers, stepper, tabs) are hand-built in Angular; KTUI is used only for dropdowns/menus.

### Verified Core facts (ground truth for the API)
- All projects target **`net10.0`**; solution is `src/Arius.slnx`; central versions in `src/Directory.Packages.props`.
- Core uses **source-generated Mediator** (martinothamar) v3.0.2 — NOT MediatR. `IMediator.Send(ICommand)`, `IMediator.CreateStream(IStreamQuery)`, `IMediator.Publish(INotification)`. **`AddMediator()` must be called in the outermost assembly** (here `Arius.Api`) so the generator discovers `INotificationHandler<T>` in both Core and Api.
- `AddArius(services, IBlobContainerService, string? passphrase, string accountName, string containerName)` registers everything as **singletons scoped to ONE repository** (`src/Arius.Core/ServiceCollectionExtensions.cs`). It registers a `NullBlobServiceFactory` **unless a real `IBlobServiceFactory` is already present** — so the API must register `new AzureBlobServiceFactory()` (from `Arius.AzureBlob`, a parameterless `public` class) before `AddArius`, or container discovery returns empty.
- Composition recipe (mirror `Arius.Cli/CliBuilder.cs:BuildProductionServices`): `blobServiceFactory.CreateAsync(account,key)` → `OpenContainerServiceAsync(container, PreflightMode)` → `IBlobContainerService`; then `services.AddMediator(); services.AddArius(...)`; `BuildServiceProvider()`.
- Handlers: `ArchiveCommand:ICommand<ArchiveResult>`, `RestoreCommand:ICommand<RestoreResult>`, `RepairChunkIndexCommand`, `ListQuery:IStreamQuery<RepositoryEntry>`, `ContainerNamesQuery(AccountName,AccountKey?):IStreamQuery<string>`, `ChunkHydrationStatusQuery:IStreamQuery<ChunkHydrationStatusResult>`.
- `RestoreOptions.ConfirmRehydration : Func<RestoreCostEstimate, CancellationToken, Task<RehydratePriority?>>?` — return `null` to cancel, else `Standard`/`High`. `RestoreCostEstimate` is rich (chunk counts + per-priority EUR costs + `TotalStandard`/`TotalHigh`).
- Progress is reported both via `INotification` events (`Features/ArchiveCommand/Events.cs`, `Features/RestoreCommand/Events.cs`) and via `IProgress<long>` factory + queue-depth callbacks on the `*Options` records. The CLI bridges these through a singleton `ProgressState` + `INotificationHandler<T>` (`Arius.Cli/ProgressState.cs`, `Commands/*/ *ProgressHandlers.cs`) — that pattern assumes **one command per process**, so the multi-job API needs per-job correlation (below).
- `[Flags] RepositoryEntryState`: `LocalPointer=1, LocalBinary=2, LocalDirectory=4, Repository=8, RepositoryHydrated=16, RepositoryArchived=32, RepositoryRehydrating=64`. Entries: `RepositoryFileEntry(RelativePath, State, ContentHash?, OriginalSize, Created, Modified)`, `RepositoryDirectoryEntry(RelativePath, State, FileTreeHash?)`.
- `ISnapshotService` has `ListBlobNamesAsync()` + `ResolveAsync(version)`; `SnapshotManifest{Timestamp, RootHash, FileCount, TotalSize, AriusVersion}`. `ListQueryOptions.Version` is `StartsWith`-matched against the snapshot blob filename (the timestamp string).

---

## Architecture

```
Angular (Arius.Web, Metronic v9 Tailwind/KTUI)
   │  REST (JSON)  +  SignalR hub (/hubs/arius)
   ▼
Arius.Api (.NET 10 minimal API)
   ├─ RepositoryProviderRegistry → per-repo IServiceProvider (AddMediator + AddArius)
   ├─ JobsHub + per-job event forwarders → SignalR groups (keyed by jobId)
   ├─ App SQLite (storage_accounts, repositories, jobs, schedules) on a mounted volume
   └─ SchedulerService (BackgroundService + Cronos) → cron archive jobs
   ▼
Arius.Core (Mediator handlers) ──► Azure Blob (containers) + ~/.arius caches (chunk index, filetree, snapshots)
```

Runs as **one Docker container on Synology** (API serves the built Angular as static files); app SQLite, Data-Protection keys, and `~/.arius` caches live on a mounted volume; each repo's `local_path` maps to a mounted host folder.

---

## Part A — Arius.Core additions (2 vertical slices)

Follow existing conventions: vertical slice under `src/Arius.Core/Features/<Name>/`, `internal` types except the Mediator contract, numbered `// ── Stage N ──` banners, register with a per-repo factory in `AddArius` exactly like the existing handlers.

### A1. `SnapshotsQuery` — `Features/SnapshotsQuery/`
```csharp
public sealed record SnapshotsQuery() : ICommand<IReadOnlyList<SnapshotInfo>>;
public sealed record SnapshotInfo(string Version, DateTimeOffset Timestamp, long FileCount);
```
- Not a stream (snapshot sets are tiny; `ListBlobNamesAsync` is already materialized).
- `Version` = the snapshot blob filename (the timestamp string formatted with `SnapshotService.TimestampFormat`) so the UI can round-trip it back into `ListQuery`/`RestoreCommand` `Version` (which `StartsWith`-matches it). The README's "v28" labels are pure UI ordinals derived from position.
- Handler: `ListBlobNamesAsync()` → for each, `ResolveAsync(version)` (disk-cache-first, cheap) → `SnapshotInfo(version, manifest.Timestamp, manifest.FileCount)`.
- **1-line Core touch:** add an `internal string GetVersion(RelativePath blobName)` to `ISnapshotService` (the blob→filename logic is currently `private static GetSnapshotFileName`). Keeps the slice from reaching into static helpers.

### A2. `StatsQuery` — `Features/StatsQuery/`
```csharp
public sealed record StatsQuery(string? Version = null) : ICommand<RepositoryStats>;
public sealed record RepositoryStats(long Files, long OriginalSize, long StoredSize, long UniqueChunks, bool IsPending);
```
- `Files`/`OriginalSize` from `SnapshotManifest.FileCount`/`.TotalSize`.
- `StoredSize`/`UniqueChunks` from the chunk index, **over DISTINCT chunk_hash** (tar-bundled files share one chunk/size — summing per content-hash would massively over-count):
  ```sql
  SELECT COUNT(*) AS unique_chunks, COALESCE(SUM(chunk_size),0) AS stored_size
  FROM (SELECT chunk_hash, MAX(chunk_size) AS chunk_size FROM chunk_index_entries GROUP BY chunk_hash);
  ```
- **Core touch:** add `internal ChunkIndexStats GetStats()` + `internal bool IsFullyLoaded()` to `IChunkIndexService`/`ChunkIndexService`, delegating to a new aggregate query in `ChunkIndexLocalStore`. Handlers already call `internal` members within the assembly (e.g. `ListQueryHandler` uses `IChunkIndexService.LookupAsync`), so no `InternalsVisibleTo` needed.
- **Honor the "pending" caveat (README §2b):** the chunk index loads shards lazily per prefix; a cold cache under-reports. Return current local-store numbers and set `IsPending=true` until all prefixes for the latest snapshot are loaded (`IsFullyLoaded()` — counts `loaded_prefixes` vs the 256 two-char prefix universe). **Never** force a full chunk-index download for a stats-tab open.

Register both in `AddArius` with the same per-repo `AddSingleton<ICommandHandler<...>>(sp => new …Handler(...))` factory style as the existing handlers. These are the **only** Core changes (2 slices + 3 tiny `internal` accessors + 2 registrations); no event-system or public-surface change.

Tests: `Arius.Core.Tests` (TUnit) — `SnapshotsQuery` against a seeded fake blob container; `StatsQuery` distinct-chunk aggregation + `IsPending` behavior on a partially-loaded index.

---

## Part B — Arius.Api (new project `src/Arius.Api`)

`Microsoft.NET.Sdk.Web`, `net10.0`, references `Arius.Core` + `Arius.AzureBlob`, copies the **exact** `Mediator.SourceGenerator` `PackageReference` block from `Arius.Cli.csproj`. Add to `Arius.slnx`. New packages in `Directory.Packages.props`: **`Cronos`** (cron parsing) — SignalR + Data Protection + static files ship in the framework; `Microsoft.Data.Sqlite`, `Mediator.*`, `Serilog.*`, `Azure.Storage.Blobs` already present. Add `Arius.Api.Tests` under `/Tests/`.

Folder layout:
```
src/Arius.Api/
  Program.cs                  builder, Serilog, AddMediator, MapHub<JobsHub>, endpoint groups, scheduler, static-file SPA fallback
  Composition/                RepositoryProvider, RepositoryProviderRegistry, AccountProvider
  Endpoints/                  Overview, Repos, Accounts, Snapshots, Stats, Jobs, Schedules (minimal-API groups)
  Hubs/                       JobsHub, JobSink, *Forwarders (INotificationHandler<T>), RestoreApprovalRegistry
  Jobs/                       SchedulerService, JobQueue, JobRunner
  Data/                       AppDatabase, AccountStore, RepositoryStore, JobStore, ScheduleStore, SecretProtector
  Contracts/                  request/response DTOs + domain→DTO mappers
```

### B1. Per-repository composition — `RepositoryProviderRegistry`
`AddArius` produces stateful, cache-owning, **`IDisposable`** singletons (`ChunkIndexService` owns a SQLite store), so providers must be reused, not rebuilt per request. Two lifetimes:
- **Read/interactive providers** (List, Snapshots, Stats, ContainerNames): one **cached, long-lived** provider per `repoId` in a `ConcurrentDictionary<repoId, Lazy<Task<ServiceProvider>>>`, `PreflightMode.ReadOnly`. Warm caches across requests. **Evict + dispose** on `PATCH /repos/{id}` (key/passphrase/path change), repo delete, and **archive completion** (snapshot changed → caches stale).
- **Long-running command providers** (Archive, Restore): a **fresh, dedicated** provider per job, owned by the job, disposed in `finally`. Rationale: (a) event isolation — each provider has its own `IMediator`, so per-job notification handlers receive only that job's events with **no correlation id needed**; (b) `ChunkIndexService` is single-shot after `FlushAsync` (archive) and must not poison the shared read provider.
- **Serialize writers per repo** (the JobQueue runs ≤1 archive/restore per repo at a time) to avoid two write-providers contending on the same on-disk SQLite. Reads stay concurrent (WAL).
- `AccountProvider`: a lightweight provider (no container bound) that registers the **real `AzureBlobServiceFactory`** for `ContainerNamesQuery` (the Add-existing wizard) — otherwise the `NullBlobServiceFactory` default returns an empty list.

### B2. Event → SignalR bridge
Hybrid, leaning on per-job provider isolation:
- **`*Options` callbacks as captured closures** (zero global state): when starting a job we hold `jobId` + `IHubContext<JobsHub>`; build options whose `CreateHashProgress`/`CreateUploadProgress`/`CreateLargeFileDownloadProgress`/queue callbacks `SendAsync` to `Clients.Group(jobId)`.
- **`INotification` events via a per-job `JobSink`**: register `JobSink{ JobId, IHubContext }` as a singleton **in the job's provider**, plus `INotificationHandler<T>` forwarders (in `Arius.Api`, discovered by `AddMediator()`) that push each event to `Clients.Group(JobId)` as a typed `Log`/progress message. This is the direct analogue of the CLI's `ProgressState`+handlers, but per-job and correlated by provider isolation (no fragile `AsyncLocal` across Core's parallel workers). Mirror `ProgressState`'s aggregation per job to feed the 4-stat grids (Files/Uploaded/Deduped/Throughput; restore counts).
- **Restore cost handshake (`ConfirmRehydration`):** a `RestoreApprovalRegistry` (singleton, keyed by `jobId`) holds a `TaskCompletionSource<RehydratePriority?>`. `ConfirmRehydration = async (estimate, ct) => { await Clients.Group(jobId).SendAsync("CostEstimate", ToDto(estimate)); ct.Register(() => registry.Resolve(jobId, null)); return await registry.Register(jobId); }`. Hub method `Approve(jobId, priority?)` completes the TCS (Decline = `null`).

### B3. REST + SignalR surface (maps every UI surface → Core handler / app DB)
| UI surface | Transport | Endpoint / hub method | Backend |
|---|---|---|---|
| Overview KPIs + repo table + jobs summary | REST | `GET /overview` | app DB aggregate (+ lazy `SnapshotsQuery`/`StatsQuery`) |
| Repos / repo detail / properties save | REST | `GET /repos`, `GET /repos/{id}`, `PATCH /repos/{id}` | app DB (+ evict provider on PATCH) |
| Files browser + time-travel | **SignalR stream** | `JobsHub.StreamEntries(repoId, version?, prefix?, filter?, includeLocal)` | `ListQuery` ← `ListQueryOptions{Version,Prefix,Filter,Recursive,LocalPath}` |
| Snapshot picker + scrubber | REST | `GET /repos/{id}/snapshots` | **NEW `SnapshotsQuery`** |
| Statistics | REST | `GET /repos/{id}/stats?version=` | **NEW `StatsQuery`** (incl. `isPending`) |
| Add-existing: discover containers | **SignalR stream** | `JobsHub.StreamContainers(accountId)` | `ContainerNamesQuery` via `AccountProvider` |
| Accounts / repos / schedules CRUD | REST | `/accounts`, `/repos`, `/repos/{id}/schedules` | app DB |
| Archive | REST kickoff + **SignalR** | `POST /repos/{id}/archive`→`{jobId}`; `Subscribe(jobId)` | `ArchiveCommand` + forwarders |
| Restore + cost approval | REST kickoff + **SignalR** | `POST /repos/{id}/restore`→`{jobId}`; `CostEstimate`→`Approve(jobId,priority)` | `RestoreCommand` + TCS handshake |
| Jobs table + live console | REST + **SignalR** | `GET /jobs` + `Subscribe(jobId)` / jobs feed | app DB + forwarders |
| Global cross-repo search | **SignalR stream** | `JobsHub.SearchAll(query)` | fan-out `ListQuery(Filter=query, Recursive)` across repos |

**DTO rules:** `RelativePath`/`ContentHash`/`ChunkHash`/`FileTreeHash` → canonical lowercase strings; `RepositoryEntryState` → both raw `int state` and a decoded `stateFlags` object (the State Ring reads `stateFlags`); `BlobTier`→lowercase string; `RehydratePriority`→`"standard"|"high"`.

### B4. App SQLite DB
**Raw `Microsoft.Data.Sqlite`** (already used by `ChunkIndexLocalStore`; AGENTS.md "Simplicity First" → no EF Core for ~4 tables) in a thin `AppDatabase` + per-table store classes mirroring the existing local-store idiom (`CreateOrUpgradeSchema`, WAL, parameterized commands). Tables per README: `storage_accounts`, `repositories`, `jobs` (id = GUID = SignalR group id; status `queued|running|rehydrating|completed|failed|cancelled`), `schedules`. DB path from config (`Arius__AppDbPath`, default `/data/arius-app.sqlite`).
**Secrets at rest:** ASP.NET Core **Data Protection** (`PersistKeysToFileSystem(/data/keys)`), not Core's `PassphraseEncryptionService` (which is content encryption keyed by the user passphrase, with no server key). Store `account_key`/`passphrase` as protected bytes, unprotect only when building a provider.

### B5. Cron scheduler
`SchedulerService : BackgroundService` (not Quartz) ticking each minute: read `schedules WHERE enabled=1`, compare `Cronos.CronExpression.GetNextOccurrence(...)`, enqueue a `jobs` row into a `JobQueue` (`Channel`, one worker per `repo_id` → serializes writers per B1), recompute `next_run`. `JobRunner` builds the per-job provider, runs the command with the SignalR forwarders, updates the `jobs` row.

### B6. Docker
Multi-stage: Node (`ng build --configuration production`) → .NET (`dotnet publish src/Arius.Api`) → `aspnet:10.0` runtime with the Angular `dist` copied into `wwwroot` (`UseDefaultFiles` + `UseStaticFiles` + `MapFallbackToFile("index.html")`; hub at `/hubs/arius`, same-origin → no CORS). `docker-compose.yml` mounts `/data` (app SQLite + DP keys + `HOME=/data` so `~/.arius` caches persist) and one host folder per repo `local_path`.

---

## Part C — Arius.Web (new Angular app `src/Arius.Web`)

Angular 20 standalone components, **signals**, new control flow (`@for`/`@if`), `OnPush`, `inject()`. Node project — **not** in `Arius.slnx`; its `dist/` is copied into the API's `wwwroot` at Docker build. Document the relationship in root `README.md`/`AGENTS.md`.

### C1. Scaffold
1. Take the `metronic-tailwind-angular` seed → `src/Arius.Web`.
2. Drop in licensed Metronic source from `~/Downloads/metronic-v9.5.0`: `config.ktui.css` tokens, `assets/css/styles.css`, `vendors/keenicons/{outline,filled,solid}/`, `assets/js/core.bundle.js`. Reference `demo8` markup (`metronic-tailwind-html-demos/dist/html/demo8/index.html`) for the shell/drawer classes.
3. `angular.json` `styles[]` = `src/tailwind.css` (imports `config.ktui.css` + Arius `@theme`), keenicons `style.css`×3, `assets/css/styles.css`; `scripts[]` = `assets/js/core.bundle.js`.
4. Self-host **Inter** (400/500/600/700) in `assets/fonts/inter/` + `@font-face`; drop the prototype's Google-Fonts link.
5. `package.json`: add `@microsoft/signalr`.

### C2. KTUI integration (`core/ktui/`)
- `KtService` wraps `window.KTComponents?.init()` / `KTLayout?.init()` (null-safe). `AppShell` calls it on `Router.events`→`NavigationEnd` (debounced via `afterNextRender`). A `[ktInit]` directive re-inits dynamic hosts (dropdown menus inside `@if`/`@for`).
- **Prefer native Angular**; use KTUI JS only for **dropdowns/menus** + the demo8 mobile sidebar drawer. Hand-build (the prototype already does): the slide-over **drawers** (`translateX(100%)→0` `.26s cubic-bezier(.4,0,.2,1)`, scrim `.2s`), **cost modal**, **global search overlay**, **repo tab bar** (router child routes + accent underline), **wizard stepper**, **snapshot scrubber**, **toggles** (mutual-exclusion), **state ring**, **collect checkbox**, **tree**, **dark log**. Buttons/inputs/badges use Metronic classes pinned to spec via Arius utility classes.
- `ViewEncapsulation.None` on shell + feature components (Metronic CSS is global); bespoke CSS uses `ar-*`-prefixed classes / Tailwind utilities.

### C3. State Ring — `shared/state-ring/` (critical)
Standalone `OnPush` SVG component. Inputs: `size` (19 rows / 15 legend button / 76 diagram), `state` (the `RepositoryEntryState` flags int) **or** explicit `colors`. Renders the README SVG exactly (viewBox `0 0 24 24`, outer r=11, inner r=7.4, 4 `<path>` + 2 white separators). Flags→colors (collapse chunk state to hydrated/not-hydrated — **do not** reintroduce the WPF purple rehydration color):
```
PRESENT='#27272a' HYDR='#2563eb' NOTHYDR='#9cc4f5' EMPTY='#e4e7ec'
leftOuter  = LocalPointer ? PRESENT : EMPTY
leftInner  = LocalBinary  ? HYDR    : EMPTY
rightOuter = Repository   ? PRESENT : EMPTY
rightInner = RepositoryHydrated ? HYDR : (RepositoryArchived|RepositoryRehydrating) ? NOTHYDR : EMPTY
```
(Note `#2563eb` ≠ accent `#3b82f6` — keep a distinct `--ring-hydrated` token.) Tooltip/`aria-label` derived from flags ("In sync", "Pointer only", "Archive tier — rehydration required", "Rehydrating", "Not archived", "Mixed").

### C4. Routing + components
```
'overview'                         OverviewComponent (top-bar search hidden)
'repos/:repoId'  →  RepoDetailComponent (header + tab bar)
    'files'        FilesTabComponent        ?version=&prefix=&filter=
    'statistics'   StatisticsTabComponent
    'properties'   PropertiesTabComponent
'repos/add'        AddRepoWizardComponent
'repos/create'     CreateRepoWizardComponent
'jobs'             JobsComponent
'settings'         SettingsComponent (placeholder)
```
Drawers + global search are signal-driven overlays (not routes). Folder layout: `core/{layout,ktui,api,routing}`, `features/{overview,repo/{files,statistics,properties},jobs,wizards/{add,create},drawer,search,settings}`, `shared/{state-ring,collect-checkbox,state-legend,live-console,segment-toggle,pill,kpi-card,icon-chip,snapshot-scrubber}`.
Shell (`core/layout/app-shell`, demo8): 86px icon rail (logo + 64×62 r13 tiles Overview/Repos/Jobs/Settings + bell + gradient avatar) · floating content card (radius 16, margin `14 14 14 0`) · 64px top bar (breadcrumb + 300px global search box w/ ⌘K, hidden on Overview) · `<router-outlet>`.

### C5. Services & state
- `ApiService` (typed REST over `HttpClient`) — repos/snapshots/stats/accounts/jobs/schedules; DTOs mirror Core records (camelCase).
- `RealtimeService` (`@microsoft/signalr`) — one shared `HubConnection` (`/hubs/arius`, auto-reconnect); `listEntries` (server→client stream), `startArchive`/`startRestore` (`{jobId, lines$, progress$, cost$, done$}`), `approveRehydration(jobId, priority)`, `jobsFeed()`.
- Per-feature signal stores mirroring the prototype `Component` state: `ExplorerStore` (`expanded`, `selectedFolder`, `fileFilter`, `viewSnap`, `entries`), `CollectStore` (`collected` map → count/bytes), `DrawerStore` (`drawerType`, `streamState`, `streamLines`, `progress`, `restorePriority`, `archiveTier`, `archiveRemoveLocal`/`archiveNoPointers` w/ mutual exclusion), `WizardStore`, `SearchStore`, `JobsStore`. An `effect()` re-subscribes `listEntries` when `viewSnap`/`selectedFolder`/`fileFilter` change; directories → tree, files → detail list. Port `fmtSize` (bytes → "2.41 TB") and flag decoding — the API returns bytes + flags int, not the prototype's pre-formatted strings.

### C6. Streaming UX
- `LiveConsoleComponent` (dark `#0b0b0f` mono auto-scroll, cap retained lines) fed by SignalR `LogLine{ts,source,text,severity}`; severity→prototype colors. Each Core event maps to a console line; reproduce the prototype `streamScript`/`restoreRunScript` line shapes.
- Archive machine `idle→running→done`; Restore machine `idle→analyzing→cost→running→done` (cost modal from `RestoreCostEstimateDto`: 3-stat grid + Standard/High priority cards showing `totalStandard`/`totalHigh`; ETAs "~15h"/"~1h" hardcoded only as fallback). Empty collected set ⇒ whole-repo restore (`targetPath=null`).

### C7. Design tokens (`config.ktui.css` + Arius `@theme`)
Encode README tokens: `--primary:#3b82f6` (keep), `--accent-soft:#eff6ff`/`--accent-softer:#f7f9ff`; surfaces `--muted:#f4f4f5`/card `#fff`, borders `#ececef`/`#e4e4e7`/`#f0f0f2`; foreground `#18181b`/`#27272a`/`#71717a`/`#a1a1aa`; state colors (ok/warn/violet/sky/danger) + tier colors (Hot `#d97706`, Cool `#0ea5e9`, Cold `#3b82f6`, Archive `#8b5cf6`); ring colors (separate tokens); radius (card 13, shell 16, control 9); Inter type scale (h1 22 / section 15.5 / body 13.5–14 / KPI 24–25, `letter-spacing:-.02em`); soft shadows; `--row-h` density var. Add Arius utility classes (`.ar-btn` 40px/r9, `.ar-card` r13, `.ar-pill`, `.ar-chip`, `.ar-input`) where Metronic defaults drift from spec.

### C8. Dev / proxy
`ng serve` (4200) + `proxy.conf.json` forwarding REST paths and `/hubs` (with **`"ws": true`**) to `Arius.Api` (e.g. `:5080`) — same-origin in dev, no CORS.

---

## Phased execution (full build)

1. **Foundations** — Core: `SnapshotsQuery` + `StatsQuery` (+ tests). Api: project, `Arius.slnx`, `RepositoryProvider(Registry)`, app DB + stores, `AzureBlobServiceFactory` wiring, health endpoint. Web: scaffold from seed, drop Metronic in, demo8 shell, tokens, `KtService`/`[ktInit]`, `StateRingComponent`.
2. **Read-only browser** — REST `repos/overview/snapshots/stats`; `JobsHub.StreamEntries`; Angular Overview + Repo detail Files tab (tree + detail list + state ring), Statistics, time-travel picker/scrubber, collect set, legend. End-to-end against a real container.
3. **Streaming archive + restore** — `POST archive/restore` + forwarders + `ConfirmRehydration` TCS handshake; Angular drawers + cost modal + live log + state machines.
4. **Accounts/repos/jobs/scheduler** — Accounts/repos CRUD + Add/Create wizards (`ContainerNamesQuery` via `AccountProvider`) + Properties save; `jobs`/`schedules` + `SchedulerService` + `JobQueue`/`JobRunner`; Jobs screen + unified console feed.
5. **Search + Docker** — global cross-repo search overlay; Settings placeholder; multi-stage Dockerfile + `docker-compose` for Synology (volumes); production build served from `wwwroot`.

After each phase: build + tests green, then a quick manual smoke before moving on.

---

## Critical files
- Core consume/extend: `src/Arius.Core/ServiceCollectionExtensions.cs`, `Features/ListQuery/ListQuery.cs`, `Features/RestoreCommand/{RestoreCommand,RestoreCostCalculator,Events}.cs`, `Features/ArchiveCommand/{ArchiveCommand,Events}.cs`, `Shared/Snapshot/SnapshotService.cs`, `Shared/ChunkIndex/{IChunkIndexService,ChunkIndexService,ChunkIndexLocalStore}.cs`, `Shared/Storage/IBlobContainerService.cs`.
- Composition templates: `src/Arius.Cli/CliBuilder.cs`, `src/Arius.Cli/ProgressState.cs`, `src/Arius.Cli/Commands/*/*ProgressHandlers.cs`, `src/Arius.AzureBlob/AzureBlobServiceFactory.cs`, `src/Arius.Cli/Arius.Cli.csproj` (Mediator generator ref).
- Reference UI: `src/Arius.Explorer/RepositoryExplorer/{StateCircle.xaml,FileItemViewModel.cs}` (ring), `Infrastructure/RepositorySession.cs` (per-repo session).
- Design source of truth: `openspec/changes/2026-06-16-arius-web-claude-design-handoff/{README.md,Arius.Web.dc.html}`; Metronic `~/Downloads/metronic-v9.5.0/` (`config.ktui.css`, `demo8/index.html`, `vendors/keenicons/`).
- Project wiring: `src/Arius.slnx`, `src/Directory.Packages.props`, `AGENTS.md`, `src/Arius.Architecture.Tests` (add/adjust ArchUnit rules for the new `Arius.Api` boundary).

## Risks & mitigations
- **Event correlation** relies on one provider per long-running job + serialized writers per repo (B1/B2). If two write jobs ever share a repo provider, events cross-talk — the JobQueue prevents this.
- **`NullBlobServiceFactory` trap** — must register real `AzureBlobServiceFactory` before `AddArius` / in `AccountProvider`, or container discovery is empty.
- **`ChunkIndexService` is single-shot after `FlushAsync`** — never reuse an archive job's provider for reads; evict the cached read provider on archive completion.
- **Stats `isPending`** is intrinsic to lazy shard loading — report honestly, don't force a full download.
- **KTUI re-init timing** — dynamic dropdowns won't initialize until re-scanned; `[ktInit]` + router hook must cover every dynamic host. Prefer native overlays (the highest-churn risk).
- **Metronic vs prototype metrics** — `kt-*` defaults differ from the hand-tuned spec (40px/r9 buttons, r13/16 cards, 46/38 rows); pin via Arius utility classes and verify visually.
- **ArchUnit** — adding `Arius.Api` (references Core + AzureBlob) may trip existing layering rules; update `Arius.Architecture.Tests` accordingly.

## Verification (end-to-end)
- **Build/tests:** `dotnet build src/Arius.slnx`; `dotnet test` for `Arius.Core.Tests` (new queries) + `Arius.Api.Tests`; `Arius.Architecture.Tests` green. Run one TUnit test per the memory note (`-- --treenode-filter`).
- **Run locally:** start `Arius.Api` (`dotnet run --project src/Arius.Api`) pointed at a real Azure account/container (the user's repos); `ng serve` with the SignalR-aware proxy. Walk each screen: Overview → open repo → browse the tree, filter, time-travel via scrubber/picker, hover state rings, open the legend; Statistics (observe `pending` then settle); Properties save; Archive drawer (start → live log → done); Restore drawer (collect files → analyzing → **cost modal** → approve Standard/High → running → done); Add/Create wizards (discover containers); Jobs screen + live console; global ⌘K search.
- **Docker:** `docker compose up` on the multi-stage image; confirm the SPA + SignalR work same-origin and volumes persist app DB + caches.
- Use the `verify`/`run` skills to drive the app and screenshot key screens against the prototype for fidelity.
