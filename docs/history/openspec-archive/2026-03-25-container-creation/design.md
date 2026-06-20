## Context

The `IBlobStorageService` interface and its Azure implementation handle all blob operations (upload, download, HEAD, list, copy), but there is no container creation method. The production CLI path never calls `BlobContainerClient.CreateIfNotExistsAsync()`. The only place this is called is in `AzureFixture.cs:52` (the E2E test fixture), which masks the bug. A first-time user pointing at a new container gets a cryptic `RequestFailedException` instead of automatic creation.

## Goals / Non-Goals

**Goals:**
- Add `CreateContainerIfNotExistsAsync` to `IBlobStorageService`
- Implement it in `AzureBlobStorageService` via `BlobContainerClient.CreateIfNotExistsAsync()`
- Call it at the start of pipeline handlers before any blob operations
- Idempotent: safe to call on every run, no-op if container already exists

**Non-Goals:**
- Container deletion or management
- Container-level access policy configuration
- Removing the redundant call in `AzureFixture` (it's harmless)

## Decisions

### 1. Call site: start of pipeline handlers

**Decision**: Call `CreateContainerIfNotExistsAsync` at the start of both `ArchivePipelineHandler.Handle()` and `RestorePipelineHandler.Handle()`, before any blob operations.

**Rationale**: Placing it in the handler ensures it runs once per operation. Placing it in DI registration is problematic because the container client may not be fully configured at registration time. The handler call is explicit, visible, and testable.

**Alternative considered**: Calling in the `AzureBlobStorageService` constructor. Rejected because constructors should not perform async I/O, and it would require blocking or lazy initialization patterns.

### 2. Interface method on `IBlobStorageService`

**Decision**: Add `Task CreateContainerIfNotExistsAsync(CancellationToken ct)` to `IBlobStorageService`.

**Rationale**: Core depends on the interface, not the Azure implementation. The container creation must be abstracted. The method is simple, no return value needed (fire and forget the boolean from Azure SDK).

## Risks / Trade-offs

- **Extra API call per run** → `CreateIfNotExistsAsync` is one lightweight HEAD+PUT-if-missing call. For existing containers it's just a HEAD (200) and returns. Negligible. → No mitigation needed.
- **Permissions** → The storage account key grants full container management rights. If the user provided a SAS token with limited permissions, container creation would fail. → Mitigation: the CLI currently requires the full account key, so this is not an issue. If SAS support is added later, this call should be conditional.
