## 1. Solution Structure & Project Setup

- [x] 1.1 Create .NET solution with projects: Arius.Core, Arius.Azure, Arius.Cli, Arius.Api
- [ ] 1.2 Create Vue 3 + TypeScript project for Arius.Web (Vite scaffolding)
- [x] 1.3 Create test projects: Arius.Core.Tests, Arius.Azure.Tests, Arius.Integration.Tests
- [x] 1.4 Add NuGet dependencies: Azure.Storage.Blobs, Spectre.Console, Spectre.Console.Cli, Mediator, Microsoft.Data.Sqlite, Microsoft.AspNetCore.SignalR, FluentResults, Azurite test containers (for testing), TUnit as test framework (! NOT xUnit), Shouldly if the TUnit assertions do not suffice, ArchUnitNET for architecture testing.
- [ ] 1.5 Add npm dependencies: vue, typescript, @microsoft/signalr
- [ ] 1.6 Create Dockerfile (multi-stage: build API + Web → single container)

## 2. Core Domain Models

- [x] 2.1 Define core types: BlobHash (SHA-256 wrapper), PackId, SnapshotId, TreeHash, RepoId
- [x] 2.2 Define Snapshot model: Id, Time, Tree (root hash), Paths, Hostname, Username, Tags, Parent
- [x] 2.3 Define TreeNode model: Name, Type (file/dir/symlink), Size, MTime, Mode, ContentHashes[], SubtreeHash
- [x] 2.4 Define IndexEntry model: BlobHash, PackId, Offset, Length, BlobType (data/tree)
- [x] 2.5 Define PackHeader model: array of (BlobHash, BlobType, Offset, Length)
- [ ] 2.6 Define RepoConfig model: RepoId, Version, GearSeed, PackSize, ChunkMin, ChunkAvg, ChunkMax
- [x] 2.7 Define Chunk record: ReadOnlyMemory<byte> Data

## 3. Encryption (Arius.Core)

- [ ] 3.1 Port CryptoExtensions (AES-256-CBC, OpenSSL-compatible, PBKDF2-SHA256) into Arius.Core
- [ ] 3.2 Implement master key generation (32-byte random key)
- [ ] 3.3 Implement two-level key architecture: passphrase → PBKDF2 → derived key → decrypt key file → master key
- [ ] 3.4 Implement key file format: plain JSON with salt, iterations, and encrypted master key payload
- [ ] 3.5 Implement key file serialization/deserialization
- [ ] 3.6 Implement multi-key support: add key, remove key, change password, list keys
- [ ] 3.7 Write encryption unit tests: roundtrip encrypt/decrypt, OpenSSL compatibility, stream-based large data
- [ ] 3.8 Write key management unit tests: add/remove/change password, reject removing last key
- [ ] 3.9 Write manual key recovery test: verify master key can be extracted using openssl CLI with passphrase

## 4. Chunking (Arius.Core)

- [ ] 4.1 Define IChunker interface: IAsyncEnumerable<Chunk> ChunkAsync(Stream, CancellationToken)
- [ ] 4.2 Implement GearChunker: gear table generation from seed, rolling hash, min/avg/max boundary logic
- [ ] 4.3 Write chunker unit tests: boundary detection, min/max enforcement, small file single chunk, deterministic output from same seed
- [ ] 4.4 Write chunker dedup tests: insert byte at start, verify most chunks unchanged

## 5. Pack File Management (Arius.Core)

- [ ] 5.1 Implement PackerManager: accumulate blobs until configurable pack size reached
- [ ] 5.2 Implement pack file creation: build TAR archive with blob files named by SHA-256 hash + manifest.json
- [ ] 5.3 Implement gzip compression of TAR archive
- [ ] 5.4 Implement pack pipeline: TAR → gzip → AES-256-CBC encrypt → SHA-256 hash of encrypted output = pack ID
- [ ] 5.5 Implement pack file extraction: decrypt → gunzip → untar → read manifest.json → extract blobs by hash
- [ ] 5.6 Write packer unit tests: pack creation, roundtrip create/extract, manifest parsing, configurable size
- [ ] 5.7 Write manual recovery test: verify pack can be recovered with openssl + gunzip + tar CLI commands

## 6. Azure Backend (Arius.Azure)

