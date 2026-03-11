## 1. Core Models and Interfaces

- [x] 1.1 Add `BlobTier` enum (Hot/Cool/Cold/Archive) to `src/Arius.Core/Models/CoreTypes.cs`
- [x] 1.2 Add `IBlobStorageProvider` interface to `src/Arius.Core/Infrastructure/IBlobStorageProvider.cs` (UploadAsync, DownloadAsync, ListAsync, DeleteAsync, SetTierAsync)
- [x] 1.3 Add `TargetTier` property (type `BlobTier`, default `Archive`) to `BackupRequest` in `src/Arius.Core/Application/Backup/BackupContracts.cs`

## 2. AzureBlobStorageProvider

- [x] 2.1 Create `src/Arius.Azure/AzureBlobStorageProvider.cs` implementing `IBlobStorageProvider` using `BlobContainerClient`
- [x] 2.2 Implement `UploadAsync` — upload stream to blob and set the specified tier
- [x] 2.3 Implement `DownloadAsync` — return stream from `BlobClient.DownloadStreamingAsync`
- [x] 2.4 Implement `ListAsync` — enumerate blobs with prefix using `BlobContainerClient.GetBlobsAsync`
- [x] 2.5 Implement `DeleteAsync` — delete blob via `BlobClient.DeleteIfExistsAsync`
- [x] 2.6 Implement `SetTierAsync` — call `BlobClient.SetAccessTierAsync`
- [x] 2.7 Implement `CreateContainerIfNotExistsAsync` helper (called from `AzureBlobRepositoryStore.InitAsync`)

## 3. AzureBlobRepositoryStore

- [x] 3.1 Create `src/Arius.Azure/AzureBlobRepositoryStore.cs` mirroring `FileSystemRepositoryStore`'s `InitAsync / BackupAsync / RestoreAsync` public surface
- [x] 3.2 Implement `InitAsync` — create container, generate key, write `config.json` and `keys/{id}.json` at Cold tier
- [x] 3.3 Implement `BackupAsync` — chunk files, pack new blobs, upload packs to `data/{prefix2}/{packId}.pack` at `request.TargetTier`; upload snapshot to `snapshots/{id}.json` and index delta to `index/{snapshotId}.json` at Cold tier
- [x] 3.4 Implement `RestoreAsync` — load snapshot, resolve required pack blobs, download from `data/{prefix2}/{packId}.pack`, decrypt and unpack, write restored files to target path
- [x] 3.5 Throw informative exception in `RestoreAsync` if a pack blob returns HTTP 409 BlobArchived (Archive tier not rehydrated)
- [x] 3.6 Delete placeholder `src/Arius.Azure/Class1.cs`

## 4. BackupHandler Wire-up

- [x] 4.1 Inject `IBlobStorageProvider` into `BackupHandler` (constructor injection) in place of the hardcoded `FileSystemRepositoryStore`
- [x] 4.2 Pass `request.TargetTier` through to `BackupAsync` when calling `AzureBlobRepositoryStore.BackupAsync`

## 5. CLI Tier Option

- [x] 5.1 Add `--tier` option (`BlobTier`, default `Archive`) to `BackupCommand` in `src/Arius.Cli/Commands/BackupCommand.cs`
- [x] 5.2 Pass the resolved `--tier` value as `TargetTier` in the `BackupRequest` constructed by `BackupCommand`

## 6. Integration Tests (Azurite)

- [x] 6.1 Create `tests/Arius.Azure.Tests/AzuriteFixture.cs` — TUnit class-level fixture that starts an Azurite container via `Testcontainers.Azurite` and exposes a `BlobServiceClient`
- [x] 6.2 Create `tests/Arius.Azure.Tests/AzureBlobRepositoryStoreTests.cs` with tests exercising:
  - init → backup (`TargetTier = Cold`) → restore round-trip (files match byte-for-byte)
  - second backup with no changes deduplicates (no new pack blobs uploaded)
- [x] 6.3 Delete placeholder `tests/Arius.Azure.Tests/Class1.cs` and `SmokeTests.cs`

## 7. Build and Test

- [x] 7.1 Run `dotnet build Arius.slnx` and fix any compilation errors
- [x] 7.2 Run `dotnet test --project tests/Arius.Core.Tests` — all 63 existing tests must still pass
- [x] 7.3 Run `dotnet test --project tests/Arius.Azure.Tests` — new Azurite-based integration tests must pass
- [x] 7.4 Commit with message summarising the Azure blob backend implementation
