# Service lifetimes & scoping

> **Code:** `src/Arius.Core/ServiceCollectionExtensions.cs`, `src/Arius.Api/Composition/RepositoryProviderRegistry.cs` · **Decisions:** [ADR-0013](../../decisions/adr-0013-core-host-separation.md), [ADR-0015](../../decisions/adr-0015-chunk-index-scalability.md), [ADR-0016](../../decisions/adr-0016-multi-machine-cache-coherence.md) · **Terms:** [chunk index](../../glossary.md#chunk-index), [epoch](../../glossary.md#epoch)

## Purpose

How Arius.Core's shared services and feature handlers are scoped and how long they live — and why the long-lived hosts (Api, Explorer) must manage provider lifecycle explicitly instead of treating the services as process-global singletons. This is the one home for the lifetime model; the shared-service docs describe *behaviour*, not lifetime.

## How it works

`AddArius` registers **every** Core service and handler — `SnapshotService`, `ChunkIndexService`, `ChunkStorageService`, `FileTreeService`, and all the `*CommandHandler`/`*QueryHandler` factories — as DI **singletons**, with the repository's `accountName`/`containerName` (and passphrase, via `IEncryptionService`) captured in each registration. So "singleton" does **not** mean process-global: it means **one instance per `IServiceProvider`, and each provider is bound to exactly one repository**. The *provider* is the unit of scope, not the process. `AddMediator()` is called by the host (not by `AddArius`), so the host owns the provider's lifecycle.

Each host composes that per-repository provider differently:

| Host | Provider strategy | Lifetime |
|---|---|---|
| **CLI** (`Arius.Cli`) | one provider per command invocation | short-lived — the process exits after the command. Provider ≈ command. This is the model the services were originally designed for. |
| **Explorer** (`Arius.Explorer`) | one provider per opened repository | long-lived process → hits the hazards below. |
| **Api** (`Arius.Api`, via `RepositoryProviderRegistry`) | **two** lifetimes (below) | long-lived process serving many repos and requests. |

`RepositoryProviderRegistry` runs two provider lifetimes side by side:

- **Read providers** — built lazily and **cached per repository** (`GetReadProviderAsync`, a `Lazy<Task<ServiceProvider>>` keyed by repository id), opened with `PreflightMode.ReadOnly`, given an inert `JobSink`. They warm caches across requests (`ls`, queries, restore reads). They are **evicted and disposed** (`Evict`) on a properties change, a delete, or **after an archive**.
- **Job providers** — a **fresh, dedicated** provider per long-running archive/restore (`CreateJobProviderAsync`), wired to a per-job `JobSink` and its own `IMediator`, **owned and disposed by the job** when it ends.

## Key invariants

- A provider is bound to one repository; never share a provider across repositories (account/container are baked in at registration).
- A **mutating run (archive)** must use a provider that is then evicted/disposed. The chunk index is **single-shot after flush** — see *Why this shape*. Reusing a flushed provider for a second mutation is a bug.
- Read-only consumers (`ls`, `restore`, queries) may share a cached read provider, but its caches are coherence-sensitive: they must be invalidated when the snapshot epoch changes ([ADR-0016](../../decisions/adr-0016-multi-machine-cache-coherence.md)). The Api achieves this by evicting the read provider after any mutation.

## Why this shape

Per-repository provider scoping falls out of Core ⊥ host separation ([ADR-0013](../../decisions/adr-0013-core-host-separation.md)): the host composes one Core graph per repository and drives it through `IMediator`.

The singleton-within-a-provider model was conceived when a provider was always **short-lived** — one CLI command, build → use → process exits — where a process-lifetime singleton and a repository-session are the same thing. That assumption **leaks** in the long-lived hosts:

- **`ChunkIndexService` is single-shot after flush.** Its in-provider state (dirty rows, the run-scoped shard listing, coverage claims) is built for one archive run and is "spent" once flushed, so the same instance cannot correctly serve a second archive. This is the genuinely **session-scoped** service. The Api contains the hazard with fresh per-job providers + eviction-after-archive; Explorer faces the same hazard for repeated archives in one session.
- **`SnapshotService` / `FileTreeService` / `ChunkStorageService`** are read-mostly and tolerate reuse, but their caches are epoch-sensitive ([ADR-0016](../../decisions/adr-0016-multi-machine-cache-coherence.md)) — stale across a remote change by another machine. Read-provider eviction (or `ChunkIndexService.InvalidateCaches` on epoch mismatch) keeps them honest. These are the "singletons that only work because the process is short-lived" — fine for CLI, kept correct in the Api only by deliberate eviction.

## Open seams / future

The "single-shot after flush" contract is **implicit** — enforced only by host discipline (per-job providers + eviction), not by the type system or a Core-level session boundary. A long-lived host that forgets to evict/replace a provider after a mutation silently reuses spent state. Candidates to harden this: make the mutation lifecycle explicit as a disposable per-run session in Core (so reuse is impossible by construction), or make the services re-initialisable instead of single-shot. Until then, hosts must follow the eviction rules above, and [`hosts/web.md`](../hosts/web.md) / [`hosts/explorer.md`](../hosts/explorer.md) document how each one does.
