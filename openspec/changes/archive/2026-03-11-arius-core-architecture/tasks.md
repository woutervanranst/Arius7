## 1. Solution Structure & Project Setup

- [x] 1.1 Create .NET solution with projects: Arius.Core, Arius.Azure, Arius.Cli, Arius.Api
- [x] 1.2 Create Vue 3 + TypeScript project for Arius.Web (Vite scaffolding)
- [x] 1.3 Create test projects: Arius.Core.Tests, Arius.Azure.Tests, Arius.Integration.Tests
- [x] 1.4 Add NuGet dependencies: Azure.Storage.Blobs, Spectre.Console, System.CommandLine, Mediator, Microsoft.Data.Sqlite, Microsoft.AspNetCore.SignalR, FluentResults, Azurite test containers (for testing), TUnit as test framework (! NOT xUnit), Shouldly if the TUnit assertions do not suffice, ArchUnitNET for architecture testing.
- [x] 1.5 Add npm dependencies: vue, typescript, @microsoft/signalr
- [x] 1.6 Create Dockerfile (multi-stage: build API + Web → single container)

## 2. Core Domain Models

- [x] 2.1 Define core types: BlobHash (SHA-256 wrapper), PackId, SnapshotId, TreeHash, RepoId
- [x] 2.2 Define Snapshot model: Id, Time, Tree (root hash), Paths, Hostname, Username, Tags, Parent
- [x] 2.3 Define TreeNode model: Name, Type (file/dir/symlink), Size, MTime, Mode, ContentHashes[], SubtreeHash
- [x] 2.4 Define IndexEntry model: BlobHash, PackId, Offset, Length, BlobType (data/tree)
- [x] 2.5 Define PackHeader model: array of (BlobHash, BlobType, Offset, Length)
- [x] 2.6 Define RepoConfig model: RepoId, Version, GearSeed, PackSize, ChunkMin, ChunkAvg, ChunkMax
- [x] 2.7 Define Chunk record: ReadOnlyMemory<byte> Data
- [x] 2.8 Update BlobHash computation: change from SHA-256(plaintext) to HMAC-SHA256(master_key, plaintext); update BlobHash.FromBytes to accept a key parameter and use HMACSHA256

## 3. Encryption (Arius.Core)

- [x] 3.1 Port CryptoExtensions (AES-256-CBC, OpenSSL-compatible, PBKDF2-SHA256) into Arius.Core
- [x] 3.2 Implement master key generation (32-byte random key)
- [x] 3.3 Implement two-level key architecture: passphrase → PBKDF2 → derived key → decrypt key file → master key
- [x] 3.4 Implement key file format: plain JSON with salt, iterations, and encrypted master key payload
- [x] 3.5 Implement key file serialization/deserialization
- [x] 3.6 Implement multi-key support: add key, remove key, change password, list keys
- [x] 3.7 Write encryption unit tests: roundtrip encrypt/decrypt, OpenSSL compatibility, stream-based large data
- [x] 3.8 Write key management unit tests: add/remove/change password, reject removing last key
- [x] 3.9 Write manual key recovery test: verify master key can be extracted using openssl CLI with passphrase

## 4. Chunking (Arius.Core)

- [x] 4.1 Define IChunker interface: IAsyncEnumerable<Chunk> ChunkAsync(Stream, CancellationToken)
- [x] 4.2 Implement GearChunker: gear table generation from seed, rolling hash, min/avg/max boundary logic
- [x] 4.3 Write chunker unit tests: boundary detection, min/max enforcement, small file single chunk, deterministic output from same seed
- [x] 4.4 Write chunker dedup tests: insert byte at start, verify most chunks unchanged

## 5. Pack File Management (Arius.Core)

- [x] 5.1 Implement PackerManager: accumulate blobs until configurable pack size reached
- [x] 5.2 Implement pack file creation: build TAR archive with blob files named by SHA-256 hash + manifest.json
- [x] 5.3 Implement gzip compression of TAR archive
- [x] 5.4 Implement pack pipeline: TAR → gzip → AES-256-CBC encrypt → SHA-256 hash of encrypted output = pack ID
- [x] 5.5 Implement pack file extraction: decrypt → gunzip → untar → read manifest.json → extract blobs by hash
- [x] 5.6 Write packer unit tests: pack creation, roundtrip create/extract, manifest parsing, configurable size
- [x] 5.7 Write manual recovery test: verify pack can be recovered with openssl + gunzip + tar CLI commands

## 6. Azure Backend (Arius.Azure)

