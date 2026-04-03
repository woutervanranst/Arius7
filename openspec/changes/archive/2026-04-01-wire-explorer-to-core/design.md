## Context

The current `LsCommand` is a batch Mediator command (`ICommand<LsResult>` / `ICommandHandler`) that collects all entries into a `List`, does a global chunk index lookup, and returns an `LsResult` with `IReadOnlyList<LsEntry>`. It only emits file entries, not directory entries. The Explorer needs streaming results (for progressive tree population) and a merged repository view of cloud + local state. The CLI would also benefit from streaming output instead of buffering everything.

Mediator v3.0.x (already referenced as `Mediator.Abstractions`) provides `IStreamQuery<TResponse>` / `IStreamQueryHandler<TQuery, TResponse>`, consumed via `mediator.CreateStream(query)` returning `IAsyncEnumerable<TResponse>`.

The old Arius codebase had a `PointerFileEntriesQueryHandler` that used exactly this two-phase merge pattern: iterate state repository entries first (checking local existence), then iterate local filesystem entries (skipping already-yielded). This is the pattern we replicate against the new Merkle tree architecture.

## Goals / Non-Goals

**Goals:**
- Replace batch `LsCommand`/`LsResult` with streaming `IStreamQuery<RepositoryEntry>` / `IStreamQueryHandler`
- Two-phase merge of cloud (Merkle tree) and local filesystem per directory
- Emit both directory and file entries as discriminated union
- Orthogonal `Recursive` and `Prefix` parameters
- Add `ContainerNamesQuery` as `IStreamQuery<string>` for Explorer repository picker
- Update CLI `ls` verb to consume streaming
- Sufficient unit + integration test coverage for the streaming handler
- Wire Explorer ViewModels to new APIs

**Non-Goals:**
- No new Explorer features beyond wiring
- No changes to Archive or Restore pipeline logic
- No hydration status implementation (background fetch is Explorer-side, out of scope)
- No changes to tree blob format or snapshot format
- No drive-by refactors

## Decisions

### Decision 1: Mediator `IStreamQuery<RepositoryEntry>` for streaming

**Choice**: `LsCommand : IStreamQuery<RepositoryEntry>`, `LsHandler : IStreamQueryHandler<LsCommand, RepositoryEntry>`. Consumer calls `mediator.CreateStream(command)`.

**Rationale**: Mediator v3.0.x natively supports streaming queries. This keeps the Ls command in the Mediator pipeline (logging, validation behaviors via `IStreamPipelineBehavior`), matches how the old Arius used `IStreamQuery` for `PointerFileEntriesQuery` and `ContainerNamesQuery`, and the source generator auto-registers the handler.

### Decision 2: Two-phase merge algorithm per directory

```
WalkMergedAsync(treeHash?, localDirPath?, prefix, recursive, ct):
  1. Download + deserialize tree blob for treeHash (if non-null)
     → cloudEntries: Dictionary<string, TreeEntry>
  2. Phase 1 — Cloud iteration:
     For each cloud entry (files then dirs):
       - Check if local counterpart exists (File.Exists / Directory.Exists)
       - Yield cloud+local or cloud-only entry
       - Track yielded names in a HashSet
  3. Phase 2 — Local iteration (if localDirPath is non-null):
     For each local filesystem entry:
       - Skip if name already in yielded set
       - Yield local-only entry
  4. If recursive: descend into union of child directories
     - cloud+local dir → recurse(childTreeHash, childLocalPath)
     - cloud-only dir  → recurse(childTreeHash, null)
     - local-only dir  → recurse(null, childLocalPath)
```

