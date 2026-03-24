## Context

The local disk cache at `~/.arius/cache/<repo-id>/` uses `SHA256(accountName + containerName)[:12]` (lowercase hex) as the directory identifier. This hash is computed by `ChunkIndexService.ComputeRepoId()` and used in two path-construction methods:

- `ChunkIndexService.GetL2Directory()` → `~/.arius/cache/<repo-id>/chunk-index/`
- `TreeBuilder.GetDiskCacheDirectory()` → `~/.arius/cache/<repo-id>/filetrees/`

Both `accountName` and `containerName` are plain strings available at every call site. Azure naming rules guarantee they are filesystem-safe: account names are `[a-z0-9]{3,24}`, container names are `[a-z0-9-]{3,63}`.

The `blob-storage` spec currently defines the path scheme in the "Per-repository cache identification" requirement.

## Goals / Non-Goals

**Goals:**
- Make cache directories human-readable so users can identify which repo a directory belongs to
- Simplify the code by removing the unnecessary hash computation
- Establish a `~/.arius/{account}-{container}/` base path that the audit-logging change can extend with a `logs/` subdirectory

**Non-Goals:**
- Migrating existing hashed cache directories (they are rebuildable caches)
- Changing the blob storage container layout (only the local disk path changes)
- Supporting non-Azure backends (account/container naming is Azure-specific, but the path scheme would work for any string pair)

## Decisions

### Decision 1: Flat `{account}-{container}` naming (not nested)

**Choice:** `~/.arius/mystorageacct-photos/` (flat, single hyphen separator)

**Alternatives considered:**
- Nested `~/.arius/mystorageacct/photos/` — cleaner for multiple containers under one account, but adds directory depth and complicates path construction
- Underscore separator `mystorageacct_photos` — underscores are not valid in Azure account names, so this would be unambiguous, but hyphens are more conventional for slugs

**Rationale:** Azure account names cannot contain hyphens. Container names can. So `{account}-{container}` is unambiguous — the first hyphen is always the separator (account names are `[a-z0-9]` only). This keeps it flat and simple.

### Decision 2: Drop the `cache/` intermediate directory

**Choice:** `~/.arius/{account}-{container}/chunk-index/` instead of `~/.arius/cache/{account}-{container}/chunk-index/`

**Rationale:** The `cache/` level was useful when directories were opaque hashes (it grouped them under a named purpose). With human-readable names, the top level is self-describing. Dropping `cache/` also makes room for sibling directories like `logs/` at the repo level without nesting under `cache/`.

### Decision 3: Replace `ComputeRepoId` with a simple string format method

**Choice:** Replace `ComputeRepoId(accountName, containerName)` with a method like `GetRepoDirectoryName(accountName, containerName)` that returns `$"{accountName}-{containerName}"`. Keep it as a static method for testability and single-source-of-truth.

**Rationale:** A dedicated method (rather than inlining the format string) ensures consistent naming across all call sites and is easily unit-tested.

### Decision 4: No migration

**Choice:** Old `~/.arius/cache/<hash>/` directories are left orphaned.

**Rationale:** They are disk caches, fully rebuildable on next access. Their total size is typically small (index shards + tree blobs). Users who want to reclaim space can delete `~/.arius/cache/` manually.

## Risks / Trade-offs

- **[Orphaned directories]** → Users with many repositories may accumulate orphaned `~/.arius/cache/<hash>/` directories. Mitigation: document in release notes that `~/.arius/cache/` can be safely deleted.
- **[Separator ambiguity edge case]** → If Azure ever allows hyphens in account names, `{account}-{container}` would become ambiguous. Mitigation: Azure account naming rules have been stable for 10+ years; extremely unlikely to change. The method is the single point to update if needed.
- **[Docker volume mounts]** → The `blob-storage` spec mentions Docker cache mounts at `~/.arius/cache/`. The mount path changes to `~/.arius/`. Mitigation: document the new path. Since the old path would be empty, Docker users need to update their `-v` mount.
