# Migrate v5 Arius repositories to v7

## Context

`arius7` is the successor to the old `woutervanranst/Arius` tool ("v5"). The user has ~5 real
repositories in the v5 format that must become readable/writable by arius7 (v7). The goal is a
**standalone migration project** that, given a v5 repo in Azure Blob Storage, produces a valid v7
repo **in place** — without re-downloading, re-encrypting, or re-hashing any chunk data — such that a
subsequent v7 `archive` (which dedups against the migrated chunks) and `restore` both succeed.

This is feasible because arius7 was already deliberately built for v5 backward-compatibility:
- **Salted hash matches.** v7 `ComputeHash = SHA256(UTF8(passphrase) ++ data)`
  (`PassphraseEncryptionService.cs:93`) reproduces v5's salted SHA-256 (salt = ASCII passphrase).
  So v5 blob names (`chunks/<hex>`) already equal v7 `ContentHash`, and a later v7 `archive`
  re-hashes the same files to the same hash → dedup hits, no re-upload.
- **Legacy read paths exist.** v7 auto-detects and reads legacy AES-256-CBC ("Salted__") encryption
  and gzip compression (decompress-only `GZipCompressionService`, `NoopVerifier`), which is exactly
  v5's `application/aes256cbc+gzip` format.
- **Tar entry naming matches.** v5 names tar entries by `hash.ToString()` (lowercase hex); v7's tar
  restore requires entry names to be a parseable `ContentHash` (`RestoreCommandHandler` tar path) —
  so v5 tar bundles are restorable by v7 unchanged.

The migration's job (per the user's framing): read the v5 SQLite state DB, **upsert** v7 metadata
onto the chunk blobs, build the chunk index with the **existing repair** functionality, then build
the v7 filetrees + snapshot from the SQLite data.

## Decisions (confirmed with user)

1. **Standalone `Arius.Migration` console project** (not a CLI verb). References `Arius.Core` +
   `Arius.AzureBlob`; uses the public `ServiceCollectionExtensions.AddArius(...)` to get all services
   via DI (same pattern as `Arius.Explorer/Infrastructure/RepositorySession.cs:41`).
2. **Upsert (merge) chunk metadata, do not replace.** Preserves v5's `SmallChunkCount` and keeps the
   chunks readable by v5 too — a non-destructive, free safety net (we already read existing metadata
   while enumerating). v5 read-compatibility holds for the migrated snapshot until a subsequent v7
   `archive` diverges the state (v7 writes GCM+zstd chunks v5 can't read and does not update the v5
   `states/` DB).
3. **Revert the CBC PBKDF2 iteration count to `10_000`** (it is currently a bug; see below).
4. **Migrate in place**, one container, left intact in `ariusci` for inspection (the upsert makes
   this non-destructive — the original v5 `states/` DB and chunk bodies are untouched).
5. **ASCII passphrase only** and **non-Archive tiers** for the test (documented limitations, below).

## Required fix in Arius.Core (prerequisite)

`src/Arius.Core/Shared/Encryption/PassphraseEncryptionService.cs:33`
```
private const int CbcPbkdf2Iter = 100_000; //SonarQube S5344: at least 100k iterations
```
Revert to `10_000` (with an S5344 suppression comment explaining it is required to read legacy v5
archives, not a new key derivation).

Evidence it's a bug:
- Git: the line was `10_000` until commit `c3d6ae6a "fix: sonarqube recommendation"` bumped it,
  touching only that line and **not** regenerating fixtures.
- It is used **only** to decrypt legacy CBC blobs and by a **test-only** encryptor
  (`WrapForCbcEncryption`, whose sole caller is `Arius.Integration.Tests/.../CbcEncryptionServiceAdapter.cs`).
  Production writes are GCM, with a **separate** constant `GcmPbkdf2Iter = 100_000` (pinned by
  `AesGcmEncryptionTests.cs:100`) — leave that untouched.
- Real v5 blobs use 10,000 (v5 source `const int iterations = 10_000`; OpenSSL default; this file's
  own docstring at line 19 says "10,000"; the CBC golden test documents 10k at `AesGcmEncryptionTests.cs:293`).
- At 100k, v7 cannot decrypt any v5 blob (chunks, tars, or the state DB), and the CBC golden test is
  effectively broken. Reverting fixes both.

Also add: `[assembly: InternalsVisibleTo("Arius.Migration")]` to `src/Arius.Core/AssemblyMarker.cs`
(mirrors existing test entries) so the project can use the internal `FileTreeStagingSession` /
`FileTreeStagingWriter` / `FileTreeBuilder`.

