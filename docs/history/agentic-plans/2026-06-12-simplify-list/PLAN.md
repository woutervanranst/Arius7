# Refactor `ls`: a symmetric remote/local directory walk (breadth-first)

## Context

The list command rewrite (PR #103, merged as a5e2eb6) is functionally solid but hard to understand. The conceptual operation is simple — *per directory, read the remote listing (persisted filetree node) and the local listing (filesystem), merge them by name, emit entries, recurse* — but the code obscures it:

- **No symmetric domain language.** Remote side speaks `FileEntry`/`DirectoryEntry`, local side speaks `LocalFileState`/`LocalDirectoryState`/`LocalDirectorySnapshot`; nothing in the names says "these are the two mirrored halves of one merge".
- **Plumbing dominates.** A 3-stage channel pipeline (Walk → Resolve → Consume) with five handler-private types (`WalkItem` nullable-union, `RepositoryFileCandidate`, `DirectoryToWalk`, `LocalDirectoryState`, `LocalDirectorySnapshot`), two bounded channels, two `Task.Run`s and a linked CTS — all to support a 32-item batching scheme for chunk-index lookups.
- **The batching scheme contradicts the spec.** `openspec/specs/list-query/spec.md:70` mandates: *"Sizes SHALL be looked up in per-directory batches (all file hashes in a single directory are batched into one `LookupAsync` call)."* The Resolve stage's fixed 32-item batches deviate from this.
- **`LocalFileSnapshotBuilder.BuildFiles` takes 4 `Func<>` parameters**, one of which (`readPointerHash`) is always `_ => null` in production and whose output (`LocalFileState.PointerHash`) is never consumed.

Goal: rewrite `ListQueryHandler` as a single-stage **breadth-first** walk with an explicit remote/local symmetric vocabulary, class docstrings stating intent (including a table of the symmetric read/merge quartet), high-level step comments, and ~half the types. Public output model (`RepositoryEntry`, `RepositoryEntryState`) and CLI stay untouched.

Traversal decision (user): **breadth-first, files before directories within each directory** — per directory, yield its file entries first, then its subdirectory entries, then enqueue the subdirectories. Rationale: listing the root of a big repo shows the full shallow structure before descending; first-result latency is unchanged. The old depth-first order came only from the spec text (no recorded rationale for `ls`; the repo's depth-first rationale is archive-specific tar affinity). Requires a one-word spec edit; tests don't pin traversal order (they sort or use `OfType<>().Single()`).

## Design

### Domain vocabulary — the symmetric model

One new file `src/Arius.Core/Features/ListQuery/DirectoryListings.cs` holds both halves side by side, each docstring naming its mirror. Concrete records, no generics — the symmetry lives in names and shapes, not type parameters (remote files are an ordered reference sequence; local files are a by-name overlay dictionary).

```csharp
/// <summary>What the repository knows about one directory: the persisted filetree node,
/// split into files and subdirectories. The mirror of <see cref="LocalDirectoryListing"/>.</summary>
internal sealed record RemoteDirectoryListing(
    IReadOnlyList<FileEntry>      Files,           // tree order = the listing's reference order
    IReadOnlyList<DirectoryEntry> Subdirectories)
{
    public static readonly RemoteDirectoryListing Empty = new([], []);
    public static RemoteDirectoryListing From(IReadOnlyList<FileTreeEntry> treeEntries) => ...;
}

/// <summary>What the local filesystem knows about the same directory: immediate child files
/// (pointer + binary paired under the binary name) and subdirectory names.
/// The mirror of <see cref="RemoteDirectoryListing"/>.</summary>
internal sealed record LocalDirectoryListing(
    Dictionary<PathSegment, LocalFile> Files,      // keyed by binary name; consumed during merge
    IReadOnlySet<PathSegment>          Subdirectories)
{
    public static LocalDirectoryListing Empty => new([], ...); // fresh Files dict — merge mutates it
}

/// <summary>A binary/pointer pair on disk, keyed by binary name.</summary>
internal sealed record LocalFile(
    PathSegment Name, bool BinaryExists, bool PointerExists,
    long? Size, DateTimeOffset? Created, DateTimeOffset? Modified);
```

Note: `LocalFile` drops `PointerHash` (never consumed) and `Path` (derivable as `directory / Name`). Naming avoids collisions with the existing `Shared` types `LocalDirectory` (rooted dir) and `FileEntry`/`DirectoryEntry` (filetree).

`DirectoryListings.cs` also hosts `LocalDirectoryReader` (replaces `LocalFileSnapshotBuilder`):
- `Read(RelativeFileSystem fs, RelativePath directory, ILogger logger)` — enumeration + warn-and-continue on IO errors (moved out of the handler's `BuildLocalDirectorySnapshot`).
- Testable core `PairFiles(IEnumerable<LocalFileEntry> files, Func<RelativePath,bool> fileExists, Func<RelativePath,(long Size, DateTimeOffset Created, DateTimeOffset Modified)> stat)` — 4 Funcs → 2: `readPointerHash` deleted; size + timestamps merged into one `stat` (both are only called for existing binaries). The Funcs stay because the existing `BuildFiles_DoesNotProbeForCounterpartsAlreadyPresentInEnumeratedSet` test asserts "this probe must not happen" via throwing Funcs — a concrete filesystem can't express that.

### Handler structure — one stage, zero channels, breadth-first

Per-directory `LookupAsync` (spec-mandated) removes the only reason for the Resolve stage, which removes the channels entirely. `Handle` becomes a plain async iterator:

```csharp
public async IAsyncEnumerable<RepositoryEntry> Handle(ListQuery command, ...)
{
    // 1. Resolve the snapshot and descend to the prefix directory.
    // 2. Walk; accumulate summary counters as entries stream past.
    await foreach (var entry in WalkAsync(...)) { /* counters */; yield return entry; }
    // 3. Summary log.
}

private async IAsyncEnumerable<RepositoryEntry> WalkAsync(...)
{
    // Breadth-first walk (FIFO queue): each directory's own entries are emitted in full —
    // files first, then subdirectories — before any descent, so the shallow overview of a
    // large repository streams out before the walk sinks into subtrees. Per directory:
    //   read the remote listing (persisted filetree node)
    //   read the local listing  (filesystem, immediate children only)
    //   merge files           — remote is the reference sequence, local overlays, leftovers local-only
    //   merge subdirectories  — remote order first, then local-only; doubles as the traversal worklist
    var pending = new Queue<DirectoryToWalk>();
    pending.Enqueue(start);
    while (pending.Count > 0)
    {
        ct.ThrowIfCancellationRequested();
        var dir    = pending.Dequeue();
        var remote = await ReadRemoteDirectoryAsync(dir.TreeHash, ct);
        var local  = ReadLocalDirectory(fs, dir.Path, dir.ExistsLocally);

        await foreach (var file in MergeFilesAsync(dir.Path, remote, local, filter, ct))
            yield return file;

        var subdirectories = MergeSubdirectories(dir.Path, remote, local);
        foreach (var sub in subdirectories)
        {
            yield return new RepositoryDirectoryEntry(sub.Path, StateOf(sub), sub.TreeHash);
            if (recursive) pending.Enqueue(sub);
        }
    }
}

private sealed record DirectoryToWalk(RelativePath Path, FileTreeHash? TreeHash, bool ExistsLocally);
```

The symmetric read/merge quartet (the names ARE the domain story):

- `ReadRemoteDirectoryAsync(FileTreeHash?, ct)` → `RemoteDirectoryListing` (via `IFileTreeService.ReadAsync`; `Empty` when no tree hash)
- `ReadLocalDirectory(RelativeFileSystem?, RelativePath, bool)` → `LocalDirectoryListing` (via `LocalDirectoryReader.Read`; `Empty` when no local root / dir absent)
- `MergeFilesAsync(directory, remote, local, filter, ct)` → file entries, three commented steps (~45 lines):
  1. **pair** — each remote file `Remove`s its local counterpart, *even when the filter rejects it*, so the local-only pass never re-emits it (preserve the existing comment explaining this)
  2. **resolve** — ONE `LookupAsync` over the directory's remote content hashes → size + `RepositoryHydrated`/`RepositoryArchived` flags
  3. **emit** — remote files in tree order, then local-only leftovers (`ContentHash: null`)
- `MergeSubdirectories(parent, remote, local)` → `List<DirectoryToWalk>` — remote subdirs in tree order (flag `LocalDirectory` if present locally), then local-only; entry `State` is derived (`TreeHash != null → Repository`, `ExistsLocally → LocalDirectory`), so no separate emitted-names bookkeeping.

### What dies / what survives

Dies: `WalkItem`, `RepositoryFileCandidate`, `LocalDirectoryState`, `LocalDirectorySnapshot`, `LocalFileState`, `LocalFileSnapshotBuilder` (file deleted), both channels, `ResolveAsync`, `ResolveBatchAsync`, `ChannelCapacity`, `ResolveBatchSize`, both `Task.Run`s + linked CTS, the `[phase] resolve` log marker (no test asserts it), the pipeline/channel docstring essay. Handler goes ~511 → ~280 lines.

Survives unchanged: constructor signature (used by `ServiceCollectionExtensions.cs:124` and `RepositoryTestFixture.cs:224`), start/summary log lines and counters, `ResolveStartingPointAsync`, `MatchesFilter`, `LocalStateOf` (now over `LocalFile`), `PathSegmentOrdinalIgnoreCaseComparer` (moves next to `LocalDirectoryReader`).

### Docstrings & comments

**`ListQueryHandler` class docstring** (replaces the channel essay) tells this story: *"`ls` is a synchronized walk of two trees that mirror each other: the snapshot's persisted filetree (remote) and the local directory tree (local). Per directory, both listings are read and merged — the remote listing is the reference sequence, the local listing overlays it: remote entries absorb their local counterpart, leftovers trail as local-only. Files first, then subdirectories; the walk is breadth-first so the shallow structure of a large repository streams out before any subtree is descended. One chunk-index lookup per directory supplies sizes and storage tiers. Streaming: each directory's entries yield as soon as it is merged; memory is bounded by directory width plus the traversal frontier, not repository size."* — followed by the quartet table:

```
/// | Step                     | Remote (repository)                      | Local (filesystem)                       |
/// |--------------------------|------------------------------------------|------------------------------------------|
/// | Read                     | ReadRemoteDirectoryAsync(treeHash)        | ReadLocalDirectory(fs, path)             |
/// |                          |   → RemoteDirectoryListing                |   → LocalDirectoryListing                |
/// | Merge files              | MergeFilesAsync: remote files in tree order, each absorbing its local       |
/// |                          | counterpart; leftovers trail as local-only                                  |
/// | Merge subdirectories     | MergeSubdirectories: remote subdirs in tree order, flagged if present       |
/// |                          | locally; local-only subdirs trail — result doubles as the traversal worklist|
```

Keep the two existing case-sensitivity sentences (overlay matching is exact/case-sensitive; `Prefix`/`Filter` are case-insensitive user conveniences) — hard-won nuance.

**`DirectoryListings.cs`**: record docstrings each naming their mirror (sketched above), both shapes in one file side by side.

**Inside methods**: one comment per high-level step (`// pair / resolve / emit`, the per-directory walk steps), no line-by-line noise.

## Files

| File | Change |
|---|---|
| `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs` | Rewrite per above |
| `src/Arius.Core/Features/ListQuery/DirectoryListings.cs` | **New**: `RemoteDirectoryListing`, `LocalDirectoryListing`, `LocalFile`, `LocalDirectoryReader` |
| `src/Arius.Core/Features/ListQuery/LocalFileSnapshotBuilder.cs` | **Deleted** |
| `src/Arius.Core/Features/ListQuery/ListQuery.cs` | Untouched (public model + CLI unaffected) |
| `openspec/specs/list-query/spec.md` | Line 86: "depth-first tree walk" → "breadth-first tree walk (each directory's entries — files first, then subdirectories — stream before any descent)". Size-lookup requirement (line 70) now actually met |
| `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs` | ~14 `Handle_*` behavioral tests unchanged (the safety net; none pin traversal order). Retarget the 2 `BuildFiles_*` tests to `LocalDirectoryReader.PairFiles` with the 2-Func signature; replace the `PointerHash` assertion with `PointerExists`; keep the throwing-Func no-redundant-probe assertions |

Future work (noted, not built): `RemoteDirectoryListing`/`LocalDirectoryListing` could later unify restore's repo-only tree walk and archive's `FilePair` local walk; no shared abstraction now.

## Honest perf assessment

- **Lookups**: tiny directories get one small `LookupAsync` instead of riding a shared 32-batch, but `ChunkIndexService` (`ChunkIndexService.cs:77`) dedupes, shards, parallelizes cold downloads, and caches shards locally — warm calls are in-memory dictionary hits, dwarfed by the one `ReadAsync` blob read per directory the walk already pays. Huge directories improve: one call with all hashes parallelizes cold shard downloads vs N sequential 32-item calls today.
- **Channel removal** loses walk-ahead prefetch (downloading the next directory while the consumer renders the current one). For CLI rendering (microseconds/row vs blob I/O) wall-clock impact is ~zero. If it ever matters, a single bounded channel can be reintroduced without touching the domain types.
- **First-result latency**: same or better — entries yield per directory, no batch buffering, no channel hops. BFS additionally streams the full shallow overview before any deep descent.
- **Memory**: bounded by directory width plus the BFS frontier (all directories at the current level — tiny records: path + hash + bool); not a practical concern.

## Verification

```bash
dotnet build src/Arius.slnx
dotnet test src/Arius.Core.Tests --filter "FullyQualifiedName~ListQuery"
dotnet test src/Arius.Core.Tests
dotnet test src/Arius.Cli.Tests
dotnet test src/Arius.Architecture.Tests
```

Behavioral invariants the existing tests pin down (must stay green unmodified): state-flag combinations across the remote/local matrix, `OriginalSize == null` + bare `Repository` flag for unindexed hashes, prefix segment matching (case-insensitive), filter applies to files not directories, prompt cancellation, no container creation when snapshot missing. Additionally, run `ls` against a real or fixture repository (`verify`/`run` skills) to eyeball the new breadth-first output order.
