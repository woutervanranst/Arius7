# ListQuery: archive-style pipeline, repository/local naming, state-flags enum, StorageTierHint

## Context

The previous change made `ListQueryHandler` + `ls` stream via a bounded channel. The user now wants:

1. **Mirror `ArchiveCommandHandler` structure/style** (`src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`): type-level XML doc with stage breakdown + channel table + ASCII diagram, `// ── section ──` headers, aligned fields, `[phase]` log markers, stages as tasks that complete their channel writer in `finally`. Also take inspiration from the original Arius `PointerFileEntriesQueryHandler` (channel + producer tasks + `ReadAllAsync` yield loop).
2. **Rename** cloud→repository (files in the archive/blob storage) and local→`localFile`/`localDirectory`, including public API.
3. **Replace the four bools + `Hydrated`** on the result records with a combinable `[Flags]` enum: `LocalPointer`, `LocalBinary`, (`LocalDirectory`), `Repository`, `RepositoryHydrated`, `RepositoryArchived`, `RepositoryRehydrating`.
4. **`StorageTierHint` in the chunk index** so ls can tell Hydrated vs Archived without per-chunk blob calls (user-confirmed design; tier recorded at archive time, backfilled by repair).
5. **Keep** `ContentHash` on `RepositoryFileEntry` and the live `ChunkHydrationStatusQuery` refinement (user-confirmed): the hint can go stale and rehydration-pending is only knowable live.
6. `RelativePath` on `RepositoryEntry` is **already strongly typed** — nothing to do; answer to the user's question: yes, the handler already overlays repository files with local state (the new flags make the overlay explicit: consumer reads one `State` to know local and/or repository presence).

## 1. Public model — `src/Arius.Core/Features/ListQuery/ListQuery.cs`

```csharp
[Flags]
public enum RepositoryEntryState
{
    None = 0,
    // local disk
    LocalPointer   = 1 << 0,  // pointer sidecar on disk (files)
    LocalBinary    = 1 << 1,  // binary on disk (files)
    LocalDirectory = 1 << 2,  // directory exists on disk (directories)
    // repository (blob storage)
    Repository            = 1 << 3,  // present in the snapshot file tree
    RepositoryHydrated    = 1 << 4,  // tier hint hot/cool/cold; implies Repository
    RepositoryArchived    = 1 << 5,  // tier hint archive; implies Repository
    RepositoryRehydrating = 1 << 6,  // rehydration pending; implies RepositoryArchived; set by live refinement only
}

public abstract record RepositoryEntry(RelativePath RelativePath, RepositoryEntryState State);

public sealed record RepositoryFileEntry(
    RelativePath RelativePath, RepositoryEntryState State,
    ContentHash? ContentHash, long? OriginalSize,
    DateTimeOffset? Created, DateTimeOffset? Modified) : RepositoryEntry(RelativePath, State);

public sealed record RepositoryDirectoryEntry(
    RelativePath RelativePath, RepositoryEntryState State,
    FileTreeHash? TreeHash) : RepositoryEntry(RelativePath, State);
```

Replaces `ExistsInCloud`, `ExistsLocally`, `HasPointerFile`, `BinaryExists`, `Hydrated`. Handler always sets `Repository` together with `RepositoryHydrated`/`RepositoryArchived`; hint absent → `Repository` only.

## 2. Chunk index: `StorageTierHint` (mandatory, breaking — no back-compat)

Dev-only repos; the user will delete existing ones. No migration, no legacy parsing.

- **`src/Arius.Core/Shared/ChunkIndex/Shard.cs`** — `ShardEntry` becomes `ShardEntry(ContentHash, ChunkHash, long OriginalSize, long CompressedSize, BlobTier StorageTierHint)` (mandatory → every construction site, incl. tests, must pass it). New line format with the tier as a compact numeric token, explicit wire mapping `Hot=1, Cool=2, Cold=3, Archive=4` (switch, not `(int)` cast — independent of enum ordering):
  - large (content-hash == chunk-hash): `<ch> <os> <cs> <tier>` (4 fields)
  - small: `<ch> <chunk> <os> <cs> <tier>` (5 fields)
  - field count stays the discriminator (4 = large, 5 = small); 3-field legacy lines are now a `FormatException`.
