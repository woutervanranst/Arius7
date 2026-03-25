## Why

The production CLI path does not call `CreateIfNotExistsAsync` on the blob container before use. If the container does not exist, the first blob operation crashes with an unhandled Azure `RequestFailedException`. The only place `CreateIfNotExistsAsync` is called is in the E2E test fixture (`AzureFixture.cs:52`), masking the bug in tests. First-time users hitting a new container will get a cryptic error instead of automatic container creation.

## What Changes

- **Add `CreateIfNotExistsAsync` to startup path**: Call `BlobContainerClient.CreateIfNotExistsAsync()` during DI setup or at the start of the archive/restore pipeline handler, before any blob operations. This is idempotent — safe to call on every run.
- **Add `CreateContainerIfNotExistsAsync` to `IBlobStorageService`**: Add a container-level operation to the storage interface so the creation is abstracted (not Azure-specific in Core).

## Capabilities

### New Capabilities
_None_

### Modified Capabilities
- `blob-storage`: Add container creation requirement — the system SHALL ensure the container exists before performing any blob operations.

## Impact

- **Arius.Core/Storage**: `IBlobStorageService` gets `CreateContainerIfNotExistsAsync` method.
- **Arius.AzureBlob**: `AzureBlobStorageService` implements it via `BlobContainerClient.CreateIfNotExistsAsync()`.
- **Arius.Core**: `ArchivePipelineHandler` and `RestorePipelineHandler` call it at the start of `Handle()`, or it's called during DI registration.
- **Tests**: E2E fixture's explicit `CreateIfNotExistsAsync` call becomes redundant but harmless.
- **Small, low-risk change**.
