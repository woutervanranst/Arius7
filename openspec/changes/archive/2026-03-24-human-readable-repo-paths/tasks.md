## 1. Core path construction

- [x] 1.1 Replace `ComputeRepoId(accountName, containerName)` with `GetRepoDirectoryName(accountName, containerName)` in `ChunkIndexService.cs` — return `$"{accountName}-{containerName}"` instead of the SHA256 hash
- [x] 1.2 Update `ChunkIndexService.GetL2Directory()` to use `Path.Combine(home, ".arius", GetRepoDirectoryName(...), "chunk-index")` (dropping the `cache/` level)
- [x] 1.3 Update `FileTreeBuilder.GetDiskCacheDirectory()` in `TreeService.cs` to use `Path.Combine(home, ".arius", GetRepoDirectoryName(...), "filetrees")` (dropping the `cache/` level)

## 2. Tests

- [x] 2.1 Remove or replace `ComputeRepoId` determinism and uniqueness tests in `ShardTests.cs` with tests for `GetRepoDirectoryName` format
- [x] 2.2 Update integration test cache cleanup in `PipelineFixture.DisposeAsync` to use the new path scheme
- [x] 2.3 Update E2E test cache cleanup in `E2ETests.DisposeAsync` to use the new path scheme
- [ ] 2.4 Run full test suite (`dotnet test`) and verify all tests pass

## 3. Cleanup

- [x] 3.1 Remove the `SHA256`/`Convert.ToHexString` imports from `ChunkIndexService.cs` if no longer needed
