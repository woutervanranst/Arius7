# Chunk storage

> **Code:** `src/Arius.Core/Shared/ChunkStorage/` (`ChunkStorageService.cs`)  ·  **Decisions:** [ADR-0017](../../../decisions/adr-0017-idempotent-non-distributed-recovery.md)  ·  **Terms:** [chunk](../../../glossary.md#chunk) · [large chunk](../../../glossary.md#large-chunk) · [tar chunk](../../../glossary.md#tar-chunk) · [thin chunk](../../../glossary.md#thin-chunk) · [storage tier hint](../../../glossary.md#storage-tier-hint)

## Purpose

`ChunkStorageService` owns the **chunk blob protocol**: how a [chunk](../../../glossary.md#chunk) is encoded onto and decoded off Azure Blob Storage. It is the single place that constructs chunk blob names, drives the compress → encrypt → upload stack, writes the metadata that marks a blob *committed*, and resolves rehydration state for archive-tier blobs. It deliberately knows **nothing about content-hash lookup** — that is the chunk index's job (see [`chunk-index.md`](./chunk-index.md)).

## How it works

Feature handlers hold an `IChunkStorageService` and never touch `BlobPaths.ChunkPath(...)`, content types, or chunk metadata keys themselves. The service collaborates with three lower-level services injected through its primary constructor: `IBlobContainerService` (raw blob I/O), `ICompressionService` (zstd), and `IEncryptionService` (AES-256-GCM).

### The three upload shapes

There is no kind-switching `Upload(kind, …)`; each chunk realization has its own entry point, distinguished only by blob-path convention and the `arius_type` metadata value:

| Method | Body | `arius_type` | Notes |
|---|---|---|---|
| `UploadLargeAsync` | one file, compressed (+encrypted) | `large` | `chunk hash == content hash` |
| `UploadTarAsync` | a tar bundle of small files, compressed (+encrypted) | `tar` | no `original_size` — that lives on the thin chunks |
| `UploadThinAsync` | **empty** | `thin` | a pointer; metadata carries `parent_chunk_hash` |

`UploadLargeAsync` and `UploadTarAsync` both forward to the private `UploadChunkAsync`, differing only by `ariusType` and an `isTar` flag (which selects the content type and suppresses `original_size`). Both return a `ChunkUploadResult(ChunkHash, StoredSize, AlreadyExisted, OriginalSize?)` the caller records in the index.

### Body-first, metadata-sentinel commit

This is the load-bearing invariant of the whole protocol — see [ADR-0017](../../../decisions/adr-0017-idempotent-non-distributed-recovery.md). `UploadChunkAsync` streams the body first and writes the `arius_type` metadata **last**, so metadata presence *is* the commit point. There is no transaction across body + metadata + index; recovery is reconstructed by reading the blob's own metadata.

```mermaid
flowchart TD
    Start["UploadChunkAsync(chunkHash, content, tier, smallFileThreshold)"] --> Seek["require seekable source<br/>rewind to position 0"]
    Seek --> Open["blobs.OpenWriteAsync(ChunkPath, contentType)<br/>IfNoneMatch=* (create-if-not-exists)"]
    Open -->|201 created| Pipe
    Open -->|409/412 conflict| Exists

    subgraph Pipe["stream the body"]
      direction TB
      Src["source bytes"] --> Z["zstd compress"] --> Tee["TeeStream"]
      Tee --> Enc["AES-256-GCM encrypt"] --> Cnt["CountingStream → blob"]
      Tee -.compressed bytes.-> Ver["IUploadVerifier.Sink"]
    end

    Pipe --> Verify{"verifier hash<br/>== chunk hash?"}
    Verify -->|"no / mismatch"| DelFail["DeleteAsync + throw InvalidDataException"]
    Verify -->|"yes / not verified"| Commit["SetMetadataAsync(arius_type, chunk_size, [original_size])<br/>← COMMIT POINT"]
    Commit --> Ceil{"Archive requested and<br/>chunk_size ≤ smallFileThreshold?"}
    Ceil -->|yes| Cold["actual tier = Cold"]
    Ceil -->|no| Asked["actual tier = requested tier"]
    Cold --> SetTier
    Asked --> SetTier
    SetTier["SetTierAsync(actual tier)"] --> Done["ChunkUploadResult(AlreadyExisted: false, ActualTier)"]

    Exists["GetMetadataAsync(ChunkPath)"] --> HasType{"has arius_type?"}
    HasType -->|"yes (committed)"| Reuse["ChunkUploadResult(AlreadyExisted: true)<br/>recover size + tier from metadata"]
    HasType -->|"no (partial debris)"| Retry["DeleteAsync → rewind → retry upload"]
    Retry --> Open
```

The conflict path (`catch (BlobAlreadyExistsException)`) is what makes re-runs idempotent: a blob already carrying `arius_type` is reused (size recovered via `TryReadOriginalSize`, tier read straight off the blob); a body blob *without* it is debris from an interrupted run and is deleted and retried. `OriginalSize` is omitted on tar blobs (`if (!isTar)`) because a tar's per-file sizes live on its thin chunks.

### The archive-tier ceiling

`tier` is a **ceiling, not a mandate**. `GetActualStorageTier` downgrades an `Archive` request to `Cold` when the stored chunk is within `smallFileThreshold` (the caller passes `ArchiveCommandOptions.SmallFileThreshold`), and the tier the blob actually landed on is returned as `ChunkUploadResult.ActualTier`.

This exists because the two sizes differ. The archive command routes a *file* to the large or tar path by its **uncompressed** size, but what gets billed and rehydrated is the **stored** (compressed + encrypted) blob — and a file above the threshold can compress back well within it. Rehydrating such a chunk costs far more than the archive tier saves on it; v5 Arius put the break-even for a 1 MB chunk at roughly 5.5 years. Applying the ceiling here rather than in the handler is what makes it exact: `storedSize` does not exist until the write block closes, a few lines above the `SetTierAsync` call, and both the large and tar upload paths funnel through this one method (so the tiny end-of-run partial tar bundle is covered too). See [ADR-0023](../../../decisions/adr-0023-archive-tier-small-chunk-ceiling.md).

### Inline round-trip verification

The upload stack tees the compressed bytes into an `IUploadVerifier` (see `UploadVerifier.cs`) so the stored frame is confirmed restorable *before* the commit metadata is written — never by re-downloading the offline archive-tier blob. `RoundTripVerifier` decompresses the tee'd bytes on a background task through a bounded `Pipe` (flat memory) and re-hashes; `NoopVerifier` (`Stream.Null` sink) is used for codecs Arius already trusts (the legacy BCL gzip path). If the restored hash differs from the chunk hash, the blob is deleted and the upload fails loudly rather than recording an unrecoverable chunk. (See [ADR-0017](../../../decisions/adr-0017-idempotent-non-distributed-recovery.md) and the round-trip rationale in [`compression.md`](./compression.md).)

### Thin chunk upload

`UploadThinAsync` uploads an **empty** body to `chunks/<content-hash>` with metadata `arius_type=thin`, `parent_chunk_hash`, `original_size`, `chunk_size`, always at `BlobTier.Cool`. It runs the same conflict recovery as large/tar: an existing blob with `arius_type` is accepted (`return false`); one without is deleted and retried. The return value distinguishes *created* (`true`) from *already-committed* (`false`).

### Download

`DownloadAsync` mirrors the upload stack in reverse and returns **plaintext** the caller can restore or untar directly:

```text
SelectReadableChunkBlobAsync → blobs.DownloadAsync → [progress] → decrypt → decompress → ChunkDownloadStream
```

`SelectReadableChunkBlobAsync` prefers the rehydrated copy (`chunks-rehydrated/<hash>`) when it exists and is **not** archive-tier; otherwise it falls back to `chunks/<hash>`. `ChunkDownloadStream` is a thin wrapper whose only job is to own disposal of the whole decrypt/decompress/download stack as one unit. The read path auto-detects gzip vs zstd from the frame header, so content type is informational only.

### Hydration status & rehydration lifecycle

`GetHydrationStatusAsync` is the single authority for resolving a chunk's hydration state into the shared `ChunkHydrationStatus` enum (`Unknown`, `Available`, `NeedsRehydration`, `RehydrationPending`):

```mermaid
flowchart TD
    A["GetHydrationStatusAsync(chunkHash)"] --> B{"chunks/<hash> exists?"}
    B -->|no| U["Unknown"]
    B -->|yes| C{"tier != Archive?"}
    C -->|yes| AV["Available"]
    C -->|"no (archived)"| D{"chunks-rehydrated/<hash> exists?"}
    D -->|yes| E{"rehydrated tier == Archive?"}
    E -->|yes| P["RehydrationPending"]
    E -->|no| AV
    D -->|no| F{"primary IsRehydrating?"}
    F -->|yes| P
    F -->|no| N["NeedsRehydration"]
```

`StartRehydrationAsync` is a server-side copy from `chunks/<hash>` to `chunks-rehydrated/<hash>` targeting `BlobTier.Cold` with a `RehydratePriority` — a temporary readable copy, leaving the archive-tier original untouched.

Cleanup is two-phase so restore can preview before deleting: `PlanRehydratedCleanupAsync` enumerates the `chunks-rehydrated/` prefix **once**, capturing `ChunkCount` and `TotalBytes`, and returns an `IRehydratedChunkCleanupPlan`. The plan's `ExecuteAsync` then deletes those captured names with 16 parallel workers and returns the actual `RehydratedChunkCleanupResult` — no second enumeration. `ListRehydratedChunksAsync` is the bulk counterpart used by listings: a single prefix listing mapping each chunk hash to *ready* (non-archive tier) vs *still rehydrating* (archive tier), avoiding per-chunk metadata calls.

## Key invariants

- **Metadata presence = commit; snapshot last.** The `arius_type` sentinel is written only after the body succeeds and round-trips. A body blob without `arius_type` is partial debris, safe to delete and retry. ([ADR-0017](../../../decisions/adr-0017-idempotent-non-distributed-recovery.md))
- **Storage owns the blob protocol; the index owns lookup.** Feature handlers never construct chunk blob names, pick content types, write chunk metadata keys, or build the compress/encrypt chain — those live only here. Content-hash → chunk-hash resolution lives only in the chunk index. (Mixing them is the failure mode this split prevents.)
- **Upload sources must be seekable.** `UploadChunkAsync` throws if `!content.CanSeek`, because the conflict-recovery retry rewinds and re-streams the body.
- **The requested tier is a ceiling, and the reported tier is the truth.** A chunk within `smallFileThreshold` is never archived, and `ChunkUploadResult.ActualTier` — not the request — is what the caller records as the chunk's [storage tier hint](../../../glossary.md#storage-tier-hint). A hint that claimed `Archive` for an online blob would make restore pay to rehydrate something it could simply download.
- **The conflict path reports the observed tier and never re-tiers.** A blob recovered from a prior run is already committed, and on Azure moving one out of the archive tier *is* a rehydration — slow and paid. It is left where it is, and the index is told where that is.
- **Round-trip verification gates the commit.** A chunk whose stored frame does not decompress back to its chunk hash is deleted, not recorded — the archive tier is offline and cannot be re-verified later.
- **Download returns plaintext.** The returned stream is already decrypted and decompressed; callers (large-file restore, tar extraction) consume bytes directly.
- **Rehydration never mutates the original.** It copies `chunks/` → `chunks-rehydrated/`; the archive-tier source is left in place, and the rehydrated copy is transient (cleaned up after restore).

## Why this shape

- **No transaction, no coordinator, no pre-flight scan.** Body-first + metadata-sentinel makes the commit a property of the blob itself, so any re-run converges by reading metadata — the alternatives (per-chunk `HEAD`, a distributed lock, a per-tar sidecar manifest) were rejected in [ADR-0017](../../../decisions/adr-0017-idempotent-non-distributed-recovery.md).
- **Verify at write time, not by re-read.** The archive tier is offline; the only cheap moment to confirm a chunk restores is while its bytes still stream past, hence the tee + bounded-pipe verifier rather than a later download.
- **Three explicit upload methods, not a kind switch.** The realizations differ in body shape and metadata, not in a runtime kind flag — separate methods keep callers honest about which one they mean (spec scenarios: *"call `UploadTarAsync` rather than a generic kind-switching upload API"*).
- **Plan/execute cleanup split.** Restore must show the user a confirmation total before deleting rehydrated blobs; capturing it during the single planning enumeration avoids listing `chunks-rehydrated/` twice.

## Open seams / future

- **Legacy read variants.** `ContentTypes` still carries CBC/gzip variants for reading pre-zstd blobs; new writes are always zstd + GCM. When legacy blobs are no longer in the wild these can be dropped.
- **No background reaper for partial debris.** Interrupted-upload bodies are cleaned up *lazily* — only when a later run re-attempts the same chunk hash and hits the conflict path. Orphaned bodies for chunks never re-archived persist until a future sweep mechanism exists.
- **Rehydration target is hardcoded to `BlobTier.Cold` / 16 delete workers.** Tier and parallelism are constants; surfacing them as policy is a future change point if rehydration economics shift.
- **The archive-tier ceiling corrects the tier, not the blob count.** A highly compressible file above `SmallFileThreshold` still becomes its own small standalone blob — correctly tiered, but not bundled with its neighbours. Routing it into a tar instead would need the compressed size known *before* the route is chosen; see the open seams in [archive-command](../features/archive-command.md#open-seams-future).
