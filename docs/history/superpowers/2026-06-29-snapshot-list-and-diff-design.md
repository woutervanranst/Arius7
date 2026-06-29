# Design: `arius snapshot list` and `arius snapshot diff`

- **Date:** 2026-06-29
- **Status:** Approved (pending spec review)
- **Related:** GitHub issue #134 (`arius diff <snapA> <snapB>`)

## Summary

Add a `snapshot` command group to the CLI with two read-only verbs:

- **`arius snapshot list`** — list every snapshot oldest→newest, with a 1-based convenience index (oldest = 1 … latest = n).
- **`arius snapshot diff <a> <b>`** — report what changed between two snapshots, classified into a fully MECE set of categories.

Two Core changes back these:

1. The existing `SnapshotsQuery` is **renamed to `SnapshotsListQuery`** and **converted from a materialized `ICommand` to a streaming `IStreamQuery<SnapshotInfo>`**. Its consumers (CLI, Api, tests) are migrated.
2. A new **`SnapshotDiffQuery`** streaming feature slice walks two filetrees in lockstep with Merkle pruning and emits one classified entry per changed path.

## Motivation and context

Snapshots are an immutable, content-addressed Merkle tree (`SnapshotManifest.RootHash` → filetree nodes; file leaves carry a `ContentHash`, directory edges carry a `FileTreeHash`). Diffing two snapshots is therefore `O(changed nodes)`, not `O(total files)`: subtrees whose `FileTreeHash` matches are skipped wholesale. Issue #134 validated this on a real repo (2267 → 2476 files; only 3 nodes read).

There was no CLI surface to enumerate snapshots, and no diff at all. The Web/Explorer UIs already enumerate snapshots through Core's Mediator query; the CLI should use the same handler rather than duplicate the listing logic.

## Part A — `snapshot list`

### A1. Convert and rename the snapshot-listing query

`SnapshotsQuery` (`src/Arius.Core/Features/SnapshotsQuery/SnapshotsQuery.cs`) today:

```csharp
public sealed record SnapshotsQuery() : ICommand<IReadOnlyList<SnapshotInfo>>;
public sealed record SnapshotInfo(string Version, DateTimeOffset Timestamp, long FileCount);
public sealed class SnapshotsQueryHandler(...) : ICommandHandler<SnapshotsQuery, IReadOnlyList<SnapshotInfo>> { ... }
```

becomes, in a renamed folder `src/Arius.Core/Features/SnapshotsListQuery/SnapshotsListQuery.cs`:

```csharp
public sealed record SnapshotsListQuery() : IStreamQuery<SnapshotInfo>;
public sealed record SnapshotInfo(string Version, DateTimeOffset Timestamp, long FileCount); // unchanged
public sealed class SnapshotsListQueryHandler(ISnapshotService snapshots, ILogger<SnapshotsListQueryHandler> logger)
    : IStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>
{
    public async IAsyncEnumerable<SnapshotInfo> Handle(
        SnapshotsListQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        var blobNames = await snapshots.ListBlobNamesAsync(ct);          // oldest → newest
        foreach (var blobName in blobNames)
        {
            var version  = snapshots.GetVersion(blobName);
            var manifest = await snapshots.ResolveAsync(version, ct);     // disk-cache-first
            if (manifest is null) { logger.LogWarning("[snapshots] manifest for {Version} could not be resolved; skipping", version); continue; }
            yield return new SnapshotInfo(version, manifest.Timestamp, manifest.FileCount);
        }
    }
}
```

Behaviour is identical except each `SnapshotInfo` is yielded as its manifest resolves. **Oldest→newest ordering is preserved** — the CLI convenience index relies on it.

### A2. Migrate consumers