## v5 → v7 mapping (verified)

v5 SQLite state DB (`states/<name>` blob = encrypt(gzip(sqlite)), metadata `DatabaseVersion=5`,
"latest" = lexicographic-max):
- `PointerFileEntries(RelativeName TEXT PK, Hash BLOB → BinaryProperties.Hash, CreationTimeUtc, LastWriteTimeUtc)`
- `BinaryProperties(Hash BLOB PK, OriginalSize INT, ArchivedSize INT, ParentHash BLOB NULL, StorageTier INT)`

Chunk classification from `BinaryProperties` (build `parentHashes = {ParentHash where not null}`):
- **LARGE** — `ParentHash == null` AND `Hash ∉ parentHashes`. Blob `chunks/<hash>`.
- **TAR**   — `ParentHash == null` AND `Hash ∈ parentHashes`. Blob `chunks/<tarHash>`.
- **THIN**  — `ParentHash != null` (a small file inside a tar). **No blob exists in v5.**

v7 metadata to upsert (keys in `BlobMetadataKeys`, mirroring `ChunkStorageService` exactly):

| Type | v7 metadata added | Mechanism |
|---|---|---|
| LARGE `chunks/<hash>` | `arius_type=large`, `original_size=OriginalSize`, `chunk_size=blob.ContentLength` | merge → `SetMetadataAsync` |
| TAR `chunks/<tarHash>` | `arius_type=tar`, `chunk_size=blob.ContentLength` (no `original_size`, per `ChunkStorageService.cs:127`) | merge → `SetMetadataAsync` |
| THIN | new empty blob `chunks/<fileHash>` with `arius_type=thin`, `parent_chunk_hash=<tarHash>`, `original_size`, `chunk_size=<tar.ContentLength>` | `IChunkStorageService.UploadThinAsync(...)` (idempotent; do not hand-roll) |

`chunk_size` = the blob's `ContentLength` (encrypted stored size — matches v7's own convention).
Do **not** use v5 `ArchivedSize`. Content-Type and tier are left unchanged.

## The migration (single-threaded, readable, numbered stages)

Project layout:
```
src/Arius.Migration/
  Arius.Migration.csproj        // net10.0, OutputType=Exe; refs Arius.Core, Arius.AzureBlob, Microsoft.Data.Sqlite
  Program.cs                    // parse --account/-a, --key/-k, --passphrase/-p, --container/-c (mirror v7 CLI); build IServiceProvider via AddArius
  MigrateV5.cs                  // the orchestrator below, with mirrored "// ── Stage N ──" banners in its summary
```
The orchestrator builds the provider once (`new AzureBlobContainerService(...)` →
`services.AddArius(blobContainer, passphrase, account, container)`), then resolves and runs:

- **── Stage 1: Read v5 state DB ──** List the `states/` prefix (no v7 constant — use
  `RelativePath.Root / PathSegment.Parse("states")`), pick lexicographic-max name; assert metadata
  `DatabaseVersion=="5"`; assert passphrase is ASCII (else abort). `DownloadAsync` →
  `IEncryptionService.WrapForDecryption` (auto-detects Salted__) → `ICompressionService.WrapForDecompression`
  (auto-detects gzip) → temp `.sqlite`; open with `Microsoft.Data.Sqlite`.
- **── Stage 2: Load + classify ──** Read both tables (raw SQL). `Hash` BLOB →
  `Convert.ToHexString(bytes).ToLowerInvariant()` → `ContentHash.Parse` (exactly 64 hex). Classify
  LARGE/TAR/THIN. Enumerate `chunks/` once (`ListAsync(BlobPaths.ChunksPrefix, includeMetadata:true)`)
  → map `hash → (ContentLength, Tier, existing Metadata)`. **Validate one TAR**: download it, open
  `TarReader`, assert every entry name parses as `ContentHash`; fail fast with a clear message
  otherwise (a v5 tar with non-hash entry names cannot be restored by v7).
- **── Stage 3: Upsert chunk metadata ──** Per table above; merge into the existing metadata dict
  from Stage 2 and `SetMetadataAsync`; `UploadThinAsync` for thin stubs. Run to completion before
  Stage 4 (a partial run leaves chunks without `arius_type`, which repair silently drops).
