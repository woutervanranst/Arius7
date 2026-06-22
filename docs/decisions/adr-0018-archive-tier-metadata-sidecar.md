---
status: "accepted"
date: 2026-06-21
decision-makers: ["Wouter Van Ranst"]
consulted: ["Claude Code"]
informed: ["Arius maintainers"]
confidence: "high"
---

# v5→v7 migration only: carry chunk descriptors for Archive-tier blobs in a metadata sidecar

## Context and Problem Statement

The v5→v7 [migration](../design/migration.md) must stamp every chunk blob with its v7 descriptor — the `arius_type` sentinel (the commit point of [ADR-0017](adr-0017-idempotent-non-distributed-recovery.md)) plus `original_size` / `chunk_size` / `parent_chunk_hash` — so that `ChunkIndexService.RepairAsync` can rebuild the index from the authoritative chunk blobs. The descriptor is normally written with `Set Blob Metadata`. But a real v5 repository legitimately has chunks already in Azure's **Archive** access tier, and Azure rejects `Set Blob Metadata` on an archived blob with `409 BlobArchived`. The migration's Stage 3 hits exactly this against `ariusciuse/v5migrationtest`.

Rehydrating every archived chunk just to attach metadata is slow and costly, and defeats the migration's "non-destructive, in-place, no re-upload" premise. Native v7 never has this problem: it writes the descriptor while the blob is still Hot/Cool, *before* it is tiered.

The question for this ADR is **where to carry the v7 descriptor of an Archive-tier chunk** so that repair can read it, without rehydrating the chunk and without burdening the native v7 write path.

## Decision Drivers

