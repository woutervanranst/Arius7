## Why

The `FileSystemRepositoryStore` provides a working end-to-end pipeline (init → backup → restore) but stores data on the local filesystem. The system's purpose is Azure Blob Storage as its sole backend. This change wires the fully implemented chunking, packing, and crypto stack into an Azure-backed store, and adds per-backup tier selection to let callers control cost/access trade-offs at backup time.

## What Changes

- Add `AzureBlobRepositoryStore` in `Arius.Azure` implementing the same init/backup/restore interface as `FileSystemRepositoryStore` but backed by Azure Blob Storage
- Add `IBlobStorageProvider` interface in `Arius.Core` abstracting upload, download, list, and delete operations
- Implement `AzureBlobStorageProvider` in `Arius.Azure` using `Azure.Storage.Blobs` SDK
- Add `TargetTier` option to `BackupRequest` (Hot / Cool / Cold / Archive); default is Archive
- Upload pack files using the caller-specified tier; metadata (snapshots, index, keys, config) always Cold
- `RestoreFileAsync`: download packs from Azure; rehydrate Archive-tier packs as needed before download
- Integration tests use Azurite (local Azure emulator) via Docker test container

## Capabilities

### New Capabilities
- `azure-storage-provider`: `IBlobStorageProvider` interface + `AzureBlobStorageProvider` implementation — upload, download, list, delete blobs with tier assignment
- `azure-backup-restore`: `AzureBlobRepositoryStore` — full init/backup/restore pipeline over Azure Blob Storage using the existing chunker, packer, and crypto stack

### Modified Capabilities
- `backup`: Add `TargetTier` parameter (Hot/Cool/Cold/Archive, default Archive) controlling the access tier for uploaded data packs
- `azure-backend`: Add tier-selection requirement (callers can override the default Archive tier for pack uploads)

## Impact

- `Arius.Core`: new `IBlobStorageProvider` interface; `BackupRequest` gains optional `TargetTier` property
- `Arius.Azure`: new `AzureBlobStorageProvider` and `AzureBlobRepositoryStore`
- `Arius.Cli` `BackupCommand`: new `--tier` option wired to `TargetTier`
- `Arius.Azure.Tests`: Azurite-based integration tests for the Azure store
- No changes to existing `FileSystemRepositoryStore` or `Arius.Core.Tests`
