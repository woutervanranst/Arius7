## 1. Solution & Project Setup

- [ ] 1.1 Create .NET solution with Arius.Core, Arius.Cli, Arius.AzureBlob projects and test projects
- [ ] 1.2 Add NuGet dependencies: System.CommandLine, Mediator, Azure.Storage.Blobs, SharpCompress (or equivalent tar library)
- [ ] 1.3 Set up Dockerfile for Linux container deployment (Synology NAS target)
- [ ] 1.4 Configure DI wiring: CLI registers Core services and AzureBlob storage provider

## 2. Encryption & Hashing

- [ ] 2.1 Implement AES-256-CBC encrypt/decrypt (openssl-compatible: `Salted__` prefix, PBKDF2 SHA-256, 10K iterations) as streaming operations
- [ ] 2.2 Implement passphrase-seeded SHA-256 hashing (`SHA256(passphrase + data)`) and plain SHA-256 hashing as streaming operations
- [ ] 2.3 Implement pluggable encryption stream wrapper: encrypt/decrypt when passphrase present, pass-through when absent
- [ ] 2.4 Unit tests: encrypt → decrypt roundtrip, decrypt openssl-generated ciphertext, passphrase-seeded hash determinism, different passphrases produce different hashes, plaintext mode produces plain SHA-256 hashes, stream pass-through without passphrase

## 3. Storage Abstraction

- [ ] 3.1 Define `IStorageProvider` interface in Core: upload, download, HEAD, list by prefix, set tier, initiate rehydration copy
- [ ] 3.2 Implement `AzureBlobStorageProvider` in Arius.AzureBlob using Azure.Storage.Blobs SDK
- [ ] 3.3 Implement container prefix layout: `chunks/`, `trees/`, `snapshots/`, `chunk-index/`, `chunks-rehydrated/`
- [ ] 3.4 Integration tests with Azurite test container: upload/download/HEAD/list/tier operations

## 4. Snapshot State Model

- [ ] 4.1 Define tree node model: versioned JSON with entries (name, type, hash, size, created, modified)
- [ ] 4.2 Implement tree hash computation: `SHA256(passphrase + serialized entries)`
- [ ] 4.3 Implement tree node serialization/deserialization (JSON, gzip, encrypt/decrypt)
- [ ] 4.4 Implement tree builder: build merkle tree bottom-up from a flat list of (relative path, content hash, metadata) entries
- [ ] 4.5 Implement tree traversal: walk from root tree hash, download/decrypt tree nodes on demand, resolve paths
- [ ] 4.6 Implement snapshot manifest: create/read encrypted manifest (root tree hash, timestamp, file count, total size, Arius version)
- [ ] 4.7 Implement local tree cache: cache downloaded tree blobs by hash, persist across CLI invocations
- [ ] 4.8 Unit tests: tree builder roundtrip, tree traversal path resolution, tree node extensibility (unknown fields ignored)

## 5. Chunk Index

- [ ] 5.1 Implement chunk index shard model: content-hash → tar-chunk-hash entries, sharded by 2-byte prefix
- [ ] 5.2 Implement chunk index read: download shard, decompress, deserialize, lookup content hash
- [ ] 5.3 Implement chunk index write: add entries, serialize, compress, upload affected shards
- [ ] 5.4 Implement content-hash → chunk resolution: try `HEAD chunks/<content-hash>`, fall back to chunk index
- [ ] 5.5 Unit tests: shard lookup, shard update with new entries, resolution strategy (direct vs index fallback)

## 6. Pointer Files

- [ ] 6.1 Implement pointer file read/write: `<filename>.pointer.arius` containing hex content hash
- [ ] 6.2 Implement pointer file comparison: read pointer, compare with computed hash, determine skip/update/create
- [ ] 6.3 Unit tests: create/read/update pointer, missing pointer, out-of-sync pointer

## 7. Archive Pipeline

- [ ] 7.1 Implement filesystem enumeration stage: recursive walk with graceful error handling (skip unreadable, log warning), produce `IAsyncEnumerable<FileInfo>`
- [ ] 7.2 Implement hash stage: N parallel workers consuming from Channel, compute passphrase-seeded hash, compare with pointer file
- [ ] 7.3 Implement decide stage: classify as skip (pointer matches + chunk exists) / upload-large / add-to-tar-buffer
- [ ] 7.4 Implement large file upload: gzip → encrypt → upload as `chunks/<content-hash>` with content type `application/aes256cbc+gzip`
- [ ] 7.5 Implement tar buffer: accumulate small files, seal at target size (64 MB default), tar → gzip → encrypt → upload as `chunks/<tar-hash>` with content type `application/aes256cbc+tar+gzip`
- [ ] 7.6 Implement upload dedup guard: `ConcurrentDictionary<hash, Task>` preventing duplicate concurrent uploads
- [ ] 7.7 Implement finalize stage: write/update pointer files, build merkle tree, upload new tree nodes (with HEAD dedup), upload snapshot manifest, update chunk index shards
- [ ] 7.8 Implement `--remove-local`: delete binary files after successful archive, keep pointer files only
- [ ] 7.9 Implement `--tier` option: set storage tier for newly uploaded chunks (default Archive)
- [ ] 7.10 Wire up archive Mediator command: connect all pipeline stages with bounded Channels, configure parallelism
- [ ] 7.11 Implement streaming progress events for archive: file hashed, upload started/completed, warning, snapshot created

