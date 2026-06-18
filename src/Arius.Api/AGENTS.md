# Arius.Api — agent contract

The ASP.NET minimal-API host that relays Arius.Core over REST + SignalR (the Angular web UI's backend).

Read the root [`../../AGENTS.md`](../../AGENTS.md) first — it owns the cross-cutting rules (think-before-coding, simplicity, testing workflow, code style, docs map). This file only covers what is specific to *this* project.

- How it works (host design): [`../../docs/design/hosts/web.md`](../../docs/design/hosts/web.md)
- DI lifetimes & per-repo providers: [`../../docs/design/cross-cutting/service-lifetimes.md`](../../docs/design/cross-cutting/service-lifetimes.md)
- Events/progress this host forwards: [`../../docs/design/cross-cutting/events-and-progress.md`](../../docs/design/cross-cutting/events-and-progress.md)
- Terms (chunk index, snapshot, tier, rehydration): [`../../docs/glossary.md`](../../docs/glossary.md)

## Layout
- `Program.cs` — composition root. Everything app-level is a singleton; `SchedulerService` is the one `IHostedService`. REST under `/api`, hub at `/hubs/arius`, Angular SPA from `wwwroot` with a fallback to `index.html`.
- `Composition/RepositoryProviderRegistry.cs` — builds per-repo Core service graphs.
- `Endpoints/` — minimal-API `Map*Endpoints()` extensions (Account/Repository/Browse/Job); thin, sync CRUD over `AppDatabase`.
- `Hubs/JobsHub.cs` + `Hubs/{Archive,Restore}Forwarders.cs` — SignalR surface and Core→client event forwarders.
- `Jobs/` — `JobRunner`, `JobSink`, `RestoreApprovalRegistry`, `SchedulerService`, `JobFormat`.
- `AppData/` — `AppDatabase`, `Records.cs`, `SecretProtector`.
- `Contracts/` — `Dtos.cs`, `EntryDto.cs` (+ `EntryMapping.ToDto`). Map Core types to DTOs here, never expose Core records directly.

## Project-local idioms & gotchas
- **Two provider lifetimes** (`RepositoryProviderRegistry`). *Read providers* are cached per repo (`PreflightMode.ReadOnly`), warm caches across requests, and get an inert `JobSink()`. *Job providers* are built fresh per archive/restore, owned and disposed by the job. After an archive (or a properties change / delete) call `Evict(repositoryId)` — the read cache's chunk index is single-shot after flush, so it must be rebuilt.
- **Per-job event isolation is by provider, not a correlation id.** Each job provider registers its own `JobSink` singleton; the auto-registered `INotificationHandler<T>` forwarders resolve *that job's* sink. Don't add a jobId filter to the forwarders.
- **`AddMediator()` must be called in `BuildAsync`**, not inside `AddArius` — the Mediator source generator runs against *this* assembly, so it must register the forwarders living here. Adding a new Core event to forward = add an `INotificationHandler<T>` in `Hubs/`; it's picked up automatically.
- **`JobRunner` serializes writers per repo** via a `SemaphoreSlim` keyed by repositoryId — two mutating jobs never share a repo's on-disk state. Restore's `ConfirmRehydration` callback streams a `CostEstimate` then awaits `RestoreApprovalRegistry`; a dropped SignalR connection (`OnDisconnectedAsync`) declines it.
- **`JobSink` is the only client channel**: `Log`/`Progress`/`CostEstimate`/`Done` to the job's SignalR group. It also holds the aggregate counters (`Interlocked`) feeding the drawer's stat grid.
- **App SQLite (`AppDatabase`: accounts/repos/jobs/schedules) is separate from Core's chunk-index cache.** Raw `SqliteConnection` + WAL + parameterized commands, mirroring Core's local-store idiom.
- **Secrets at rest**: account keys and passphrases go through `SecretProtector` (ASP.NET Data Protection, key ring on the mounted volume) — *not* Core's passphrase content encryption. Never store plaintext keys; always `Protect`/`Unprotect`.

## Run & test
- Run: `dotnet run --project src/Arius.Api` (dev state under `.appstate/`; CORS allows `http://localhost:4200` for `ng serve`).
- No dedicated Api unit project: this host is exercised via `src/Arius.Integration.Tests`, `src/Arius.E2E.Tests`, and the ArchUnit rules in `src/Arius.Architecture.Tests`. Build the host after any Core contract change — see the root contract's testing section.
