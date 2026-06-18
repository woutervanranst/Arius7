---
status: "accepted"
date: 2026-06-17
decision-makers: ["Wouter Van Ranst"]
consulted: ["Claude Code"]
informed: ["Arius maintainers"]
confidence: "high"
---

# Decouple Arius.Core from its hosts and its storage backend

## Context and Problem Statement

Arius.Core holds the archival domain: the chunk index, chunk storage, snapshot, file-tree, encryption, and compression services that implement archive/restore/list. Several different hosts need to drive that domain — the `Arius.Cli` command-line tool, the `Arius.Explorer` desktop app, and the `Arius.Api` + `Arius.Web` server. The domain also has to reach a storage backend that today is Azure Blob (`Arius.AzureBlob`, built on `Azure.Storage.Blobs`), but is explicitly intended to gain S3 and local-filesystem backends later.

If Core called the Azure SDK directly, or if hosts reached into Core's services to run repository behavior, the domain would fuse to one delivery mechanism and one storage vendor. A backup tool whose data must stay recoverable for years cannot afford that coupling: swapping the front end or the backend would mean rewriting the domain.

The question for this ADR is how Arius.Core should be insulated from the hosts that drive it and from the storage technology it persists to.

## Decision Drivers

* Core is the long-lived asset; hosts and the storage vendor are replaceable around it.
* Multiple hosts (Cli, Explorer, Api/Web) must share one domain implementation without forking it.
* A future S3 or filesystem backend must drop in without touching Core.
* The boundary must be mechanically enforced, not just documented, so it does not erode.
* Hosts must not bypass the boundary by calling Core implementation types directly (see ADR-0010).

## Considered Options

* Let Core reference `Azure.Storage.Blobs` directly and let hosts call Core services directly.
* Decouple Core through two seams: hosts drive Core only via `IMediator` commands/queries; Core persists only through a storage abstraction in `Shared/Storage`, with a separate `Arius.AzureBlob` adapter.
* Keep the storage abstraction but skip the mediator boundary, letting hosts call Core handlers/services directly.

## Decision Outcome

Chosen option: "Decouple Core through two seams", because it gives Core exactly two outward contracts — the mediator message surface inward from hosts, and the `IBlobContainerService` family outward to storage — each of which is a stable interface owned by Core and enforced by architecture tests.

Confidence: high. The seams already exist in code: every host operation is an `IMediator` command/query handler under `src/Arius.Core/Features/`, Core's only storage dependency is the interface set in `src/Arius.Core/Shared/Storage/`, and `DependencyTests` fails the build if either seam is breached. This is a record of an implemented decision, not a proposal.

Before:

```text
Arius.Cli ─► Arius.Core ──(Azure.Storage.Blobs)──► Azure
   hosts call Core services directly; Core references the Azure SDK
```

After:

```text
hosts ──IMediator commands/queries──► Arius.Core ──IBlobContainerService──► Arius.AzureBlob ─► Azure
                                                  (future: S3, filesystem)
```

### The inward seam: hosts drive Core only through IMediator

Each host operation is a Mediator contract with a handler under `src/Arius.Core/Features/`: `ArchiveCommand`, `RestoreCommand`, `RepairChunkIndexCommand`, `ListQuery`, `ContainerNamesQuery`, `ChunkHydrationStatusQuery`, `SnapshotsQuery`, `StatisticsQuery`. Handlers coordinate the Core shared services; hosts never touch those services. `ArchiveCommandHandler` is itself injected an `IMediator` and publishes its pipeline progress (`FileScannedEvent` and the other events in `ArchiveCommand/Events.cs`) as notifications, which hosts subscribe to without knowing the handler internals. ADR-0010 owns this policy in full; this ADR records that the same seam is what keeps the three hosts off Core's implementation types.

### The outward seam: Core persists only through Shared/Storage

Core's entire storage dependency is three interfaces and their DTOs in `src/Arius.Core/Shared/Storage/`:

* `IBlobServiceFactory` — creates an account-scoped `IBlobService` from an account name and optional key.
* `IBlobService` — lists containers and, via `OpenContainerServiceAsync(name, PreflightMode, ...)`, returns a validated `IBlobContainerService`.
* `IBlobContainerService` — all blob I/O (upload, download, HEAD, list, set-metadata, set-tier, server-side copy for rehydration, delete).

These interfaces speak Core's vocabulary — `RelativePath`, `BlobTier`, `RehydratePriority`, `BlobMetadata`, `UploadResult` — and as the comment on `IBlobContainerService` states, "no Azure-specific types cross this boundary." `Arius.AzureBlob` is the adapter: `AzureBlobContainerService`, `AzureBlobService`, and `AzureBlobServiceFactory` implement these interfaces over `Azure.Storage.Blobs`. A future S3 or filesystem backend is a new assembly implementing the same three interfaces; Core is unchanged.

