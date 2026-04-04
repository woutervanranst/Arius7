## 1. TreeCacheService — Core Implementation

- [ ] 1.1 Create `TreeCacheService` class in `Arius.Core/Shared/FileTree/` with constructor accepting `IBlobContainerService`, `IEncryptionService`, `string accountName`, `string containerName`. Create disk cache directory `~/.arius/{repo}/filetrees/` in constructor.
- [ ] 1.2 Add `TreeBlobSerializer.Deserialize(byte[])` method (inverse of `Serialize`) for reading plaintext cache files.
- [ ] 1.3 Implement `ReadAsync(string hash, CancellationToken)`: disk cache check → Azure download fallback → deserialize via `TreeBlobSerializer.DeserializeFromStorageAsync` → write plaintext to disk → return `TreeBlob`. On cache hit, read via `TreeBlobSerializer.Deserialize`.
- [ ] 1.4 Implement `WriteAsync(string hash, TreeBlob, CancellationToken)`: serialize via `TreeBlobSerializer.SerializeForStorageAsync` → upload to Azure (`overwrite: false`, catch `BlobAlreadyExistsException`) → write plaintext to disk cache.
- [ ] 1.5 Implement `ValidateAsync(CancellationToken)`: enumerate `snapshots/` directory for timestamp-named marker files → sort → latest local timestamp. Call `ListAsync("snapshots/")` to find latest remote → compare. On mismatch: `ListAsync("filetrees/")` → create empty marker file on disk for each remote blob not already cached; delete chunk-index L2 directory files.
- [ ] 1.6 Implement `ExistsInRemote(string hash)`: always `File.Exists` on disk (works on both paths after `ValidateAsync`). Throw `InvalidOperationException` if called before `ValidateAsync`.
- [ ] 1.7 Implement `WriteSnapshotMarkerAsync(string timestamp, CancellationToken)`: create empty file at `~/.arius/{repo}/snapshots/{timestamp}`. Create directory if needed.

## 2. Wire ListQueryHandler Through Cache

- [ ] 2.1 Add `TreeCacheService` dependency to `ListQueryHandler` constructor (replace or supplement `IBlobContainerService` for tree blob reads).
- [ ] 2.2 Replace `_blobs.DownloadAsync` + `TreeBlobSerializer.DeserializeFromStorageAsync` at line 102 (root blob read) with `_treeCacheService.ReadAsync(treeHash)`.
- [ ] 2.3 Replace `_blobs.DownloadAsync` + `TreeBlobSerializer.DeserializeFromStorageAsync` at line 258 (subtree traversal) with `_treeCacheService.ReadAsync(currentHash)`.

## 3. Wire RestoreCommandHandler Through Cache

- [ ] 3.1 Add `TreeCacheService` dependency to `RestoreCommandHandler` constructor.
- [ ] 3.2 Replace `_blobs.DownloadAsync` + `TreeBlobSerializer.DeserializeFromStorageAsync` at line 569 (WalkTreeAsync) with `_treeCacheService.ReadAsync(treeHash)`.

## 4. Wire Archive Tree Build Through Cache

- [ ] 4.1 Add `TreeCacheService` dependency to `TreeBuilder` (or refactor `TreeBuilder` to accept it in constructor instead of `IBlobContainerService` for tree operations).
- [ ] 4.2 Replace ad-hoc disk cache check + `GetMetadataAsync` HEAD in `EnsureUploadedAsync` with `TreeCacheService.ExistsInRemote(hash)`.
- [ ] 4.3 Replace inline `_blobs.UploadAsync` + `File.WriteAllBytes` in `EnsureUploadedAsync` with `TreeCacheService.WriteAsync(hash, treeBlob)`.
- [ ] 4.4 Remove `_diskCacheDir` field and `GetDiskCacheDirectory` method from `TreeBuilder` (cache directory management moves to `TreeCacheService`).

## 5. Archive Pipeline Integration

- [ ] 5.1 Call `TreeCacheService.ValidateAsync()` at the start of the end-of-pipeline phase in `ArchiveCommandHandler` (before `FlushAsync` and `BuildAsync`).
- [ ] 5.2 After `SnapshotService.CreateAsync` succeeds, call `TreeCacheService.WriteSnapshotMarkerAsync(timestamp)` to write the empty marker file.

## 6. DI Registration

- [ ] 6.1 Register `TreeCacheService` as singleton in `AddArius`, wiring `IBlobContainerService`, `IEncryptionService`, `accountName`, and `containerName`.

## 7. Tests

- [ ] 7.1 Unit test: `ReadAsync` cache hit — verify no Azure call when disk file exists.
- [ ] 7.2 Unit test: `ReadAsync` cache miss — verify Azure download, disk write-through, and correct `TreeBlob` returned.
- [ ] 7.3 Unit test: `WriteAsync` — verify Azure upload and disk cache write.
- [ ] 7.4 Unit test: `WriteAsync` with existing blob — verify `BlobAlreadyExistsException` is caught and disk cache is still written.
- [ ] 7.5 Unit test: `ValidateAsync` snapshot match — verify no `ListAsync("filetrees/")` call.
- [ ] 7.6 Unit test: `ValidateAsync` snapshot mismatch — verify `ListAsync("filetrees/")` called, empty marker files created on disk, chunk-index L2 directory deleted.
- [ ] 7.7 Unit test: `ValidateAsync` mismatch does not overwrite existing cache files.
- [ ] 7.8 Unit test: `ValidateAsync` no local markers — verify slow-path triggered.
- [ ] 7.9 Unit test: `ValidateAsync` no remote snapshots — verify fast-path mode (empty repo).
- [ ] 7.10 Unit test: `ExistsInRemote` — verify `File.Exists` check (same behavior on both paths).
- [ ] 7.11 Unit test: `ExistsInRemote` before `ValidateAsync` — verify `InvalidOperationException`.
- [ ] 7.12 Unit test: `WriteSnapshotMarkerAsync` — verify empty file created with correct name.
- [ ] 7.13 Integration test: `ListQueryHandler` uses `TreeCacheService.ReadAsync` instead of direct blob download.
- [ ] 7.14 Integration test: `RestoreCommandHandler` uses `TreeCacheService.ReadAsync` instead of direct blob download.
- [ ] 7.15 Integration test: `TreeBuilder` uses `TreeCacheService.ExistsInRemote` and `WriteAsync` instead of ad-hoc cache logic.
- [ ] 7.16 Integration test: End-to-end archive → ls → restore cycle with cache validation (snapshot match fast path).
