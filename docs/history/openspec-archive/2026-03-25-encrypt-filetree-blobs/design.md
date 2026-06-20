## Context

Arius encrypts all blob types (chunks, snapshots, chunk-index shards) using `IEncryptionService.WrapForEncryption` before uploading to Azure Blob Storage — except filetree blobs. Filetree blobs are uploaded as plaintext UTF-8 text, leaking directory structure, file names, and timestamps to anyone with storage account read access.

The `ShardSerializer` in `Arius.Core/ChunkIndex/` already implements the exact pattern needed: `text → gzip → encrypt` for remote storage, with separate plaintext methods for local disk cache. `FileTreeBuilder` already has `IEncryptionService` injected but only uses it for hash computation.

## Goals / Non-Goals

**Goals:**
- Filetree blobs in Azure are gzip-compressed and encrypted (when passphrase provided), matching all other blob types
- Follow the established `ShardSerializer` pattern exactly for consistency
- Local disk cache remains plaintext for debuggability and because we trust the local filesystem
- Content type reflects encryption state (matching chunk-index convention)

**Non-Goals:**
- Backwards compatibility with existing unencrypted filetree blobs (still in development)
- Azure-level server-side encryption (SSE) or client-side encryption SDK
- Changing the tree hash computation (already passphrase-seeded, working correctly)
- Changing the text serialization format itself

## Decisions

### Decision 1: Mirror the ShardSerializer pattern exactly

Add `SerializeForStorage` / `DeserializeFromStorage` methods to `FileTreeBlobSerializer`, following `ShardSerializer.SerializeAsync` / `Deserialize` exactly:

```
Upload:   Serialize(tree) → gzip → encrypt → byte[]
Download: stream → decrypt → gunzip → DeserializeAsync(stream)
```

The existing `Serialize` / `Deserialize` / `DeserializeAsync` methods remain unchanged — they serve the local disk cache path (equivalent to `ShardSerializer.SerializeLocal` / `DeserializeLocal`).

**Why mirror rather than extract a shared abstraction:** The two serializers operate on different types (`FileTreeBlob` vs `Shard`) with different serialization formats. A shared base would add indirection without reducing code — the gzip+encrypt wrapping is ~5 lines per method. Copy the pattern, not an abstraction.

### Decision 2: Conditional content type on upload

Add `FileTreeEncrypted` and `FileTreePlaintext` content types to `BlobConstants.ContentTypes`, following the existing convention:

| Blob type | Encrypted | Plaintext |
|---|---|---|
| Large chunk | `application/aes256cbc+gzip` | `application/gzip` |
| Tar chunk | `application/aes256cbc+tar+gzip` | `application/tar+gzip` |
| Snapshot | `application/aes256cbc+gzip` | `application/gzip` |
| Chunk index | `application/aes256cbc+gzip` | `application/gzip` |
| **Filetree** | **`application/aes256cbc+gzip`** | **`application/gzip`** |

`TreeService.EnsureUploadedAsync` selects the content type based on `_encryption.IsEncrypted`, same as `ChunkIndexService` does at line 172.

### Decision 3: Download paths wrap with decrypt + gunzip

`RestorePipelineHandler.WalkTreeAsync` and `LsHandler.WalkTreeAsync` both download filetree blobs via `_blobs.DownloadAsync` and pass the stream directly to `FileTreeBlobSerializer.DeserializeAsync`. Both need to wrap the download stream through `decrypt → gunzip` before deserialization.

Use a new `DeserializeFromStorageAsync(stream, encryption)` method on `FileTreeBlobSerializer` so the callers stay clean — one method call instead of manually wrapping streams at each call site.

### Decision 4: Disk cache stores plaintext (no change)

`TreeService.EnsureUploadedAsync` writes raw serialized bytes to the local disk cache at `~/.arius/{account}-{container}/filetrees/{hash}`. This stays plaintext — same rationale as `ShardSerializer.SerializeLocal`: we trust the local filesystem and plaintext cache aids debugging.

The upload path: serialize → write to cache (plaintext) → gzip+encrypt → upload.
The download path in restore/ls does NOT write to disk cache (it's a read-through from blob storage), so no cache format issue there.

### Decision 5: IEncryptionService plumbed to RestorePipelineHandler and LsHandler

Both handlers need `IEncryptionService` to decrypt filetree blobs on download. Check whether they already have it injected; if not, add it via constructor injection (same pattern used everywhere else in the codebase).

## Risks / Trade-offs

**[Risk] Filetree blobs are small — gzip overhead could increase size** → Unlikely in practice. Even small text compresses well with gzip. And consistency across all blob types outweighs marginal size differences.

**[Risk] Worst-case recovery for filetree blobs** → Same as other blob types: `openssl enc -d ... | gunzip`. This is already documented as the recovery pattern in REQUIREMENTS.md line 223. Filetree blobs just didn't participate before.
