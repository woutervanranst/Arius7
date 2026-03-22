## 1. Solution Structure & Project Setup

- [x] 1.1 Create .NET solution with projects: Arius.Core, Arius.Cli, Arius.AzureBlob
- [x] 1.2 Create test projects: Arius.Core.Tests, Arius.Integration.Tests, Arius.E2E.Tests, Arius.Architecture.Tests
- [x] 1.3 Add NuGet dependencies: Mediator, FluentValidation, FluentResults, Azure.Storage.Blobs, System.CommandLine, Spectre.Console, Humanizer, TUnit, NSubstitute, Shouldly, ArchUnitNET
- [x] 1.4 Configure project references: Cli → Core, AzureBlob → Core (implements interfaces)
- [x] 1.5 Add Dockerfile for Synology deployment
- [x] 1.6 Architecture tests: enforce Core has no Azure dependency, no circular references, Mediator commands in Core

## 2. Encryption & Hashing

- [x] 2.1 Define `IEncryptionService` interface in Core (WrapForEncryption, WrapForDecryption, ComputeHash streaming + byte[])
- [x] 2.2 Implement `PassphraseEncryptionService`: AES-256-CBC, openssl-compatible (Salted__ prefix, PBKDF2 SHA-256 10K iterations), streaming encrypt/decrypt
- [x] 2.3 Implement `PlaintextPassthroughService`: no-op stream wrapper, plain SHA256
- [x] 2.4 Implement passphrase-seeded hashing: SHA256(passphrase_bytes + data_bytes) with streaming support
- [x] 2.5 Unit tests: encrypt/decrypt roundtrip, openssl CLI compatibility (shell out to openssl), Salted__ prefix check, streaming large file (no memory spike)
- [x] 2.6 Golden file tests: decrypt actual chunks from previous Arius version with known passphrase
- [x] 2.7 Unit tests: hash determinism, passphrase-seeded vs. plaintext, same file different passphrase produces different hash

## 3. Blob Storage Abstraction

- [x] 3.1 Define `IBlobStorageService` in Core: upload (streaming + metadata + tier), download (streaming), HEAD (exists + metadata + tier), list by prefix, set metadata, copy blob
- [x] 3.2 Implement Azure Blob Storage service in Arius.AzureBlob using Azure.Storage.Blobs SDK
- [x] 3.3 Implement blob metadata handling: arius-type (large/tar/thin), arius-complete, original-size, chunk-size, compressed-size
- [x] 3.4 Implement container layout: chunks/, chunks-rehydrated/, filetrees/, snapshots/, chunk-index/ with correct tier management
- [x] 3.5 Integration test fixture: Azurite via TestContainers, shared across test run, unique container per test
- [x] 3.6 Integration tests: upload/download roundtrip, HEAD check with metadata, tier setting, list by prefix

## 4. Chunk Index

- [x] 4.1 Implement shard entry format: parse and serialize `<content-hash> <chunk-hash> <original-size> <compressed-size>\n`
- [x] 4.2 Implement shard merge: load existing shard, append new entries, serialize
- [x] 4.3 Implement shard compression: gzip (+ encrypt if passphrase) before upload, decompress on load
- [x] 4.4 Implement L1 in-memory LRU cache with configurable size budget
- [x] 4.5 Implement L2 disk cache at `~/.arius/cache/<repo-id>/chunk-index/`
- [x] 4.6 Implement L3 Azure download with save-to-L2 and promote-to-L1
- [x] 4.7 Implement batched dedup lookup: buffer N hashes, group by prefix, resolve through tiers
- [x] 4.8 Implement in-flight set (ConcurrentDictionary) for duplicate prevention within a run
- [x] 4.9 Implement repo-id derivation: SHA256(accountname + container)[:12]
- [x] 4.10 Unit tests: shard parse/serialize roundtrip, merge correctness, LRU eviction, batch grouping
- [x] 4.11 Integration tests: tiered lookup through Azurite, cache persistence across runs

## 5. File Tree (Merkle Tree)

- [x] 5.1 Define tree blob JSON format: entries with name, type, hash, created, modified (no size)
- [x] 5.2 Implement tree blob serialization/deserialization with deterministic JSON output (sorted entries by name)
- [x] 5.3 Implement tree hash computation: SHA256 of serialized JSON (passphrase-seeded if encrypted)
- [x] 5.4 Implement manifest writer: append (path, hash, created, modified) entries to unsorted temp file during pipeline
- [x] 5.5 Implement external sort of manifest file by path
- [x] 5.6 Implement bottom-up tree builder: stream sorted manifest, build one directory at a time, upload tree blobs, cascade hashes up
- [x] 5.7 Implement tree blob dedup: check if tree hash already exists in filetrees/ before uploading
- [x] 5.8 Skip empty directories (no tree blob for dirs without files)
- [x] 5.9 Implement tree blob caching on disk at `~/.arius/cache/<repo-id>/filetrees/`
- [x] 5.10 Unit tests: JSON serialization roundtrip, tree hash determinism, empty dir skipping, metadata change produces new hash
- [x] 5.11 Integration tests: tree blob upload/download, dedup across runs, cache hit