**Rationale**: Mirrors the proven `PointerFileEntriesQueryHandler` pattern from old Arius. Memory-bounded (one directory's entries at a time). The `HashSet` of yielded names is scoped to a single directory (small). Local existence check uses `File.Exists` / `Directory.Exists` — cheap single syscalls, same as `LocalFileEnumerator`.

### Decision 3: Entry types — discriminated union records

```csharp
public abstract record RepositoryEntry(string RelativePath);

public sealed record RepositoryFileEntry(
    string RelativePath,
    string? ContentHash,        // from tree blob (null for local-only)
    long? OriginalSize,         // from chunk index (null if unavailable)
    DateTimeOffset? Created,    // from tree blob
    DateTimeOffset? Modified,   // from tree blob
    bool ExistsInCloud,
    bool ExistsLocally,
    bool? HasPointerFile,       // from FilePair (null if no local path)
    bool? BinaryExists          // from FilePair (null if no local path)
) : RepositoryEntry(RelativePath);

public sealed record RepositoryDirectoryEntry(
    string RelativePath,
    string? TreeHash,           // from tree blob (null for local-only)
    bool ExistsInCloud,
    bool ExistsLocally
) : RepositoryEntry(RelativePath);
```

**Rationale**: C# record inheritance with pattern matching. Same shape as old `PointerFileEntriesQueryResult` hierarchy but with cloud/local merge fields. `ContentHash` nullable for local-only files.

### Decision 4: Prefix navigates, Recursive controls depth

**Choice**: `Prefix` is used to navigate down to a target directory in the tree (downloading only the tree blobs on the path). Once at the target, `Recursive` controls whether we descend further. They are orthogonal.

**Rationale**: For the Explorer, `Prefix=photos/2024/, Recursive=false` expands one folder. For the CLI, `Prefix=photos/, Recursive=true` lists everything under photos. The current handler already has prefix-based pruning via `IsPathRelevant`; we extend it to work as navigation to a starting point rather than a post-hoc filter.

### Decision 5: Per-directory batch size lookup

**Choice**: For each directory, collect all file content hashes, call `ChunkIndexService.LookupAsync(hashes)` once per directory, then yield file entries with sizes populated.

**Rationale**: Avoids N+1 lookups while keeping the streaming granularity at directory level. The chunk index has local shard caching so repeated calls across directories share cache benefits. This is a pragmatic middle ground vs. the old approach of global batch (which required buffering all entries).

### Decision 6: ContainerNamesQuery in Arius.AzureBlob

**Choice**: `ContainerNamesQuery : IStreamQuery<string>`, `ContainerNamesQueryHandler : IStreamQueryHandler<ContainerNamesQuery, string>`. Lives in `Arius.AzureBlob` since it needs Azure SDK. Validates containers by checking for `snapshots/` prefix with `maxResults=1`.

**Rationale**: Follows the old Arius pattern exactly. Container listing is Azure-specific. The Explorer calls `mediator.CreateStream(new ContainerNamesQuery { ... })`.

### Decision 7: `LsCommand` replaces `LsCommand` + `LsResult` entirely

**Choice**: Remove `LsResult` and the batch `ICommandHandler`. The `LsCommand` becomes `IStreamQuery<RepositoryEntry>` instead of `ICommand<LsResult>`. Error handling (snapshot not found) throws an exception from the handler rather than returning a result object with `Success=false`.

**Rationale**: Single consumer pattern. No need for both batch and streaming. The CLI catches exceptions for user-facing error messages. Simpler API.

### Decision 8: Explorer DI — per-repository container rebuild

**Choice**: When the user selects a repository in the Explorer, build a new DI `ServiceProvider` with the new `AddArius(blobStorage, passphrase, accountName, containerName)` signature. Dispose the old one.

**Rationale**: The new `AddArius` requires connection parameters upfront. Matches CLI pattern. Explorer's `Program.cs` sets up a root container for app-level services, then creates a scoped container per repository.

## Risks / Trade-offs

- **[Risk] macOS worktree cannot build WPF** → Mitigation: Core + CLI changes are testable on macOS. Explorer changes are structural (will be validated by CI on Windows). Focus unit tests on the streaming handler (in Arius.Core.Tests), not on WPF ViewModels.
- **[Risk] Per-directory size lookup adds latency per directory** → Mitigation: Chunk index shards are locally cached after first access. Acceptable tradeoff vs. global buffering.
- **[Risk] Breaking CLI `ls` output format** → Mitigation: CLI is the only consumer. Updated in same change. Table output format stays the same; only consumption changes from batch to `await foreach`.
- **[Trade-off] Phase 2 local iteration requires knowing Phase 1 names** → The `HashSet<string>` from Phase 1 is directory-scoped (small, bounded). No global state.
- **[Trade-off] `File.Exists` per cloud entry in Phase 1** → One syscall per file. Fast for typical directory sizes. Same pattern `LocalFileEnumerator` uses.