### Composition wires the seams together at the host

`Arius.Core.ServiceCollectionExtensions.AddArius(blobContainer, passphrase, accountName, containerName)` registers Core's services and handler factories against an injected `IBlobContainerService` — Core never constructs a storage client. The host owns composition:

* `AddArius` deliberately does **not** call `AddMediator()`. Its comment explains why: the Mediator source generator must run in the outermost assembly so it discovers notification handlers in both Core and the host.
* `Arius.Api`'s `RepositoryProviderRegistry.BuildAsync` resolves an `IBlobContainerService` from the injected `IBlobServiceFactory` (an `Arius.AzureBlob.AzureBlobServiceFactory`), then calls `services.AddMediator()` followed by `services.AddArius(...)`, building one per-repository provider with its own `IMediator` + Core graph.
* `Arius.Explorer.Program` registers `AzureBlobServiceFactory` as the `IBlobServiceFactory` and likewise calls `AddMediator()` in the host assembly.

So the storage backend is chosen once, at the host composition root, and injected across the seam; Core stays vendor-agnostic.

### Consequences and Tradeoffs

* Good, because the same Core assembly serves Cli, Explorer, and Api/Web unchanged — only host composition differs.
* Good, because a new storage backend is an adapter implementing `IBlobServiceFactory` / `IBlobService` / `IBlobContainerService`, with zero Core edits.
* Good, because `IBlobContainerService` carries domain tier semantics (Hot/Cool/Cold/Archive, rehydration via `CopyAsync`) so Core expresses archive-tier intent without an Azure type.
* Good, because the boundary is build-enforced (`DependencyTests`), so it cannot quietly erode.
* Bad, because the storage interface must model every capability Core needs (HEAD metadata, optimistic-concurrency upload, server-side copy) in a backend-neutral shape, which is more design work than calling the Azure SDK directly.
* Bad, because the `AddMediator()`-in-the-host rule is a non-obvious composition constraint each host must honor; `AddArius` documents it in a comment rather than enforcing it.
* Bad, because the no-S3/no-filesystem backend remains a designed-but-unbuilt hook (per the foundation proposal's non-goals), so the abstraction carries some unexercised generality.

### Confirmation

`src/Arius.Architecture.Tests/DependencyTests.cs` enforces both seams against the loaded `Arius.Core`, `Arius.AzureBlob`, and `Arius.Cli` assemblies:

* `Core_Should_Not_Reference_Azure` — Core must not depend on any type in the `Azure` namespace.
* `Core_Should_Not_Depend_On_AzureBlob` — Core must not depend on the `Arius.AzureBlob` adapter.
* `Cli_Should_Not_Reference_Azure` — the CLI host must reach Azure only through `Arius.AzureBlob`, never the SDK directly.
* `Mediator_Command_And_Stream_Handlers_Should_Live_In_Core_Only` and `Core_Is_Exposed_Primarily_Through_Mediator` — keep the inward seam intact (the ADR-0010 boundary): handlers live in Core, and non-Core assemblies may not depend on Core implementation types except the allowed contract surface.

The decision is being followed when these tests pass and the only storage types Core references are the interfaces and DTOs under `Arius.Core.Shared.Storage`.

## Pros and Cons of the Options

### Core references Azure SDK directly; hosts call Core services directly

* Good, because it is the least code to write initially.
* Bad, because it welds the domain to Azure Blob, blocking S3/filesystem backends.
* Bad, because every host would couple to Core internals, so the three hosts could not safely share one evolving domain.
* Bad, because there would be no enforceable boundary, so coupling would grow unchecked.

### Decouple Core through two seams (chosen)

* Good, because Core has exactly two stable contracts: mediator messages inward, `IBlobContainerService` outward.
* Good, because hosts and storage backends are swappable without touching the domain.
* Good, because `DependencyTests` enforces both seams at build time.
* Bad, because the storage interface and the host composition rules add up-front design cost.

### Storage abstraction only, no mediator boundary

* Good, because it still permits alternate storage backends.
* Bad, because hosts would bind to Core service signatures, recreating host coupling and undoing ADR-0010.
* Bad, because progress/event notifications would have no uniform delivery seam across hosts.

## More Information

* Foundation design narrative (solution structure, "Core knows nothing about Azure Blob specifically", S3/filesystem as architecture-supported non-goal): `docs/history/openspec-archive/2026-03-24-arius-core-foundation/design.md`.
* Foundation proposal: `docs/history/openspec-archive/2026-03-24-arius-core-foundation/proposal.md`.
* Related decision on the inward (host→Core) seam: ADR-0010 — Core use cases go through command and query handlers.