## 6. Snapshot

- [x] 6.1 Define snapshot manifest format: root tree hash, timestamp, file count, total size, Arius version
- [x] 6.2 Implement snapshot creation: serialize, compress/encrypt, upload to snapshots/<timestamp>
- [x] 6.3 Implement snapshot listing: list snapshots/ prefix, parse timestamps, sort
- [x] 6.4 Implement snapshot resolution: latest or specific -v version
- [x] 6.5 Unit tests: manifest serialization roundtrip, timestamp format
- [x] 6.6 Integration tests: create snapshot, list snapshots, resolve latest

## 7. FilePair & Local Enumeration

- [x] 7.1 Define FilePair model: RelativePath, BinaryExists, PointerExists, PointerHash, FileSize, Created, Modified
- [x] 7.2 Implement recursive depth-first file enumeration with graceful error handling (skip inaccessible, log warning)
- [x] 7.3 Implement .pointer.arius detection: always treat suffix as pointer, validate hex hash content, warn on invalid
- [x] 7.4 Implement FilePair assembly: match binaries with pointers, detect orphan pointers (thin archive), detect standalone binaries
- [x] 7.5 Implement OS-neutral path normalization: backslash → forward slash, handle reserved characters
- [x] 7.6 Unit tests: FilePair assembly (all combinations: binary+pointer, binary-only, pointer-only, stale pointer, invalid pointer content)
- [x] 7.7 Unit tests: path normalization edge cases (Windows paths, unicode filenames, deeply nested)

## 8. Archive Pipeline

- [x] 8.1 Define Mediator ArchiveCommand and ArchiveResult
- [x] 8.2 Implement pipeline orchestration: Channel<T> between stages, bounded channels with backpressure
- [x] 8.3 Implement enumerate stage: produce Channel<FilePair> from depth-first enumeration
- [x] 8.4 Implement hash stage (×N workers): stream file through IEncryptionService.ComputeHash, produce Channel<HashedFilePair>
- [x] 8.5 Implement dedup stage (×1): batched index lookup, in-flight set, route results to manifest writer + upload channel or skip
- [x] 8.6 Implement size router: split Channel into large file channel and small file channel based on threshold
- [x] 8.7 Implement large file upload: stream read → gzip → encrypt → upload with blob metadata, emit IndexEntry
- [x] 8.8 Implement tar builder (×1): accumulate small files to temp file, name entries by content-hash, seal at target size, hand off to Channel<SealedTar>
- [x] 8.9 Implement tar upload: gzip → encrypt → upload tar blob, then create thin chunks for each entry, emit IndexEntries
- [x] 8.10 Implement index updater: collect all IndexEntries, merge into shards, upload once at end
- [x] 8.11 Implement tree building: external sort manifest → bottom-up tree construction → snapshot creation
- [x] 8.12 Implement pointer writer (×N parallel): create/update .pointer.arius files, skip if --no-pointers
- [x] 8.13 Implement --remove-local: delete binaries after successful snapshot, reject if combined with --no-pointers
- [x] 8.14 Implement progress event emission: FileScanned, FileHashing, FileHashed, ChunkUploading, ChunkUploaded, TarBundleSealing, TarBundleUploaded, SnapshotCreated

## 9. Archive Crash Recovery

- [x] 9.1 Implement HEAD check before upload: 200+arius-complete → skip and recover index entry, 200 without → overwrite, 404 → upload
- [x] 9.2 Implement thin chunk recovery: read tar-hash from thin chunk body on HEAD hit
- [x] 9.3 Implement large chunk recovery: read original-size and chunk-size from blob metadata on HEAD hit
- [x] 9.4 Integration test: fault-injection blob service that throws after N uploads, verify re-run produces correct snapshot
- [x] 9.5 Integration test: crash after tar upload but before thin chunks, verify re-run handles partial thin chunks

## 10. Restore Pipeline

- [x] 10.1 Define Mediator RestoreCommand and RestoreResult (supports: single file, multiple files, directory, full snapshot)
- [x] 10.2 Implement snapshot resolution and tree traversal to target path(s)
- [x] 10.3 Implement conflict check: hash local files, skip matches, prompt on mismatch (y/N/all), support --overwrite
- [x] 10.4 Implement chunk resolution: index lookup for each content hash, group by chunk hash
- [x] 10.5 Implement rehydration status check: check chunks-rehydrated/ existence, check blob tier
- [x] 10.6 Implement cost estimation: calculate rehydration cost (Standard/High), download egress from chunk-size values
- [x] 10.7 Implement Phase 1 — download available chunks: streaming download → decrypt → gunzip → write to path (large) or stream tar extraction (tar bundles)
- [x] 10.8 Implement Phase 2 — rehydration kick-off: copy-blob to chunks-rehydrated/ with priority, retry with backoff on throttle
- [x] 10.9 Implement idempotent re-run: skip already-restored files, download newly rehydrated, re-request pending
- [x] 10.10 Implement cleanup prompt: delete chunks-rehydrated/ blobs after full restore
- [x] 10.11 Implement pointer file creation during restore (unless --no-pointers), set file dates from tree metadata
- [x] 10.12 Implement progress event emission for restore: download progress, files restored, rehydration status