- **DI** (`src/Arius.Core/ServiceCollectionExtensions.cs`): change the registration from `ICommandHandler<SnapshotsQuery, IReadOnlyList<SnapshotInfo>>` to `IStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>` (the handler needs no `accountName`/`containerName`, like the current one).
- **Api** (`src/Arius.Api/Endpoints/BrowseEndpoints.cs`): `var snapshots = await mediator.Send(new SnapshotsQuery(), ct)` becomes a `await foreach (var s in mediator.CreateStream(new SnapshotsListQuery(), ct))` that collects into the existing `List<SnapshotDto>`. The REST response and the `SnapshotDto` contract are unchanged, so the Angular Web UI is untouched.
- **Tests**: `src/Arius.Core.Tests/Features/SnapshotsQuery/SnapshotsQueryHandlerTests.cs` (rename folder/file, drive the handler as an async stream) and `src/Arius.Cli.Tests/TestSupport/CliHarness.cs` (mock `IStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>` returning an async sequence instead of an `ICommandHandler`).

### A3. CLI `snapshot` group and `list` verb

- Introduce the **first nested command group** in `src/Arius.Cli/CliBuilder.cs` `BuildRootCommand()`:

  ```csharp
  var snapshot = new Command("snapshot", "Inspect snapshots");
  snapshot.Subcommands.Add(SnapshotListVerb.Build(serviceProviderFactory));
  snapshot.Subcommands.Add(SnapshotDiffVerb.Build(serviceProviderFactory));
  rootCommand.Subcommands.Add(snapshot);
  ```

- `src/Arius.Cli/Commands/Snapshot/SnapshotListVerb.cs` — `static Build(...)` returning a `Command`, following the existing `LsVerb` shape. It consumes `mediator.CreateStream(new SnapshotsListQuery(), ct)` and, **as each row arrives**, increments a 1-based counter and prints `index · version · timestamp · fileCount` via `AnsiConsole.MarkupLine` (Humanizer for counts/relative time). No buffering, matching the `ls` streaming idiom. The index is purely a display ordinal — not persisted, not part of the query.

## Part B — `snapshot diff`

### B1. New Core feature slice

`src/Arius.Core/Features/SnapshotDiffQuery/`:

```csharp
public sealed record SnapshotDiffQuery(string VersionA, string VersionB) : IStreamQuery<SnapshotDiffEntry>;

public enum ChangeType { Added, Removed, Modified, TimestampChanged }   // plain enum, NOT [Flags]

// Before/After reuse the existing domain record FileEntry (Name, ContentHash, Created, Modified).
public sealed record SnapshotDiffEntry(ChangeType Change, RelativePath Path, FileEntry? Before, FileEntry? After);
```

- `Added` → `Before == null`; `Removed` → `After == null`; `Modified`/`TimestampChanged` → both set.
- **`ChangeType` is a plain enum, deliberately not `[Flags]`.** `[Flags]` is correct for `RepositoryEntryState`, whose states *co-occur* on one entry. The diff is the opposite: each path is exactly one of Added/Removed/Modified/TimestampChanged. A plain enum encodes the MECE "exactly one" invariant at the type level; `[Flags]` would admit illegal combinations.

### B2. Handler — lockstep pruned walk

`SnapshotDiffQueryHandler : IStreamQueryHandler<SnapshotDiffQuery, SnapshotDiffEntry>`, injected with `ISnapshotService`, `IFileTreeService`, and a logger (registered as a factory in `ServiceCollectionExtensions`, like `ListQueryHandler`).