* The descriptor carrier must be writable while the chunk blob is in the Archive tier (so: not the chunk blob's own metadata).
* Native v7's free, in-place metadata path must stay unchanged — this is a migration-only problem.
* It must be reproducible in the test stack (Azurite), since real Archive-tier behaviour cannot be exercised there.
* No new recurring cost proportional to the whole repository.
* Repair stays the single source of truth: it must resolve a descriptor for every committed chunk, archived or not.

## Considered Options

* **Set metadata in place** — the failing baseline (`409 BlobArchived`).
* **Rehydrate → set metadata → re-archive** each archived chunk.
* **Blob index tags** — "read metadata, else read tags".
* **Switch all v7 metadata to tags** (one path for every chunk).
* **A zero-byte Cool-tier metadata sidecar** carrying the descriptor, under a separate sibling prefix.

## Decision Outcome

Chosen option: **a zero-byte Cool-tier metadata sidecar under a separate prefix**, because it is the only carrier that is archive-writable, ~free, fully Azurite-testable, and reuses a blob shape (the thin stub) the codebase already trusts — while leaving the native v7 write path untouched.

Stage 3 branches on the chunk's tier (`MigrateV5.WriteMetadataAsync`): a non-Archive chunk keeps the in-place `SetMetadataAsync` upsert; an Archive chunk gets a zero-byte sidecar at `BlobPaths.V5LegacySideCarPath(hash)` — prefix `chunks-v5legacy-metadata/`, content type `ContentTypes.V5LegacyMetadataSideCar`, Cool tier, `overwrite: true`. The descriptor lives entirely in the sidecar's blob metadata; the body is empty.

`ChunkIndexService.RepairAsync` reads it back in `GetRepairEntriesAsync`: it lists `BlobPaths.V5LegacySideCarPrefix` once into a hash→metadata map, then for each chunk resolves the descriptor as **its own metadata if that carries `arius_type`, otherwise the sidecar**. A chunk with neither is skipped with a warning. The **tier is always taken from the live `chunks/` listing, never from the sidecar** — a sidecar is written once at migration time and cannot track later rehydration.

Two sub-decisions:

* **Separate prefix, not colocated in `chunks/`.** A sidecar cannot be `chunks/{hash}` (the data blob owns that name), so colocating would force an artificial name (`chunks/{hash}.d`) and pollute a namespace that several paths iterate assuming "everything here is a chunk" — the `ContentHash.Parse` + `arius_type` switch in `GetRepairEntriesAsync`, archived-chunk enumeration for tier-stats — each of which would need a new skip-filter, and an `arius_type=large` sidecar would look exactly like a real large chunk. A separate sibling prefix keeps `chunks/` untouched and is unambiguous 1:1 on the identical hash.
* **Name is origin-based (`chunks-v5legacy-metadata/`), not function-based (`chunk-descriptors/`).** Branding the prefix as v5-migration scaffolding discourages native v7 from ever leaning on it; the trade-off is that "v5legacy" is baked into the durable layout.

> This sidecar is **not** the "sidecar manifest per tar" that [ADR-0017](adr-0017-idempotent-non-distributed-recovery.md) rejected. That was a per-tar *member manifest* proposed to replace thin chunks (and rejected because it breaks uniform `HEAD chunks/<hash>` lookup). This is a per-archived-chunk *metadata* blob that feeds repair only. Same word, different mechanism.

Confidence: high. The mechanism is small, implemented, and unit-tested against the in-memory blob fake; native v7 is provably unaffected (it never reaches the Archive branch).

Before — Stage 3 sets metadata in place for every chunk:

```text
SetMetadata chunks/<hash>  →  409 BlobArchived  ✗ (chunk already in Archive tier)
```

After — the carrier is chosen by tier:

```text
tier == Archive ? Upload chunks-v5legacy-metadata/<hash> (0 bytes, Cool, descriptor in metadata)
                : SetMetadata chunks/<hash> (merge descriptor onto existing v5 metadata)

repair: descriptor = own metadata (has arius_type) ?? sidecar[hash]   // tier always from live chunks/ listing
```

### Consequences and Tradeoffs

* Good, because the carrier is writable while the chunk is archived — no rehydration, no re-upload, migration stays in-place.
* Good, because it costs ~$0 recurring: zero-byte Cool blobs are billed for their actual (zero) bytes, unlike per-tag monthly charges.
* Good, because it is fully Azurite-testable (just blobs + metadata), which the tags option is not.
* Good, because it reuses the thin-stub shape (`ChunkStorageService.UploadThinAsync`) rather than introducing a new blob abstraction.
* Good, because native v7 is entirely unaffected — a freshly created v7 repo has zero sidecars, so repair pays only one empty prefix listing.
* Neutral, because repair does one extra `chunks-v5legacy-metadata/` listing and a hash-join — the same shape as the existing thin→tar enrich pass, and repair is rare.
* Bad, because it is +1 object per archived chunk, doubling the object count (not the storage) for the migrated archived set.
* Bad, because a sidecar can be orphaned if chunk pruning/GC is ever added — any future delete of a chunk must also delete its sidecar (flagged in `BlobConstants.cs`).
* Bad, because the prefix name bakes "v5legacy" into the durable layout, a misnomer if the mechanism is ever reused for a non-v5 purpose.

### Confirmation

The read-back contract is confirmed by unit tests against the in-memory blob fake (real Archive-tier behaviour is not reproducible in Azurite):

* `ChunkIndexServiceRepairTests.RepairAsync_ArchivedLargeChunk_ReadsDescriptorFromSidecar` — an Archive large chunk with no own metadata is rebuilt from its sidecar, and the Archive tier is preserved from the live listing, not the sidecar.
* `ChunkIndexServiceRepairTests.RepairAsync_ArchivedTarWithSidecar_ThinInheritsArchiveTierAndSize` — a thin chunk inherits its archived parent tar's tier and size resolved via the tar's sidecar.

End-to-end, the migration of `ariusciuse/v5migrationtest` (which previously failed with `409 BlobArchived`) completes and the rebuilt index restores byte-identically.

The decision holds while: Stage 3 writes the sidecar only for `BlobTier.Archive` chunks and keeps `SetMetadataAsync` for the rest; repair resolves "own metadata else sidecar" and reads tier exclusively from the live `chunks/` listing; and the native v7 archive path continues to write metadata before tiering (so it never needs a sidecar).

## Pros and Cons of the Options

### Zero-byte Cool-tier metadata sidecar, separate prefix (chosen)

* Good, because it is archive-writable, ~free, and Azurite-testable.
* Good, because it reuses the thin-stub blob shape and adds no new storage abstraction.
* Good, because a separate prefix keeps `chunks/` consumers untouched (no new skip-filters).
* Bad, because +1 object per archived chunk and a potential future orphan if GC is added.

### Set metadata in place

* Good, because it is the simplest path and has no extra objects.
* Bad, because Azure forbids `Set Blob Metadata` on Archive-tier blobs (`409 BlobArchived`) — it cannot describe the very chunks the migration must describe.

### Rehydrate → set metadata → re-archive

* Good, because the descriptor lands on the chunk blob itself, no second object.
* Bad, because per-blob rehydration is slow (hours) and costly, and re-archiving resets early-deletion clocks — prohibitive for a bulk, non-destructive migration.

### Blob index tags ("read metadata, else tags")

* Good, because Azure *permits* `Set Blob Tags` on archived blobs, tags ride on the blob (no extra object), and are queryable via `FindBlobsByTags`.
* Bad, because tags carry a small but **perpetual** per-tag monthly charge (~$3–30/mo per 1–10M chunks) to solve a one-time migration problem.
* Bad, because tags are weakly supported in Azurite (archive-tier + tags-in-listing), so the path could not be tested deterministically.
* Bad, because it needs a new blob abstraction (`SetTagsAsync`, a tags trait on listing / `BlobListItem.Tags`).

### Switch all v7 metadata to tags

* Good, because it would be a single code path (no tier branch) for every chunk.
* Bad, because native v7 never needs tags (it writes metadata before tiering), so this pays the perpetual per-tag cost on *every* chunk in *every* repository to solve a migration-only problem.

## More Information

* Write side (tier branch): [`MigrateV5.WriteMetadataAsync`](https://github.com/woutervanranst/Arius7/blob/master/src/Arius.Migration/MigrateV5.cs).
* Read side (resolve own-metadata-else-sidecar): [`ChunkIndexService.GetRepairEntriesAsync`](https://github.com/woutervanranst/Arius7/blob/master/src/Arius.Core/Shared/ChunkIndex/ChunkIndexService.cs), described in [repair-chunk-index](../design/core/features/repair-chunk-index.md).
* Constants and the GC note: [`BlobPaths.V5LegacySideCarPath`, `ContentTypes.V5LegacyMetadataSideCar`](https://github.com/woutervanranst/Arius7/blob/master/src/Arius.Core/Shared/Storage/BlobConstants.cs).
* The commit-sentinel model the descriptor participates in: [ADR-0017](adr-0017-idempotent-non-distributed-recovery.md).