- **── Stage 4: Build chunk index (reuse repair) ──** `IChunkIndexService.RepairAsync()` — enumerates
  `chunks/`, reads the metadata just written, builds & uploads shards. Do **not** call
  `AddEntries`/`FlushAsync` (repair uploads shards directly).
- **── Stage 5: Build filetrees ──** `FileTreeStagingSession.OpenAsync(cacheRoot)`;
  `new FileTreeStagingWriter(session.StagingRoot)`. Per `PointerFileEntry`:
  `relPath = RelativePath.FromPlatformRelativePath(RelativeName)` (normalizes `\`→`/`) then
  `.RemoveSuffix(".pointer.arius", OrdinalIgnoreCase)` — skip+log if empty/root/invalid;
  `contentHash = ContentHash.Parse(hex(Hash))`; timestamps parsed to `DateTimeOffset` UTC (null →
  `DateTimeOffset.UnixEpoch`, never `UtcNow`); `await writer.AppendFileEntryAsync(relPath, contentHash, created, modified)`.
  Accumulate `fileCount` and `totalSize` (Σ `OriginalSize` over distinct referenced binaries). Then
  `rootHash = await new FileTreeBuilder(encryption, fileTreeService, logger).SynchronizeAsync(session.StagingRoot)`.
- **── Stage 6: Snapshot + promote ──** If `rootHash` is null (empty repo) → done.
  `snapshot = await snapshotService.CreateAsync(rootHash.Value, fileCount, totalSize)`; then
  `await chunkIndex.PromoteToSnapshotVersionAsync(BlobPaths.SnapshotPath(snapshot.Timestamp).Name.ToString())`.

**Stage order 3→4→5→6 is mandatory** and mirrors archive's end-of-pipeline: repair must run before
the snapshot is created so `PromoteToSnapshotVersionAsync` tags the rebuilt shards to the new
snapshot version (creating the snapshot first makes promote a no-op).

## Edge cases to handle (from adversarial review)

- `RelativeName` transform (highest uncertainty): verify against a real v5 DB whether it carries the
  `.pointer.arius` suffix and which separators; skip+log rows that don't parse as a valid
  `RelativePath` rather than crashing.
- `Hash` is a raw 32-byte BLOB → must hex-lowercase; assert exactly 64 hex chars; skip NULL FKs.
- Timestamps: detect v5 column affinity (`SELECT typeof(...)`) — TEXT ISO-8601 vs INTEGER ticks — and
  parse accordingly.
- Idempotent: `SetMetadataAsync`/`UploadThinAsync`/`RepairAsync`/`CreateAsync` re-run safely.

## Verification (end-to-end against `ariusci`)

Environment: dotnet 10 + Docker present; `az` not installed (use `-k` key auth). Wipe
`~/.arius/ariusci-v5migrationtest/` between runs to avoid local-cache poisoning.

0. Apply the CBC fix; `dotnet build`. (A focused unit test that decrypts a Docker-produced v5 chunk
   at 10k is good insurance.)
1. **Create an authentic v5 repo** with the published image (amd64-only → `--platform linux/amd64`):
   ```
   docker run --rm --platform linux/amd64 -v "$SRC":/archive woutervanranst/arius \
     archive /archive -n ariusci -k <key> -c v5migrationtest -p <ascii-pass> --tier cool
   ```
   `$SRC` mixes: a >1 MB file (→ large chunk), several <1 MB files (→ tar bundle), a subdirectory, an
   empty file, and a path containing a space. Fallback if Docker/emulation fails: build
   `woutervanranst/Arius` from source and run its archive.
2. **Migrate:** `dotnet run --project src/Arius.Migration -- -a ariusci -k <key> -c v5migrationtest -p <ascii-pass>`
3. **`ls`** → `dotnet run -c Release --project src/Arius.Cli -- ls -a ariusci -k <key> -c v5migrationtest -p <pass>`
   asserts the snapshot lists all original files.
4. **`restore`** to a fresh dir, then `diff -r --exclude='*.pointer.arius' "$SRC" "$R1"` (+ checksum
   compare); timestamps within tolerance.
5. **Add a new file** and **`archive --tier Cool`**; assert the result reports `Deduped = <original count>`,
   `Uploaded = 1` (proves the salted-hash dedup against migrated chunks). Cross-check that existing
   `chunks/<hex>` blob names are unchanged and exactly one new chunk appeared.
6. **`restore`** the latest snapshot to another fresh dir; assert all files (old + new) byte-identical.
7. Leave `ariusci/v5migrationtest` intact.

A new live test in `Arius.E2E.Tests` can mirror `AzureE2EBackendFixture` credential plumbing
(`ARIUS_E2E_ACCOUNT`/`ARIUS_E2E_KEY`) but must use a **named, persistent** container (do not call the
fixture's auto-teardown). Coverage on arm64 via `coverlet.console` per project memory.

## Risks & limitations

1. CBC fix correctness — gates everything; covered by a decrypt test on a real v5 chunk.
2. ASCII-passphrase only — UTF-8 vs ASCII salt diverges hashes/keys; guard + document (real repos).
3. Archive-tier v5 blobs — migration works (metadata only) but restore needs rehydration (hours);
   test deliberately uses `cool`. The migration ignores v5 `StorageTier` (repair reads the blob's
   actual Azure tier).
4. v5 schema drift across point releases / the Docker image's DB version differing from the user's
   real repos — assert `DatabaseVersion`, verify column names against a real DB before a real run.
5. `RelativeName` suffix/separator handling — verify on real data; skip+log invalid rows.

## Critical files

- `src/Arius.Core/Shared/Encryption/PassphraseEncryptionService.cs` (revert `CbcPbkdf2Iter`→`10_000`)
- `src/Arius.Core/AssemblyMarker.cs` (add `InternalsVisibleTo("Arius.Migration")`)
- `src/Arius.Core/ServiceCollectionExtensions.cs` (`AddArius` — DI entry the project calls)
- `src/Arius.Core/Shared/Storage/BlobConstants.cs` (`BlobMetadataKeys`, `BlobPaths`, `ContentTypes`)
- `src/Arius.Core/Shared/Storage/IBlobContainerService.cs` (`ListAsync`/`SetMetadataAsync`/`UploadAsync`/`DownloadAsync`)
- `src/Arius.Core/Shared/ChunkStorage/ChunkStorageService.cs` (`UploadThinAsync`; metadata contract to mirror)
- `src/Arius.Core/Shared/ChunkIndex/ChunkIndexService.cs` (`RepairAsync`, `PromoteToSnapshotVersionAsync`)
- `src/Arius.Core/Shared/FileTree/{FileTreeStagingSession,FileTreeStagingWriter,FileTreeBuilder}.cs`
- `src/Arius.Core/Shared/Snapshot/SnapshotService.cs` (`CreateAsync`)
- `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs` (canonical stage 6 order to mirror)
- `src/Arius.Explorer/Infrastructure/RepositorySession.cs` (non-CLI `AddArius` host pattern)
- NEW: `src/Arius.Migration/{Arius.Migration.csproj,Program.cs,MigrateV5.cs}`

## Implementation outcome (2026-06-16)

Built and verified end-to-end against an authentic v5.0.190 repo in `ariusci/v5migrationtest`
(Docker `woutervanranst/arius`, `--tier Cool`; tree = 1 large file + 6 small files bundled into one
tar + nested dir + empty file + spaced filename).

- **CBC fix is two parts, not one.** Reverting `CbcPbkdf2Iter` 100_000 → 10_000 was necessary but not
  sufficient: commit `5cd4e2f0 "feat: fix goldenfiles for 100k iterations"` (same day as the Sonar
  bump) had also **regenerated the 3 CBC golden fixtures to 100k**, making arius7's legacy CBC reader
  self-consistent at 100k but unable to decrypt real v5 (10k) blobs. Restored those 3 fixtures to
  their 10k content via `git checkout 5cd4e2f0^ -- <files>`
  (`2552b810…`, `680ccc69…`, `9ffc39c1…` under `src/Arius.Core.Tests/Encryption/GoldenFiles/`).
  GCM is untouched (`GcmPbkdf2Iter = 100_000`). All 33 Encryption-namespace tests pass.
- **v5 schema confirmed** exactly as planned (`--dry-run` against the real DB): `RelativeName` is
  stored clean (no `.pointer.arius` suffix, forward slashes, no leading slash); `CreationTimeUtc`/
  `LastWriteTimeUtc` are TEXT ISO-8601; `BinaryProperties` has a separate parent row per tar.
- **E2E result:** migrate → `ls` (7 files) → `restore` (byte-identical) → `archive` of a new file
  (**Deduped: 7, Uploaded: 1** — salted-hash dedup against the migrated chunk index) → `restore`
  latest (8 files, all checksums match). The v5 repo is left intact in `ariusci` (in-place upsert;
  the `states/` DB and chunk bodies are untouched, so it stays v5-readable).
