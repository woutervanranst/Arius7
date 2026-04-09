## 1. Shared Chunk Boundaries

- [x] 1.1 Add `RepositoryPaths` and move repository-local cache/log directory helper usage off `ChunkIndexService`
- [x] 1.2 Move `ChunkHydrationStatus` into `Shared/ChunkIndex/ChunkHydrationStatus.cs` and update references to the shared type
- [x] 1.3 Add the `ChunkStorageService` interface and implementation surface, including upload, download, hydration status, rehydration, and cleanup-plan APIs

## 2. Chunk Storage Mechanics

- [x] 2.1 Move large and tar chunk upload protocol details into `ChunkStorageService`, including progress wiring, gzip/encryption transforms, metadata writes, tier assignment, and already-exists recovery
- [x] 2.2 Move thin chunk creation and thin-chunk recovery rules into `ChunkStorageService`
- [x] 2.3 Move chunk download source selection, decryption, gunzip, and hydration-state resolution into `ChunkStorageService`
- [x] 2.4 Implement rehydration start and rehydrated cleanup planning/execution in `ChunkStorageService`

## 3. Feature Refactors

- [x] 3.1 Refactor `ArchiveCommandHandler` to use `ChunkIndexService.LookupAsync(string)` and `ChunkStorageService` for large, tar, and thin chunk operations while keeping archive orchestration in the handler
- [x] 3.2 Refactor `RestoreCommandHandler` to use `ChunkStorageService` for download, hydration status, rehydration start, and rehydrated cleanup planning while keeping restore orchestration in the handler
- [x] 3.3 Refactor `ChunkHydrationStatusQueryHandler` to resolve hydration state through `ChunkStorageService` after chunk-index lookup

## 4. Verification

- [ ] 4.1 Update unit and integration tests around chunk upload/download, hydration status, and cleanup to target the new service boundary
- [ ] 4.2 Update feature tests to assert archive/restore orchestration through `ChunkIndexService` and `ChunkStorageService` rather than raw chunk blob details
- [ ] 4.3 Run the relevant test suites and verify no user-visible archive, restore, or list behavior regresses
