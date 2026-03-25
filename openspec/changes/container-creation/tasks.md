## 1. Interface and Implementation

- [ ] 1.1 Add `Task CreateContainerIfNotExistsAsync(CancellationToken ct)` to `IBlobStorageService`
- [ ] 1.2 Implement in `AzureBlobStorageService` via `BlobContainerClient.CreateIfNotExistsAsync()`

## 2. Call Site Integration

- [ ] 2.1 Call `CreateContainerIfNotExistsAsync` at the start of `ArchivePipelineHandler.Handle()` before any blob operations
- [ ] 2.2 Call `CreateContainerIfNotExistsAsync` at the start of `RestorePipelineHandler.Handle()` before any blob operations

## 3. Tests

- [ ] 3.1 Integration test: archive to a non-existent container succeeds (container auto-created)
- [ ] 3.2 Integration test: archive to an existing container succeeds (idempotent, no error)
