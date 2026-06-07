## Why

The chunk index still relies on materialized in-memory shard dictionaries, an unbounded in-memory archive write session, and in-memory full-repair grouping. At repositories with millions of chunks, those structures become the memory bottleneck even though the chunk index is recoverable metadata and can be treated as a local disk-backed working cache.

This follow-up moves normal chunk-index work to bounded-memory disk-backed operations while preserving the current remote per-prefix shard blob format. That avoids returning to a single large uploaded/downloaded database file and keeps small archives from rewriting the whole index for tiny changes.

## What Changes

- Replace the current local L1/L2 shard-cache implementation with a local SQLite-backed chunk-index store owned entirely by `src/Arius.Core/Shared/ChunkIndex/`.
- Keep SQLite local-only: it is a cache and working store, not a repository source of truth and not uploaded as a chunk-index artifact.
- Preserve the current remote `chunk-index/<prefix>` shard blob layout and serialization format for this change.
- Store loaded shard rows, loaded-prefix freshness state, and archive dirty entries in the local SQLite store so normal lookup, entry recording, and flush avoid unbounded managed-memory collections.
- Validate loaded prefixes lazily against the current latest snapshot identity. A changed snapshot marks existing loaded-prefix state as needing per-prefix revalidation rather than forcing a repository-wide local cache purge.
- Rebuild full repair through disk-backed local state instead of grouping all reconstructed entries in memory.
- Keep dynamic chunk-prefix splitting out of scope, while introducing internal seams (`ChunkIndexRouter`) so a later change can replace fixed-prefix routing with longest-prefix routing and route-manifest interpretation behind `ChunkIndexService`.
- After a successful archive flush, promote loaded-prefix snapshot version to the newly published snapshot so prefixes validated during the run stay trusted under the new snapshot without re-probing remote.
- Use synchronous SQLite ADO.NET APIs inside chunk-index internals because `Microsoft.Data.Sqlite` async APIs execute synchronously.
- Use bounded batching for SQLite writes and remote shard hydration so ingestion does not become per-row overhead or an unbounded in-memory queue.
- Keep `ChunkIndexService` as the public operational facade; SQLite and the local-store implementation remain internal implementation details.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `chunk-index-service`: Change chunk-index local cache, archive write-session, flush, and repair behavior to use bounded-memory disk-backed local state while preserving current remote shard compatibility and source-of-truth semantics.
- `blob-storage`: Expand blob metadata returned by storage operations with an opaque remote blob identity so chunk-index prefixes can be validated lazily without downloading unchanged shard bodies.

Restore and list are unchanged consumers of `ChunkIndexService.LookupAsync` and required no code changes for this work, so they carry no capability delta. The SQLite implementation stays hidden behind the `chunk-index-service` facade; that boundary is asserted by the architecture tests rather than by restore/list requirements.

## Impact

- Affected code: `src/Arius.Core/Shared/ChunkIndex/`, `src/Arius.Core/Shared/Storage/`, storage backend implementations, `src/Arius.Core/ServiceCollectionExtensions.cs`, and chunk-index tests.
- Affected behavior: local chunk-index cache files change from per-prefix plaintext L2 files plus L1 memory pages to a local SQLite working store. The store is still discardable and recoverable from remote shard blobs or chunk blobs via repair.
- Affected dependencies: add a SQLite ADO.NET dependency, expected to be `Microsoft.Data.Sqlite`, used only by chunk-index internals. Do not introduce EF Core.
- Remote compatibility: current `chunk-index/<prefix>` blob names and shard serialization remain unchanged.
- Public API: `ChunkIndexService` remains the public facade for current callers. Any new internal route/local-store abstractions stay inside `Shared/ChunkIndex`.
- Out of scope: dynamic prefix splitting and route manifest publication are planned follow-ups rather than part of this change.