## 11. Ls Command

- [x] 11.1 Define Mediator LsCommand and LsResult
- [x] 11.2 Implement tree traversal for ls: walk tree blobs, collect file entries
- [x] 11.3 Implement path prefix filter: only traverse matching subtree
- [x] 11.4 Implement filename substring filter (case-insensitive)
- [x] 11.5 Implement size lookup from chunk index original-size field
- [x] 11.6 Implement output formatting: path, size (humanized), created, modified
- [x] 11.7 Integration tests: ls with various filters, verify correct files returned

## 12. CLI

- [x] 12.1 Set up System.CommandLine with three root verbs: archive, restore, ls
- [x] 12.2 Implement common options: --account, --key, --passphrase, --container
- [x] 12.3 Implement archive-specific options: --tier, --remove-local, --no-pointers, --small-file-threshold, --tar-target-size, --dedup-cache-mb
- [x] 12.4 Implement restore-specific options: -v, --no-pointers, --overwrite
- [x] 12.5 Implement ls-specific options: -v, --prefix, --filter
- [x] 12.6 Implement FluentValidation for command options (e.g., reject --remove-local + --no-pointers)
- [x] 12.7 Implement account key resolution: CLI parameter → UserSecrets fallback
- [x] 12.8 Implement Spectre.Console archive progress display: aggregate progress + in-flight files with % + tar bundle status
- [x] 12.9 Implement Spectre.Console restore display: cost estimation table, priority prompt, download progress, remaining summary
- [x] 12.10 Implement Spectre.Console ls output: table with path, size, dates
- [x] 12.11 Wire up DI: register Core services, encryption service (based on passphrase), blob storage

## 13. Integration Tests — Roundtrip Scenarios

- [x] 13.1 Archive single large file → restore → verify byte-identical
- [x] 13.2 Archive single small file (tar-bundled) → restore → verify byte-identical
- [x] 13.3 Archive mix of large and small files → restore full snapshot → verify all files
- [x] 13.4 Archive with encryption → restore → verify byte-identical
- [x] 13.5 Archive nested directory structure → restore → verify tree structure and all files
- [x] 13.6 Incremental archive: add/modify/delete files between runs → restore each snapshot version → verify correct content per version
- [x] 13.7 Deduplication: archive two identical files in different directories → verify single chunk uploaded, both files restored
- [x] 13.8 Thin archive: archive with --remove-local → archive again (pointer-only) → restore → verify

## 14. Integration Tests — Edge Cases

- [x] 14.1 Stale pointer (binary hash ≠ pointer hash): verify pointer overwritten, correct hash archived
- [x] 14.2 Pointer-only file with missing chunk in index: verify warning logged, file excluded from snapshot
- [x] 14.3 File renamed between runs: verify old path absent in new snapshot, new path present, same chunk deduplicated
- [x] 14.4 File deleted between runs: verify absent from new snapshot, present in previous snapshot
- [x] 14.5 Special characters in filenames: spaces, unicode, brackets, dots, very long names — archive and restore roundtrip
- [x] 14.6 Empty file (0 bytes) roundtrip
- [x] 14.7 File exactly at small-file-threshold boundary → routed to large pipeline
- [x] 14.8 Binary file named `something.pointer.arius` (naming collision): treated as pointer, warn if invalid hex content
- [x] 14.9 --no-pointers: verify no pointer files created
- [x] 14.10 --remove-local + --no-pointers: verify rejected

## 15. Integration Tests — Crash Recovery & OpenSSL Compat

- [x] 15.1 Crash recovery: fault injection after large file uploads → re-run → verify no duplicate uploads, correct snapshot
- [x] 15.2 Crash recovery: fault injection after tar upload but before all thin chunks → re-run → verify correct snapshot
- [x] 15.3 Crash recovery: fault injection after all uploads but before index → re-run → verify index correct
- [x] 15.4 OpenSSL compatibility: archive encrypted large file → download raw blob → decrypt with openssl CLI → gunzip → verify byte-identical
- [x] 15.5 OpenSSL compatibility: archive encrypted tar bundle → download raw blob → decrypt → gunzip → tar extract → verify files by content-hash name

## 16. E2E Tests (Real Azure, Gated)

- [ ] 16.1 Set up real Azure test configuration: env vars for account name/key, unique container per run, auto-cleanup
- [ ] 16.2 Archive to Hot tier → restore → verify content
- [ ] 16.3 Archive to Cool tier → restore → verify content
- [ ] 16.4 Archive to Archive tier → verify blob tier is set (no rehydration test — too slow/costly)
- [ ] 16.5 Large file (100 MB+) upload/download streaming works end-to-end
