## ADDED Requirements

### Requirement: AzureBlobRepositoryStore provides init/backup/restore over Azure
The system SHALL provide `AzureBlobRepositoryStore` in `Arius.Azure` that implements the same `InitAsync / BackupAsync / RestoreAsync` contract as `FileSystemRepositoryStore` but uses `IBlobStorageProvider` for all storage I/O. It SHALL reuse the existing chunking, packing, and crypto components from `Arius.Core` without modification.

#### Scenario: Init creates container and writes config
- **WHEN** `InitAsync` is called with a repository name and passphrase
- **THEN** the Azure container is created if it does not exist, a key is derived and uploaded to `keys/{id}.json` at Cold tier, and `config.json` is uploaded at Cold tier

#### Scenario: Backup uploads pack files and metadata
- **WHEN** `BackupAsync` is called with a source path and `TargetTier`
- **THEN** new pack files are uploaded to `data/{prefix2}/{packId}.pack` at the specified `TargetTier`, and snapshot / index delta files are uploaded at Cold tier

#### Scenario: Restore downloads pack files and reconstructs files
- **WHEN** `RestoreAsync` is called for a snapshot
- **THEN** the required pack files are downloaded from Azure, decrypted, unpacked, and the original files are written to the restore path

### Requirement: Blob path conventions
The system SHALL store blobs under the following path conventions:

| Content | Blob name pattern | Tier |
|---|---|---|
| Config | `config.json` | Cold |
| Key files | `keys/{id}.json` | Cold |
| Snapshots | `snapshots/{id}.json` | Cold |
| Index deltas | `index/{snapshotId}.json` | Cold |
| Pack files | `data/{prefix2}/{packId}.pack` | Caller-specified (default Archive) |

`{prefix2}` SHALL be the first 2 hex characters of the pack ID.

#### Scenario: Pack blob path includes prefix subdirectory
- **WHEN** a pack with ID `abcdef...` is uploaded
- **THEN** it is stored at `data/ab/{packId}.pack`

#### Scenario: Metadata blobs use Cold tier regardless of TargetTier
- **WHEN** a snapshot, index delta, key file, or config file is uploaded
- **THEN** it is uploaded with `BlobTier.Cold` regardless of the `TargetTier` specified on `BackupRequest`

### Requirement: Restore only handles immediately downloadable tiers
In this change, `RestoreAsync` SHALL only support restoring packs uploaded to Hot, Cool, or Cold tier. If a required pack is in Archive tier and has not been rehydrated, the restore operation SHALL throw an informative exception rather than silently hang or corrupt data.

#### Scenario: Restore from Cold-tier packs succeeds
- **WHEN** all required packs are in Cold (or Hot/Cool) tier
- **THEN** restore completes successfully and all files are written to the target path

#### Scenario: Restore from Archive-tier packs throws
- **WHEN** a required pack is in Archive tier and has not been rehydrated
- **THEN** `RestoreAsync` throws an exception indicating the pack requires rehydration before restore

### Requirement: Azurite-based integration tests for Azure store
The system SHALL provide integration tests in `Arius.Azure.Tests` that exercise the full `init → backup → restore` pipeline against an Azurite emulator started via `DotNet.Testcontainers`. Tests SHALL use `TargetTier = Cold` to ensure immediate downloadability.

#### Scenario: init → backup → restore round-trip
- **WHEN** an init, then a backup with `TargetTier = Cold`, then a restore are performed against Azurite
- **THEN** all restored files match the original content byte-for-byte

#### Scenario: Second backup deduplicates
- **WHEN** a second backup is performed with no file changes
- **THEN** no new pack files are uploaded (deduplication is honoured)
