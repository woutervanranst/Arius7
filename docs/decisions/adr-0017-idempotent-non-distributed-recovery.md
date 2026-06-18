---
status: "accepted"
date: 2026-06-17
decision-makers: ["Wouter Van Ranst"]
consulted: ["Claude Code"]
informed: ["Arius maintainers"]
confidence: "high"
---

# Idempotent, non-distributed crash recovery via metadata-presence commit

## Context and Problem Statement

Arius archives large file collections to Azure Blob Storage and must produce restorable backups even when a run is interrupted — Docker restarts on a NAS, killed processes, network failures mid-upload. Azure Blob has no cross-blob transaction: writing a chunk body, its metadata, and a chunk-index entry are separate operations, and a crash can land between any of them. A backup tool cannot leave the repository in a state where a snapshot references data that was never durably written, nor can it require expensive pre-flight existence scans of up to 500M chunks on every run.

Two storage-shaped facts drive the design. First, an interrupted `OpenWriteAsync` can leave a body blob with no Arius metadata. Second, `BlockBlobClient.OpenWriteAsync` only accepts `overwrite: true`, so create-if-not-exists must be expressed as an `IfNoneMatch = ETag.All` precondition that Azure (412) and Azurite (409) surface as conflicts (`AzureBlobContainerService.IsAlreadyExistsError`, mapped to `BlobAlreadyExistsException`).

The question for this ADR is what commit and crash-recovery model Arius should use so that interrupted runs are safely resumable, without distributed locks, leases, or per-chunk pre-flight existence checks.

## Decision Drivers

* Archive and restore must be crash-recoverable and idempotent — re-running the same command after any interruption must converge, never corrupt.
* A published snapshot must never reference data that is not durably stored.
* No distributed coordinator is available or wanted: a run is a single process against blob storage with no lease/lock service.
* Avoid a pre-flight existence round-trip per chunk; the upload itself should decide create-vs-skip.
* Small files must not become individually rehydratable archive-tier blobs (per-transaction rehydration cost is prohibitive), yet must still support uniform lookup and recovery.

## Considered Options

* Metadata-presence commit point: write the body first, write the `arius_type` metadata sentinel last; treat metadata presence as the commit, drive create-if-not-exists off `BlobAlreadyExistsException`, publish the snapshot only after all referenced data is durable.
* Pre-flight `HEAD chunks/<hash>` per content hash before each upload, then conditional upload.
* A distributed lock / blob-lease coordinator serializing archive runs and tracking in-progress state.
* A sidecar manifest per tar bundle recording its members, instead of per-member thin chunks.

## Decision Outcome

Chosen option: "metadata-presence commit point", because it makes commit a property of the stored blob itself rather than of any external coordinator, so any re-run can reconstruct exactly what is and is not committed by reading metadata — with no locks and no pre-flight scan.

Two invariants define the model:

1. **Metadata presence = commit.** A chunk's body is streamed first; the `arius_type` metadata sentinel is written only after the body (and round-trip verification) succeed. A blob carrying `arius_type` is committed; a body blob without it is the debris of an interrupted run.
2. **Snapshot last.** The snapshot manifest is created and promoted only after every chunk, thin chunk, filetree blob, and chunk-index shard it references is durable. A snapshot therefore never points at uncommitted data.

Confidence: high. The mechanism is implemented and directly unit-tested in `ChunkStorageServiceUploadTests`, the commit ordering is explicit in `ChunkStorageService.UploadChunkAsync`, and the storage-level conflict mapping is covered by `BlobStorageServiceTests.OpenWrite_SecondWrite_ThrowsBlobAlreadyExistsException`.

Before — a pre-flight check decides upload-vs-skip with an extra round-trip and a TOCTOU window:

```text
HEAD chunks/<hash>  →  exists?  →  skip
                    →  missing? →  upload body → write metadata
```

After — the upload is optimistic; the conflict path inspects the sentinel:

```text
OpenWrite chunks/<hash> with IfNoneMatch=*   →  201 → write arius_type sentinel last (commit)
                                             →  409/412 BlobAlreadyExists →
        GetMetadata: has arius_type? → committed → skip (reuse, recover size from metadata)
                                     → no  arius_type? → partial → Delete + retry upload
```

This is the create-if-not-exists path in `ChunkStorageService.UploadChunkAsync` (`catch (BlobAlreadyExistsException)` → `GetMetadataAsync` → `ContainsKey(BlobMetadataKeys.AriusType)` → reuse, else `DeleteAsync` + `goto retry`) and, identically, in `UploadThinAsync`.

### Thin chunks vs orphan tars

Small files (`< SmallFileThreshold`) are packed by `TarBuilder` into one tar chunk (`UploadTarAsync`, `arius_type=tar`, blob name = tar hash). For each member, `UploadThinAsync` writes an ~empty `arius_type=thin` blob named by the member's content hash, with `parent_chunk_hash` pointing at the tar. Thin chunks pay one Cool-tier write per small file to buy two properties the recovery model needs: **uniform lookup** (`HEAD chunks/<content-hash>` resolves any file, large or small) and **per-member recovery** (a re-run after a tar upload but before the chunk index is flushed rediscovers each member→tar mapping from the thin chunk, rather than re-uploading the tar). The cost-benefit is deliberate: the orphan-tar failure mode — a crash between tar upload and full thin-chunk creation can leave ~one tar bundle's worth of bytes unreferenced until a future GC — is accepted as minor storage waste in exchange for never losing the mapping and never needing a distributed coordinator.

### Consequences and Tradeoffs