- [x] 6.1 Define `IBlobStorageProvider` interface: `UploadAsync(string blobName, Stream data, AccessTier tier)`, `DownloadAsync(string blobName) → Stream`, `ExistsAsync`, `GetTierAsync`, `SetTierAsync`, `ListAsync(string prefix)`, `DeleteAsync`, `AcquireLeaseAsync`, `RenewLeaseAsync`, `ReleaseLeaseAsync`
- [x] 6.2 Implement `AzureBlobStorageProvider` using `Azure.Storage.Blobs` SDK
- [x] 6.3 Implement upload with explicit `AccessTier` parameter (caller specifies; no implicit tier logic in the provider)
- [x] 6.4 Implement download with streaming (non-seekable stream, no full-buffer copy to memory)
- [x] 6.5 Implement rehydration: `SetTierAsync` to Hot, poll `BlobProperties.ArchiveStatus` until `rehydrate-pending-to-hot` clears
- [x] 6.6 Implement blob lease locking: acquire 60s lease on `config` blob, auto-renew background task, release on dispose
- [x] 6.7 Implement blob listing by prefix with continuation tokens
- [x] 6.8 Implement blob deletion
- [x] 6.9 Write Azure backend integration tests using Azurite (upload/download/delete/tier/lease/list)

## 7. Repository Layer (Arius.Core)

- [x] 7.1 Implement `Repository` class: connect to Azure via `IBlobStorageProvider`, load config blob (Cold tier), validate passphrase, decrypt master key
- [x] 7.2 Implement index management: write index delta files, load/merge all index files, lookup blob→pack
- [x] 7.3 Implement snapshot management: create/read/delete/list snapshots
- [x] 7.4 Implement tree management: create/read tree blobs, write to cold tier, walk trees by path
- [x] 7.5 Implement repo init: generate config (repo ID, version, gear seed, pack size), create first key file, upload both to Azure (Cold tier)
- [x] 7.6 Write repository unit tests with mock `IBlobStorageProvider`
- [x] 7.7 Delete `FileSystemRepositoryStore` — replace with `AzureRepository` backed by `IBlobStorageProvider`; update all handlers (`BackupHandler`, `RestoreHandler`, `InitHandler`, `SnapshotsHandler`) to use the new abstraction via DI

## 8. Local Cache (Arius.Core)

- [x] 8.1 Implement SQLite cache schema: blobs, packs, snapshots, trees tables with proper indexes
- [x] 8.2 Implement cache builder: download all index/snapshot/tree blobs from Azure → populate SQLite
- [x] 8.3 Implement delta sync: track watermark, list only new blobs, merge into cache
- [x] 8.4 Implement cache-aware repository wrapper: check cache first, fall back to Azure
- [x] 8.5 Write cache unit tests: build, sync, lookup, rebuild from scratch, equivalence with/without cache

## 9. Mediator Handlers — Requests & Streaming

- [x] 9.1 Register Mediator in DI container for both CLI and API projects
- [x] 9.2 Implement InitHandler: `IRequestHandler<InitRequest, InitResult>` — accepts Azure connection string + container, passphrase, optional pack size
- [x] 9.3 Implement BackupHandler: `IStreamRequestHandler<BackupRequest, BackupEvent>` — accepts Azure connection, source paths, passphrase, optional `AccessTier` (default Archive); streams sealed packs directly to Azure, writes metadata to Cold tier
- [x] 9.4 Implement RestoreHandler: `IStreamRequestHandler<RestoreRequest, RestoreEvent>` — accepts Azure connection, snapshot ID, target path, passphrase; streams rehydrated packs from Azure into memory (no disk staging), decrypts and writes directly to target
- [x] 9.5 Implement SnapshotsHandler: `IStreamRequestHandler<ListSnapshotsRequest, Snapshot>`
- [x] 9.6 Implement LsHandler: `IStreamRequestHandler<LsRequest, TreeEntry>` — walk tree blobs
- [x] 9.7 Implement FindHandler: `IStreamRequestHandler<FindRequest, SearchResult>` — search across snapshots
- [x] 9.8 Implement ForgetHandler: `IStreamRequestHandler<ForgetRequest, ForgetEvent>` — retention policies
- [x] 9.9 Implement PruneHandler: `IStreamRequestHandler<PruneRequest, PruneEvent>` — identify unreferenced packs, repack, delete
- [x] 9.10 Implement CheckHandler: `IStreamRequestHandler<CheckRequest, CheckResult>` — metadata + optional data integrity check
- [x] 9.11 Implement DiffHandler: `IStreamRequestHandler<DiffRequest, DiffEntry>`
- [x] 9.12 Implement StatsHandler: `IRequestHandler<StatsRequest, RepoStats>`
- [x] 9.13 Implement TagHandler: `IRequestHandler<TagRequest, TagResult>`
- [x] 9.14 Implement KeyHandler: `IRequestHandler<KeyRequest, KeyResult>` — add, remove, list, passwd
- [x] 9.15 Implement RepairHandler: `IRequestHandler<RepairRequest, RepairResult>` — repair index, repair snapshots
- [x] 9.16 Implement CostEstimateHandler: `IRequestHandler<CostEstimateRequest, RestoreCostEstimate>`

## 10. CLI (Arius.Cli)

