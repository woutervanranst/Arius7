## Why

The chunk index still relies on materialized in-memory shard dictionaries, an unbounded in-memory archive write session, and in-memory full-repair grouping. At repositories with millions of chunks, those structures become the memory bottleneck even though the chunk index is recoverable metadata and can be treated as a local disk-backed working cache.

This follow-up moves normal chunk-index work to bounded-memory disk-backed operations while preserving the current remote per-prefix shard blob format. That avoids returning to a single large uploaded/downloaded database file and keeps small archives from rewriting the whole index for tiny changes.

## What Changes

- Replace the current local L1/L2 shard-cache implementation with a local SQLite-backed chunk-index store owned entirely by `src/Arius.Core/Shared/ChunkIndex/`.
- Keep SQLite local-only: it is a cache and working store, not a repository source of truth and not uploaded as a chunk-index artifact.
- Preserve the current remote `chunk-index/<prefix>` shard blob layout and serialization format for this change.
- Store loaded shard rows, loaded-prefix freshness state, and archive dirty entries in the local SQLite store so normal lookup, entry recording, and flush avoid unbounded managed-memory collections.
- Rebuild full repair through disk-backed local state instead of grouping all reconstructed entries in memory.
- Keep dynamic chunk-prefix splitting out of scope, while introducing internal seams so a later change can replace fixed-prefix routing with longest-prefix routing and route-manifest interpretation behind `ChunkIndexService`.
- Add bounded restore/list chunk-index lookup behavior so restore and list no longer require all candidate content hashes or lookup results to be materialized at once.
- Use synchronous SQLite ADO.NET APIs inside chunk-index internals because `Microsoft.Data.Sqlite` async APIs execute synchronously.
- Use bounded batching for SQLite writes and remote shard hydration so ingestion does not become per-row overhead or an unbounded in-memory queue.
- Keep `ChunkIndexService` as the public operational facade; SQLite and the local-store implementation remain internal implementation details.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `chunk-index-service`: Change chunk-index local cache, archive write-session, flush, and repair behavior to use bounded-memory disk-backed local state while preserving current remote shard compatibility and source-of-truth semantics.
- `restore-pipeline`: Change restore chunk resolution to use bounded streaming or batching instead of materializing all content hashes and lookup results at once.
- `list-query`: Change list size lookup to use bounded chunk-index lookup batches while preserving streaming output.

## Impact

- Affected code: `src/Arius.Core/Shared/ChunkIndex/`, `src/Arius.Core/ServiceCollectionExtensions.cs`, and chunk-index tests.
- Affected behavior: local chunk-index cache files change from per-prefix plaintext L2 files plus L1 memory pages to a local SQLite working store. The store is still discardable and recoverable from remote shard blobs or chunk blobs via repair.
- Affected dependencies: add a SQLite ADO.NET dependency, expected to be `Microsoft.Data.Sqlite`, used only by chunk-index internals. Do not introduce EF Core.
- Remote compatibility: current `chunk-index/<prefix>` blob names and shard serialization remain unchanged.
- Public API: `ChunkIndexService` remains the public facade for current callers. Any new internal route/local-store abstractions stay inside `Shared/ChunkIndex`.
- Out of scope: dynamic prefix splitting and route manifest publication are planned follow-ups rather than part of this change.