- [ ] 6.1 Define IBlobStorageProvider interface: Upload, Download, Rehydrate, GetRehydrationStatus, SetTier, List, Delete, AcquireLease, ReleaseLease
- [ ] 6.2 Implement AzureBlobStorageProvider using Azure.Storage.Blobs SDK
- [ ] 6.3 Implement upload with tier assignment (Cold for metadata paths, Archive for data/ paths)
- [ ] 6.4 Implement download with streaming (non-seekable stream support)
- [ ] 6.5 Implement rehydration: initiate SetBlobAccessTier to Hot, poll BlobProperties.ArchiveStatus
- [ ] 6.6 Implement blob lease locking: acquire 60s lease on config blob, auto-renew background task, release on dispose
- [ ] 6.7 Implement blob listing by prefix with continuation tokens
- [ ] 6.8 Implement blob deletion
- [ ] 6.9 Write Azure backend integration tests (with Azurite or live against test container)

## 7. Repository Layer (Arius.Core)

- [ ] 7.1 Implement Repository class: connect, load config, validate passphrase, decrypt master key
- [ ] 7.2 Implement index management: write index delta files, load/merge all index files, lookup blob→pack
- [ ] 7.3 Implement snapshot management: create/read/delete/list snapshots
- [ ] 7.4 Implement tree management: create/read tree blobs, write to cold tier, walk trees by path
- [ ] 7.5 Implement repo init: generate config (repo ID, version, gear seed, pack size), create first key file
- [ ] 7.6 Write repository unit tests with mock IBlobStorageProvider

## 8. Local Cache (Arius.Core)

- [ ] 8.1 Implement SQLite cache schema: blobs, packs, snapshots, trees tables with proper indexes
- [ ] 8.2 Implement cache builder: download all index/snapshot/tree blobs from Azure → populate SQLite
- [ ] 8.3 Implement delta sync: track watermark, list only new blobs, merge into cache
- [ ] 8.4 Implement cache-aware repository wrapper: check cache first, fall back to Azure
- [ ] 8.5 Write cache unit tests: build, sync, lookup, rebuild from scratch, equivalence with/without cache

## 9. Mediator Handlers — Requests & Streaming

- [ ] 9.1 Register Mediator in DI container for both CLI and API projects
- [ ] 9.2 Implement InitHandler: IRequestHandler<InitRequest, InitResult>
- [ ] 9.3 Implement BackupHandler: IStreamRequestHandler<BackupRequest, BackupEvent> — scan, chunk, dedup, pack, upload, create snapshot
- [ ] 9.4 Implement RestoreHandler: IStreamRequestHandler<RestoreRequest, RestoreEvent> — plan, estimate cost, rehydrate, download, decrypt, reassemble
- [ ] 9.5 Implement SnapshotsHandler: IStreamRequestHandler<ListSnapshotsRequest, Snapshot>
- [ ] 9.6 Implement LsHandler: IStreamRequestHandler<LsRequest, TreeEntry> — walk tree blobs
- [ ] 9.7 Implement FindHandler: IStreamRequestHandler<FindRequest, SearchResult> — search across snapshots
- [ ] 9.8 Implement ForgetHandler: IStreamRequestHandler<ForgetRequest, ForgetEvent> — retention policies
- [ ] 9.9 Implement PruneHandler: IStreamRequestHandler<PruneRequest, PruneEvent> — identify unreferenced packs, repack, delete
- [ ] 9.10 Implement CheckHandler: IStreamRequestHandler<CheckRequest, CheckResult> — metadata + optional data integrity check
- [ ] 9.11 Implement DiffHandler: IStreamRequestHandler<DiffRequest, DiffEntry>
- [ ] 9.12 Implement StatsHandler: IRequestHandler<StatsRequest, RepoStats>
- [ ] 9.13 Implement TagHandler: IRequestHandler<TagRequest, TagResult>
- [ ] 9.14 Implement KeyHandler: IRequestHandler<KeyRequest, KeyResult> — add, remove, list, passwd
- [ ] 9.15 Implement RepairHandler: IRequestHandler<RepairRequest, RepairResult> — repair index, repair snapshots
- [ ] 9.16 Implement CostEstimateHandler: IRequestHandler<CostEstimateRequest, RestoreCostEstimate>

## 10. CLI (Arius.Cli)