1. Resolve both manifests: `snapshots.ResolveAsync(VersionA/VersionB)`. If either is `null`, throw (snapshot not found / no snapshots).
2. **Cross-version warning:** if `manifestA.AriusVersion != manifestB.AriusVersion`, `logger.LogWarning(...)`. This is the guard from issue #134 against the cross-platform CRLF/LF hash-bug boundary (commit `69bf12e8`), where every directory hash differs even for identical content — defeating pruning and reporting spurious changes. The CLI's Serilog console surfaces the warning.
3. **BFS lockstep walk** over a `Queue<(FileTreeHash? aHash, FileTreeHash? bHash, RelativePath dirPath)>` seeded with `(manifestA.RootHash, manifestB.RootHash, RelativePath.Root)`. For each dequeued pair:
   - If `aHash == bHash` → **prune** (skip the whole subtree; emit nothing).
   - Otherwise read both nodes via `FileTreeService.ReadAsync` (a `null` hash → empty listing) and split each into files and subdirectories by `Name`.
   - **Files** (match by `Name`):
     - in A only → `Removed`
     - in B only → `Added`
     - in both: `ContentHash` differs → `Modified`; same `ContentHash` and (`Created` or `Modified` differ) → `TimestampChanged`; otherwise identical → emit nothing.
   - **Subdirectories** (match by `Name`):
     - in both: same `FileTreeHash` → prune; differ → enqueue `(aHash, bHash, childPath)`.
     - in A only → enqueue `(aHash, null, childPath)` → its whole subtree streams out as `Removed`.
     - in B only → enqueue `(null, bHash, childPath)` → its whole subtree streams out as `Added`.
   - `yield return` each classified entry as it is discovered.

Reads go through `FileTreeService.ReadAsync` (cache-first, disk write-through) — the same read path `ls`/`restore` use; no `ValidateAsync` is required for the read-only path.

### B3. CLI `diff` verb

`src/Arius.Cli/Commands/Snapshot/SnapshotDiffVerb.cs` — two positional arguments `<a>` and `<b>`.