- **`src/Arius.Core/Shared/ChunkIndex/ChunkIndexLocalStore.cs`** — add `storage_tier_hint INTEGER NOT NULL CHECK (storage_tier_hint BETWEEN 1 AND 4)` to `chunk_index_entries`; keep `SchemaVersion = "1"`, no upgrade step (stale dev caches: delete the cache dir). Update every SELECT/INSERT and the `ReadEntry` helper.
- **`ArchiveCommandHandler`** — the two `ShardEntry` creation sites (stage 4a large ~line 451, stage 4c thin entries ~line 546) pass `StorageTierHint: opts.UploadTier` (the tar blob's tier governs its thin entries).
- **`ChunkIndexService.RepairAsync`** — populate the hint from `BlobItem.Tier` (already surfaced by `IBlobContainerService` listings); fall back to `Hot` if the listing carries no tier.

Note for the flags: even with a mandatory hint, a list-query lookup can still *miss* (hash not in the index at all — same case where `OriginalSize` is `null` today), so a bare `Repository` state (no Hydrated/Archived bit) remains possible.

## 3. `ListQueryHandler` — archive-style two-stage pipeline

Rewrite `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs` mirroring `ArchiveCommandHandler` conventions (doc header with stages/channels tables + diagram, `── Tuning knobs ──` / `── Dependencies ──` sections, `[phase]` logs, writers completed in `finally`, faults propagated via `Complete(ex)`):

```text
Walk (×1) ─► walkItemChannel (bounded) ─► Resolve (×1) ─► entryChannel (bounded) ─► Handle (yield)
```

- **Stage 1 Walk (×1)**: iterative DFS (explicit stack, as today) reading one file-tree node at a time + the local top-directory-only overlay snapshot. Emits, in listing order, into `walkItemChannel`: already-resolved entries (directories, local-only files) and *unresolved repository-file candidates* (need size + tier). Internal union record, e.g. `WalkItem` with `Resolved(RepositoryEntry)` / `Candidate(FileEntry RepositoryFile, LocalFileState? LocalFile, RelativePath Directory)`.
- **Stage 2 Resolve (×1)**: FIFO; buffers consecutive candidates up to `SizeLookupBatchSize = 32` (small for responsiveness, per user); flushes the batch (one `IChunkIndexService.LookupAsync` for sizes + tier hints) before forwarding any resolved passthrough item — order preserved, batches naturally flush at directory boundaries. Maps hint → flags.

  **Why batch at all** (user asked — honest assessment from `ChunkIndexService.LookupAsync`, `ChunkIndexService.cs:75`): batching does *not* reduce shard downloads (prefixes are deduped globally via `loaded_prefixes`, and with `ShardPrefixLength = 2` a 32-hash batch spans ~30 distinct shards anyway), and on cache hit SQLite is queried per hash either way. Today's gain is only modest: the per-prefix snapshot-validation query is amortized (1 of ~3 SQLite calls per hash skipped) plus fewer async transitions. The real win that makes batching worthwhile:
- **`ChunkIndexService.LookupAsync` (batched overload)** — parallelize `EnsurePrefixLoadedAndSynchronizedAsync` across the batch's *distinct prefixes* (bounded, e.g. 8 concurrent) instead of the current sequential `foreach`. On a cold cache this turns ls's shard fetching from ~256 sequential downloads into concurrent ones — the dominant cold-start latency. SQLite writes stay serialized behind the existing `_localStateGate`. (The method already carries a `NOTE: needs to be battle tested during list/restore optimization`.) Stage 1/3 of the pipeline keeps running while a batch resolves, so tree reads overlap lookups regardless.
- **Consumer**: unchanged from current code — `ReadAllAsync` + per-item `ThrowIfCancellationRequested`, `finally` cancels a linked CTS and awaits both stage tasks.
- **Renames**: `cloudEntries`→`repositoryEntries` (with `repositoryDirectories`/`repositoryFiles` views), `CloudFileCandidate`→repository-file candidate, keep `LocalFileState`/`LocalDirectoryState`/`localFileSystem` naming.
- **Overlay semantics** (matches the reference handler's intent, confirmed by user): the file-tree index is the primary sequence; the local directory is enumerated once into a per-directory dictionary, each repository file *consumes* its local counterpart (`Remove`-on-match), and the leftovers are emitted last as local-only — repository entries first, local-only last, no union/distinct pass over both sets. This is already how the current code works; document it in the handler doc header.
- **Overlay optimization**: stop reading pointer-file *contents* during ls — `LocalFileState.PointerHash` is never used by the list query (only pointer/binary *existence* feeds the flags), yet `BuildLocalDirectorySnapshot` does a file read per pointer. Pass a `_ => null` reader from the handler; `LocalFileSnapshotBuilder` keeps the capability (its direct tests still cover it).

Flag assembly: files — `LocalPointer` from `LocalFileState.PointerExists`, `LocalBinary` from `BinaryExists`, `Repository`(+`Hydrated`/`Archived` from hint) when from the tree; local-only files get only local bits. Directories — `Repository` and/or `LocalDirectory`.

## 4. Consumers

- **`src/Arius.Core/Features/ChunkHydrationStatusQuery/ChunkHydrationStatusQuery.cs`** — filter becomes `file.State.HasFlag(RepositoryEntryState.Repository) && file.ContentHash is not null`; rename local `cloudFiles`→`repositoryFiles`. Query/result shape otherwise unchanged (live refinement stays).
- **`src/Arius.Explorer/RepositoryExplorer/FileItemViewModel.cs`** — colors from flags: `LocalPointer`→PointerFileStateColor, `LocalBinary`→BinaryFileStateColor, `Repository`→PointerFileEntryStateColor; initial `HydrationStatus`: `RepositoryRehydrating`→RehydrationPending, `RepositoryArchived`→NeedsRehydration, `RepositoryHydrated`→Available, bare `Repository`→Unknown.
- **`RepositoryExplorerViewModel.cs`** (lines ~263, ~461) — same flag-based filter.
- **`src/Arius.Cli/Commands/Ls/LsVerb.cs`** — only record-shape compile fixes (uses path/size/dates).

## 5. Tests

- **`src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`** — rename Cloud→Repository in test names/variables/seeded names (`cloud-only.txt`→`repository-only.txt` etc.); assert `State` flag combinations instead of bools; add: (a) overlay matrix test (repository-only / local-only / both, incl. pointer+binary bits), (b) tier-hint mapping test (`ShardEntry` with `Archive` hint → `RepositoryArchived`, hot/cool → `RepositoryHydrated`, no hint → bare `Repository`).
- **`src/Arius.Core.Tests/Shared/ChunkIndex/ShardTests.cs` / `ShardSerializerTests.cs`** — round-trip with each tier value (numeric 1–4 wire format); 4-vs-5-field discriminator; 3-field line now throws `FormatException`.
- **`ChunkIndexLocalStoreTests.cs`** — hint round-trip through SQLite.
- **`ChunkIndexServiceRepairTests.cs`** — repair populates hint from blob tier.
- **`ChunkIndexServiceLookupTests.cs`** — batched lookup with hashes spanning multiple prefixes loads them concurrently and returns correct entries (and still honors pending-flush precedence).
- **Archive tests** (`Arius.Core.Tests/Features/ArchiveCommand/…`) — uploaded entries carry the hint.
- **`ChunkHydrationStatusQuery` tests, `Arius.Integration.Tests/Pipeline/ListQueryIntegrationTests.cs`, `Arius.Cli.Tests`, `Arius.Explorer.Tests`** — mechanical record-shape/flag updates.

## Verification

1. `dotnet build src/Arius.slnx` (covers Explorer compile).
2. `dotnet test --project src/Arius.Core.Tests/...` (run 2–3×; the cancellation test was flake-prone), `src/Arius.Cli.Tests`, `src/Arius.Integration.Tests` (Explorer tests are Windows-only — compile check only on macOS).
3. Grep for leftovers: `ExistsInCloud|ExistsLocally|HasPointerFile|BinaryExists|CloudFile` should return nothing outside git history.
