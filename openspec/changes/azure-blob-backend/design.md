## Context

The chunking, packing, encryption, and local `FileSystemRepositoryStore` pipeline is fully implemented and tested. The next step is wiring these components into an Azure Blob Storage–backed store. The existing `arius-core-architecture` design (D1–D11) already covers the Azure repository layout, tier strategy, rehydration, and locking at a high level. This document covers only the decisions that are **new or refined** for this specific implementation change.

The `Arius.Azure` project already has a project skeleton and `Azure.Storage.Blobs` NuGet reference. The `Arius.Azure.Tests` project exists with a placeholder smoke test.

## Goals / Non-Goals

**Goals:**
- Define `IBlobStorageProvider` in `Arius.Core` (upload, download, list, delete, tier-set)
- Implement `AzureBlobStorageProvider` in `Arius.Azure` using the Azure.Storage.Blobs SDK
- Implement `AzureBlobRepositoryStore` in `Arius.Azure` — same init/backup/restore contract as `FileSystemRepositoryStore` but using `IBlobStorageProvider`
- Add `TargetTier` to `BackupRequest` (Hot/Cool/Cold/Archive); default Archive; wired through CLI `--tier`
- Azurite-based integration tests covering init → backup → restore over a local Azure emulator
- Keep `FileSystemRepositoryStore` and `Arius.Core.Tests` unchanged

**Non-Goals:**
- Rehydration polling / multi-phase restore (Archive-tier packs) — restore in this change only works for tiers directly downloadable (Hot/Cool/Cold). Archive restore requires rehydration, deferred to a later change.
- Blob lease locking (deferred)
- Local SQLite cache (deferred)
- Delta index sync watermark (deferred)

## Decisions

### D1: `IBlobStorageProvider` lives in `Arius.Core`, not `Arius.Azure`

The handlers (`BackupHandler`, `RestoreHandler`) in `Arius.Core` need to call storage operations. Placing the interface in `Arius.Core` keeps the core logic testable without taking a dependency on the Azure SDK. `Arius.Azure` provides the concrete implementation.

**Interface shape:**
```csharp
public interface IBlobStorageProvider
{
    Task UploadAsync(string blobName, Stream content, BlobTier tier, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string blobName, CancellationToken ct = default);
    IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct = default);
    Task DeleteAsync(string blobName, CancellationToken ct = default);
    Task SetTierAsync(string blobName, BlobTier tier, CancellationToken ct = default);
}
```

`BlobTier` is an enum in `Arius.Core.Models`: `Hot, Cool, Cold, Archive`.

### D2: `AzureBlobRepositoryStore` is a near-clone of `FileSystemRepositoryStore`

Rather than introducing a full abstraction layer between handlers and storage in this change, `AzureBlobRepositoryStore` replicates the same `InitAsync / BackupAsync / RestoreAsync` public surface with the same internal logic — but every local file I/O call is replaced with `IBlobStorageProvider` calls.

**Rationale:** Introducing a shared `RepositoryEngine` abstraction here would be premature. The two stores have different restore strategies (filesystem reads directly vs. Azure may need rehydration). Deferring the abstraction avoids over-engineering before the full restore pipeline is clear.

### D3: Blob path conventions

Follows the D1 layout from the `arius-core-architecture` design exactly:

| Content | Blob name pattern | Tier |
|---|---|---|
| Config | `config.json` | Cold |
| Key files | `keys/{id}.json` | Cold |
| Snapshots | `snapshots/{id}.json` | Cold |
| Index deltas | `index/{snapshotId}.json` | Cold |
| Pack files | `data/{prefix2}/{packId}.pack` | Caller-specified (default Archive) |

`{prefix2}` = first 2 hex chars of the pack ID (reduces Azure list operation cost for repos with many packs).

### D4: `TargetTier` on `BackupRequest`

`BackupRequest` gains an optional `TargetTier` (default `Archive`). This flows through `BackupHandler` → `BackupAsync` → `IBlobStorageProvider.UploadAsync`. Metadata blobs (config, keys, snapshots, index) always use `Cold` regardless of `TargetTier`.

For Azurite: Azurite does not fully enforce Archive-tier behaviour (no rehydration needed). All tiers work for upload/download in tests.

### D5: Restore in this change only handles immediately downloadable tiers

If a pack is in Archive tier and has not been rehydrated, `DownloadAsync` will throw (Azure returns 409 BlobArchived). For this change, restore only works when packs were uploaded to Hot/Cool/Cold. A follow-on change will add the rehydration polling phase. Tests are written with `TargetTier = Cold` to ensure immediate downloadability.

### D6: Azurite for integration tests

`DotNet.Testcontainers` spins up an Azurite container. Tests inherit from a base class that starts/stops the container and provides a `BlobServiceClient` pointed at the emulator. The connection string for Azurite is the well-known `UseDevelopmentStorage=true` equivalent.

### D7: `AzureBlobStorageProvider` uses `BlobContainerClient`

Each `AzureBlobRepositoryStore` instance targets one container. The container is created on `InitAsync` if it does not exist. `BlobContainerClient` is injected (or constructed from a connection string).

## Risks / Trade-offs

- **[Archive tier in tests]** → Azurite's Archive tier does not block downloads. Tests use Cold tier; real-world Archive behaviour is validated manually or in a later change with live Azure.
- **[Streaming upload size]** → `UploadAsync` loads pack bytes into a `MemoryStream`. For the current 10 MB default pack size this is fine; for larger packs it would be wasteful. Can be replaced with `BlobClient.UploadAsync(Stream, ...)` using a streaming approach in a later change.
- **[No lease locking]** → Concurrent backups to the same Azure container are not guarded in this change. Documented as a known gap.
- **[Azurite container startup time]** → First test run may be slow (container pull). CI should cache the Docker layer.

## Open Questions

- Should `IBlobStorageProvider` expose `ExistsAsync` to avoid unnecessary downloads for dedup checks, or is the index the canonical source of truth? (Current decision: index is canonical — no `ExistsAsync` needed for correctness.)
- Should pack blob names include the 2-char prefix subdirectory in this change or defer to a later refactor? (Current decision: include prefix now to match the spec layout from the start.)
