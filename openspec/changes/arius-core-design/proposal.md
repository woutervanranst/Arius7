## Why

Arius needs a complete redesign of its core architecture to support content-addressable archival to Azure Blob Archive tier at extreme scale (500M+ files, 1 TB+). The previous design used a monolithic SQLite database for state, which is prohibitive at scale — downloading and parsing a multi-GB index on every CLI invocation doesn't work. The system needs a new state storage design, a concurrent pipeline architecture, and a clean separation between core logic, CLI, and storage backend to enable future backends (S3, local filesystem) and a web UI.

## What Changes

- **New CLI application** (`Arius.Cli`) using `System.CommandLine` with three commands: `archive`, `restore`, `ls`
- **New core library** (`Arius.Core`) using Mediator pattern for command dispatch, with streaming progress updates to the CLI
- **New storage abstraction** (`Arius.AzureBlob`) isolating all Azure Blob Storage operations behind interfaces Core depends on
- **New state model**: Git-style encrypted merkle tree for snapshots, replacing the monolithic SQLite index
  - Individual encrypted tree blobs (one per directory) stored in Cool tier, content-addressed by passphrase-seeded SHA-256
  - Flat snapshot manifests (root tree hash + metadata) with UTC sub-second naming
  - Chunk index sharded by 2-byte content-hash prefix (65,536 shards) for content-hash → tar-chunk-hash lookups
- **Content-addressable chunk storage**: large files as solo encrypted chunks, small files (<1 MB configurable) bundled into tar archives (target 64 MB configurable), all gzip-compressed and AES-256-CBC encrypted (openssl-compatible)
- **Pointer files** as local-only cache for archive dedup (rebuildable, content hash only)
- **Concurrent pipeline architecture** using Channels for archive (enumerate → hash → decide → upload → finalize) and restore (resolve → rehydrate → download → decrypt → write)
- **Two-phase restore**: immediately process already-rehydrated chunks while polling for remaining rehydrations
- **Extensible tree node metadata**: file size, created date, modified date, with versioned format for future fields
- **Client-side encryption** throughout: chunks, tree blobs, snapshot manifests, chunk index shards — nothing in blob storage is plaintext
- **All blob names are opaque hashes** seeded with the passphrase — no file names, paths, or directory structure leak to storage
- **Cross-platform path handling** (normalize `/` vs `\`, reserved characters)
- **Graceful filesystem enumeration** (skip unreadable files/folders with warnings)
- **`--remove-local` option** for archive: delete local binaries after successful archive, keeping only pointer files
- **Docker support** for running on Synology NAS

## Capabilities

### New Capabilities
- `archive`: Archive a local directory tree to Azure Blob Storage with file-level deduplication, concurrent hashing/uploading, tar bundling of small files, client-side encryption, pointer file management, and snapshot creation
- `restore`: Restore files from Azure Blob Storage with rehydration management, two-phase processing, local chunk caching, concurrent downloading/decryption, and tar extraction
- `ls`: List files in a snapshot with path-based browsing via merkle tree traversal, metadata display (size, dates), prefix/substring filtering, and version selection
- `storage-abstraction`: Storage backend interface abstracting blob operations (upload, download, HEAD, list, set tier, rehydrate) away from Core
- `encryption`: AES-256-CBC encryption/decryption compatible with openssl CLI, using PBKDF2 with SHA-256 and 10K iterations, `Salted__` prefix
- `snapshot-state`: Encrypted merkle tree snapshot model with content-addressed tree blobs, snapshot manifests, and chunk index for content-hash → chunk-hash resolution
- `concurrency`: Channel-based pipeline architecture with bounded parallelism, dedup guards for concurrent uploads/rehydrations, and backpressure

### Modified Capabilities
_(none — greenfield)_

## Impact

- **New projects**: `Arius.Cli`, `Arius.Core`, `Arius.AzureBlob`, plus test projects
- **Dependencies**: `System.CommandLine`, `Mediator`, `Azure.Storage.Blobs`, `SharpCompress` (or similar for tar), testing with Azure Test Containers
- **Azure Blob Storage**: container layout with `chunks/`, `trees/`, `snapshots/`, `chunk-index/`, `chunks-rehydrated/` prefixes
- **Backwards compatibility**: chunks must remain compatible with existing archive-tier blobs (openssl/tar/gzip format). Snapshot format is new (migration out of scope for this change)