## 8. Restore Pipeline

- [ ] 8.1 Implement resolve stage: traverse snapshot tree to find target files, resolve content-hash → chunk-hash for each
- [ ] 8.2 Implement rehydrate stage phase 1: check `chunks-rehydrated/` for already-rehydrated chunks, pass through immediately
- [ ] 8.3 Implement rehydrate stage phase 2: submit rehydration requests for archive-tier chunks with dedup guard, batch-poll for completion
- [ ] 8.4 Implement download stage: N parallel workers downloading from `chunks-rehydrated/` into local cache
- [ ] 8.5 Implement decrypt+extract stage: decrypt, gunzip, un-tar (for bundles), extract target files. Cache tar locally for reuse.
- [ ] 8.6 Implement write stage: write files to disk, set created/modified timestamps from snapshot metadata, create pointer files
- [ ] 8.7 Implement cleanup: delete `chunks-rehydrated/` blobs and local chunk cache after full restore
- [ ] 8.8 Implement `-v` version selection: default to latest snapshot (lexicographic last), or specific snapshot name
- [ ] 8.9 Implement restore scopes: single file, multiple files, directory (recursive), full snapshot
- [ ] 8.10 Wire up restore Mediator command: connect all pipeline stages with bounded Channels
- [ ] 8.11 Implement streaming progress events for restore: rehydration requested/completed, download started/completed, file restored, warning

## 9. Ls Command

- [ ] 9.1 Implement ls core: download snapshot manifest, traverse tree for requested path, display entries with metadata
- [ ] 9.2 Implement prefix filter: filter by path prefix using tree traversal (only download needed tree nodes)
- [ ] 9.3 Implement substring filter: full tree traversal with substring match (populate cache as it goes)
- [ ] 9.4 Implement `-v` version selection for ls
- [ ] 9.5 Wire up ls Mediator command

## 10. CLI

- [ ] 10.1 Set up System.CommandLine with `arius archive`, `arius restore`, `arius ls` commands
- [ ] 10.2 Implement global options: `--account-name`, `--account-key`, `--passphrase` (optional), `--container`
- [ ] 10.3 Implement archive-specific options: `--tier`, `--remove-local`, `--small-file-threshold`, `--tar-target-size`
- [ ] 10.4 Implement restore-specific options: `-v`, file/directory path arguments
- [ ] 10.5 Implement ls-specific options: `-v`, prefix filter, substring filter
- [ ] 10.6 Implement streaming progress display: consume progress events from Core and render to console
- [ ] 10.7 Implement extensive logging throughout all operations for traceability

## 11. Cross-Platform & Path Handling

- [ ] 11.1 Implement path normalization: convert `\` to `/`, strip drive letters, ensure relative paths
- [ ] 11.2 Handle reserved characters in filenames across Windows/Linux
- [ ] 11.3 Unit tests: path normalization on Windows-style paths, round-trip through tree storage

## 12. Integration & End-to-End Tests

- [ ] 12.1 Set up test infrastructure: Azurite test container + option for real Azure Blob Storage account
- [ ] 12.2 E2E test: archive a directory, restore it fully, verify all files match original content and metadata
- [ ] 12.3 E2E test: archive, modify a file in place (same name, different content), archive again, restore latest snapshot has new content, restore previous snapshot has old content
- [ ] 12.4 E2E test: archive with `--remove-local`, verify binaries deleted and pointer files remain, restore fully
- [ ] 12.5 E2E test: archive with mix of large and small files, verify large files as solo chunks and small files in tar bundles, restore all correctly
- [ ] 12.6 E2E test: rename a file + its pointer, archive, verify snapshot captures new path, old path in previous snapshot
- [ ] 12.7 E2E test: duplicate a file, archive, verify deduplication (one chunk, two paths in snapshot)
- [ ] 12.8 E2E test: delete all pointer files, archive, verify pointer files rebuilt and snapshot correct
- [ ] 12.9 E2E test: pointer file hash out of sync with binary, archive, verify binary re-hashed and pointer updated
- [ ] 12.10 E2E test: ls with path prefix filter, substring filter, version selection
- [ ] 12.11 E2E test: restore single file, directory, full snapshot
- [ ] 12.12 E2E test: graceful handling of unreadable files/directories during archive
- [ ] 12.13 E2E test: cross-platform path handling (paths with backslashes stored as forward slashes)
- [ ] 12.14 E2E test: archive and restore without passphrase (plaintext mode), verify all data stored unencrypted and restorable
- [ ] 12.15 E2E test: archive with passphrase, attempt restore without passphrase, verify failure