**Argument resolution is CLI-side** (Core's `SnapshotDiffQuery` only ever sees version strings, mirroring the index being a CLI-only ordinal). Per argument:

- **Pure integer** (e.g. `5`) → resolve as a 1-based index into the streamed `SnapshotsListQuery` (so `5` = the 5th-oldest snapshot's `Version`). Out-of-range → friendly error.
- **Otherwise** → treat as a version. If it parses as a timestamp (e.g. `2024-04-02T13:09:54` or `2024-04-02`), normalize it to the stored version-prefix format (`SnapshotService.TimestampFormat`, colon-less UTC `yyyy-MM-ddTHHmmss.fffZ`) before matching; if it does not parse, use it verbatim as a raw `StartsWith` prefix (consistent with `ls`/`restore --version`).

Mixed forms (`diff 5 2024-12-30T16:17:32`) resolve each argument independently.

**Output:** stream git `--name-status`-style rows as entries arrive — a status letter (`A` added, `D` removed, `M` modified, `T` timestamp-only) + path, colorized via Spectre, interleaved in walk order (memory-bounded) — followed by a trailing summary line with per-category counts. `--json` is deferred (issue #134 lists output format as TBD).

## MECE analysis (the issue #134 investigation)

Issue #134's proposed set — Added, Removed, Moved/Renamed, Timestamp-only, Net-new content — is **not** MECE, for three reasons:

1. **Exhaustiveness hole.** Added/Removed are *name* set-diffs and Timestamp-only requires the *same* `ContentHash`, so an in-place content edit (same path, new content) falls through every category. Fixed by adding **`Modified`** (same path, different `ContentHash`).
2. **Not mutually exclusive.** Moved/Renamed is derived by hash-joining Removed against Added, so a moved file appears in all three arrays unless the matched pair is subtracted from Added/Removed.
3. **Wrong axis.** "Net-new content" is a content-novelty *attribute*, orthogonal to the path-operation categories. An Added file may carry new *or* pre-existing content (a copy), and net-new content also arrives via `Modified` entries — so it cross-classifies and cannot be a sibling array.

**Decisions:**

- **Rename detection is dropped.** The 1:1 hash-join is not watertight (ambiguous when a `ContentHash` appears at multiple removed *and* added paths). Dropping it makes the partition strictly simpler and fully MECE by purely local comparison: a move surfaces honestly as a `Removed` + an `Added`. It also removes the only barrier to streaming (no global join), enabling the streaming `IStreamQuery`.
- **Net-new content is dropped from v1.** Merkle pruning means A's unchanged subtrees are never walked, so "absent from A" is not soundly computable; the chunk index only answers the *different* question "absent from the whole repo, ever" and touches storage. Tracked as a follow-up (see below).

**Resulting partition** — every path in A ∪ B lands in exactly one:

| Category | Rule |
|---|---|
| `Added` | path only in B |
| `Removed` | path only in A |
| `Modified` | same path, different `ContentHash` |
| `TimestampChanged` | same path, same `ContentHash`, different `Created`/`Modified` |
| *(Unchanged)* | same path, same `ContentHash`, same timestamps — not emitted |

## Memory and performance

- **Common case:** the lockstep pruned walk allocates O(changed leaves + differing-directory BFS frontier), not O(total files). For a 1M-file repo with a typical incremental change, that is single-digit MB. Node reads are one directory's entries at a time, processed and discarded.
- **Streaming** keeps the *result* memory-bounded too: entries are yielded, never collected into giant per-category arrays inside Core. (Consumers that want grouped arrays — a future Api endpoint — collect at their own boundary, where REST must materialize anyway.)
- **Pathological cases** (the CRLF/LF hash boundary, or a genuine mass change such as touching every timestamp) defeat pruning; the walk degrades toward O(total files) and the output is intrinsically large. The `AriusVersion`-mismatch warning is the explicit guard for the hash-boundary case.

## Error handling

- Unresolvable snapshot version (either argument) → the CLI verb fails with a clear message; the Core handler throws on a `null` manifest.
- CLI index out of range, or a non-existent version prefix → friendly CLI error.
- Cross-`AriusVersion` comparison → logged warning (not an error); diff still runs.

## Testing (TDD)

- **Core** `SnapshotDiffQueryHandlerTests`: construct in-memory filetrees and assert (a) the MECE classification for each `ChangeType`, including the in-place `Modified` and `TimestampChanged` cases, and (b) that an identical subtree (equal `FileTreeHash`) is **pruned** — not read — using a counting/fake `IFileTreeService`. Reuse existing snapshot/filetree test fixtures.
- **Core** `SnapshotsListQueryHandlerTests`: rename and update to drive the streaming handler.
- **CLI** `Commands/Snapshot/` tests via `CliHarness` with mocked stream handlers: `list` numbering (oldest = 1) and `diff` argument resolution (integer index, raw prefix, and normalized timestamp).

## Documentation

- `docs/design/core/features/queries.md` — update the `SnapshotsQuery` section for the rename + streaming change; add a `SnapshotDiffQuery` description (or a sibling page if it grows).
- `docs/guide/cli.md` — document `arius snapshot list` and `arius snapshot diff`.
- `docs/glossary.md` — only if a new grounded term is introduced (none expected).

## Out of scope / follow-ups

- **No Api/Web surface for diff** in v1 (not requested). The streaming Core query leaves the door open for an endpoint later.
- **`--json` output** for diff — deferred.
- **Chunk-index-backed repo-wide "new bytes" summary** — file a GitHub issue for the deferred net-new-content axis (a chunk-index lookup of `ContentHash`es in Added ∪ Modified that are absent repo-wide), clearly labelled as "new to the repository, ever" rather than "new vs snapshot A".

## Naming summary

- Core query: `SnapshotsListQuery` (was `SnapshotsQuery`); result `SnapshotInfo` (unchanged); handler `SnapshotsListQueryHandler`; folder `Features/SnapshotsListQuery/`.
- Core diff: `SnapshotDiffQuery`, `SnapshotDiffEntry`, `ChangeType`, handler `SnapshotDiffQueryHandler`; folder `Features/SnapshotDiffQuery/`.
- CLI: command group `snapshot`; verbs `SnapshotListVerb`, `SnapshotDiffVerb` under `Commands/Snapshot/`.
