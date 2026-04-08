## Why

The local disk cache under `~/.arius/cache/` uses a truncated SHA-256 hash (`ComputeRepoId`) as the directory name (e.g. `a1b2c3d4e5f6`). This makes it impossible to tell which storage account and container a cache directory belongs to without reverse-engineering the hash. Since Azure account names (`[a-z0-9]{3,24}`) and container names (`[a-z0-9-]{3,63}`) are already filesystem-safe, there is no reason not to use them directly.

## What Changes

- **BREAKING**: Replace the hashed repo-id directory scheme with a human-readable `{accountName}-{containerName}` flat directory name under `~/.arius/`.
- Remove `ChunkIndexService.ComputeRepoId()` and its unit tests.
- Update all path-construction call sites (chunk-index L2 cache, filetree disk cache, test cleanup) to use the new naming.
- No migration of existing hashed directories — they are rebuildable caches and will be orphaned harmlessly.

## Capabilities

### New Capabilities

_(none)_

### Modified Capabilities

- `blob-storage`: The "Per-repository cache identification" requirement currently specifies `SHA256(accountname + container)[:12]` as the repo-id and `~/.arius/cache/<repo-id>/` as the cache path. This changes to `{accountName}-{containerName}` with path `~/.arius/{account}-{container}/`. The tiered chunk index cache and tree blob caching requirements reference this path.

## Impact

- **Arius.Core**: `ChunkIndexService.ComputeRepoId()` removed, `GetL2Directory()` and `FileTreeBuilder.GetDiskCacheDirectory()` updated.
- **Arius.Core.Tests**: `ComputeRepoId` unit tests removed or replaced with new path-format tests.
- **Arius.Integration.Tests / Arius.E2E.Tests**: Cache cleanup paths in `DisposeAsync` updated.
- **Disk layout**: `~/.arius/{account}-{container}/chunk-index/` and `~/.arius/{account}-{container}/filetrees/` replace `~/.arius/cache/{hash}/...`. Note: the `cache/` level is dropped — no longer needed since the directory name is self-describing.
- **Existing users**: Old `~/.arius/cache/{hash}/` directories become orphaned. They are small and can be manually deleted.
