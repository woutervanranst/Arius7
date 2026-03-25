## Why

Filetree blobs are uploaded to Azure Blob Storage as plaintext, violating REQUIREMENTS.md line 219 which states ALL blobs SHALL be encrypted when a passphrase is provided. Chunk bodies, snapshot manifests, and chunk index shards are all correctly encrypted via `IEncryptionService.WrapForEncryption`, but filetree blobs bypass encryption entirely. This leaks directory structure, file names, and timestamps to anyone with storage account read access.

## What Changes

- Filetree blobs uploaded to Azure SHALL be gzip-compressed and optionally encrypted (matching the chunk index shard pattern: `text → gzip → encrypt`)
- Filetree blobs downloaded from Azure SHALL be decrypted and decompressed before deserialization
- Local disk cache remains plaintext (trusted local filesystem)
- Content type for filetree blobs changes from always `text/plain` to `application/aes256cbc+gzip` (encrypted) or `application/gzip` (plaintext), matching the existing content type convention
- **BREAKING**: Existing unencrypted filetree blobs in storage are incompatible (acceptable — still in development)

## Capabilities

### New Capabilities

_(none — this is closing a gap in existing capabilities)_

### Modified Capabilities

- `encryption`: Add requirement that filetree blob bodies SHALL be encrypted (gzip + AES-256-CBC) when a passphrase is provided, consistent with all other blob types. Add worst-case recovery scenario for filetree blobs.
- `blob-storage`: Update filetree content type requirement from `text/plain; charset=utf-8` to encrypted/plaintext variants matching the chunk index convention.

## Impact

- `TreeBlobSerializer` — new storage serialization methods (gzip + encrypt / decrypt + gunzip)
- `TreeService.EnsureUploadedAsync` — use encrypted serialization for upload, conditional content type
- `RestorePipelineHandler.WalkTreeAsync` — decrypt + decompress download stream before deserialization
- `LsHandler.WalkTreeAsync` — same as restore
- `BlobConstants.ContentTypes` — add `FileTreeEncrypted` / `FileTreePlaintext`, update `FileTree`
- Existing tests for tree serialization and roundtrip need encryption variants
