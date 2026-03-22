## Why

Arius needs a complete rewrite (v7) to build a production-grade archival system for Azure Blob Archive tier. The previous version has chunks already in archive storage that must remain compatible, but the metadata layer (snapshots, tree structure, indexing) needs a new design to handle 500M+ files with bounded memory, proper crash recovery, and a clean architecture that separates blob storage concerns from core logic.

This change establishes the entire foundation: the three core use cases (archive, restore, ls), the concurrent pipeline architecture, the blob storage abstraction, the encryption layer, the CLI, and the testing infrastructure.

## What Changes

- New solution structure: Arius.Core (Mediator commands), Arius.Cli (System.CommandLine), Arius.AzureBlob (blob storage implementation)
- Content-addressable archive pipeline: enumerate → hash → dedup → route (large/small) → compress → encrypt → upload → index → build merkle tree → snapshot
- Restore pipeline: resolve snapshot tree → chunk resolution → cost estimation + user confirmation → download available chunks (streaming, no cache) → start rehydration for archive-tier → exit (idempotent re-run for remaining)
- Ls command: tree traversal with path/name filtering
- Three chunk types: `large` (single file), `tar` (bundle), `thin` (pointer to tar for content-addressable lookup)
- Chunk index with 65K shards, tiered L1(memory LRU)/L2(disk)/L3(Azure) cache
- Git-style merkle tree for directory structure (content-addressed tree blobs)
- AES-256-CBC encryption, openssl-compatible, backwards compatible with previous Arius chunks
- Crash-recoverable archive via blob metadata receipts (`arius-complete` flag) and idempotent re-run
- Channel-based concurrent pipelines with bounded memory (~30 MB constant regardless of file count)
- Streaming throughout — files never loaded fully into memory
- Test infrastructure: TUnit + Azurite (TestContainers) + real Azure option + golden file encryption compat tests

## Capabilities

### New Capabilities
- `archive-pipeline`: File enumeration, FilePair resolution, streaming hash, dedup check against chunk index, size-based routing, large file upload, tar builder (single, directory-affinity, seal at 64 MB), thin chunk creation, index shard merge and upload, merkle tree construction (external sort + bottom-up build), snapshot creation, pointer file writing. Crash recovery via blob metadata receipts and idempotent re-run.
- `restore-pipeline`: Snapshot resolution, tree traversal to target path, conflict check against local files, chunk index lookup, rehydration status check, cost estimation with user confirmation, streaming download+decrypt+gunzip+extract (no local cache, stream-and-discard), rehydration kick-off for archive-tier chunks, idempotent re-run for remaining files, cleanup of rehydrated blobs.
- `ls-command`: Snapshot listing, tree blob traversal, path prefix filtering, filename substring filtering, metadata display (size, dates).
- `encryption`: AES-256-CBC with openssl-compatible format (Salted__ prefix, PBKDF2 SHA-256 10K iterations), passphrase-seeded hashing (SHA256(passphrase + data)), pluggable stream wrapper (encrypt/passthrough), backwards compatibility with previous Arius encrypted chunks.
- `blob-storage`: Azure Blob abstraction (IBlobStorageService), chunk types (large/thin/tar) with blob metadata (arius-type, arius-complete, sizes), container layout (chunks/, chunks-rehydrated/, filetrees/, snapshots/, chunk-index/), tiered chunk index cache (L1 memory LRU → L2 disk → L3 Azure), tree blob caching (immutable, indefinite), per-repo cache directory.
- `cli`: System.CommandLine verbs (archive, restore, ls), common options (account, key, passphrase, container), archive progress display with in-flight file visibility, restore cost confirmation UX, streaming progress events from Core via Mediator.

### Modified Capabilities
<!-- No existing specs to modify — this is a greenfield project -->

## Impact

- New .NET solution with three projects: Arius.Core, Arius.Cli, Arius.AzureBlob
- New test projects: Arius.Core.Tests, Arius.Integration.Tests, Arius.E2E.Tests, Arius.Architecture.Tests
- Dependencies: Mediator, FluentValidation, FluentResults, Azure.Storage.Blobs, System.CommandLine, Humanizer, TUnit, NSubstitute, ArchUnitNET
- Docker support for deployment on Synology NAS
- Must maintain backwards compatibility with existing encrypted chunks in Azure Archive tier from previous Arius version