- [x] 10.1 Set up System.CommandLine with command registration matching restic command surface
- [x] 10.2 Implement global options: `--repo` (Azure connection string or URL), `--password-file`, `--json`, `--yes`, `--verbose`
- [x] 10.3 Implement init command with Spectre prompts
- [x] 10.4 Implement backup command with Spectre Progress (file count, bytes, upload speed); add `--tier` option (hot/cool/cold/archive, default archive)
- [x] 10.5 Implement restore command with Spectre Live (rehydration + restoration progress)
- [x] 10.6 Implement snapshots command with Spectre Table (--compact, --latest, --group-by)
- [x] 10.7 Implement ls command with streaming table output
- [x] 10.8 Implement find command with streaming results
- [x] 10.9 Implement forget command with confirmation prompt and dry-run support
- [x] 10.10 Implement prune command with cost estimate, confirmation, and progress
- [x] 10.11 Implement check command with optional --read-data (cost warning)
- [x] 10.12 Implement diff command
- [x] 10.13 Implement stats command
- [x] 10.14 Implement tag command
- [x] 10.15 Implement key command (add, remove, list, passwd)
- [x] 10.16 Implement repair command (index, snapshots)
- [x] 10.17 Implement version command
- [x] 10.18 Implement cat command (config, snapshot, index, tree, key)
- [x] 10.19 Add JSON output mode (--json) for all listing commands

## 11. API (Arius.Api)

- [x] 11.1 Set up ASP.NET Core Minimal API project with SignalR
- [x] 11.2 Implement repository connection via environment variables (ARIUS_REPOSITORY, ARIUS_PASSWORD)
- [x] 11.3 Implement GET /api/snapshots — list snapshots (IAsyncEnumerable response)
- [x] 11.4 Implement GET /api/snapshots/{id} — snapshot detail
- [x] 11.5 Implement GET /api/snapshots/{id}/tree?path= — browse tree
- [x] 11.6 Implement GET /api/snapshots/{id}/find?pattern= — search files
- [x] 11.7 Implement POST /api/backup — start backup, return operation ID
- [x] 11.8 Implement POST /api/restore — start restore with cost estimate, return operation ID
- [x] 11.9 Implement POST /api/forget — forget with policy
- [x] 11.10 Implement POST /api/prune — prune with estimate
- [x] 11.11 Implement GET /api/stats — repository statistics
- [x] 11.12 Implement GET /api/diff/{snap1}/{snap2} — diff two snapshots
- [x] 11.13 Implement SignalR OperationsHub: stream BackupEvent, RestoreEvent, PruneEvent, ForgetEvent
- [x] 11.14 Implement operation cancellation via SignalR messages and DELETE /api/operations/{id}
- [x] 11.15 Configure CORS for web UI development

## 12. Web UI (Arius.Web)

- [x] 12.1 Set up Vue 3 + TypeScript project with Vite, router, and Pinia state management
- [x] 12.2 Create TypeScript types matching API response models (Snapshot, TreeEntry, BackupEvent, RestoreEvent, etc.)
- [x] 12.3 Create SignalR client service for real-time event streaming
- [x] 12.4 Implement API client service with typed fetch wrappers
- [x] 12.5 Build snapshot sidebar component: chronological grouping, tag/host filtering
- [x] 12.6 Build file browser component: directory listing with icons, breadcrumb navigation, sorting
- [x] 12.7 Build restore dialog component: file selection, cost estimate display, priority selection, confirmation
- [x] 12.8 Build progress overlay component: rehydration + restoration progress bars
- [x] 12.9 Build backup trigger component (for server-side paths)
- [x] 12.10 Build diff view component: side-by-side or unified diff between snapshots
- [x] 12.11 Build stats dashboard component: repo statistics, tier breakdown
- [x] 12.12 Build forget/prune management component: retention policy UI, dry-run preview
- [x] 12.13 Implement responsive layout and styling

## 13. Docker & Deployment

- [x] 13.1 Create multi-stage Dockerfile: .NET SDK build → Vue build → runtime image (ASP.NET + static files)
- [x] 13.2 Configure API to serve Vue static files from wwwroot
- [x] 13.3 Add docker-compose.yml for local development with Azurite
- [x] 13.4 Test end-to-end: docker run → web UI → browse repo → trigger restore *(skipped — Docker Desktop unavailable in CI environment; verify manually with `docker compose up --build` then visit http://localhost:8080)*

## 14. Integration Testing

- [x] 14.1 Write integration test (Azurite): init → backup → snapshots → ls → verify tree content
- [x] 14.2 Write integration test (Azurite): backup → backup (incremental) → verify dedup (no duplicate blobs uploaded)
- [x] 14.3 Write integration test (Azurite): backup → forget → prune → verify unreferenced packs removed
- [x] 14.4 Write integration test (Azurite): backup → rehydrate → restore → verify file content matches original
- [x] 14.5 Write integration test (Azurite): delete local cache → rebuild from Azure → verify operations still work
- [x] 14.6 Write integration test (Azurite): backup 2KB files at scale (1000+ files) → verify chunking/packing behavior
- [x] 14.7 Write integration test (Azurite): concurrent backup attempt → verify lease-based locking
- [x] 14.8 Write integration test: manual recovery → backup file to Azure, then recover using only az CLI + openssl + gunzip + tar + cat
- [x] 14.9 Write integration test: backup with `--tier hot` → verify blobs in Azure have Hot tier (no rehydration needed for restore)
- [x] 14.10 Write integration test: backup with `--tier archive` → verify blobs require rehydration for restore