- [ ] 10.1 Set up Spectre.Console.Cli with command registration matching restic command surface
- [ ] 10.2 Implement global options: --repo, --password-file, --json, --yes, --verbose
- [ ] 10.3 Implement init command with Spectre prompts
- [ ] 10.4 Implement backup command with Spectre Progress (file count, bytes, upload speed)
- [ ] 10.5 Implement restore command with Spectre Live (rehydration + restoration progress)
- [ ] 10.6 Implement snapshots command with Spectre Table (--compact, --latest, --group-by)
- [ ] 10.7 Implement ls command with streaming table output
- [ ] 10.8 Implement find command with streaming results
- [ ] 10.9 Implement forget command with confirmation prompt and dry-run support
- [ ] 10.10 Implement prune command with cost estimate, confirmation, and progress
- [ ] 10.11 Implement check command with optional --read-data (cost warning)
- [ ] 10.12 Implement diff command
- [ ] 10.13 Implement stats command
- [ ] 10.14 Implement tag command
- [ ] 10.15 Implement key command (add, remove, list, passwd)
- [ ] 10.16 Implement repair command (index, snapshots)
- [ ] 10.17 Implement version command
- [ ] 10.18 Implement cat command (config, snapshot, index, tree, key)
- [ ] 10.19 Add JSON output mode (--json) for all listing commands

## 11. API (Arius.Api)

- [ ] 11.1 Set up ASP.NET Core Minimal API project with SignalR
- [ ] 11.2 Implement repository connection via environment variables (ARIUS_REPOSITORY, ARIUS_PASSWORD)
- [ ] 11.3 Implement GET /api/snapshots — list snapshots (IAsyncEnumerable response)
- [ ] 11.4 Implement GET /api/snapshots/{id} — snapshot detail
- [ ] 11.5 Implement GET /api/snapshots/{id}/tree?path= — browse tree
- [ ] 11.6 Implement GET /api/snapshots/{id}/find?pattern= — search files
- [ ] 11.7 Implement POST /api/backup — start backup, return operation ID
- [ ] 11.8 Implement POST /api/restore — start restore with cost estimate, return operation ID
- [ ] 11.9 Implement POST /api/forget — forget with policy
- [ ] 11.10 Implement POST /api/prune — prune with estimate
- [ ] 11.11 Implement GET /api/stats — repository statistics
- [ ] 11.12 Implement GET /api/diff/{snap1}/{snap2} — diff two snapshots
- [ ] 11.13 Implement SignalR OperationsHub: stream BackupEvent, RestoreEvent, PruneEvent, ForgetEvent
- [ ] 11.14 Implement operation cancellation via SignalR messages and DELETE /api/operations/{id}
- [ ] 11.15 Configure CORS for web UI development

## 12. Web UI (Arius.Web)

- [ ] 12.1 Set up Vue 3 + TypeScript project with Vite, router, and Pinia state management
- [ ] 12.2 Create TypeScript types matching API response models (Snapshot, TreeEntry, BackupEvent, RestoreEvent, etc.)
- [ ] 12.3 Create SignalR client service for real-time event streaming
- [ ] 12.4 Implement API client service with typed fetch wrappers
- [ ] 12.5 Build snapshot sidebar component: chronological grouping, tag/host filtering
- [ ] 12.6 Build file browser component: directory listing with icons, breadcrumb navigation, sorting
- [ ] 12.7 Build restore dialog component: file selection, cost estimate display, priority selection, confirmation
- [ ] 12.8 Build progress overlay component: rehydration + restoration progress bars
- [ ] 12.9 Build backup trigger component (for server-side paths)
- [ ] 12.10 Build diff view component: side-by-side or unified diff between snapshots
- [ ] 12.11 Build stats dashboard component: repo statistics, tier breakdown
- [ ] 12.12 Build forget/prune management component: retention policy UI, dry-run preview
- [ ] 12.13 Implement responsive layout and styling

## 13. Docker & Deployment

- [ ] 13.1 Create multi-stage Dockerfile: .NET SDK build → Vue build → runtime image (ASP.NET + static files)
- [ ] 13.2 Configure API to serve Vue static files from wwwroot
- [ ] 13.3 Add docker-compose.yml for local development with Azurite
- [ ] 13.4 Test end-to-end: docker run → web UI → browse repo → trigger restore

## 14. Integration Testing

- [ ] 14.1 Write integration test: init → backup → snapshots → ls → verify tree content
- [ ] 14.2 Write integration test: backup → backup (incremental) → verify dedup (no duplicate blobs)
- [ ] 14.3 Write integration test: backup → forget → prune → verify unreferenced packs removed
- [ ] 14.4 Write integration test: backup → restore → verify file content matches original
- [ ] 14.5 Write integration test: delete local cache → rebuild from Azure → verify operations still work
- [ ] 14.6 Write integration test: backup 2KB files at scale (1000+ files) → verify chunking/packing behavior
- [ ] 14.7 Write integration test: concurrent backup attempt → verify lease-based locking
- [ ] 14.8 Write integration test: manual recovery → backup file, then recover using only az CLI + openssl + gunzip + tar + cat
