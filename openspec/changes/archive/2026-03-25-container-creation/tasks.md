## 1. Interface and Implementation

- [x] 1.1 Add `Task CreateContainerIfNotExistsAsync(CancellationToken ct)` to `IBlobStorageService`
- [x] 1.2 Implement in `AzureBlobStorageService` via `BlobContainerClient.CreateIfNotExistsAsync()`

## 2. Call Site Integration

- [x] 2.1 Call `CreateContainerIfNotExistsAsync` at the start of `ArchivePipelineHandler.Handle()` before any blob operations
- [x] 2.2 Call `CreateContainerIfNotExistsAsync` at the start of `RestorePipelineHandler.Handle()` before any blob operations

## 3. Tests

- [x] 3.1 Integration test: archive to a non-existent container succeeds (container auto-created)
- [x] 3.2 Integration test: archive to an existing container succeeds (idempotent, no error)
