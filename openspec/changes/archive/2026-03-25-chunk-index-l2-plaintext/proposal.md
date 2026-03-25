## Why

The L2 (local disk) cache for chunk index shards stores the same encrypted and gzip-compressed bytes as the remote Azure blobs. This is unnecessary: the local cache lives in `~/.arius/` under the user's own profile and requires no protection from the storage provider. Encrypting and compressing locally adds CPU overhead on every L2 read/write with no security or space benefit.

## What Changes

- The L2 disk cache (`~/.arius/{account}-{container}/chunk-index/`) will store shards as **plaintext lines** (no gzip, no encryption).
- The L3 wire format (Azure blobs) remains unchanged: gzip + optional AES-256-CBC encryption.
- `ShardSerializer` gains a local-format serialize/deserialize path (plaintext, no streams wrapping).
- `ChunkIndexService.SaveToL2` and the L2 hit path in `LoadShardAsync` switch to the new local serializer.
- The spec requirement for index shard management is updated to distinguish L2 local format from L3 wire format.

## Capabilities

### New Capabilities

_(none)_

### Modified Capabilities

- `archive-pipeline`: The requirement for index shard merge and upload conflates L2 local format with L3 wire format. It needs to specify that L2 stores plaintext lines (no compression, no encryption) while L3 uses gzip + optional encryption.

## Impact

- `src/Arius.Core/ChunkIndex/ShardSerializer.cs` — new plaintext serialize/deserialize methods
- `src/Arius.Core/ChunkIndex/ChunkIndexService.cs` — `SaveToL2` and `LoadShardAsync` L2 branch
- Existing L2 cache files on disk will be unreadable after the change (stale encrypted bytes). They will be treated as missing and re-fetched from L3 on next run — this is safe and self-healing.
- Tests that verify L2 roundtrip behavior need updating.