* Good, because recovery needs no external state: re-running the command is the recovery procedure, and the source of truth is the blobs themselves.
* Good, because there is no per-chunk pre-flight HEAD; the upload's `IfNoneMatch` conflict carries the existence answer, and only conflicts pay the extra `GetMetadata`.
* Good, because the snapshot-last ordering guarantees that any resolvable snapshot is fully restorable.
* Good, because metadata-presence is robust to the exact crash point — a half-written body without `arius_type` is simply overwritten, never mistaken for committed.
* Bad, because thin chunks add one Cool-tier blob write per small file; at 500M small files that is a real transaction-count cost paid for uniform lookup and recovery.
* Bad, because the non-atomic tar+thin boundary can orphan up to ~one tar bundle of bytes per crash; cleanup is deferred to a future GC (designed-for, not implemented).
* Bad, because the model assumes effectively single-writer-per-repository; it tolerates concurrent runs (the conflict path is safe) but does not coordinate them, so two simultaneous archives can each do redundant work and rebundle small files differently.

### Confirmation

The model is confirmed by tests that exercise each crash boundary against an in-memory / fault-injecting blob fake:

* `ChunkStorageServiceUploadTests.UploadLargeAsync_DeletesPartialExistingBlobAndRetries` — a conflicting blob with no `arius_type` is deleted and re-uploaded (interrupted-body recovery).
* `ChunkStorageServiceUploadTests.UploadLargeAsync_ReturnsOriginalSizeFromExistingCommittedBlob` and `UploadTarAsync_ReusesCompletedExistingBlob` — a conflicting blob that carries `arius_type` is treated as committed and skipped (idempotent re-run).
* `ChunkStorageServiceUploadTests.UploadLargeAsync_OpenWriteConflict_FetchesMetadataAfterSuccessfulStreamUpload` and `..._RetryAfterMetadataConflict_ReportsSingleProgressSequence` — the conflict-on-commit path resolves correctly.
* `ChunkStorageServiceUploadTests.UploadThinAsync_ReturnsFalse_WhenCommittedBlobAlreadyExists` and `UploadThinAsync_DeletesPartialExistingBlobAndRetries` — the same metadata-presence rule governs thin chunks.
* `ChunkStorageServiceUploadTests.UploadLargeAsync_FailsLoudly_AndDoesNotRecord_WhenStoredChunkDoesNotRoundTrip` — a chunk that fails round-trip is deleted before any metadata sentinel is written, so it can never be seen as committed.
* `BlobStorageServiceTests.OpenWrite_SecondWrite_ThrowsBlobAlreadyExistsException` — the storage layer maps the `IfNoneMatch=*` precondition failure to `BlobAlreadyExistsException`, the signal the whole model relies on.

The decision holds while all of the following remain true: chunk and thin uploads write the `arius_type` sentinel only after a verified body; create-if-not-exists is driven by `BlobAlreadyExistsException` rather than a pre-flight HEAD; `ArchiveCommandHandler` creates and promotes the snapshot (stage 6d) only after all upload and chunk-index stages have drained; and `FileTreeService` continues to treat `BlobAlreadyExistsException` on a content-addressed filetree blob as an idempotent no-op.

## Pros and Cons of the Options

### Metadata-presence commit point (chosen)

* Good, because commit state lives in the blob, making re-run the recovery path with zero external coordination.
* Good, because the upload conflict doubles as the existence check — no separate pre-flight round-trip.
* Good, because snapshot-last publication guarantees referential durability.
* Bad, because thin chunks add a per-small-file write and a non-atomic tar boundary that can orphan bytes.

### Pre-flight HEAD per chunk

* Good, because it is conceptually simple and avoids relying on conflict semantics.
* Bad, because it adds a round-trip per content hash on every run, costly at hundreds of millions of chunks.
* Bad, because the time-of-check/time-of-use gap between HEAD and upload still needs conflict handling, so it does not actually remove the conflict path it tries to avoid.

### Distributed lock / blob-lease coordinator

* Good, because it could serialize runs and explicitly track in-progress work.
* Bad, because it introduces a coordinator and lease-renewal/expiry failure modes the current single-process NAS deployment neither has nor wants.
* Bad, because a crash still requires reconciling against actual blob state, so the durable invariant must exist anyway — the lock adds machinery without removing the need for metadata-presence recovery.

### Sidecar manifest per tar instead of thin chunks

* Good, because it records all tar members in one blob, avoiding a write per small file.
* Bad, because `HEAD chunks/<content-hash>` no longer answers existence for a small file — lookup must consult manifests, breaking uniform resolution.
* Bad, because a crash that rebuilds a tar differently leaves the old manifest referencing members now bundled elsewhere, a harder orphan/consistency problem than per-member thin chunks.

## More Information

* Recovery, idempotency, and thin-chunk-vs-orphan-tar reasoning (decisions 4, 7, 8; trade-offs on orphan thin/tar chunks): `docs/history/openspec-archive/2026-03-24-arius-core-foundation/design.md`.
* Commit ordering and create-if-not-exists: `src/Arius.Core/Shared/ChunkStorage/ChunkStorageService.cs` (`UploadChunkAsync`, `UploadThinAsync`).
* Optimistic-concurrency mapping at the storage edge: `src/Arius.AzureBlob/AzureBlobContainerService.cs` (`OpenWriteAsync` with `IfNoneMatch = ETag.All`, `IsAlreadyExistsError`).
* Metadata sentinel definition: `src/Arius.Core/Shared/Storage/BlobConstants.cs` (`BlobMetadataKeys.AriusType`).
* Snapshot-last ordering: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs` (end-of-pipeline stages 6a–6d).
